using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using CliFx;
using CliFx.Attributes;
using CliFx.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Playwright;
using MovieTheater.Console;
using MovieTheater.Db;
using MovieTheater.Services;

namespace MovieTheater.Imdb
{
    /// <summary>
    /// One-time gentle, resumable pass that re-scrapes each movie's IMDB page to verify
    /// its id and refill normalized data (runtime, full plot, MPAA rating, release date,
    /// genres, cast/crew) into the new columns and FK tables. Legacy columns and posters
    /// are never touched. Resumes on rows where ImdbVerifiedDate IS NULL.
    /// </summary>
    [Command("scrape-imdb", Description = "Re-scrape IMDB to verify ids and fill normalized movie data.")]
    public class ScrapeImdbCommand : BasicDICommand, ICommand
    {
        [CommandOption("limit", Description = "Max number of movies to process this run.")]
        public int? Limit { get; set; }

        [CommandOption("dry-run", Description = "Scrape and print results without writing to the database.")]
        public bool DryRun { get; set; }

        [CommandOption("imdb-id", Description = "Scrape a single explicit IMDB id and print it (implies dry-run).")]
        public string SingleImdbId { get; set; }

        [CommandOption("rescrape", Description = "Also reprocess rows already verified (default: only unverified).")]
        public bool Rescrape { get; set; }

        [CommandOption("missing-cache", Description = "Select titles (movies AND series) whose IMDB title page is NOT in the local cache, regardless of ImdbVerifiedDate — fills coverage gaps left by the OMDB enrich path. Implies --include-series.")]
        public bool MissingCache { get; set; }

        [CommandOption("include-series", Description = "Also scrape Series rows (not just Movies) using the same resume/rescrape rules.")]
        public bool IncludeSeries { get; set; }

        [CommandOption("retype", Description = "Classify+cache rows not yet typed (TitleType=Unknown), ignoring ImdbVerifiedDate. Resumable across the run.")]
        public bool Retype { get; set; }

        [CommandOption("cast-limit", Description = "Max billed actors to capture per movie.")]
        public int CastLimit { get; set; } = 15;

        [CommandOption("skip-plot-summaries", Description = "Skip the extra /plotsummary page load (synopsis + summaries).")]
        public bool SkipPlotSummaries { get; set; }

        [CommandOption("delay-min", Description = "Minimum delay between titles, ms.")]
        public int DelayMinMs { get; set; } = 2000;

        [CommandOption("delay-max", Description = "Maximum delay between titles, ms.")]
        public int DelayMaxMs { get; set; } = 5000;

        [CommandOption("headful", Description = "Run the browser with a visible window (debugging).")]
        public bool Headful { get; set; }

        [CommandOption("no-cache", Description = "Don't write scraped pages to the local IMDB page cache.")]
        public bool NoCache { get; set; }

        [CommandOption("cache-dir", Description = "Root dir for the local IMDB page cache (default: data/imdb-cache).")]
        public string CacheDir { get; set; }

        private const string UserAgent =
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36";

        private readonly IDbContextFactory<MovieDb> dbFactory;
        private readonly ILogger<ScrapeImdbCommand> logger;
        private readonly ImdbTitleScraper scraper = new ImdbTitleScraper();
        private readonly Random rng = new Random();
        private ImdbPageCache cache;

        public ScrapeImdbCommand(MovieTheaterConfiguration config) : base(config)
        {
            dbFactory = GetRequiredService<IDbContextFactory<MovieDb>>();
            logger = GetRequiredService<ILogger<ScrapeImdbCommand>>();
        }

        public async ValueTask ExecuteAsync(IConsole console)
        {
            var cancel = console.RegisterCancellationHandler();

            cache = NoCache ? null : new ImdbPageCache(CacheDir);
            if (cache != null)
                console.Output.WriteLine($"Caching scraped pages under {cache.Root}");

            using var playwright = await Playwright.CreateAsync();
            await using var browser = await playwright.Chromium.LaunchAsync(
                new BrowserTypeLaunchOptions { Headless = !Headful });
            var context = await browser.NewContextAsync(
                new BrowserNewContextOptions { UserAgent = UserAgent, Locale = "en-US" });
            var page = await context.NewPageAsync();

            await WarmUpAsync(page);

            // Single-id smoke test: scrape and print, never write.
            if (!string.IsNullOrWhiteSpace(SingleImdbId))
            {
                var single = await ScrapeWithRetryAsync(page, SingleImdbId.Trim(), cancel);
                PrintResult(console, single);
                return;
            }

            // --missing-cache covers both tables and ignores ImdbVerifiedDate: it targets titles
            // whose IMDB page never made it into the local cache (e.g. rows the OMDB enrich path
            // stamped as verified but never actually IMDB-scraped). It implies series coverage.
            bool wantSeries = IncludeSeries || MissingCache;

            List<TitleRow> todo;
            using (var db = await dbFactory.CreateDbContextAsync())
            {
                IQueryable<Db.Movie> mrows = db.Movies.Where(m => m.imdbID != null && m.imdbID != "");
                if (!MissingCache)
                    // --retype drives off TitleType (the classification pass): every row is Unknown until
                    // typed, so this covers the already-verified library and resumes on what's left.
                    mrows = Retype
                        ? mrows.Where(m => m.TitleType == TitleType.Unknown)
                        : mrows.Where(m => Rescrape || m.ImdbVerifiedDate == null);
                var movieRows = await mrows.OrderBy(m => m.id)
                    .Select(m => new TitleRow { Id = m.id, ImdbId = m.imdbID, Title = m.Title, IsSeries = false })
                    .ToListAsync();

                var seriesRows = new List<TitleRow>();
                if (wantSeries)
                {
                    IQueryable<Db.Series> srows = db.Series.Where(s => s.imdbID != null && s.imdbID != "");
                    if (!MissingCache)
                        srows = srows.Where(s => Rescrape || s.ImdbVerifiedDate == null);
                    seriesRows = await srows.OrderBy(s => s.Id)
                        .Select(s => new TitleRow { Id = s.Id, ImdbId = s.imdbID, Title = s.Title, IsSeries = true })
                        .ToListAsync();
                }

                todo = movieRows.Concat(seriesRows).ToList();

                if (MissingCache)
                {
                    var probe = cache ?? new ImdbPageCache(CacheDir);
                    todo = todo.Where(t => !probe.Has(t.ImdbId, "title")).ToList();
                }

                todo = todo.OrderBy(t => t.IsSeries).ThenBy(t => t.Id).ToList();
                if (Limit.HasValue) todo = todo.Take(Limit.Value).ToList();
            }

            console.Output.WriteLine($"Scraping {todo.Count} title(s) " +
                $"({todo.Count(t => !t.IsSeries)} movie, {todo.Count(t => t.IsSeries)} series){(DryRun ? " (dry-run)" : "")}…");
            int done = 0, flagged = 0, failed = 0;

            foreach (var row in todo)
            {
                if (cancel.IsCancellationRequested)
                {
                    console.Output.WriteLine("Cancellation requested — stopping (progress is saved per-movie).");
                    break;
                }

                try
                {
                    var result = await ScrapeWithRetryAsync(page, row.ImdbId, cancel);
                    if (DryRun)
                    {
                        PrintResult(console, result);
                    }
                    else
                    {
                        var status = await ApplyAsync(row, result);
                        if (status == ImdbApplyStatus.Flagged) flagged++;
                        if (status == ImdbApplyStatus.NotFound) failed++;
                    }
                    done++;
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Failed scraping {ImdbId} ({Title})", row.ImdbId, row.Title);
                    console.Error.WriteLine($"  ! {row.ImdbId} ({row.Title}): {ex.Message}");
                }

                if (done % 25 == 0)
                    console.Output.WriteLine($"  …{done}/{todo.Count} (flagged for review: {flagged}, not found: {failed})");

                await DelayAsync(cancel);
            }

            console.Output.WriteLine($"Done. Processed {done}, flagged {flagged}, not found {failed}.");
        }

        private async Task WarmUpAsync(IPage page)
        {
            // Visiting the homepage first clears IMDB's bot challenge so subsequent
            // title pages return the full (HTTP 200) document instead of the 202 lite page.
            try
            {
                await page.GotoAsync("https://www.imdb.com/",
                    new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 45000 });
                await page.WaitForTimeoutAsync(2500);
            }
            catch (PlaywrightException ex)
            {
                logger.LogWarning(ex, "IMDB homepage warm-up failed; continuing anyway.");
            }
        }

        private async Task<ImdbScrapeResult> ScrapeWithRetryAsync(IPage page, string imdbId, CancellationToken cancel)
        {
            const int maxAttempts = 4;
            for (int attempt = 1; ; attempt++)
            {
                try
                {
                    return await scraper.ScrapeAsync(page, imdbId, CastLimit, !SkipPlotSummaries, cache);
                }
                catch (ImdbChallengeException) when (attempt < maxAttempts)
                {
                    // Re-warm and back off exponentially before retrying.
                    int backoff = 3000 * attempt;
                    logger.LogWarning("Challenged on {ImdbId} (attempt {Attempt}); re-warming in {Backoff}ms.", imdbId, attempt, backoff);
                    await Task.Delay(backoff, cancel);
                    await WarmUpAsync(page);
                }
                catch (ImdbChallengeException ex)
                {
                    return new ImdbScrapeResult { ImdbId = imdbId, Found = false, FailureReason = ex.Message };
                }
                catch (TimeoutException ex) when (attempt < maxAttempts)
                {
                    logger.LogWarning("Timeout on {ImdbId} (attempt {Attempt}); retrying.", imdbId, attempt);
                    await Task.Delay(2000 * attempt, cancel);
                }
            }
        }

        private async Task<ImdbApplyStatus> ApplyAsync(TitleRow row, ImdbScrapeResult result)
        {
            using var db = await dbFactory.CreateDbContextAsync();
            if (row.IsSeries)
            {
                var series = await db.Series.FirstOrDefaultAsync(s => s.Id == row.Id);
                if (series == null) return ImdbApplyStatus.NotFound;
                return await ImdbDataApplier.ApplyAsync(db, series, result);
            }
            var movie = await db.Movies.FirstOrDefaultAsync(m => m.id == row.Id);
            if (movie == null) return ImdbApplyStatus.NotFound;
            return await ImdbDataApplier.ApplyAsync(db, movie, result);
        }

        private async Task DelayAsync(CancellationToken cancel)
        {
            int lo = Math.Max(0, DelayMinMs);
            int hi = Math.Max(lo + 1, DelayMaxMs);
            try { await Task.Delay(rng.Next(lo, hi), cancel); }
            catch (OperationCanceledException) { }
        }

        private static void PrintResult(IConsole console, ImdbScrapeResult r)
        {
            var o = console.Output;
            o.WriteLine($"── {r.ImdbId} ──");
            if (!r.Found) { o.WriteLine($"  NOT FOUND: {r.FailureReason}"); return; }
            o.WriteLine($"  Title:    {r.Title} ({r.Year})");
            o.WriteLine($"  Released: {r.ReleaseDate:yyyy-MM-dd}   Runtime: {r.RuntimeMinutes} min   MPAA: {r.MpaaRating}   IMDb: {r.ImdbRating}");
            o.WriteLine($"  Genres:   {string.Join(", ", r.Genres)}");
            o.WriteLine($"  Director: {string.Join(", ", r.Directors.Select(p => p.DisplayName))}");
            o.WriteLine($"  Writers:  {string.Join(", ", r.Writers.Select(p => p.DisplayName))}");
            o.WriteLine($"  Cast:     {string.Join(", ", r.Actors.Select(p => $"{p.DisplayName} ({p.Character})"))}");
            o.WriteLine($"  Plot:     {r.Plot}");
            o.WriteLine($"  Summaries:{r.Summaries.Count} (synopsis: {(string.IsNullOrEmpty(r.Synopsis) ? "none" : r.Synopsis.Length + " chars")})");
            foreach (var s in r.Summaries)
                o.WriteLine($"    - [{s.Author ?? "—"}] {(s.Text.Length > 100 ? s.Text.Substring(0, 100) + "…" : s.Text)}");
        }

        private class TitleRow
        {
            public int Id { get; set; }
            public string ImdbId { get; set; }
            public string Title { get; set; }
            public bool IsSeries { get; set; }
        }
    }
}
