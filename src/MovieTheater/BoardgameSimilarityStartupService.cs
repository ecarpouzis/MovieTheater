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
    /// Loads the persisted boardgame-similarity cache at startup. The compare itself only
    /// runs when a new game is added (see <see cref="BoardgameSimilarityService.RebuildAsync"/>),
    /// which persists its result; here we just read it back into memory. A full rebuild is
    /// triggered only as a one-time bootstrap when nothing has been persisted yet.
    ///
    /// Runs as a <see cref="BackgroundService"/> so it executes AFTER the web host begins
    /// listening rather than blocking it. Blocking startup here (a full rebuild takes ~20s+
    /// and is DB-heavy) delayed the server from answering the /api/status liveness probe,
    /// which got the pod killed and restart-looped under load.
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
                var loaded = await _service.LoadAsync(db);

                // Rebuild only when we have to: nothing persisted yet (fresh DB / first run
                // after this feature shipped), or some game never got computed because an
                // insert's rebuild broke. Otherwise the load above is all that's needed.
                if (loaded == 0)
                {
                    await _service.RebuildAsync(db);
                    _logger.LogInformation("Boardgame similarity cache bootstrapped (no persisted data found).");
                }
                else if (await _service.HasUncomputedGamesAsync(db))
                {
                    _logger.LogInformation("Boardgame similarity cache loaded ({Count} games) but some are missing; rebuilding.", loaded);
                    await _service.RebuildAsync(db);
                }
                else
                {
                    _logger.LogInformation("Boardgame similarity cache loaded ({Count} games).", loaded);
                }
            }
            catch (Exception ex)
            {
                // Never let a rebuild failure crash the host; the cache can rebuild later.
                _logger.LogError(ex, "Boardgame similarity rebuild failed at startup.");
            }
        }
    }
}
