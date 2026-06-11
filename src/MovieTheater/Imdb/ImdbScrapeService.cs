using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Playwright;

namespace MovieTheater.Imdb
{
    /// <summary>
    /// Long-lived service that scrapes a single IMDB title on demand (e.g. when a movie is
    /// inserted), reusing one warmed-up headless browser across calls. Page access is
    /// serialized, so it is safe to register as a singleton and call from request handlers.
    /// The bulk <see cref="ScrapeImdbCommand"/> runs in a separate process and manages its
    /// own browser; both share <see cref="ImdbDataApplier"/> for identical DB writes.
    /// </summary>
    public sealed class ImdbScrapeService : IAsyncDisposable
    {
        private const string UserAgent =
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36";

        private readonly ILogger<ImdbScrapeService> logger;
        private readonly ImdbTitleScraper scraper = new ImdbTitleScraper();
        private readonly SemaphoreSlim gate = new SemaphoreSlim(1, 1);

        private IPlaywright playwright;
        private IBrowser browser;
        private IBrowserContext context;
        private IPage page;
        private bool warmedUp;

        public ImdbScrapeService(ILogger<ImdbScrapeService> logger)
        {
            this.logger = logger;
        }

        public async Task<ImdbScrapeResult> ScrapeAsync(string imdbId, int castLimit = 15, bool includePlotSummaries = true)
        {
            await gate.WaitAsync().ConfigureAwait(false);
            try
            {
                await EnsureBrowserAsync().ConfigureAwait(false);
                try
                {
                    return await scraper.ScrapeAsync(page, imdbId, castLimit, includePlotSummaries).ConfigureAwait(false);
                }
                catch (ImdbChallengeException)
                {
                    // Session got challenged — re-warm once and retry.
                    await WarmUpAsync().ConfigureAwait(false);
                    return await scraper.ScrapeAsync(page, imdbId, castLimit, includePlotSummaries).ConfigureAwait(false);
                }
            }
            catch (ImdbChallengeException ex)
            {
                return new ImdbScrapeResult { ImdbId = imdbId, Found = false, FailureReason = ex.Message };
            }
            finally
            {
                gate.Release();
            }
        }

        private async Task EnsureBrowserAsync()
        {
            if (browser == null)
            {
                playwright = await Playwright.CreateAsync().ConfigureAwait(false);
                browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true }).ConfigureAwait(false);
                context = await browser.NewContextAsync(
                    new BrowserNewContextOptions { UserAgent = UserAgent, Locale = "en-US" }).ConfigureAwait(false);
                page = await context.NewPageAsync().ConfigureAwait(false);
            }
            if (!warmedUp)
                await WarmUpAsync().ConfigureAwait(false);
        }

        private async Task WarmUpAsync()
        {
            try
            {
                await page.GotoAsync("https://www.imdb.com/",
                    new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 45000 }).ConfigureAwait(false);
                await page.WaitForTimeoutAsync(2500).ConfigureAwait(false);
                warmedUp = true;
            }
            catch (PlaywrightException ex)
            {
                logger.LogWarning(ex, "IMDB homepage warm-up failed; continuing anyway.");
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (context != null) await context.CloseAsync().ConfigureAwait(false);
            if (browser != null) await browser.DisposeAsync().ConfigureAwait(false);
            playwright?.Dispose();
            gate.Dispose();
        }
    }
}
