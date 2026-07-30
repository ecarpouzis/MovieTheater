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
    /// there is nothing worth migrating a schema for. The cost is that <see cref="Age"/> is unknown right
    /// after a restart, which is exactly why the UI treats "no report yet" as its own warning rather than
    /// as good news — see <see cref="StaleAfter"/>.</para>
    /// </summary>
    public static class PatchedArtifactAlerts
    {
        /// <summary>No report within this window means the WATCHDOG itself is the problem (task dead,
        /// script broken, Ziggy down). A guard that goes quiet must never read as "all clear", so the UI
        /// escalates on staleness too. 30-minute cadence + generous slack for a slow hashing pass.</summary>
        public static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(95);

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
                    var age = receivedUtc.HasValue ? DateTime.UtcNow - receivedUtc.Value : (TimeSpan?)null;
                    return new Snapshot(
                        Reported: receivedUtc.HasValue,
                        Ok: ok,
                        FindingCount: findingCount,
                        ReceivedUtc: receivedUtc,
                        Age: age,
                        // Never-reported counts as stale: it means we have no evidence either way, and
                        // "no evidence" must not be rendered as a green light.
                        Stale: !receivedUtc.HasValue || age > StaleAfter,
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
            bool Stale,
            string? PayloadJson);
    }
}
