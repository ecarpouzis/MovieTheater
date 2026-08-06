using System;

namespace MovieTheater.Arcade
{
    /// <summary>
    /// "Is the arcade host's desktop session sitting on the physical console, or has someone left a
    /// remote desktop open?" — pushed here by Ziggy's arcade watchdog (check I) and read back by the
    /// lobby/room so players see WHY the stream got worse instead of blaming their own connection.
    ///
    /// <para>WHY THIS EXISTS: the emulators render into a real interactive Windows session. An attached
    /// RDP client replaces the physical displays with the RDP display, which refreshes at ~32 Hz, and a
    /// DISCONNECTED session leaves DWM stalled at a similar rate — either way window capture (the heavy
    /// lane) and the GPU pipeline behind it run well below 60 fps, with no error anywhere. It looks
    /// exactly like a bad network. The recovery already exists (<c>tscon /dest:console</c>, the
    /// "MovieTheater - Reattach Console" task); what was missing was anyone TELLING the players.</para>
    ///
    /// <para>DELIBERATELY IN-MEMORY, same shape and same reasoning as <see cref="PatchedArtifactAlerts"/>:
    /// a current-state signal with a heartbeat, not history. A pod restart self-heals on the next post.</para>
    ///
    /// <para>FAILS TO SILENCE, on purpose. If the watchdog stops posting we do NOT keep showing the last
    /// degraded state — see <see cref="Snapshot.Stale"/>. A warning banner that latches on because the
    /// reporter died would train everyone to ignore the banner, which costs more than the missed warning:
    /// this is a performance HINT, not a correctness alarm. Staleness is still reported in the payload so
    /// an admin surface can say "we have lost contact" rather than "everything is fine".</para>
    /// </summary>
    public static class ArcadeHostSession
    {
        /// <summary>The watchdog posts on every state CHANGE and otherwise heartbeats every ~5 minutes
        /// (it runs a 30 s cycle), so silence past this window means the reporter is the problem.</summary>
        public static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(12);

        /// <summary>How long after a recovery we still say so. Long enough that someone who opened the
        /// lobby right as the console came back learns the stream just got better; short enough that it
        /// isn't a permanent decoration.</summary>
        public static readonly TimeSpan RecoveredWindow = TimeSpan.FromMinutes(3);

        private static readonly object Gate = new();
        private static bool degraded;
        private static string? kind;          // console | remote | disconnected | unknown
        private static string? detail;        // human-readable, straight from the watchdog
        private static int? sessionId;
        private static bool recovering;       // the reattach task has been triggered, not yet confirmed
        private static DateTime? receivedUtc;
        private static DateTime? degradedSinceUtc;
        private static DateTime? recoveredUtc;

        /// <summary>Store the watchdog's latest session reading. Transitions are computed HERE, not on
        /// Ziggy: the watchdog is restarted by hand and by task recycles, and a reporter that has just
        /// started has no idea what the previous state was — so it would either invent a recovery or miss
        /// one. This process only forgets across a deploy, and a deploy resets us to "nothing reported",
        /// which shows no banner at all rather than a wrong one.</summary>
        public static void Record(bool isDegraded, string? reportedKind, string? reportedDetail, int? reportedSessionId, bool isRecovering)
        {
            lock (Gate)
            {
                var now = DateTime.UtcNow;
                var wasDegraded = receivedUtc.HasValue && degraded;

                if (isDegraded && !wasDegraded) degradedSinceUtc = now;
                if (!isDegraded && wasDegraded) recoveredUtc = now;
                if (isDegraded) recoveredUtc = null;   // a new degradation retires the old "recovered" note

                degraded = isDegraded;
                kind = reportedKind;
                detail = reportedDetail;
                sessionId = reportedSessionId;
                recovering = isRecovering;
                receivedUtc = now;
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
                    var stale = receivedUtc.HasValue && age > StaleAfter;
                    var recentlyRecovered = recoveredUtc.HasValue && now - recoveredUtc.Value <= RecoveredWindow;

                    return new Snapshot(
                        Reported: receivedUtc.HasValue,
                        // Stale never presents as degraded — the whole fail-to-silence rule in one line.
                        Degraded: degraded && !stale,
                        Kind: kind,
                        Detail: detail,
                        SessionId: sessionId,
                        Recovering: recovering && !stale,
                        RecentlyRecovered: recentlyRecovered && !stale,
                        DegradedSinceUtc: degradedSinceUtc,
                        RecoveredUtc: recoveredUtc,
                        ReceivedUtc: receivedUtc,
                        Age: age,
                        Stale: stale);
                }
            }
        }

        public sealed record Snapshot(
            bool Reported,
            bool Degraded,
            string? Kind,
            string? Detail,
            int? SessionId,
            bool Recovering,
            bool RecentlyRecovered,
            DateTime? DegradedSinceUtc,
            DateTime? RecoveredUtc,
            DateTime? ReceivedUtc,
            TimeSpan? Age,
            bool Stale);
    }
}
