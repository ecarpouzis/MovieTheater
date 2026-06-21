using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MovieTheater.Db;
using MovieTheater.Services.Poster;

namespace MovieTheater
{
    /// <summary>
    /// One-time self-heal: on startup, clears any "poster" that is actually IMDb's placeholder logo
    /// (…/imdb_logo.png) — see <see cref="PlaceholderPosterCleaner"/>. Runs here (in the web app, which has
    /// the posters mount) because the deploy environment has no shell access to run the equivalent CLI
    /// command. Idempotent — a no-op once none remain, so it costs one cheap query per boot thereafter and
    /// doubles as a safety net if one ever slips back in. A <see cref="BackgroundService"/> so it runs AFTER
    /// the host is listening (never blocks the liveness probe) and never crashes the app on failure.
    /// </summary>
    internal class PlaceholderPosterCleanupStartupService : BackgroundService
    {
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly ILogger<PlaceholderPosterCleanupStartupService> _logger;

        public PlaceholderPosterCleanupStartupService(
            IServiceScopeFactory scopeFactory,
            ILogger<PlaceholderPosterCleanupStartupService> logger)
        {
            _scopeFactory = scopeFactory;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<MovieDb>();
                var imageRepo = scope.ServiceProvider.GetRequiredService<IPosterImageRepository>();
                var cleared = await PlaceholderPosterCleaner.RunAsync(db, imageRepo, stoppingToken);
                if (cleared > 0)
                    _logger.LogInformation("Cleared {Count} IMDb-logo placeholder poster(s) on startup.", cleared);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Placeholder-poster startup cleanup failed (will retry next boot).");
            }
        }
    }
}
