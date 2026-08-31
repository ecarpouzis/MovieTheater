using System.Diagnostics;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MovieTheater.Books.Controllers;
using MovieTheater.Books.Db;
using MovieTheater.Books.Identity;

namespace MovieTheater.Books.Services
{
    /// <summary>
    /// Keeps the expensive per-user browse payloads (facets, group heads, the first groups page) permanently warm so
    /// no visitor ever pays the recompute. On the standalone site those caches expired every few minutes, which
    /// guaranteed that whoever visited "after a while" personally sat through it (measured there: facets 13 s cold).
    ///
    /// <para><b>Change-driven, not periodic</b> — an idle machine does no background work at all. Every poll reads
    /// SQLite's <c>PRAGMA data_version</c> on a dedicated long-lived connection (a header read, effectively free;
    /// it is per-connection, which is why the connection has to be kept). Only when some other connection has
    /// committed does it run the catalog FINGERPRINT below, and only when that moved does it warm. The fingerprint
    /// deliberately excludes the user-activity tables (<c>UserItemState</c>, <c>GroupMark</c>,
    /// <c>KnownIdentity</c>): reading a book or marking a series must never trigger a re-warm, and none of the
    /// warmed payloads depend on them. A slow heartbeat bounds the worst case if an exotic in-place edit ever slips
    /// past the fingerprint.</para>
    ///
    /// <para><b>Mechanism.</b> For each row in <c>KnownIdentity</c> — the last-seen identity payload the auth
    /// handler records per user — it fabricates the exact principal the header would have produced and invokes the
    /// REAL controller actions. They cache under their normal keys, so a live request is a pure cache hit on an
    /// identical code path: zero drift between what is warmed and what is served. A user is cold until their first
    /// request after a fresh install (no KnownIdentity row yet) — stated, and accepted.</para>
    ///
    /// <para>Each target runs in its own DI scope (fresh DbContext), sequentially, so a pass never floods SQLite.
    /// Nothing throws out of the loop: one bad target is logged and skipped, one bad pass retries next poll.</para>
    /// </summary>
    public sealed class CacheWarmupService : BackgroundService
    {
        private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(5);
        private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(60);
        /// <summary>Staleness backstop for anything the fingerprint cannot see. Four light passes a day.</summary>
        private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromHours(6);

        private static readonly string[] WarmedGroupings = { "collection", "series", "publisher", "decade" };

        /// <summary>
        /// The prose half's shelves (R9 S11). Warmed for the same reason the comic ones are: these heads run
        /// over 100k+ books, and `author` is a credit aggregation — the first visitor must not be the one who
        /// computes them. <see cref="BrowseController.BookGroupAxes"/> is the one list; this follows it.
        /// </summary>
        private static string[] WarmedBookGroupings => BrowseController.BookGroupAxes;

        /// <summary>
        /// Catalog-only change fingerprint. Counts catch inserts and deletes; the SUM terms catch the realistic
        /// in-place edits (re-links, rating reloads, dedup exclusions, name re-resolves, a resolve pass). It runs
        /// only when data_version says something committed, so its cost is paid around real write activity only.
        /// </summary>
        private const string FingerprintSql = @"SELECT
  (SELECT COUNT(*) FROM Item) || '|' ||
  (SELECT IFNULL(SUM(IsExcluded),0) FROM Item) || '|' ||
  (SELECT IFNULL(SUM(IFNULL(SeriesId,0)),0) FROM Item) || '|' ||
  (SELECT IFNULL(MAX(ResolvedAt),'') FROM Item) || '|' ||
  (SELECT COUNT(*) FROM Series) || '|' ||
  (SELECT IFNULL(SUM(LENGTH(IFNULL(Name,''))),0) FROM Series) || '|' ||
  (SELECT IFNULL(SUM(IFNULL(ResolvedRating,0)),0) FROM Series) || '|' ||
  (SELECT COUNT(*) FROM ComicDetail) || '|' ||
  (SELECT COUNT(*) FROM Insight) || '|' ||
  (SELECT IFNULL(SUM(IsCurrent),0) FROM Insight) || '|' ||
  (SELECT COUNT(*) FROM InsightTag) || '|' ||
  (SELECT COUNT(*) FROM ItemTag) || '|' ||
  (SELECT COUNT(*) FROM SeriesTag) || '|' ||
  (SELECT COUNT(*) FROM ItemCredit) || '|' ||
  (SELECT COUNT(*) FROM TagAlias) || '|' ||
  (SELECT COUNT(*) FROM KidSafeTag) || '|' ||
  (SELECT COUNT(*) FROM Rating) || '|' ||
  (SELECT IFNULL(SUM(IFNULL(Value,0)),0) FROM Rating) || '|' ||
  (SELECT COUNT(*) FROM Publisher) || '|' ||
  (SELECT COUNT(*) FROM Folder) || '|' ||
  (SELECT COUNT(*) FROM CollectionNode) || '|' ||
  (SELECT COUNT(*) FROM CvVolume) || '|' ||
  (SELECT COUNT(*) FROM ExternalWork) || '|' ||
  (SELECT COUNT(*) FROM LocgComic)";

        private readonly IServiceScopeFactory scopeFactory;
        private readonly BooksOptions options;
        private readonly ILogger<CacheWarmupService> logger;

        private SqliteConnection? probe;
        private long? lastDataVersion;
        private string? lastFingerprint;
        private DateTime lastWarmUtc = DateTime.MinValue;

        public CacheWarmupService(IServiceScopeFactory scopeFactory, BooksOptions options, ILogger<CacheWarmupService> logger)
        {
            this.scopeFactory = scopeFactory;
            this.options = options;
            this.logger = logger;
        }

        private enum WarmReason { None, Startup, DataChanged, Heartbeat }

        protected override async Task ExecuteAsync(CancellationToken ct)
        {
            if (options.DbPath == null) return;
            try { await Task.Delay(StartupDelay, ct); }
            catch (OperationCanceledException) { return; }

            while (!ct.IsCancellationRequested)
            {
                try
                {
                    var reason = await DecideAsync(ct);
                    if (reason != WarmReason.None) await WarmAsync(reason, ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
                catch (Exception ex) { logger.LogWarning(ex, "Books cache warm cycle failed; retrying next poll."); }

                try { await Task.Delay(PollInterval, ct); }
                catch (OperationCanceledException) { return; }
            }
        }

        private async Task<WarmReason> DecideAsync(CancellationToken ct)
        {
            if (lastWarmUtc == DateTime.MinValue) return WarmReason.Startup;
            if (DateTime.UtcNow - lastWarmUtc >= HeartbeatInterval) return WarmReason.Heartbeat;

            // Cheap first gate: has ANY connection committed since we last looked? Ordinary browsing trips this
            // too (positions, marks), so a moved version only graduates to a re-warm when the catalog fingerprint
            // itself moved.
            var version = await ProbeAsync<long>("PRAGMA data_version", ct);
            if (version == lastDataVersion) return WarmReason.None;
            lastDataVersion = version;
            var fingerprint = await ProbeAsync<string>(FingerprintSql, ct);
            return fingerprint == lastFingerprint ? WarmReason.None : WarmReason.DataChanged;
        }

        private async Task WarmAsync(WarmReason reason, CancellationToken ct)
        {
            List<ClaimsPrincipal> principals;
            using (var scope = scopeFactory.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<BooksDb>();
                principals = (await db.KnownIdentities.AsNoTracking()
                        .Select(k => new { k.UserId, k.Username, k.IsAdmin, k.MaturityCeiling })
                        .ToListAsync(ct))
                    .Select(k => BooksIdentity.Principal(k.UserId, k.Username ?? "", k.IsAdmin, k.MaturityCeiling))
                    .ToList();
            }

            var sw = Stopwatch.StartNew();
            int ok = 0, failed = 0;
            foreach (var principal in principals)
            {
                ct.ThrowIfCancellationRequested();
                void Tally(bool success) { if (success) ok++; else failed++; }

                Tally(await RunAsync<BrowseController>(principal, "facets", c => c.GetFacets(null, ct), ct));

                // group-letters computes and caches EXACTLY the heads phase without paying for band items, so all
                // four groupings warm cheaply and the band fetches then hit that same cache.
                foreach (var groupBy in WarmedGroupings)
                    Tally(await RunAsync<BrowseController>(principal, "heads:" + groupBy,
                        c => c.GetGroupLetters(groupBy, null, null, null, false, false, ct: ct), ct));

                // The same phase over the BOOK half. Unfiltered only: a rail with chips on it is a different
                // signature and computes on demand, exactly as the comic side does.
                foreach (var groupBy in WarmedBookGroupings)
                    Tally(await RunAsync<BrowseController>(principal, "heads:book:" + groupBy,
                        c => c.GetGroupLetters(groupBy, null, null, "book", false, false, ct: ct), ct));

                // The default first band. Its response is not cached (megabytes per variant), but running it keeps
                // the projection's join pages hot so the real request stays at warm speed.
                Tally(await RunAsync<BrowseController>(principal, "groups:default",
                    c => c.GetGroups("collection", null, "series", 20, 0, 48, 0, null, null, null, null, false, false, ct: ct), ct));

                // Explore. Its payload IS cached (one entry per user × ceiling × day seed), and assembling it is
                // the most expensive read in the vertical, so warming it is the difference between "fresh
                // arrivals appear within a poll" and "the first visitor after midnight pays for them". Only the
                // no-seed entry is warmed; an explicit ?seed= re-roll is a one-off and simply expires.
                Tally(await RunAsync<ExploreController>(principal, "explore:comic", c => c.Get(null, null, ct), ct));
                Tally(await RunAsync<ExploreController>(principal, "explore:book", c => c.Get("book", null, ct), ct));

                // The NOVELS landing. Unlike everything above it caches NOTHING — `/novels` is a plain
                // filtered page — so this warms the only thing there is to warm: SQLite's pages for the
                // credit/detail joins the list projects through. Measured on prod 2026-08-31 at 8.9 s cold
                // against 0.30 s once those pages were hot, which is the whole difference. Same reasoning
                // as the movie side's "groups:default" step, whose response is not cached either.
                Tally(await RunAsync<NovelsController>(principal, "novels:landing",
                    c => c.List(null, null, null, null, null, null, 0, 60, null, null, null, false, ct), ct));
            }

            // The kids landing is identical for every account (its ceiling is forced to 0 and nothing per-user is
            // composed), so it is warmed ONCE per pass rather than per identity.
            if (principals.Count > 0)
                if (await RunAsync<ExploreController>(principals[0], "explore:kids", c => c.GetKids(null, ct), ct)) ok++;
                else failed++;

            lastWarmUtc = DateTime.UtcNow;
            // Snapshot AFTER warming, so a write racing the pass re-triggers next poll instead of being absorbed.
            try
            {
                lastDataVersion = await ProbeAsync<long>("PRAGMA data_version", ct);
                lastFingerprint = await ProbeAsync<string>(FingerprintSql, ct);
            }
            catch (Exception ex) { logger.LogWarning(ex, "Books post-warm fingerprint snapshot failed; next poll re-checks."); }

            logger.LogInformation("Books cache warm ({Reason}): {Users} users, {Ok} targets warmed, {Failed} failed, {Elapsed:0.0}s",
                reason.ToString().ToLowerInvariant(), principals.Count, ok, failed, sw.Elapsed.TotalSeconds);
        }

        /// <summary>
        /// Invoke a real controller action under a fabricated request context so it computes and caches exactly what
        /// a live request would. Returns false (and logs) on failure — one bad target must never abort the pass.
        /// </summary>
        private async Task<bool> RunAsync<TController>(
            ClaimsPrincipal principal, string target, Func<TController, Task> action, CancellationToken ct)
            where TController : ControllerBase
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var controller = ActivatorUtilities.CreateInstance<TController>(scope.ServiceProvider);
                controller.ControllerContext = new ControllerContext
                {
                    HttpContext = new DefaultHttpContext
                    {
                        RequestServices = scope.ServiceProvider,
                        User = principal,
                        RequestAborted = ct,
                    },
                };
                await action(controller);
                return true;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Books cache warm target {Target} failed for user {UserId}.",
                    target, BooksIdentity.UserId(principal));
                return false;
            }
        }

        /// <summary>
        /// The dedicated read connection. <c>data_version</c> answers "has anyone ELSE committed since THIS
        /// connection last looked", so it only works from a connection we keep around.
        /// </summary>
        private async Task<T> ProbeAsync<T>(string sql, CancellationToken ct)
        {
            if (probe == null)
            {
                probe = new SqliteConnection(new SqliteConnectionStringBuilder
                {
                    DataSource = options.DbPath,
                    Mode = SqliteOpenMode.ReadOnly,
                }.ToString());
                await probe.OpenAsync(ct);
                await using var tune = probe.CreateCommand();
                tune.CommandText = "PRAGMA busy_timeout=5000;";
                await tune.ExecuteNonQueryAsync(ct);
            }
            await using var cmd = probe.CreateCommand();
            cmd.CommandText = sql;
            var value = await cmd.ExecuteScalarAsync(ct)
                ?? throw new InvalidOperationException("Books warm probe returned null.");
            return (T)Convert.ChangeType(value, typeof(T));
        }

        public override void Dispose()
        {
            probe?.Dispose();
            base.Dispose();
        }
    }
}
