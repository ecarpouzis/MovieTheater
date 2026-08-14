using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
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

namespace MovieTheater.Imdb
{
    /// <summary>
    /// Bootstraps episodes for <see cref="Series"/> rows that have NONE — the pre-existing series that
    /// were never part of the ingest's episode scrape. For each such series it discovers the season list
    /// from IMDb, scrapes every season's episode cards (paginating past the 50-cap), and writes
    /// <see cref="Episode"/> rows keyed by <see cref="Episode.SeriesId"/> so they show in the series view.
    /// (File mapping/playability is a separate pass.) Dry-run by default; <c>--apply</c> writes.
    /// </summary>
    [Command("bootstrap-series-episodes", Description = "Scrape IMDb episodes from scratch for Series that have none; write Episode rows by SeriesId.")]
    public class BootstrapSeriesEpisodesCommand : BasicDICommand, ICommand
    {
        [CommandOption("apply", Description = "Write Episode rows. Omit for a dry run (default).")]
        public bool Apply { get; set; }

        [CommandOption("limit", Description = "Max series to process this run.")]
        public int? Limit { get; set; }

        [CommandOption("include-pending", Description = "Also include still-pending (ReviewBatch) series. Default: approved only.")]
        public bool IncludePending { get; set; }

        [CommandOption("headful", Description = "Run the browser with a visible window (debugging).")]
        public bool Headful { get; set; }

        [CommandOption("debug-tt", Description = "Probe one tt's episodes page (selectors, counts, sample text) and exit. No writes.")]
        public string DebugTt { get; set; }

        [CommandOption("delay-min", Description = "Min delay between season pages, ms.")]
        public int DelayMinMs { get; set; } = 1500;

        [CommandOption("delay-max", Description = "Max delay between season pages, ms.")]
        public int DelayMaxMs { get; set; } = 3500;

        // Shared with every Playwright IMDb scraper — the single copy lives on ImdbScrapeService so a
        // Chromium bump can't leave one command behind the engine's Sec-CH-UA and hit "Human Verification".
        private const string UserAgent = ImdbScrapeService.UserAgent;

        private readonly IDbContextFactory<MovieDb> dbFactory;
        private readonly ILogger<BootstrapSeriesEpisodesCommand> logger;
        private readonly Random rng = new();

        public BootstrapSeriesEpisodesCommand(MovieTheaterConfiguration config) : base(config)
        {
            dbFactory = GetRequiredService<IDbContextFactory<MovieDb>>();
            logger = GetRequiredService<ILogger<BootstrapSeriesEpisodesCommand>>();
        }

        private class EpDto
        {
            [JsonPropertyName("tt")] public string Tt { get; set; }
            [JsonPropertyName("text")] public string Text { get; set; }
        }

        public async ValueTask ExecuteAsync(IConsole console)
        {
            var w = console.Output;

            if (!string.IsNullOrWhiteSpace(DebugTt))
            {
                using var dpw = await Playwright.CreateAsync();
                await using var dbrowser = await dpw.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = !Headful });
                var dctx = await dbrowser.NewContextAsync(new BrowserNewContextOptions { UserAgent = UserAgent, Locale = "en-US" });
                var dpage = await dctx.NewPageAsync();
                var seasons = await DiscoverSeasonsAsync(dpage, DebugTt);
                w.WriteLine($"discovered seasons: [{string.Join(",", seasons)}]");
                foreach (var season in seasons)
                {
                    var url = $"https://www.imdb.com/title/{DebugTt}/episodes/?season={season}";
                    await dpage.GotoAsync(url, new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 45000 });
                    await dpage.WaitForTimeoutAsync(3500);
                    w.WriteLine($"  S{season} {url}");
                    w.WriteLine($"    page title: {await dpage.TitleAsync()}");
                    foreach (var sel in new[] { "article.episode-item-wrapper", ".episode-item-wrapper",
                        "section[data-testid='episodes-browse-episodes']", "[data-testid='episodes-browse-episodes'] article",
                        "article", "a.ipc-title-link-wrapper", ".ipc-title__text", "div.list_item", "div.info" })
                        w.WriteLine($"    [{sel}] = {await dpage.Locator(sel).CountAsync()}");
                    var texts = await dpage.EvalOnSelectorAllAsync<string[]>(
                        ".ipc-title__text, div.info strong a, [data-testid*='title']",
                        "els => els.slice(0,10).map(e => (e.textContent||'').trim()).filter(Boolean)");
                    w.WriteLine("    sample texts: " + string.Join(" || ", texts ?? Array.Empty<string>()));
                }
                return;
            }

            List<(int Id, string Tt, string Title)> targets;
            using (var db = await dbFactory.CreateDbContextAsync())
            {
                var q = db.Series.Where(s => s.imdbID != null && s.imdbID != ""
                    && !db.Episodes.Any(e => e.SeriesId == s.Id));
                if (!IncludePending) q = q.Where(s => s.ReviewBatch == null);
                targets = (await q.Select(s => new { s.Id, s.imdbID, s.Title }).ToListAsync())
                    .Select(s => (s.Id, s.imdbID, s.Title))
                    .OrderBy(t => t.Title, StringComparer.OrdinalIgnoreCase).ToList();
            }
            if (Limit.HasValue) targets = targets.Take(Limit.Value).ToList();
            w.WriteLine($"series with no episodes to bootstrap: {targets.Count}{(IncludePending ? " (incl. pending)" : " (approved only)")}");
            foreach (var t in targets.Take(60)) w.WriteLine($"    S{t.Id} {t.Tt}  {t.Title}");

            if (!Apply)
            {
                w.WriteLine("\nDRY RUN — nothing written. Re-run with --apply to scrape + write.");
                return;
            }

            using var pw = await Playwright.CreateAsync();
            await using var browser = await pw.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = !Headful });
            var ctx = await browser.NewContextAsync(new BrowserNewContextOptions { UserAgent = UserAgent, Locale = "en-US" });
            var page = await ctx.NewPageAsync();

            int seriesDone = 0, episodesAdded = 0, failed = 0, noSeasons = 0;
            foreach (var (id, tt, title) in targets)
            {
                try
                {
                    var seasons = await DiscoverSeasonsAsync(page, tt);
                    if (seasons.Count == 0) { noSeasons++; w.WriteLine($"  S{id} {tt} \"{title}\": no seasons found"); seriesDone++; continue; }

                    int addedForSeries = 0;
                    foreach (var season in seasons)
                    {
                        var eps = await ScrapeSeasonAsync(page, tt, season);
                        if (eps.Count == 0) continue;
                        using var db = await dbFactory.CreateDbContextAsync();
                        var existing = await db.Episodes.Where(e => e.SeriesId == id && e.SeasonNumber == season)
                            .Select(e => e.EpisodeNumber).ToListAsync();
                        var have = existing.ToHashSet();
                        foreach (var ep in eps)
                        {
                            if (have.Contains(ep.Episode)) continue;
                            db.Episodes.Add(new Episode
                            {
                                SeriesId = id,
                                SeasonNumber = season,
                                EpisodeNumber = ep.Episode,
                                Title = ep.Title,
                                ImdbId = ep.Tt,
                            });
                            addedForSeries++;
                        }
                        await db.SaveChangesAsync();
                        await Task.Delay(rng.Next(DelayMinMs, DelayMaxMs));
                    }
                    episodesAdded += addedForSeries;
                    w.WriteLine($"  S{id} \"{title}\": {seasons.Count} season(s), +{addedForSeries} episodes");
                }
                catch (Exception ex)
                {
                    failed++;
                    console.Error.WriteLine($"  S{id} {tt} failed: {ex.Message}");
                }
                seriesDone++;
            }
            w.WriteLine($"\ndone. series {seriesDone}, new episodes {episodesAdded}, no-seasons {noSeasons}, failed {failed}");
            logger.LogInformation("bootstrap-series-episodes: {Series} series, {Added} episodes, {Failed} failed", seriesDone, episodesAdded, failed);
        }

        // Read the season numbers offered on the series' episodes page (season tabs / dropdown / links).
        // Falls back to [1] when a single-season show exposes no selector.
        private async Task<List<int>> DiscoverSeasonsAsync(IPage page, string tt)
        {
            await page.GotoAsync($"https://www.imdb.com/title/{tt}/episodes/",
                new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 45000 });
            try { await page.WaitForSelectorAsync("article.episode-item-wrapper, [data-testid='tab-season-entry'], #browse-episodes-season",
                new PageWaitForSelectorOptions { Timeout = 12000 }); }
            catch (TimeoutException) { }

            const string js = @"() => {
                const nums = new Set();
                document.querySelectorAll(""a[href*='season='], [data-testid='tab-season-entry'], #browse-episodes-season option"").forEach(el => {
                    const h = el.getAttribute && el.getAttribute('href');
                    let m = h && h.match(/season=(\d+)/);
                    if (m) nums.add(parseInt(m[1]));
                    const t = (el.textContent || '').trim();
                    if (/^\d{1,3}$/.test(t)) nums.add(parseInt(t));
                });
                return Array.from(nums);
            }";
            var found = await page.EvaluateAsync<int[]>(js) ?? Array.Empty<int>();
            var seasons = found.Where(n => n >= 1 && n <= 100).Distinct().OrderBy(n => n).ToList();
            if (seasons.Count == 0) seasons.Add(1);   // single-season shows show episodes with no selector
            return seasons;
        }

        // Returns (episodeNumber, episode-tt, title) for every card on the fully-expanded season page.
        private async Task<List<(int Episode, string Tt, string Title)>> ScrapeSeasonAsync(IPage page, string tt, int season)
        {
            await page.GotoAsync($"https://www.imdb.com/title/{tt}/episodes/?season={season}",
                new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 45000 });
            try { await page.WaitForSelectorAsync("article.episode-item-wrapper", new PageWaitForSelectorOptions { Timeout = 12000 }); }
            catch (TimeoutException) { }

            var cards = page.Locator("article.episode-item-wrapper");
            int prev = await cards.CountAsync();
            int stable = 0;
            for (int i = 0; i < 80 && stable < 3; i++)
            {
                bool acted = false;
                foreach (var sel in new[] { "button.ipc-see-more__button", "button:has-text('more episode')", "label.ipc-see-more__button" })
                {
                    var btn = page.Locator(sel).First;
                    try
                    {
                        if (await btn.CountAsync() > 0 && await btn.IsVisibleAsync())
                        {
                            await btn.ScrollIntoViewIfNeededAsync(new LocatorScrollIntoViewIfNeededOptions { Timeout = 3000 });
                            await btn.ClickAsync(new LocatorClickOptions { Timeout = 4000 });
                            acted = true;
                            break;
                        }
                    }
                    catch (PlaywrightException) { }
                }
                if (!acted) await page.EvaluateAsync("window.scrollTo(0, document.body.scrollHeight)");
                await page.WaitForTimeoutAsync(1400);
                int now = await cards.CountAsync();
                if (now <= prev) stable++; else { stable = 0; prev = now; }
            }

            const string js = @"els => els.map(card => {
                const a = card.querySelector(""a[href*='/title/tt']"");
                const href = a ? a.getAttribute('href') : '';
                const m = href ? href.match(/tt\d+/) : null;
                const tEl = card.querySelector("".ipc-title__text, [data-testid='slate-list-card-title'], a.ipc-title-link-wrapper, h4"");
                const t = tEl ? tEl.textContent.trim() : '';
                return { tt: m ? m[0] : null, text: t };
            })";
            var rows = await page.EvalOnSelectorAllAsync<EpDto[]>("article.episode-item-wrapper", js);

            var outl = new List<(int, string, string)>();
            var seen = new HashSet<int>();
            foreach (var r in rows ?? Array.Empty<EpDto>())
            {
                var m = Regex.Match(r.Text ?? "", @"(?i)S\d+\s*[.•∙]?\s*E(\d+)\s*[•∙\-·]*\s*(.*)");
                if (!m.Success) continue;
                if (!int.TryParse(m.Groups[1].Value, out int epn) || !seen.Add(epn)) continue;
                var title = m.Groups[2].Value.Trim();
                outl.Add((epn, r.Tt, string.IsNullOrEmpty(title) ? null : title));
            }
            return outl;
        }
    }
}
