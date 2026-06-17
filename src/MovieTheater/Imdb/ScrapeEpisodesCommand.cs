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
    /// IMDb season pages render only the first 50 episodes server-side and lazy-load the rest behind a
    /// "more episodes" control, so the one-shot cache truncated long seasons at 50. This pass drives the
    /// live page, clicks through to the full season, extracts every episode card, and upserts the missing
    /// <see cref="Episode"/> rows. By default it targets only the capped seasons (exactly 50 parsed
    /// episodes); <c>--all-seasons</c> re-scrapes every tagged series' seasons.
    /// </summary>
    [Command("scrape-episodes", Description = "Paginate IMDb season pages to capture episodes past the 50-cap; upsert Episode rows.")]
    public class ScrapeEpisodesCommand : BasicDICommand, ICommand
    {
        [CommandOption("limit", Description = "Max season-pages to process this run.")]
        public int? Limit { get; set; }

        [CommandOption("headful", Description = "Run the browser with a visible window (debugging).")]
        public bool Headful { get; set; }

        [CommandOption("all-seasons", Description = "Re-scrape every tagged series' seasons, not just the capped (==50) ones.")]
        public bool AllSeasons { get; set; }

        [CommandOption("delay-min", Description = "Min delay between season pages, ms.")]
        public int DelayMinMs { get; set; } = 1500;

        [CommandOption("delay-max", Description = "Max delay between season pages, ms.")]
        public int DelayMaxMs { get; set; } = 3500;

        private const string UserAgent =
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36";

        private readonly IDbContextFactory<MovieDb> dbFactory;
        private readonly ILogger<ScrapeEpisodesCommand> logger;
        private readonly Random rng = new Random();

        public ScrapeEpisodesCommand(MovieTheaterConfiguration config) : base(config)
        {
            dbFactory = GetRequiredService<IDbContextFactory<MovieDb>>();
            logger = GetRequiredService<ILogger<ScrapeEpisodesCommand>>();
        }

        private class EpDto
        {
            [JsonPropertyName("tt")] public string Tt { get; set; }
            [JsonPropertyName("text")] public string Text { get; set; }
        }

        public async ValueTask ExecuteAsync(IConsole console)
        {
            // Targets = (seriesId, imdbID, season). Default: capped seasons (exactly 50 parsed).
            List<(int Sid, string Tt, int Season)> targets;
            using (var db = await dbFactory.CreateDbContextAsync())
            {
                var seriesIds = await db.Series
                    .Where(s => s.imdbID != null)
                    .Select(s => new { s.Id, s.imdbID })
                    .ToListAsync();
                var ttById = seriesIds.ToDictionary(s => s.Id, s => s.imdbID);

                var seasons = AllSeasons
                    ? await db.Episodes.Where(e => e.SeriesId != null).Select(e => new { e.SeriesId, e.SeasonNumber }).Distinct().ToListAsync()
                    : (await db.Episodes.Where(e => e.SeriesId != null).GroupBy(e => new { e.SeriesId, e.SeasonNumber })
                        .Where(g => g.Count() == 50)
                        .Select(g => new { g.Key.SeriesId, g.Key.SeasonNumber })
                        .ToListAsync());

                targets = seasons
                    .Where(s => s.SeriesId != null && ttById.ContainsKey(s.SeriesId.Value))
                    .Select(s => (s.SeriesId.Value, ttById[s.SeriesId.Value], s.SeasonNumber))
                    .OrderBy(t => t.Item2).ThenBy(t => t.SeasonNumber)
                    .ToList();
            }
            if (Limit.HasValue) targets = targets.Take(Limit.Value).ToList();
            console.Output.WriteLine($"season-pages to scrape: {targets.Count}{(AllSeasons ? " (all tagged series)" : " (capped only)")}");

            using var pw = await Microsoft.Playwright.Playwright.CreateAsync();
            await using var browser = await pw.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = !Headful });
            var ctx = await browser.NewContextAsync(new BrowserNewContextOptions { UserAgent = UserAgent, Locale = "en-US" });
            var page = await ctx.NewPageAsync();
            await page.GotoAsync("https://www.imdb.com/", new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 45000 });

            int seasonsDone = 0, episodesAdded = 0, failed = 0;
            foreach (var (sid, tt, season) in targets)
            {
                try
                {
                    var eps = await ScrapeSeasonAsync(page, tt, season, console);
                    int added = 0;
                    using var db = await dbFactory.CreateDbContextAsync();
                    var existing = await db.Episodes
                        .Where(e => e.SeriesId == sid && e.SeasonNumber == season)
                        .ToDictionaryAsync(e => e.EpisodeNumber);
                    foreach (var ep in eps)
                    {
                        if (existing.TryGetValue(ep.Episode, out var row))
                        {
                            if (string.IsNullOrEmpty(row.ImdbId) && ep.Tt != null) row.ImdbId = ep.Tt;
                            if (string.IsNullOrEmpty(row.Title) && !string.IsNullOrEmpty(ep.Title)) row.Title = ep.Title;
                        }
                        else
                        {
                            db.Episodes.Add(new Episode
                            {
                                SeriesId = sid,
                                SeasonNumber = season,
                                EpisodeNumber = ep.Episode,
                                Title = ep.Title,
                                ImdbId = ep.Tt,
                            });
                            added++;
                        }
                    }
                    await db.SaveChangesAsync();
                    episodesAdded += added;
                    console.Output.WriteLine($"  {tt} S{season}: page had {eps.Count} episodes, +{added} new");
                }
                catch (Exception ex)
                {
                    failed++;
                    console.Error.WriteLine($"  {tt} S{season} failed: {ex.Message}");
                }
                seasonsDone++;
                await Task.Delay(rng.Next(DelayMinMs, DelayMaxMs));
            }
            console.Output.WriteLine($"done. season-pages {seasonsDone}, new episodes {episodesAdded}, failed {failed}");
            logger.LogInformation("scrape-episodes: {Seasons} seasons, {Added} new episodes, {Failed} failed", seasonsDone, episodesAdded, failed);
        }

        // Returns (episodeNumber, episode-tt, title) for every card on the fully-expanded season page.
        private async Task<List<(int Episode, string Tt, string Title)>> ScrapeSeasonAsync(IPage page, string tt, int season, IConsole console)
        {
            var url = $"https://www.imdb.com/title/{tt}/episodes/?season={season}";
            await page.GotoAsync(url, new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 45000 });
            try { await page.WaitForSelectorAsync("article.episode-item-wrapper", new PageWaitForSelectorOptions { Timeout = 15000 }); }
            catch (TimeoutException) { }

            var cards = page.Locator("article.episode-item-wrapper");
            int prev = await cards.CountAsync();
            // Expand the season: click the see-more control if present, else scroll to trigger lazy-load.
            // Stop when the rendered card count stops growing (works for either mechanism).
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
                if (!acted)
                    await page.EvaluateAsync("window.scrollTo(0, document.body.scrollHeight)");
                await page.WaitForTimeoutAsync(1400);
                int now = await cards.CountAsync();
                if (now <= prev) stable++; else { stable = 0; prev = now; }
            }

            int rendered = await cards.CountAsync();

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
                // Card title looks like "S2.E51 ∙ Episode Name".
                var m = Regex.Match(r.Text ?? "", @"(?i)S\d+\s*[.•∙]?\s*E(\d+)\s*[•∙\-·]*\s*(.*)");
                if (!m.Success) continue;
                if (!int.TryParse(m.Groups[1].Value, out int epn) || !seen.Add(epn)) continue;
                var title = m.Groups[2].Value.Trim();
                outl.Add((epn, r.Tt, string.IsNullOrEmpty(title) ? null : title));
            }
            console.Output.WriteLine($"    {tt} S{season}: cards rendered={rendered}, parsed episodes={outl.Count}");
            return outl;
        }
    }
}
