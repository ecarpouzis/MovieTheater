using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MovieTheater.Db;

namespace MovieTheater.Channels
{
    /// <summary>
    /// Deletes finished watch-party channels (docs/playlists-watchparty-plan.md). A watch party is a private,
    /// hidden playlist channel; once it has begun and its lineup has fully played out with everyone gone, the
    /// row + its items serve no further purpose, so this reaps them on a gentle timer. Unstarted parties are
    /// cleaned up immediately when the last member leaves (WatchpartyController.Leave); this backstops the ones
    /// that actually ran. Follows the ArcadeRoomReaperService / ChannelScheduleMaintenanceService shape — a
    /// BackgroundService with scoped DB access and a loop that never dies on a transient failure.
    /// </summary>
    public class WatchpartyReaperService : BackgroundService
    {
        private static readonly TimeSpan Tick = TimeSpan.FromMinutes(5);
        // A started party whose whole lineup ended this long ago is considered over and reaped.
        private static readonly TimeSpan FinishedGrace = TimeSpan.FromHours(6);
        private const int BatchSize = 50;

        private readonly IServiceScopeFactory scopeFactory;
        private readonly ILogger<WatchpartyReaperService> logger;

        public WatchpartyReaperService(IServiceScopeFactory scopeFactory, ILogger<WatchpartyReaperService> logger)
        {
            this.scopeFactory = scopeFactory;
            this.logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await TickAsync(stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    // Never let a transient failure kill the loop — the next tick retries.
                    logger.LogWarning(ex, "Watch-party reaper tick failed; will retry.");
                }

                try { await Task.Delay(Tick, stoppingToken); }
                catch (OperationCanceledException) { break; }
            }
        }

        private async Task TickAsync(CancellationToken cancel)
        {
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<MovieDb>();
            var now = DateTime.UtcNow;

            // Only started parties are reaped by age here; unstarted ones are deleted the moment the last
            // member leaves (WatchpartyController.Leave), and a far-future anchor gives them no meaningful
            // creation time to age against.
            var started = await db.Channels
                .Where(c => c.WatchpartyToken != null && c.WatchpartyStartedUtc != null)
                .Take(500)
                .ToListAsync(cancel);
            if (started.Count == 0)
                return;

            var startedIds = started.Select(c => c.Id).ToList();
            var lastEnd = (await db.ChannelScheduleItems
                    .Where(i => startedIds.Contains(i.ChannelId))
                    .GroupBy(i => i.ChannelId)
                    .Select(g => new { ChannelId = g.Key, Max = g.Max(x => x.EndUtc) })
                    .ToListAsync(cancel))
                .ToDictionary(x => x.ChannelId, x => x.Max);

            var toReap = started.Where(c =>
            {
                // No materialized item (odd) → fall back to the start instant; otherwise its true end.
                var end = lastEnd.TryGetValue(c.Id, out var e) ? e : c.WatchpartyStartedUtc!.Value;
                return end < now - FinishedGrace;
            }).Take(BatchSize).ToList();

            if (toReap.Count == 0)
                return;

            db.Channels.RemoveRange(toReap); // cascades PlaylistItems + ChannelScheduleItems
            await db.SaveChangesAsync(cancel);
            logger.LogInformation("Watch-party reaper: removed {Count} finished/abandoned parties.", toReap.Count);
        }
    }
}
