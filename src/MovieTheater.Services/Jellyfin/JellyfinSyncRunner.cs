using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MovieTheater.Services.Jellyfin
{
    /// <summary>
    /// Runs the ENTIRE "Sync from Jellyfin" operation as a SERVER-SIDE background job — trigger the
    /// library scan, wait for it, then run the sync — so no part of it depends on a browser staying
    /// open. The POST starts the job and returns; the UI polls <see cref="Snapshot"/> and is a
    /// spectator, never the driver.
    ///
    /// <para>All three phases live here because owning only the last one is not enough. The sync was
    /// moved server-side first (2026-08-14) while the scan and the SEQUENCING stayed in the browser;
    /// a tab closed during the twelve-minute scan then stranded the run silently — the scan finished,
    /// the sync was never asked for, and nothing in the DB or the UI said so. A job that can be
    /// abandoned half-way by a closed laptop is not a background job.</para>
    ///
    /// <para>Single-flight: one run at a time, ever — a second start request while one runs is
    /// answered "already running" and the caller just follows the run in flight. State is one run
    /// deep (the last finished report/error), held in memory: a pod restart forgets it, which the
    /// status endpoint reports honestly as "no result available" rather than inventing an outcome.</para>
    /// </summary>
    public class JellyfinSyncRunner
    {
        /// <summary>How long to wait for Jellyfin's scan before giving up on it and syncing anyway.
        /// A full library scan runs ~12 minutes; the ceiling exists so a wedged scan degrades to "we
        /// synced what Jellyfin already knew" instead of a job that never ends.</summary>
        private static readonly TimeSpan ScanTimeout = TimeSpan.FromMinutes(45);

        private static readonly TimeSpan ScanPollInterval = TimeSpan.FromSeconds(5);

        /// <summary>The scan is asynchronous on Jellyfin's side too: right after the trigger the task
        /// may still read Idle. Treat "never seen running" as done only after this long, so the wait
        /// neither hangs on a no-op scan nor races past a real one.</summary>
        private static readonly TimeSpan ScanStartGrace = TimeSpan.FromSeconds(30);

        private readonly IServiceScopeFactory scopeFactory;
        private readonly ILogger<JellyfinSyncRunner> logger;
        private readonly object gate = new();

        private bool running;
        private DateTime? startedUtc;
        private DateTime? finishedUtc;
        private JellyfinSyncReport? lastReport;
        private string? lastError;
        private string? phase;
        private bool includeScan;

        public JellyfinSyncRunner(IServiceScopeFactory scopeFactory, ILogger<JellyfinSyncRunner> logger)
        {
            this.scopeFactory = scopeFactory;
            this.logger = logger;
        }

        /// <summary>
        /// Starts the job unless one is already in flight. Returns false when it is — the caller polls
        /// the existing run instead of stacking a second one. <paramref name="withScan"/> asks
        /// Jellyfin to re-scan the disk first; pass false to sync against the library as it stands
        /// (the scan just ran, and re-reading 27k items to learn nothing is twelve wasted minutes).
        /// </summary>
        public bool TryStart(string? startedBy, bool withScan = true)
        {
            lock (gate)
            {
                if (running) return false;
                running = true;
                startedUtc = DateTime.UtcNow;
                finishedUtc = null;
                lastReport = null;
                lastError = null;
                includeScan = withScan;
                phase = withScan ? "starting the Jellyfin scan" : "starting";
            }
            logger.LogInformation("Background Jellyfin sync started (by {User}, scan={Scan})", startedBy ?? "?", withScan);
            _ = Task.Run(RunOnceAsync);
            return true;
        }

        private void SetPhase(string? p) { lock (gate) phase = p; }

        /// <summary>
        /// Phase 1+2, server-side: ask Jellyfin to scan, then wait for the task to go quiet. Failures
        /// here are reported but never fatal — an unreachable or wedged scan still leaves a library
        /// worth syncing, and refusing to sync would be the worse outcome. Returns a note for the
        /// report when something went wrong, else null.
        /// </summary>
        private async Task<string?> RunScanPhaseAsync(JellyfinApi jellyfin, CancellationToken cancel)
        {
            try
            {
                SetPhase("asking Jellyfin to scan the library");
                await jellyfin.TriggerLibraryScanAsync();
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Jellyfin scan trigger failed — syncing against the library as it stands");
                return "Could not start a Jellyfin scan (" + ex.Message + "); synced against the library as it stood.";
            }

            var deadline = DateTime.UtcNow + ScanTimeout;
            var startedWaiting = DateTime.UtcNow;
            bool seenRunning = false;
            int consecutiveFailures = 0;

            while (DateTime.UtcNow < deadline && !cancel.IsCancellationRequested)
            {
                await Task.Delay(ScanPollInterval, cancel);
                try
                {
                    var st = await jellyfin.GetScanTaskStateAsync();
                    consecutiveFailures = 0;
                    if (st.IsRunning)
                    {
                        seenRunning = true;
                        SetPhase($"Jellyfin scanning{(st.Progress != null ? $" ({st.Progress:0}%)" : "")}");
                        continue;
                    }
                    // Idle. Done if we watched it run; otherwise give it a moment to actually start.
                    if (seenRunning) return null;
                    if (DateTime.UtcNow - startedWaiting > ScanStartGrace) return null;
                }
                catch (Exception ex)
                {
                    // One gateway hiccup among hundreds of polls must not abandon the run; the scan is
                    // unaffected by whether we can see it.
                    consecutiveFailures++;
                    logger.LogWarning(ex, "Jellyfin scan-status poll failed ({N} consecutive)", consecutiveFailures);
                    if (consecutiveFailures >= 10)
                        return "Lost contact with Jellyfin while watching the scan; synced anyway.";
                }
            }
            return seenRunning
                ? $"Jellyfin's scan was still running after {ScanTimeout.TotalMinutes:0} minutes; synced against what it had indexed so far."
                : null;
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

                string? scanNote = null;
                if (includeScan)
                {
                    var jellyfin = scope.ServiceProvider.GetRequiredService<JellyfinApi>();
                    scanNote = await RunScanPhaseAsync(jellyfin, CancellationToken.None);
                }

                SetPhase("linking files to the site");
                var sync = scope.ServiceProvider.GetRequiredService<JellyfinSyncService>();
                var rep = await sync.RunAsync(dryRun: false, progress: SetPhase);
                if (scanNote != null) rep.ScanNote = scanNote;
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
