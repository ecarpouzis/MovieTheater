using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MovieTheater.Channels
{
    /// <summary>
    /// Keeps every channel's lineup materialized ahead and its rating ceiling warm, so the viewer-facing
    /// read paths (List / Now / the grid guide) never have to do per-channel scans inline. This is what
    /// makes the feature scale to many channels: the O(channels) heavy work (eligible-set scans, schedule
    /// generation) happens here in bounded batches on a timer, not inside a user request.
    ///
    /// Bounded + resumable per the project's long-job rule: each tick processes a small, capped batch and
    /// advances a round-robin cursor, so a large channel list is covered over several ticks rather than in
    /// one long pass. EnsureScheduleAsync is idempotent (already-materialized channels are a cheap no-op),
    /// so re-running never double-generates.
    /// </summary>
    public class ChannelScheduleMaintenanceService : BackgroundService
    {
        private static readonly TimeSpan Tick = TimeSpan.FromSeconds(15);
        private static readonly TimeSpan Horizon = TimeSpan.FromHours(48);
        private const int BatchSize = 8; // channels touched per tick — keeps per-tick DB work tiny
        private const int StalePauseBatchSize = 4; // abandoned pauses lifted per tick (see ResumeStalePausesAsync)

        // On a cold start (every deploy/restart wipes the in-memory ceiling + schedule caches) the first
        // pass over all channels is what makes the guide "come up". Cover it faster — bigger batch, shorter
        // gap — until every channel has been warmed once, then drop to the gentle steady-state cadence.
        private static readonly TimeSpan WarmupTick = TimeSpan.FromSeconds(4);
        private const int WarmupBatchSize = 12;

        private readonly IServiceScopeFactory scopeFactory;
        private readonly ILogger<ChannelScheduleMaintenanceService> logger;
        private int cursor; // index into the enabled-channel list; wraps, so coverage is round-robin
        private bool firstPassDone;
        private int firstPassCovered;

        public ChannelScheduleMaintenanceService(
            IServiceScopeFactory scopeFactory, ILogger<ChannelScheduleMaintenanceService> logger)
        {
            this.scopeFactory = scopeFactory;
            this.logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            // A small initial delay so startup isn't competing with the first requests.
            try { await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken); }
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
                    // Never let a transient failure kill the loop — the next tick retries.
                    logger.LogWarning(ex, "Channel schedule maintenance tick failed; will retry.");
                }

                try { await Task.Delay(firstPassDone ? Tick : WarmupTick, stoppingToken); }
                catch (OperationCanceledException) { break; }
            }
        }

        private async Task TickAsync(CancellationToken cancel)
        {
            using var scope = scopeFactory.CreateScope();
            var schedule = scope.ServiceProvider.GetRequiredService<ChannelScheduleService>();

            // Lift abandoned pauses first: a frozen channel is excluded from the round-robin below (its
            // clock is stopped, so there's nothing to extend), which is exactly why nothing else would ever
            // un-stick a channel somebody paused and walked away from. Bounded batch, so a pile of them is
            // cleared over several ticks.
            int resumed = await schedule.ResumeStalePausesAsync(StalePauseBatchSize, cancel);

            var ids = await schedule.EnabledChannelIdsAsync(cancel);
            if (ids.Count == 0)
                return;

            if (cursor >= ids.Count)
                cursor = 0;

            int batchSize = firstPassDone ? BatchSize : WarmupBatchSize;
            var horizon = DateTime.UtcNow.Add(Horizon);
            int processed = 0;
            for (int n = 0; n < batchSize && n < ids.Count; n++)
            {
                cancel.ThrowIfCancellationRequested();
                var id = ids[(cursor + n) % ids.Count];

                // Extend the lineup to the horizon (no-op when already covered) and warm the ceiling
                // cache so the age gate is free for the next reader.
                await schedule.EnsureAndWarmChannelAsync(id, horizon, cancel);
                processed++;
            }

            cursor = (cursor + batchSize) % ids.Count;
            if (!firstPassDone)
            {
                firstPassCovered += processed;
                if (firstPassCovered >= ids.Count)
                {
                    firstPassDone = true;
                    logger.LogInformation("Channel schedule maintenance: initial warm-up of {Count} channels complete.", ids.Count);
                }
            }
            if (processed > 0)
                logger.LogDebug(
                    "Channel schedule maintenance: refreshed {Count} channel(s), auto-resumed {Resumed}.",
                    processed, resumed);
        }
    }
}
