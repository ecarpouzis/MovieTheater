using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MovieTheater.Db;
using MovieTheater.Services.Bgg;

namespace MovieTheater
{
    internal class BoardgameSimilarityStartupService : IHostedService
    {
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly BoardgameSimilarityService _service;

        public BoardgameSimilarityStartupService(IServiceScopeFactory scopeFactory, BoardgameSimilarityService service)
        {
            _scopeFactory = scopeFactory;
            _service = service;
        }

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<MovieDb>();
            await _service.RebuildAsync(db);
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
