using System;
using System.Collections.Generic;
using System.Linq;

namespace MovieTheater.Arcade
{
    /// <summary>
    /// What a device's own <c>ArcadeLinkStat</c> history says about the two codecs, for the creator's Auto codec
    /// choice (arcade codec program B5, 2026-10-02).
    ///
    /// A device AVOIDS a codec it recently refused (Path "refused": it cannot receive it) or recently DROWNED on —
    /// a measured decode deficit for at least <see cref="StrongSeconds"/>, or one that forced the room to shrink —
    /// in any of its last three sessions on that codec. In these rows strong distress can only be a decode deficit
    /// (same-host sessions, where keyframe requests alone count as strong, are never stored), so the hint describes
    /// the device's DECODER, not its network. When both codecs look bad there is no hint: the browser's own probe
    /// decides, as it always did.
    /// </summary>
    public static class ArcadeCodecHint
    {
        /// <summary>How far back a device's history counts.</summary>
        public static readonly TimeSpan Window = TimeSpan.FromDays(30);

        /// <summary>Seconds of strong distress in one session that mark the codec as too heavy for the device.</summary>
        public const int StrongSeconds = 10;

        /// <param name="rowsNewestFirst">The device's rows, newest first: codec, path, strong-distress seconds,
        /// scale-downs caused.</param>
        /// <returns>"av1" or "h264" to avoid, or null for no opinion.</returns>
        public static string? CodecToAvoid(IEnumerable<(string? Codec, string? Path, int StrongTicks, int ScaleDowns)> rowsNewestFirst)
        {
            var rows = rowsNewestFirst.ToList();
            bool Bad(string codec) => rows
                .Where(r => r.Codec == codec)
                .Take(3)
                .Any(r => r.Path == "refused" || r.StrongTicks >= StrongSeconds || r.ScaleDowns > 0);
            var av1 = Bad("av1");
            var h264 = Bad("h264");
            return av1 == h264 ? null : (av1 ? "av1" : "h264");
        }
    }
}
