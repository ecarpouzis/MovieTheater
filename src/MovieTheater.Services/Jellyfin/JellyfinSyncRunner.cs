using System;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MovieTheater.Services.Jellyfin
{
    /// <summary>
    /// Runs the Jellyfin sync as a SERVER-SIDE background job so its outcome never depends on a
    /// browser holding an HTTP request open. The sync legitimately runs for minutes (a 27k-item
    /// enumeration plus per-file keyframe round trips), which is longer than any proxy in front of
    /// the pod will wait — the admin button's old blocking endpoint "failed" with an empty gateway
    /// response while the sync quietly finished (2026-08-14). Now the POST starts the job and
    /// returns; the UI polls <see cref="Snapshot"/> for the verdict, exactly like the library-scan
    /// phase it already chains.
    ///
    /// Single-flight: one sync at a time, ever — a second start request while one runs is answered
    /// "already running" and the caller just follows the run in flight. State is one run deep (the
    /// last finished report/error), held in memory: a pod restart forgets it, which the status
    /// endpoint reports honestly as "no result available" rather than inventing an outcome.
    /// </summary>
    public class JellyfinSyncRunner
    {
        private readonly IServiceScopeFactory scopeFactory;
        private readonly ILogger<JellyfinSyncRunner> logger;
        private readonly object gate = new();

        private bool running;
        private DateTime? startedUtc;
        private DateTime? finishedUtc;
        private JellyfinSyncReport? lastReport;
        private string? lastError;
        private string? phase;

        public JellyfinSyncRunner(IServiceScopeFactory scopeFactory, ILogger<JellyfinSyncRunner> logger)
        {
            this.scopeFactory = scopeFactory;
            this.logger = logger;
        }

        /// <summary>Starts a sync unless one is already in flight. Returns false when it is — the
        /// caller polls the existing run instead of stacking a second one.</summary>
        public bool TryStart(string? startedBy)
        {
            lock (gate)
            {
                if (running) return false;
                running = true;
                startedUtc = DateTime.UtcNow;
                finishedUtc = null;
                lastReport = null;
                lastError = null;
                phase = "starting";
            }
            logger.LogInformation("Background Jellyfin sync started (by {User})", startedBy ?? "?");
            _ = Task.Run(RunOnceAsync);
            return true;
        }

        private async Task RunOnceAsync()
        {
            // The outcome is captured LOCALLY and logged from the local: once `running` clears, a new
            // TryStart may legally reset the shared fields, and reading them afterwards once logged a
            // failed run as "ok".
            string? outcome = null;
            try
            {
                // The service is transient and its DbContext comes from a factory, but resolving it
                // through a scope keeps any scoped dependency it may grow later correctly owned by
                // THIS run rather than by a long-gone HTTP request.
                using var scope = scopeFactory.CreateScope();
                var sync = scope.ServiceProvider.GetRequiredService<JellyfinSyncService>();
                var rep = await sync.RunAsync(dryRun: false,
                    progress: p => { lock (gate) phase = p; });
                outcome = rep.Aborted;
                lock (gate)
                {
                    lastReport = rep;
                    if (rep.Aborted != null) lastError = rep.Aborted;
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Background Jellyfin sync failed");
                outcome = $"[{ex.GetType().Name}] {ex.Message}";
                lock (gate) { lastError = outcome; }
            }
            finally
            {
                lock (gate)
                {
                    running = false;
                    finishedUtc = DateTime.UtcNow;
                    phase = null;
                }
                if (outcome == null) logger.LogInformation("Background Jellyfin sync finished (ok)");
                else logger.LogError("Background Jellyfin sync finished with error: {Outcome}", outcome);
            }
        }

        /// <summary>Consistent view of the run state for the status endpoint.</summary>
        public (bool Running, DateTime? StartedUtc, DateTime? FinishedUtc, JellyfinSyncReport? Report, string? Error, string? Phase) Snapshot()
        {
            lock (gate) return (running, startedUtc, finishedUtc, lastReport, lastError, phase);
        }
    }
}
