using System;

namespace MovieTheater.Arcade
{
    /// <summary>
    /// The live "are our patched binaries still patched?" signal, pushed here by Ziggy's arcade
    /// watchdog (check H -> <c>scripts/verify-patched-artifacts.ps1</c>) and read back by the admin
    /// UI so a revert produces a POPUP instead of a line in a log file nobody opens.
    ///
    /// <para>WHY THIS EXISTS: a dozen binaries we run are not what upstream ships (hand-built and
    /// byte-patched cores, cores pinned to one buildbot nightly, and 3 patched Jellyfin DLLs), and two
    /// mechanisms revert them with NO error and NO log line — the worker's <c>cores.repo.sync</c> pulls
    /// the libretro nightly on every start and silently installs STOCK over any core file that has gone
    /// missing, and any stock Jellyfin upgrade overwrites its 3 DLLs. Both failures are invisible until
    /// someone notices a bug that was fixed weeks ago has come back.</para>
    ///
    /// <para>DELIBERATELY IN-MEMORY, not a table. This is a CURRENT-STATE signal with a heartbeat, not
    /// history: the watchdog re-posts every 30 minutes, so a pod restart self-heals within one cycle and
    /// there is nothing worth migrating a schema for. The cost is that <see cref="Snapshot.Age"/> is
    /// unknown right after a restart — see <see cref="Snapshot.Warming"/> for why that is a NON-event.</para>
    /// </summary>
    public static class PatchedArtifactAlerts
    {
        /// <summary>No report within this window means the WATCHDOG itself is the problem (task dead,
        /// script broken, Ziggy down). 30-minute cadence + generous slack for a slow hashing pass.</summary>
        public static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(95);

        /// <summary>When THIS process started. Load-bearing: because the report is in-memory, every deploy
        /// resets us to "never reported", and the site is deployed several times a day. Without this clock
        /// we cannot tell "the guard is dead" from "we came up 90 seconds ago and the next 30-minute post
        /// hasn't landed yet" — and we used to report both as an alarm, which fired a scary popup at an
        /// admin after essentially every deploy. Silence is only evidence once we have been up long enough
        /// to have HEARD something.</summary>
        private static readonly DateTime ProcessStartUtc = DateTime.UtcNow;

        private static readonly object Gate = new();
        private static string? payloadJson;
        private static DateTime? receivedUtc;
        private static int findingCount;
        private static bool ok;

        /// <summary>Store the verifier's latest report. <paramref name="rawJson"/> is passed through
        /// verbatim so the UI can show exactly what the checker said (paths, hashes, status per artifact)
        /// without this layer needing to understand the finding shape.</summary>
        public static void Record(bool isOk, int findings, string? rawJson)
        {
            lock (Gate)
            {
                ok = isOk;
                findingCount = findings;
                payloadJson = rawJson;
                receivedUtc = DateTime.UtcNow;
            }
        }

        public static Snapshot Current
        {
            get
            {
                lock (Gate)
                {
                    var now = DateTime.UtcNow;
                    var age = receivedUtc.HasValue ? now - receivedUtc.Value : (TimeSpan?)null;
                    var uptime = now - ProcessStartUtc;

                    // Have not heard yet, but have not been up long enough to expect to. This is the
                    // ordinary post-deploy state, NOT a fault: it resolves itself on the watchdog's next
                    // 30-minute post with nobody doing anything.
                    var warming = !receivedUtc.HasValue && uptime <= StaleAfter;

                    return new Snapshot(
                        Reported: receivedUtc.HasValue,
                        Ok: ok,
                        FindingCount: findingCount,
                        ReceivedUtc: receivedUtc,
                        Age: age,
                        Uptime: uptime,
                        Warming: warming,
                        // Silence we should NOT be hearing: either a report went stale, or we have been up
                        // longer than a full report window and never heard anything at all. Both mean the
                        // watchdog is the problem. Warming deliberately does not qualify.
                        Stale: receivedUtc.HasValue ? age > StaleAfter : uptime > StaleAfter,
                        PayloadJson: payloadJson);
                }
            }
        }

        public sealed record Snapshot(
            bool Reported,
            bool Ok,
            int FindingCount,
            DateTime? ReceivedUtc,
            TimeSpan? Age,
            TimeSpan Uptime,
            bool Warming,
            bool Stale,
            string? PayloadJson);
    }
}
