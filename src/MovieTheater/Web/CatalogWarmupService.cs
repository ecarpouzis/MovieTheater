using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MovieTheater.Arcade;
using MovieTheater.Db;

namespace MovieTheater.Web
{
    /// <summary>
    /// The change-driven catalog warmer (R9 S7) — the Long Box's `views-perf` law the pods were
    /// missing. Every few minutes it reads a cheap <see cref="CatalogFingerprint"/>; when the catalog
    /// has actually CHANGED (or the backstop TTL has elapsed) it rebuilds the movie browse's light
    /// indexes and its facet counts — and the ARCADE lobby's group index — into the same
    /// <see cref="IMemoryCache"/> a request would fill, so the first reader after an ingest does not pay
    /// for the pass. (The fingerprint is the MOVIE catalog's; the arcade rides the same cadence rather
    /// than growing a second loop, and the 4 h backstop is what keeps its 6 h entries alive.)
    ///
    /// The house rules for a long job, all of them:
    ///  - <b>bounded per step</b>: one target per iteration, with a pause between, never "warm
    ///    everything in one go";
    ///  - <b>observable</b>: every pass logs its reason, and every target logs what it built and how
    ///    long it took;
    ///  - <b>resumable + idempotent</b>: state is the cache itself; a pass killed halfway leaves the
    ///    targets it did finish warm and the next pass redoes the rest;
    ///  - <b>never blocking a request</b>: it runs on its own scope and its own DbContext, and a
    ///    failure is logged and dropped — a cold cache is slow, not broken.
    ///
    /// It is READ-ONLY: counts, maxes and the same gated SELECTs the browse runs. Nothing here writes
    /// to the database, which is the live shared one.
    /// </summary>
    public sealed class CatalogWarmupService : BackgroundService
    {
        private readonly IServiceScopeFactory scopes;
        private readonly IMemoryCache cache;
        private readonly ILogger<CatalogWarmupService> log;
        private readonly CatalogWarmupOptions options;

        /// <summary>The age the warm builds for: 100 = no restriction, which is what a signed-out
        /// viewer and every account without an AgeRestriction setting resolve to.</summary>
        private const int UnrestrictedAge = 100;

        public CatalogWarmupService(IServiceScopeFactory scopes, IMemoryCache cache,
            ILogger<CatalogWarmupService> log, CatalogWarmupOptions? options = null)
        {
            this.scopes = scopes;
            this.cache = cache;
            this.log = log;
            this.options = options ?? new CatalogWarmupOptions();
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            if (!options.Enabled) { log.LogInformation("catalog-warmup: disabled"); return; }
            CatalogFingerprint? previous = null;
            DateTime? lastWarmUtc = null;
            // A boot pass would compete with the app's own cold start; give it a moment first.
            try { await Task.Delay(TimeSpan.FromSeconds(20), stoppingToken); } catch (OperationCanceledException) { return; }

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    var current = await ReadFingerprintAsync(stoppingToken);
                    var decision = CatalogWarmupPlan.Decide(previous, current, lastWarmUtc, DateTime.UtcNow, options);
                    previous = current;
                    if (decision.Warm)
                    {
                        log.LogInformation("catalog-warmup: warming — {Reason}", decision.Reason);
                        await WarmAsync(stoppingToken);
                        lastWarmUtc = DateTime.UtcNow;
                    }
                    else
                    {
                        log.LogDebug("catalog-warmup: skipped — {Reason} ({Fingerprint})", decision.Reason, current);
                    }
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
                catch (Exception ex)
                {
                    // A cold cache is slow, not broken — never let a warm failure take the pod down.
                    log.LogWarning(ex, "catalog-warmup: pass failed; will retry at the next check");
                }
                try { await Task.Delay(options.CheckInterval, stoppingToken); } catch (OperationCanceledException) { return; }
            }
        }

        private async Task<CatalogFingerprint> ReadFingerprintAsync(CancellationToken ct)
        {
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<MovieDb>();
            return await CatalogFingerprint.ReadAsync(db, ct);
        }

        /// <summary>One bounded step per target, each with its own scope and its own log line.</summary>
        private async Task WarmAsync(CancellationToken ct)
        {
            var targets = CatalogWarmupTargets.Default();
            var done = 0;
            foreach (var target in targets)
            {
                ct.ThrowIfCancellationRequested();
                var sw = Stopwatch.StartNew();
                try
                {
                    var built = await WarmOneAsync(target, ct);
                    done += 1;
                    log.LogInformation("catalog-warmup: {Target} → {Built} in {Ms} ms ({Done}/{Total})",
                        target.Name, built, sw.ElapsedMilliseconds, done, targets.Count);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    log.LogWarning(ex, "catalog-warmup: {Target} failed after {Ms} ms — skipping", target.Name, sw.ElapsedMilliseconds);
                }
                try { await Task.Delay(options.StepPause, ct); } catch (OperationCanceledException) { throw; }
            }

            await WarmArcadeAsync(ct);
        }

        /// <summary>
        /// The ARCADE group index, for the axes the lobby's pill opens on — the same bounded-step shape as
        /// the movie targets above, one axis per iteration with a pause between.
        ///
        /// <para>It is warmable AT ALL only since the index stopped being keyed per user (2026-08-31, see
        /// <see cref="ArcadeGameGroups.CacheKey"/>): while every account needed its own ~2 MB copy there was
        /// nothing a background pass could usefully build. Measured cold on prod at <b>25.0 s</b> for
        /// `system` and <b>22.3 s</b> for a wide axis, against 0.2–1.4 s warm.</para>
        /// </summary>
        private async Task WarmArcadeAsync(CancellationToken ct)
        {
            foreach (var by in ArcadeGameGroups.WarmedAxes)
            {
                ct.ThrowIfCancellationRequested();
                var key = ArcadeGameGroups.CacheKey(ArcadeGameGroups.UnfilteredSig, by);
                if (cache.TryGetValue(key, out _))
                {
                    log.LogDebug("catalog-warmup: arcade:{By} already warm", by);
                }
                else
                {
                    var sw = Stopwatch.StartNew();
                    try
                    {
                        using var scope = scopes.CreateScope();
                        var db = scope.ServiceProvider.GetRequiredService<MovieDb>();
                        // The lobby's own visible set (ArcadeController.VisibleGamesAsync) with no filters:
                        // at variant "all" ApplyCardFilters adds no predicate, so this IS the landing query.
                        var index = await ArcadeGameGroups.LoadIndexAsync(db.ArcadeGames.Where(g => g.IsEnabled), by, ct);
                        cache.Set(key, index, new MemoryCacheEntryOptions
                        {
                            AbsoluteExpirationRelativeToNow = TimeSpan.FromHours(6),
                            Size = index.ApproxBytes,
                        });
                        log.LogInformation("catalog-warmup: arcade:{By} → {Groups} groups in {Ms} ms",
                            by, index.Heads.Count, sw.ElapsedMilliseconds);
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        log.LogWarning(ex, "catalog-warmup: arcade:{By} failed after {Ms} ms — skipping", by, sw.ElapsedMilliseconds);
                    }
                }
                try { await Task.Delay(options.StepPause, ct); } catch (OperationCanceledException) { throw; }
            }
        }

        /// <summary>Builds one target INTO the cache under the same key a request computes. Idempotent:
        /// an entry that is already warm is left exactly as it is.</summary>
        private async Task<string> WarmOneAsync(WarmTarget target, CancellationToken ct)
        {
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<MovieDb>();
            var mq = CatalogQueries.BaseMovies(db, UnrestrictedAge);
            var sq = CatalogQueries.BaseSeries(db, UnrestrictedAge);
            var scoped = ApplyTypeScope(target.TypeScope, mq, sq);

            if (target.IsFacets)
            {
                var key = BrowseCacheKeys.Facets(UnrestrictedAge, target.TypeScope, null);
                if (cache.TryGetValue(key, out _)) return "already warm";
                var counts = await BrowseFilter.CountAsync(db, scoped.Movies, scoped.Series, 0, ct);
                cache.Set(key, counts, new MemoryCacheEntryOptions
                {
                    AbsoluteExpirationRelativeToNow = TimeSpan.FromHours(6),
                    Size = counts.ApproxBytes,
                });
                return $"{counts.Total} titles";
            }

            var groupKey = BrowseCacheKeys.Groups(null, UnrestrictedAge, target.TypeScope, null, null,
                BrowseFilter.Empty.Sig, userDependent: false, groupBy: target.GroupBy!);
            if (cache.TryGetValue(groupKey, out _)) return "already warm";
            // No user id: the warmer only ever builds the SHARED axes (`BrowseGroups.IsUserDependent`
            // is false for every one of them), so no viewer's own lists can leak into a shared entry.
            var index = await BrowseGroups.BuildIndexAsync(db, scoped.Movies, scoped.Series,
                Array.Empty<BrowseGroups.MiscLight>(), target.GroupBy!, userId: null, ct);
            cache.Set(groupKey, index, new MemoryCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = TimeSpan.FromHours(6),
                Size = index.ApproxBytes,
            });
            return $"{index.Heads.Count} groups";
        }

        /// <summary>
        /// The Type scope, without the controller: Misc never reaches a warm target.
        ///
        /// <para>An EMPTY scope is "every type", not "nothing" — the controller's own
        /// <c>ApplyTypeScope</c> returns the queries untouched for it, and that is the scope a reader
        /// reaches by CLEARING the Type chip (`moviesFacetSpec`: the landing seeds `f=type:Movies` once
        /// per tab, so clearing it later means all types). Without this branch a warm of that scope would
        /// cache an EMPTY count under the key the controller reads for "all", and the rail would show
        /// zeroes — a warm that is wrong is worse than a warm that is missing.</para>
        /// </summary>
        private static (IQueryable<Movie> Movies, IQueryable<Series> Series) ApplyTypeScope(
            System.Collections.Generic.IReadOnlyList<NormalizedTitleType> scope, IQueryable<Movie> mq, IQueryable<Series> sq)
        {
            if (scope.Count == 0) return (mq, sq);
            var movieBuckets = scope.Where(t => t is NormalizedTitleType.Movies or NormalizedTitleType.Short).ToList();
            // Same equality-not-Contains shape as the controller's ApplyTypeScope, and for the same reason
            // (EF's OPENJSON translation of a parameterized Contains) — a warm must run the query the
            // request runs, or it warms the wrong plan.
            var only = movieBuckets.Count == 1 ? movieBuckets[0] : default;
            var movies = movieBuckets.Count switch
            {
                0 => mq.Where(m => false),
                1 => mq.Where(m => m.NormalizedTitleType == only),
                _ => mq.Where(m => movieBuckets.Contains(m.NormalizedTitleType)),
            };
            var series = scope.Contains(NormalizedTitleType.Series) ? sq : sq.Where(s => false);
            return (movies, series);
        }
    }
}
