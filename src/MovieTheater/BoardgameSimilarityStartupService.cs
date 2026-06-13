using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MovieTheater.Db;
using MovieTheater.Services.Bgg;

namespace MovieTheater
{
    /// <summary>
    /// Rebuilds the boardgame-similarity cache once at startup — but as a
    /// <see cref="BackgroundService"/> so it runs AFTER the web host begins
    /// listening rather than blocking it. Blocking startup here (the rebuild takes
    /// ~20s+ and is DB-heavy) delayed the server from answering the /api/status
    /// liveness probe, which got the pod killed and restart-looped under load.
    /// </summary>
    internal class BoardgameSimilarityStartupService : BackgroundService
    {
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly BoardgameSimilarityService _service;
        private readonly ILogger<BoardgameSimilarityStartupService> _logger;

        public BoardgameSimilarityStartupService(
            IServiceScopeFactory scopeFactory,
            BoardgameSimilarityService service,
            ILogger<BoardgameSimilarityStartupService> logger)
        {
            _scopeFactory = scopeFactory;
            _service = service;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<MovieDb>();
                await _service.RebuildAsync(db);
                _logger.LogInformation("Boardgame similarity cache rebuilt.");
            }
            catch (Exception ex)
            {
                // Never let a rebuild failure crash the host; the cache can rebuild later.
                _logger.LogError(ex, "Boardgame similarity rebuild failed at startup.");
            }
        }
    }
}
