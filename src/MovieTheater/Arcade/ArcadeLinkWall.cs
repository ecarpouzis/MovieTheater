using System;

namespace MovieTheater.Arcade
{
    /// <summary>
    /// The wall-seed arithmetic (worker patch 0050) behind <c>POST /API/Arcade/Internal/LinkWall</c>: the
    /// congestion wall the worker's ABR may assume for a device before its room has measured one.
    ///
    /// <para>wall = max(SustainedKbps) over that user+device's DIRECT rows on the room's codec within
    /// <see cref="Ttl"/>, × <see cref="Headroom"/>, clamped to [<see cref="MinKbps"/>, <see cref="MaxKbps"/>];
    /// 0 when there is no qualifying row. MAX, not warm start's min: a wall is a ceiling the room creeps
    /// toward, so too high costs one collision that re-measures it (any measured cut replaces a seeded wall),
    /// while too low would pin a good link under it. ×1.15 because SustainedKbps is the floor of a healthy run
    /// (the link carried at least that), not its capacity: 34MB5J sustained 13108 against GCC capacity reads
    /// of 12.4–14.3 Mbps.</para>
    /// </summary>
    public static class ArcadeLinkWall
    {
        public static readonly TimeSpan Ttl = TimeSpan.FromHours(24);
        public const double Headroom = 1.15;
        public const int MinKbps = 5000;
        public const int MaxKbps = 40000;

        /// <summary>The wall for a device whose qualifying rows peak at <paramref name="maxSustainedKbps"/>
        /// (null or ≤ 0 = no history → 0, no seed).</summary>
        public static int FromSustained(int? maxSustainedKbps) =>
            maxSustainedKbps is int s && s > 0 ? Math.Clamp((int)(s * Headroom), MinKbps, MaxKbps) : 0;
    }
}
