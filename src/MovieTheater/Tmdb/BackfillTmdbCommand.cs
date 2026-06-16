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
    /// Resumable pass that tops up each movie's Phase-A enrichment columns from TMDB: resolves the
    /// TMDB id from the stored IMDB id (<c>/find</c>), fetches the full movie detail, and writes
    /// TmdbId/Tagline/Budget/Revenue/OriginalLanguage/Backdrop/Popularity/VoteCount/Country/Trailer
    /// via <see cref="TmdbEnrichmentApplier"/>. Resumes on rows where <c>TmdbId IS NULL</c>. Legacy
    /// and OMDB-fallback columns are otherwise left as-is. See docs/metadata-enrichment-plan.md §6.A.
    /// </summary>
    [Command("backfill-tmdb", Description = "Backfill Movie enrichment columns (TmdbId, tagline, budget, trailer, …) from TMDB.")]
    public class BackfillTmdbCommand : BasicDICommand, ICommand
    {
        [CommandOption("limit", Description = "Max number of movies to process this run.")]
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
        private readonly ILogger<BackfillTmdbCommand> logger;

        public BackfillTmdbCommand(MovieTheaterConfiguration config) : base(config)
        {
            dbFactory = GetRequiredService<IDbContextFactory<MovieDb>>();
            tmdb = GetRequiredService<TmdbApi>();
            logger = GetRequiredService<ILogger<BackfillTmdbCommand>>();
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

            List<MovieRow> todo;
            using (var db = await dbFactory.CreateDbContextAsync())
            {
                IQueryable<Db.Movie> rows = db.Movies.Where(m => m.imdbID != null && m.imdbID != "");
                if (!Rescrape) rows = rows.Where(m => m.TmdbId == null);
                var query = rows
                    .OrderBy(m => m.id)
                    .Select(m => new MovieRow { Id = m.id, Title = m.Title, ImdbId = m.imdbID });
                if (Limit.HasValue) query = query.Take(Limit.Value);
                todo = await query.ToListAsync();
            }

            console.Output.WriteLine($"Backfilling TMDB enrichment for {todo.Count} movie(s){(DryRun ? " (dry-run)" : "")}…");
            int done = 0, enriched = 0, unmatched = 0, consecutiveFailures = 0;
            bool aborted = false;

            foreach (var row in todo)
            {
                if (cancel.IsCancellationRequested)
                {
                    console.Output.WriteLine("Cancellation requested — stopping (progress is saved per-movie).");
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
                        // No TMDB match — leave TmdbId null so a later run retries. A *clean* miss (TMDB
                        // answered, just no movie record — common for TV titles) is NOT a throttle signal,
                        // so it resets the breaker; only transient HTTP failures count toward aborting.
                        console.Error.WriteLine($"  ~ {row.Title} (id {row.Id}, {row.ImdbId}): {reason}");
                        unmatched++;
                        if (transient) consecutiveFailures++; else consecutiveFailures = 0;
                    }
                    else
                    {
                        await ApplyAsync(row.Id, detail);
                        enriched++;
                        consecutiveFailures = 0;
                    }
                    done++;
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Failed enriching {Title} (id {Id})", row.Title, row.Id);
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
                    console.Output.WriteLine($"  …{done}/{todo.Count} (enriched: {enriched}, unmatched: {unmatched})");

                await DelayAsync(cancel);
            }

            console.Output.WriteLine(
                $"{(aborted ? "Stopped (rate-limited)" : "Done")}. Processed {done}, enriched {enriched}, unmatched {unmatched}.");
        }

        /// <summary>
        /// Resolve IMDB id → TMDB id (/find) → full detail. Returns (detail, null, false) on success.
        /// A clean miss (no movie record — often a TV title) is (null, reason, transient:false); an HTTP
        /// failure on the detail fetch is (null, reason, transient:true) so only that arms the breaker.
        /// </summary>
        private async Task<(TmdbMovieDetailDto detail, string reason, bool transient)> ResolveAsync(string imdbId)
        {
            var found = await tmdb.TryGetMovie(imdbId);
            if (found == null || found.Id <= 0)
                return (null, "no TMDB movie match (likely a TV title)", false);

            var detail = await tmdb.GetMovieDetail(found.Id);
            if (detail == null)
                return (null, $"TMDB detail fetch failed for tmdb id {found.Id}", true);

            return (detail, null, false);
        }

        private async Task ApplyAsync(int movieId, TmdbMovieDetailDto detail)
        {
            using var db = await dbFactory.CreateDbContextAsync();
            var movie = await db.Movies.FirstOrDefaultAsync(m => m.id == movieId);
            if (movie == null) return;

            TmdbEnrichmentApplier.Apply(movie, detail);
            await db.SaveChangesAsync();
        }

        private async Task DelayAsync(CancellationToken cancel)
        {
            if (DelayMs <= 0) return;
            try { await Task.Delay(DelayMs, cancel); }
            catch (OperationCanceledException) { }
        }

        private static void PrintResult(IConsole console, string imdbId, TmdbMovieDetailDto d, string reason)
        {
            var o = console.Output;
            o.WriteLine($"── {imdbId} ──");
            if (d == null) { o.WriteLine($"  NOT MATCHED: {reason}"); return; }
            o.WriteLine($"  TMDB id:  {d.Id}   lang: {d.OriginalLanguage}   votes: {d.VoteCount}   popularity: {d.Popularity}");
            o.WriteLine($"  Tagline:  {(string.IsNullOrWhiteSpace(d.Tagline) ? "—" : d.Tagline)}");
            o.WriteLine($"  Budget:   {(d.Budget > 0 ? d.Budget.ToString("N0") : "—")}   Revenue: {(d.Revenue > 0 ? d.Revenue.ToString("N0") : "—")}");
            o.WriteLine($"  Backdrop: {(string.IsNullOrWhiteSpace(d.BackdropPath) ? "—" : d.BackdropPath)}");
            var trailer = d.Videos?.Results?.FirstOrDefault(v =>
                string.Equals(v.Site, "YouTube", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(v.Type, "Trailer", StringComparison.OrdinalIgnoreCase));
            o.WriteLine($"  Trailer:  {(trailer != null ? "youtube:" + trailer.Key : "—")}");
        }

        private class MovieRow
        {
            public int Id { get; set; }
            public string Title { get; set; }
            public string ImdbId { get; set; }
        }
    }
}
