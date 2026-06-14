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
using Microsoft.Playwright;
using MovieTheater.Console;
using MovieTheater.Db;
using MovieTheater.Services;

namespace MovieTheater.RottenTomatoes
{
    /// <summary>
    /// Gentle, resumable pass that scrapes each movie's current Rotten Tomatoes Tomatometer
    /// (critics) and Popcornmeter (audience) scores into the Rt* columns. Resolves each movie
    /// via RT's own search (no Google). Resumes on rows where RtScoresUpdatedDate IS NULL.
    /// Modeled on <see cref="Imdb.ScrapeImdbCommand"/>.
    /// </summary>
    [Command("scrape-rotten-tomatoes", Description = "Scrape Rotten Tomatoes Tomatometer + Popcornmeter scores into the Rt* columns.")]
    public class ScrapeRottenTomatoesCommand : BasicDICommand, ICommand
    {
        [CommandOption("limit", Description = "Max number of movies to process this run.")]
        public int? Limit { get; set; }

        [CommandOption("dry-run", Description = "Scrape and print results without writing to the database.")]
        public bool DryRun { get; set; }

        [CommandOption("title", Description = "Scrape a single explicit title and print it (implies dry-run).")]
        public string SingleTitle { get; set; }

        [CommandOption("year", Description = "Release year to disambiguate the --title smoke test.")]
        public int? SingleYear { get; set; }

        [CommandOption("rescrape", Description = "Also reprocess rows already scraped (default: only RtScoresUpdatedDate IS NULL).")]
        public bool Rescrape { get; set; }

        [CommandOption("delay-min", Description = "Minimum delay between titles, ms.")]
        public int DelayMinMs { get; set; } = 2000;

        [CommandOption("delay-max", Description = "Maximum delay between titles, ms.")]
        public int DelayMaxMs { get; set; } = 5000;

        [CommandOption("headful", Description = "Run the browser with a visible window (debugging).")]
        public bool Headful { get; set; }

        private const string UserAgent =
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36";

        // Stop the whole run after this many back-to-back failures — the signature of RT
        // throttling/blocking us (vs. a few genuinely-missing titles, which reset the counter).
        private const int FailureAbortThreshold = 8;

        private readonly IDbContextFactory<MovieDb> dbFactory;
        private readonly ILogger<ScrapeRottenTomatoesCommand> logger;
        private readonly RtScraper scraper = new RtScraper();
        private readonly Random rng = new Random();

        public ScrapeRottenTomatoesCommand(MovieTheaterConfiguration config) : base(config)
        {
            dbFactory = GetRequiredService<IDbContextFactory<MovieDb>>();
            logger = GetRequiredService<ILogger<ScrapeRottenTomatoesCommand>>();
        }

        public async ValueTask ExecuteAsync(IConsole console)
        {
            var cancel = console.RegisterCancellationHandler();

            using var playwright = await Playwright.CreateAsync();
            await using var browser = await playwright.Chromium.LaunchAsync(
                new BrowserTypeLaunchOptions { Headless = !Headful });
            var context = await browser.NewContextAsync(
                new BrowserNewContextOptions { UserAgent = UserAgent, Locale = "en-US" });
            var page = await context.NewPageAsync();

            await WarmUpAsync(page);

            // Single-title smoke test: scrape and print, never write.
            if (!string.IsNullOrWhiteSpace(SingleTitle))
            {
                var single = await ScrapeWithRetryAsync(page, SingleTitle.Trim(), SingleYear, cancel);
                PrintResult(console, single);
                return;
            }

            List<MovieRow> todo;
            using (var db = await dbFactory.CreateDbContextAsync())
            {
                var query = db.Movies
                    .Where(m => Rescrape || m.RtScoresUpdatedDate == null)
                    .OrderBy(m => m.id)
                    .Select(m => new MovieRow
                    {
                        Id = m.id,
                        Title = m.Title,
                        SimpleTitle = m.SimpleTitle,
                        Year = m.ReleaseDate != null ? (int?)m.ReleaseDate.Value.Year : null
                    });
                if (Limit.HasValue) query = query.Take(Limit.Value);
                todo = await query.ToListAsync();
            }

            console.Output.WriteLine($"Scraping RT scores for {todo.Count} movie(s){(DryRun ? " (dry-run)" : "")}…");
            int done = 0, scored = 0, flagged = 0, skipped = 0, consecutiveFailures = 0;
            bool throttled = false;

            foreach (var row in todo)
            {
                if (cancel.IsCancellationRequested)
                {
                    console.Output.WriteLine("Cancellation requested — stopping (progress is saved per-movie).");
                    break;
                }

                var searchTitle = !string.IsNullOrWhiteSpace(row.SimpleTitle) ? row.SimpleTitle : row.Title;
                try
                {
                    var result = await ScrapeWithRetryAsync(page, searchTitle, row.Year, cancel);
                    if (DryRun)
                    {
                        PrintResult(console, result);
                    }
                    else if (result.Transient)
                    {
                        // Likely throttling/network — leave the row unscored (null) so a later
                        // resume retries it, and count toward the circuit-breaker.
                        console.Error.WriteLine($"  ~ {searchTitle} (id {row.Id}): transient failure — left for retry ({result.FailureReason}).");
                        skipped++;
                        consecutiveFailures++;
                    }
                    else
                    {
                        var applied = await ApplyAsync(row.Id, result);
                        if (applied) scored++; else flagged++;
                        consecutiveFailures = 0; // a real DB write means RT is still answering us
                    }
                    done++;
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Failed scraping RT for {Title} (id {Id})", searchTitle, row.Id);
                    console.Error.WriteLine($"  ! {searchTitle} (id {row.Id}): {ex.Message}");
                    consecutiveFailures++;
                }

                // Circuit-breaker: a run of back-to-back failures means RT is challenging/blocking
                // us, not that these specific titles are missing. Stop rather than burn the list.
                if (consecutiveFailures >= FailureAbortThreshold)
                {
                    throttled = true;
                    console.Error.WriteLine(
                        $"Aborting after {consecutiveFailures} consecutive failures — Rotten Tomatoes is " +
                        $"likely throttling or blocking us. Progress is saved; wait a while and re-run to resume.");
                    break;
                }

                if (done % 25 == 0)
                    console.Output.WriteLine($"  …{done}/{todo.Count} (scored: {scored}, needs review: {flagged}, retry-later: {skipped})");

                await DelayAsync(cancel);
            }

            console.Output.WriteLine(
                $"{(throttled ? "Stopped (throttled)" : "Done")}. Processed {done}, scored {scored}, " +
                $"needs review {flagged}, left for retry {skipped}.");
        }

        private async Task WarmUpAsync(IPage page)
        {
            // A homepage visit first lets RT set its cookies/anti-bot state so the search
            // page returns real results instead of a challenge.
            try
            {
                await page.GotoAsync("https://www.rottentomatoes.com/",
                    new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 45000 });
                await page.WaitForTimeoutAsync(2000);
            }
            catch (PlaywrightException ex)
            {
                logger.LogWarning(ex, "RT homepage warm-up failed; continuing anyway.");
            }
        }

        private async Task<RtScoreResult> ScrapeWithRetryAsync(IPage page, string title, int? year, CancellationToken cancel)
        {
            const int maxAttempts = 3;
            for (int attempt = 1; ; attempt++)
            {
                try
                {
                    return await scraper.ScrapeAsync(page, title, year);
                }
                catch (RtChallengeException) when (attempt < maxAttempts)
                {
                    int backoff = 3000 * attempt;
                    logger.LogWarning("Challenged on '{Title}' (attempt {Attempt}); re-warming in {Backoff}ms.", title, attempt, backoff);
                    await Task.Delay(backoff, cancel);
                    await WarmUpAsync(page);
                }
                catch (RtChallengeException ex)
                {
                    // Exhausted re-warm retries: treat as transient (likely throttling), so the
                    // row is left unscored for a later resume rather than stamped needs-review.
                    return new RtScoreResult { SearchTitle = title, Found = false, Transient = true, FailureReason = ex.Message };
                }
            }
        }

        private async Task<bool> ApplyAsync(int movieId, RtScoreResult result)
        {
            using var db = await dbFactory.CreateDbContextAsync();
            var movie = await db.Movies.FirstOrDefaultAsync(m => m.id == movieId);
            if (movie == null) return false;

            movie.RtScoresUpdatedDate = DateTime.Now;

            if (!result.Found)
            {
                movie.RtNeedsReview = true;
                movie.RtReviewReason = result.FailureReason ?? "Could not resolve on Rotten Tomatoes.";
                await db.SaveChangesAsync();
                return false;
            }

            movie.RtNeedsReview = false;
            movie.RtReviewReason = null;
            movie.RtUrl = result.ResolvedUrl;
            movie.RtTomatometer = result.Tomatometer;
            movie.RtPopcornmeter = result.Popcornmeter;
            await db.SaveChangesAsync();
            return true;
        }

        private async Task DelayAsync(CancellationToken cancel)
        {
            int lo = Math.Max(0, DelayMinMs);
            int hi = Math.Max(lo + 1, DelayMaxMs);
            try { await Task.Delay(rng.Next(lo, hi), cancel); }
            catch (OperationCanceledException) { }
        }

        private static void PrintResult(IConsole console, RtScoreResult r)
        {
            var o = console.Output;
            o.WriteLine($"── {r.SearchTitle} ──");
            if (!r.Found) { o.WriteLine($"  NOT MATCHED: {r.FailureReason}"); return; }
            o.WriteLine($"  Matched:  {r.MatchedTitle} ({r.MatchedYear})  {r.ResolvedUrl}");
            o.WriteLine($"  Tomatometer: {(r.Tomatometer.HasValue ? r.Tomatometer + "%" : "—")}   " +
                        $"Popcornmeter: {(r.Popcornmeter.HasValue ? r.Popcornmeter + "%" : "—")}");
        }

        private class MovieRow
        {
            public int Id { get; set; }
            public string Title { get; set; }
            public string SimpleTitle { get; set; }
            public int? Year { get; set; }
        }
    }
}
