using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CliFx;
using CliFx.Attributes;
using CliFx.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using MovieTheater.Console;
using MovieTheater.Db;
using MovieTheater.Services;
using MovieTheater.Services.Tmdb;

namespace MovieTheater.Tmdb
{
    /// <summary>
    /// Resumable pass that fills each series' TMDB trailer key (and a few cheap enrichment columns) from
    /// TMDB's TV endpoints: resolves the TMDB <em>TV</em> id from the stored IMDB id (<c>/find</c>,
    /// <c>tv_results</c>), fetches <c>/tv/{id}?append_to_response=videos</c>, and writes
    /// TmdbId/Tagline/OriginalLanguage/Backdrop/Popularity/VoteCount/Trailer. The film-only
    /// <see cref="BackfillTmdbCommand"/> never touched Series, so series trailer coverage starts near
    /// zero; this is its counterpart. Resumes on rows where <c>TmdbId IS NULL</c>.
    /// </summary>
    [Command("backfill-tmdb-series", Description = "Backfill Series trailer keys (and TMDB enrichment) from TMDB's TV endpoints.")]
    public class BackfillTmdbSeriesCommand : BasicDICommand, ICommand
    {
        [CommandOption("limit", Description = "Max number of series to process this run.")]
        public int? Limit { get; set; }

        [CommandOption("dry-run", Description = "Fetch and print results without writing to the database.")]
        public bool DryRun { get; set; }

        [CommandOption("imdb-id", Description = "Enrich a single explicit IMDB id and print it (implies dry-run).")]
        public string SingleImdbId { get; set; }

        [CommandOption("rescrape", Description = "Also reprocess rows already enriched (default: only TmdbId IS NULL).")]
        public bool Rescrape { get; set; }

        [CommandOption("delay-ms", Description = "Delay between titles, ms (TMDB is API-rate-limited; be polite).")]
        public int DelayMs { get; set; } = 250;

        // A run of back-to-back failures is the signature of TMDB rate-limiting/blocking us, not a few
        // genuinely-unmatchable rows (those reset the counter). Stop rather than burn the whole list.
        private const int FailureAbortThreshold = 10;

        private readonly IDbContextFactory<MovieDb> dbFactory;
        private readonly TmdbApi tmdb;
        private readonly ILogger<BackfillTmdbSeriesCommand> logger;

        public BackfillTmdbSeriesCommand(MovieTheaterConfiguration config) : base(config)
        {
            dbFactory = GetRequiredService<IDbContextFactory<MovieDb>>();
            tmdb = GetRequiredService<TmdbApi>();
            logger = GetRequiredService<ILogger<BackfillTmdbSeriesCommand>>();
        }

        public async ValueTask ExecuteAsync(IConsole console)
        {
            var cancel = console.RegisterCancellationHandler();

            // Single-id smoke test: fetch and print, never write.
            if (!string.IsNullOrWhiteSpace(SingleImdbId))
            {
                var (detail, reason, _) = await ResolveAsync(SingleImdbId.Trim());
                PrintResult(console, SingleImdbId.Trim(), detail, reason);
                return;
            }

            List<SeriesRow> todo;
            using (var db = await dbFactory.CreateDbContextAsync())
            {
                IQueryable<Db.Series> rows = db.Series.Where(s => s.imdbID != null && s.imdbID != "");
                if (!Rescrape) rows = rows.Where(s => s.TmdbId == null);
                var query = rows
                    .OrderBy(s => s.Id)
                    .Select(s => new SeriesRow { Id = s.Id, Title = s.Title, ImdbId = s.imdbID });
                if (Limit.HasValue) query = query.Take(Limit.Value);
                todo = await query.ToListAsync();
            }

            console.Output.WriteLine($"Backfilling TMDB trailers for {todo.Count} series{(DryRun ? " (dry-run)" : "")}…");
            int done = 0, enriched = 0, withTrailer = 0, unmatched = 0, consecutiveFailures = 0;
            bool aborted = false;

            foreach (var row in todo)
            {
                if (cancel.IsCancellationRequested)
                {
                    console.Output.WriteLine("Cancellation requested — stopping (progress is saved per-series).");
                    break;
                }

                try
                {
                    var (detail, reason, transient) = await ResolveAsync(row.ImdbId);
                    if (DryRun)
                    {
                        PrintResult(console, row.ImdbId, detail, reason);
                    }
                    else if (detail == null)
                    {
                        // No TMDB TV match — leave TmdbId null so a later run retries. A clean miss (TMDB
                        // answered, just no TV record) is NOT a throttle signal, so it resets the breaker;
                        // only transient HTTP failures count toward aborting.
                        console.Error.WriteLine($"  ~ {row.Title} (id {row.Id}, {row.ImdbId}): {reason}");
                        unmatched++;
                        if (transient) consecutiveFailures++; else consecutiveFailures = 0;
                    }
                    else
                    {
                        var gotTrailer = await ApplyAsync(row.Id, detail);
                        enriched++;
                        if (gotTrailer) withTrailer++;
                        consecutiveFailures = 0;
                    }
                    done++;
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Failed enriching series {Title} (id {Id})", row.Title, row.Id);
                    console.Error.WriteLine($"  ! {row.Title} (id {row.Id}): {ex.Message}");
                    consecutiveFailures++;
                }

                if (consecutiveFailures >= FailureAbortThreshold)
                {
                    aborted = true;
                    console.Error.WriteLine(
                        $"Aborting after {consecutiveFailures} consecutive failures — TMDB is likely " +
                        $"rate-limiting us. Progress is saved; wait a while and re-run to resume.");
                    break;
                }

                if (done % 50 == 0)
                    console.Output.WriteLine($"  …{done}/{todo.Count} (matched: {enriched}, with trailer: {withTrailer}, unmatched: {unmatched})");

                await DelayAsync(cancel);
            }

            console.Output.WriteLine(
                $"{(aborted ? "Stopped (rate-limited)" : "Done")}. Processed {done}, matched {enriched}, " +
                $"trailers added {withTrailer}, unmatched {unmatched}.");
        }

        /// <summary>
        /// Resolve IMDB id → TMDB TV id (/find tv_results) → full TV detail. Returns (detail, null, false)
        /// on success. A clean miss (no TV record) is (null, reason, transient:false); an HTTP failure on
        /// the detail fetch is (null, reason, transient:true) so only that arms the breaker.
        /// </summary>
        private async Task<(TmdbTvDetailDto detail, string reason, bool transient)> ResolveAsync(string imdbId)
        {
            var found = await tmdb.TryGetTvId(imdbId);
            if (found == null || found.Id <= 0)
                return (null, "no TMDB TV match (likely a film or no record)", false);

            var detail = await tmdb.GetTvDetail(found.Id);
            if (detail == null)
                return (null, $"TMDB TV detail fetch failed for tv id {found.Id}", true);

            return (detail, null, false);
        }

        // Returns true when a trailer key was written. Only non-empty values are set, mirroring the movie
        // applier, so a sparse TMDB record never blanks a column we already have.
        private async Task<bool> ApplyAsync(int seriesId, TmdbTvDetailDto detail)
        {
            using var db = await dbFactory.CreateDbContextAsync();
            var series = await db.Series.FirstOrDefaultAsync(s => s.Id == seriesId);
            if (series == null) return false;

            series.TmdbId = detail.Id;
            if (!string.IsNullOrWhiteSpace(detail.Tagline)) series.Tagline = detail.Tagline.Trim();
            if (!string.IsNullOrWhiteSpace(detail.OriginalLanguage)) series.OriginalLanguage = detail.OriginalLanguage.Trim();
            if (!string.IsNullOrWhiteSpace(detail.BackdropPath)) series.BackdropPath = detail.BackdropPath.Trim();
            if (detail.Popularity > 0) series.TmdbPopularity = detail.Popularity;
            if (detail.VoteCount > 0) series.TmdbVoteCount = detail.VoteCount;

            var trailer = TmdbEnrichmentApplier.PickYouTubeTrailerKey(detail.Videos);
            if (trailer != null) series.TrailerKey = trailer;

            await db.SaveChangesAsync();
            return trailer != null;
        }

        private async Task DelayAsync(CancellationToken cancel)
        {
            if (DelayMs <= 0) return;
            try { await Task.Delay(DelayMs, cancel); }
            catch (OperationCanceledException) { }
        }

        private static void PrintResult(IConsole console, string imdbId, TmdbTvDetailDto d, string reason)
        {
            var o = console.Output;
            o.WriteLine($"── {imdbId} ──");
            if (d == null) { o.WriteLine($"  NOT MATCHED: {reason}"); return; }
            o.WriteLine($"  TMDB TV id: {d.Id}   name: {d.Name}   lang: {d.OriginalLanguage}   votes: {d.VoteCount}");
            var trailer = TmdbEnrichmentApplier.PickYouTubeTrailerKey(d.Videos);
            o.WriteLine($"  Trailer:    {(trailer != null ? "youtube:" + trailer : "—")}");
        }

        private class SeriesRow
        {
            public int Id { get; set; }
            public string Title { get; set; }
            public string ImdbId { get; set; }
        }
    }
}
