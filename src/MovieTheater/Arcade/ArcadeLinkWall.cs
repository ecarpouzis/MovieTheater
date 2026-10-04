using System;

namespace MovieTheater.Arcade
{
    /// <summary>
    /// The wall-seed rule (worker patch 0050) behind <c>POST /API/Arcade/Internal/LinkWall</c>: the
    /// congestion wall the worker's ABR may assume for a device before its room has measured one.
    ///
    /// <para>wall = max(SustainedKbps) over that user+device's DIRECT rows on the room's codec within
    /// <see cref="Ttl"/>, × <see cref="Headroom"/>, clamped to [<see cref="MinKbps"/>, <see cref="MaxKbps"/>];
    /// 0 when there is no qualifying row. MAX, not warm start's min: a wall is a ceiling the room creeps
    /// toward, so too high costs one collision that re-measures it (any measured cut replaces a seeded wall),
    /// while too low would pin a good link under it. ×1.15 because SustainedKbps is the floor of a healthy run
    /// (the link carried at least that), not its capacity: 34MB5J sustained 13108 against GCC capacity reads
    /// of 12.4–14.3 Mbps.</para>
    ///
    /// <para><b>Only a device that COLLIDED is seeded (2026-10-04).</b> A sustained rate is what a room SENT, and
    /// a room sends what its content needs and its wall allows — it is a lower bound on the link, not a
    /// measurement of it. Seeding every device from it anchored good links to their own history: the wall held
    /// the next room under 1.15 × the last one, that room's sustained rate came out near the wall, and the
    /// device could climb only by the room's 250 kbps creep, session after session (a phone beside its router,
    /// 2026-10-04: seeded at 23362, 37 s pinned at 18349, never reached its 38657 ceiling in a 191 s room with
    /// no network-limited cut). So a seed now needs evidence that the network limited the device inside the
    /// window: a HARD descent (a run of cuts that dropped the room more than 30 %) on some qualifying row. A
    /// device with no such row climbs freely and the room measures its own wall if there is one; a genuinely
    /// constrained device pays one collision, then is seeded for <see cref="Ttl"/>.</para>
    ///
    /// <para><b>History from before <see cref="TrustedSinceUtc"/> is ignored.</b> Until worker patches 0053/0054
    /// the estimate ratcheted itself down on a perfect link and no lost packet was ever retransmitted, so every
    /// earlier row's sustained rate and descent counts describe those defects, not the device's link.</para>
    /// </summary>
    public static class ArcadeLinkWall
    {
        public static readonly TimeSpan Ttl = TimeSpan.FromHours(24);
        public const double Headroom = 1.15;
        public const int MinKbps = 5000;
        public const int MaxKbps = 40000;

        /// <summary>Rows created before this instant are not evidence of anything (worker patch 0054 went live
        /// 2026-10-04 16:25 UTC).</summary>
        public static readonly DateTime TrustedSinceUtc = new DateTime(2026, 10, 4, 16, 30, 0, DateTimeKind.Utc);

        /// <summary>The start of the lookup window at <paramref name="nowUtc"/>: <see cref="Ttl"/> back, but
        /// never before <see cref="TrustedSinceUtc"/>.</summary>
        public static DateTime WindowStart(DateTime nowUtc)
        {
            var since = nowUtc - Ttl;
            return since < TrustedSinceUtc ? TrustedSinceUtc : since;
        }

        /// <summary>The seed for a device whose qualifying rows peak at <paramref name="maxSustainedKbps"/> and
        /// total <paramref name="hardDescents"/> hard descents: 0 (no seed) unless it collided.</summary>
        public static int Seed(int? maxSustainedKbps, int hardDescents) =>
            hardDescents > 0 ? FromSustained(maxSustainedKbps) : 0;

        /// <summary>The wall arithmetic alone: <paramref name="maxSustainedKbps"/> × <see cref="Headroom"/>,
        /// clamped (null or ≤ 0 = no history → 0).</summary>
        public static int FromSustained(int? maxSustainedKbps) =>
            maxSustainedKbps is int s && s > 0 ? Math.Clamp((int)(s * Headroom), MinKbps, MaxKbps) : 0;
    }
}
