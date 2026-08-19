using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MovieTheater.Db;

namespace MovieTheater.Recommendations
{
    /// <summary>
    /// Keeps each user's personalized recommendations fresh in the background, so the "For You" channels
    /// track new ratings and new library content without a manual CLI run. Staleness is detected by the
    /// per-user <see cref="UserTasteProfile.RatingsStamp"/> — rating something new (or the library growing)
    /// changes the stamp, so no explicit "mark dirty" wiring from the rate endpoint is needed.
    ///
    /// <para>Bounded + resumable per the project's long-job rule: each tick recomputes a small capped batch
    /// of stale users and the feature index is only built on ticks that actually have work, so idle ticks
    /// are cheap. <see cref="RecommendationRefresher.PersistAsync"/> also drops the reco channels' future
    /// schedule tail, so refreshed picks start airing within minutes.</para>
    /// </summary>
    public class RecommendationMaintenanceService : BackgroundService
    {
        private static readonly TimeSpan Tick = TimeSpan.FromMinutes(2);
        private static readonly TimeSpan InitialDelay = TimeSpan.FromSeconds(30);
        private const int BatchUsers = 4; // stale users recomputed per tick; the rest wait for later ticks
        private const int TopN = 100;

        private readonly IServiceScopeFactory scopeFactory;
        private readonly ILogger<RecommendationMaintenanceService> logger;
        private readonly RecommendationRefresher refresher = new();

        // The world-state the last all-clear scan saw. While the sentinel still matches it, a tick is
        // three constant-cost aggregates and no per-user scan. Null whenever stale users may remain
        // (work was found, or a batch was capped), so the scan runs every tick until drained.
        private string? cleanSentinel;

        public RecommendationMaintenanceService(IServiceScopeFactory scopeFactory, ILogger<RecommendationMaintenanceService> logger)
        {
            this.scopeFactory = scopeFactory;
            this.logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            try { await Task.Delay(InitialDelay, stoppingToken); }
            catch (OperationCanceledException) { return; }

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
                    logger.LogWarning(ex, "Recommendation maintenance tick failed; will retry.");
                }

                try { await Task.Delay(Tick, stoppingToken); }
                catch (OperationCanceledException) { break; }
            }
        }

        private async Task TickAsync(CancellationToken cancel)
        {
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<MovieDb>();

            var sentinel = await refresher.Staleness.SentinelAsync(db, cancel);
            if (sentinel == cleanSentinel)
                return; // provably the same world the last all-clear scan saw — skip even the stale scan

            var stale = await refresher.Staleness.StaleUsersAsync(db, cancel);
            if (stale.Count == 0)
            {
                cleanSentinel = sentinel;
                return; // nothing rated/changed since last pass — skip the (heavier) index build entirely
            }
            cleanSentinel = null;

            var index = await refresher.BuildIndexAsync(db, cancel);
            int done = 0;
            foreach (var (userId, stamp) in stale.Take(BatchUsers))
            {
                cancel.ThrowIfCancellationRequested();
                var result = await refresher.ComputeAsync(db, index, userId, TopN, cancel);
                await refresher.PersistAsync(db, userId, result, stamp, cancel);
                done++;
            }
            logger.LogInformation("Recommendations: refreshed {Done} of {Stale} stale user(s).", done, stale.Count);
        }
    }
}
