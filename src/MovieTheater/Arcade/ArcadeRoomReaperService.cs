using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MovieTheater.Db;

namespace MovieTheater.Arcade
{
    /// <summary>
    /// Closes out arcade rooms whose players have all gone quiet: on a gentle timer it asks the
    /// <see cref="ArcadeRoomService"/> which rooms just emptied and stamps their <c>ArcadeSession.EndedUtc</c>.
    /// The in-memory service already dropped them; this only records the end in the durable log. Follows
    /// the <c>ChannelScheduleMaintenanceService</c> shape — a BackgroundService with scoped DB access and a
    /// loop that never dies on a transient failure.
    /// </summary>
    public class ArcadeRoomReaperService : BackgroundService
    {
        private static readonly TimeSpan Tick = TimeSpan.FromSeconds(15);

        private readonly IServiceScopeFactory scopeFactory;
        private readonly ArcadeRoomService rooms;
        private readonly ILogger<ArcadeRoomReaperService> logger;

        public ArcadeRoomReaperService(
            IServiceScopeFactory scopeFactory, ArcadeRoomService rooms, ILogger<ArcadeRoomReaperService> logger)
        {
            this.scopeFactory = scopeFactory;
            this.rooms = rooms;
            this.logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    var reaped = rooms.ReapExpired();
                    if (reaped.Count > 0)
                    {
                        using var scope = scopeFactory.CreateScope();
                        var db = scope.ServiceProvider.GetRequiredService<MovieDb>();
                        var now = DateTime.UtcNow;
                        var sessions = await db.ArcadeSessions
                            .Where(s => reaped.Contains(s.RoomCode) && s.EndedUtc == null)
                            .ToListAsync(stoppingToken);
                        foreach (var s in sessions)
                            s.EndedUtc = now;
                        if (sessions.Count > 0)
                            await db.SaveChangesAsync(stoppingToken);
                    }
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    // Never let a transient failure kill the loop — the next tick retries.
                    logger.LogWarning(ex, "Arcade room reaper tick failed; will retry.");
                }

                try { await Task.Delay(Tick, stoppingToken); }
                catch (OperationCanceledException) { break; }
            }
        }
    }
}
