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

        [CommandOption("retry-review", Description = "Reprocess only rows currently flagged RtNeedsReview (recover earlier misses).")]
        public bool RetryReview { get; set; }

        [CommandOption("include-series", Description = "Also scrape Series rows (resolved against RT's /tv/ pages) using the same resume/rescrape/retry-review rules. For the --title smoke test, treats the title as a series.")]
        public bool IncludeSeries { get; set; }

        [CommandOption("series-only", Description = "Scrape ONLY Series rows, skipping Movies. Implies --include-series.")]
        public bool SeriesOnly { get; set; }

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

            bool wantSeries = IncludeSeries || SeriesOnly;

            // Single-title smoke test: scrape and print, never write. --include-series/--series-only
            // searches RT's /tv/ pages instead of /m/.
            if (!string.IsNullOrWhiteSpace(SingleTitle))
            {
                var single = await ScrapeWithRetryAsync(page, CleanTitleQuery(SingleTitle.Trim()), SingleYear, wantSeries, cancel);
                PrintResult(console, single);
                return;
            }

            List<TitleRow> todo;
            using (var db = await dbFactory.CreateDbContextAsync())
            {
                var movieRows = new List<TitleRow>();
                if (!SeriesOnly)
                {
                    IQueryable<Db.Movie> rows = db.Movies;
                    rows = RetryReview
                        ? rows.Where(m => m.RtNeedsReview)
                        : rows.Where(m => Rescrape || m.RtScoresUpdatedDate == null);
                    movieRows = await rows
                        .OrderBy(m => m.id)
                        .Select(m => new TitleRow
                        {
                            Id = m.id,
                            IsSeries = false,
                            Title = m.Title,
                            SimpleTitle = m.SimpleTitle,
                            Year = m.ReleaseDate != null ? (int?)m.ReleaseDate.Value.Year : null
                        })
                        .ToListAsync();
                }

                var seriesRows = new List<TitleRow>();
                if (wantSeries)
                {
                    IQueryable<Db.Series> srows = db.Series;
                    srows = RetryReview
                        ? srows.Where(s => s.RtNeedsReview)
                        : srows.Where(s => Rescrape || s.RtScoresUpdatedDate == null);
                    seriesRows = await srows
                        .OrderBy(s => s.Id)
                        .Select(s => new TitleRow
                        {
                            Id = s.Id,
                            IsSeries = true,
                            Title = s.Title,
                            SimpleTitle = s.SimpleTitle,
                            // Prefer the series' start year; fall back to ReleaseDate's year.
                            Year = s.StartYear ?? (s.ReleaseDate != null ? (int?)s.ReleaseDate.Value.Year : null)
                        })
                        .ToListAsync();
                }

                // Movies first, then series, each by id — stable, resumable ordering.
                todo = movieRows.Concat(seriesRows).ToList();
                if (Limit.HasValue) todo = todo.Take(Limit.Value).ToList();
            }

            console.Output.WriteLine(
                $"Scraping RT scores for {todo.Count} title(s) " +
                $"({todo.Count(t => !t.IsSeries)} movie, {todo.Count(t => t.IsSeries)} series){(DryRun ? " (dry-run)" : "")}…");
            int done = 0, scored = 0, flagged = 0, skipped = 0, consecutiveFailures = 0;
            bool throttled = false;

            foreach (var row in todo)
            {
                if (cancel.IsCancellationRequested)
                {
                    console.Output.WriteLine("Cancellation requested — stopping (progress is saved per-movie).");
                    break;
                }

                // Search RT by the real display Title — the canonical name RT also uses. SimpleTitle is
                // our hidden franchise watch-order sort key ("Batman 3", "Bad Boys 2", "Man with No
                // Name 1"), which RT can't match — searching it stranded hundreds of titles in
                // needs-review. Fall back to SimpleTitle (with its franchise-index cleanup) only when a
                // row genuinely has no Title.
                var useTitle = !string.IsNullOrWhiteSpace(row.Title);
                var searchTitle = useTitle ? CleanTitleQuery(row.Title) : CleanSearchQuery(row.SimpleTitle);
                try
                {
                    var result = await ScrapeWithRetryAsync(page, searchTitle, row.Year, row.IsSeries, cancel);
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
                        var applied = await ApplyAsync(row, result);
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

        private async Task<RtScoreResult> ScrapeWithRetryAsync(IPage page, string title, int? year, bool isSeries, CancellationToken cancel)
        {
            const int maxAttempts = 3;
            for (int attempt = 1; ; attempt++)
            {
                try
                {
                    return await scraper.ScrapeAsync(page, title, year, isSeries);
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

        // Writes the scores onto the Movie or Series row. The Movie and Series Rt* columns are
        // identical, so a tiny abstraction over the two entities keeps one apply path.
        private async Task<bool> ApplyAsync(TitleRow row, RtScoreResult result)
        {
            using var db = await dbFactory.CreateDbContextAsync();

            IRtTarget target = row.IsSeries
                ? (await db.Series.FirstOrDefaultAsync(s => s.Id == row.Id)) is { } s ? new SeriesRtTarget(s) : null
                : (await db.Movies.FirstOrDefaultAsync(m => m.id == row.Id)) is { } m ? new MovieRtTarget(m) : null;
            if (target == null) return false;

            target.RtScoresUpdatedDate = DateTime.Now;

            if (!result.Found)
            {
                target.RtNeedsReview = true;
                target.RtReviewReason = result.FailureReason ?? "Could not resolve on Rotten Tomatoes.";
                await db.SaveChangesAsync();
                return false;
            }

            target.RtNeedsReview = false;
            target.RtReviewReason = null;
            target.RtUrl = result.ResolvedUrl;
            target.RtTomatometer = result.Tomatometer;
            target.RtPopcornmeter = result.Popcornmeter;
            await db.SaveChangesAsync();
            return true;
        }

        // Thin write-shims so movie + series share one apply path (their Rt* columns are identical).
        private interface IRtTarget
        {
            DateTime? RtScoresUpdatedDate { set; }
            bool RtNeedsReview { set; }
            string RtReviewReason { set; }
            string RtUrl { set; }
            int? RtTomatometer { set; }
            int? RtPopcornmeter { set; }
        }

        private sealed class MovieRtTarget : IRtTarget
        {
            private readonly Db.Movie m;
            public MovieRtTarget(Db.Movie m) => this.m = m;
            public DateTime? RtScoresUpdatedDate { set => m.RtScoresUpdatedDate = value; }
            public bool RtNeedsReview { set => m.RtNeedsReview = value; }
            public string RtReviewReason { set => m.RtReviewReason = value; }
            public string RtUrl { set => m.RtUrl = value; }
            public int? RtTomatometer { set => m.RtTomatometer = value; }
            public int? RtPopcornmeter { set => m.RtPopcornmeter = value; }
        }

        private sealed class SeriesRtTarget : IRtTarget
        {
            private readonly Db.Series s;
            public SeriesRtTarget(Db.Series s) => this.s = s;
            public DateTime? RtScoresUpdatedDate { set => s.RtScoresUpdatedDate = value; }
            public bool RtNeedsReview { set => s.RtNeedsReview = value; }
            public string RtReviewReason { set => s.RtReviewReason = value; }
            public string RtUrl { set => s.RtUrl = value; }
            public int? RtTomatometer { set => s.RtTomatometer = value; }
            public int? RtPopcornmeter { set => s.RtPopcornmeter = value; }
        }

        private async Task DelayAsync(CancellationToken cancel)
        {
            int lo = Math.Max(0, DelayMinMs);
            int hi = Math.Max(lo + 1, DelayMaxMs);
            try { await Task.Delay(rng.Next(lo, hi), cancel); }
            catch (OperationCanceledException) { }
        }

        // De-invert a stored ", The"/", A"/", An" article so RT search sees natural word order.
        // Handles both a trailing article ("Chronicles of Riddick, The" -> "The Chronicles of
        // Riddick") and one sitting before a subtitle colon ("Twilight Saga, The: Eclipse" ->
        // "The Twilight Saga: Eclipse").
        private static string DeinvertArticle(string t)
        {
            var mid = Regex.Match(t, @"^(.+?),\s*(the|a|an):\s*(.+)$", RegexOptions.IgnoreCase);
            if (mid.Success) return $"{mid.Groups[2].Value} {mid.Groups[1].Value}: {mid.Groups[3].Value}".Trim();
            var tail = Regex.Match(t, @"^(.+),\s*(the|a|an)$", RegexOptions.IgnoreCase);
            if (tail.Success) return $"{tail.Groups[2].Value} {tail.Groups[1].Value}".Trim();
            return t;
        }

        // Clean a real display Title for RT search: ONLY article de-inversion. Titles carry no
        // franchise numbering (unlike SimpleTitle), so the franchise-index strips in CleanSearchQuery
        // must NOT run here — they'd corrupt legitimate titles like "Kill Bill: Vol. 1".
        private static string CleanTitleQuery(string title)
        {
            if (string.IsNullOrWhiteSpace(title)) return title;
            var t = DeinvertArticle(title.Trim());
            return string.IsNullOrWhiteSpace(t) ? title : t;
        }

        // Clean a SimpleTitle (our franchise watch-order sort key) for RT search — used only when a row
        // has no display Title. Strips the library's franchise indexing so RT search has a chance:
        //   "Airplane 1: Airplane!"  -> "Airplane!"   (real title after a "<name> NN:" index)
        //   "Anchorman 1"            -> "Anchorman"   (only a trailing "1"/"01": the franchise's first
        //                                              film, whose base name maps to it; higher numbers
        //                                              like "Pink Panther 02" name a distinct film and
        //                                              are left alone — stripping would mis-score)
        //   "'60s, The"              -> "The '60s"     (de-invert a trailing article)
        // Matching still normalizes both sides, so this only needs to get RT search close.
        private static string CleanSearchQuery(string title)
        {
            if (string.IsNullOrWhiteSpace(title)) return title;
            var t = title.Trim();

            var colon = Regex.Match(t, @"^.+?\s\d{1,3}:\s*(.+)$");
            if (colon.Success) t = colon.Groups[1].Value.Trim();

            t = Regex.Replace(t, @"\s+0?1$", "").Trim();

            t = DeinvertArticle(t);

            return string.IsNullOrWhiteSpace(t) ? title : t;
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

        private class TitleRow
        {
            public int Id { get; set; }
            public bool IsSeries { get; set; }
            public string Title { get; set; }
            public string SimpleTitle { get; set; }
            public int? Year { get; set; }
        }
    }
}
