using System;
using System.Globalization;
using System.Text.RegularExpressions;

namespace MovieTheater.Music
{
    /// <summary>
    /// Can this LRC belong to a track of this length?
    ///
    /// <para>LRCLIB matches a lookup on the duration its UPLOADER recorded, which is metadata and says
    /// nothing about the timestamps in the file. When the two disagree we store lyrics timed against a
    /// different — usually longer — version, and every line then lands late by the difference.
    /// Reported 2026-08-17 on CHVRCHES' <i>Recover</i>: a 225.9 s track holding cues out to 4:14,
    /// so the pane bolded the 0:56 line while the song was most of a verse further on. The playhead
    /// was never wrong, which is exactly why it read as a player bug — the scrub bar and the highlight
    /// are driven off the same clock, so they agreed with each other and disagreed with the music.</para>
    ///
    /// <para><b>Two thresholds, and conflating them is a mistake that costs data.</b> A cue past the
    /// end is a SMELL: it may equally be a final cue written at the fade, a duration we measured a
    /// hair short, or a transcription that timestamps the outro. Proof of a WRONG VERSION is a
    /// mismatch too large to explain that way — and it is proportional, because a version really is a
    /// different length, while a sloppy tail is a couple of seconds no matter how long the song is.
    /// The first run of the repair pass used the smell as its licence to delete and cleared 72 good
    /// LRCs over 2-3 second tails (restored from backup); hence <see cref="IsVersionMismatch"/>,
    /// which is the only test allowed to authorise a write.</para>
    /// </summary>
    public static class MusicLyricsFit
    {
        /// <summary>A final cue may sit right on the end, and rounding plus a fade make a couple of
        /// seconds honest. Past this, something is at least worth looking at.</summary>
        public const double ToleranceSec = 2.0;

        /// <summary>Floor for "this cannot be the same recording". Under ten seconds is inside what a
        /// tail, a fade or a duration rounding can account for on a song of any length.</summary>
        public const double MismatchFloorSec = 10.0;

        /// <summary>…and the proportional term, because a longer song has more room for an honest
        /// tail and a wrong version is wrong in proportion. 5% of a 3:45 track is 11 s — the Recover
        /// case, at ~25 s, clears it comfortably.</summary>
        public const double MismatchFraction = 0.05;

        /// <summary>Timestamps must look like <c>[mm:ss]</c>, <c>[mm:ss.xx]</c> or <c>[mm:ss:xx]</c>.
        /// Metadata tags (<c>[ar:…]</c>, <c>[offset:…]</c>) carry no time and never match.</summary>
        private static readonly Regex CueRx =
            new(@"\[(\d+):(\d{1,2}(?:[.:]\d{1,3})?)\]", RegexOptions.Compiled);

        /// <summary>The latest cue in an LRC, in seconds; null when it carries no timestamps at all.
        /// Nothing guarantees an LRC is sorted, so this is a max rather than a read of the last line.</summary>
        public static double? LastCueSec(string? lrc)
        {
            if (string.IsNullOrEmpty(lrc)) return null;
            double last = -1;
            foreach (Match m in CueRx.Matches(lrc))
            {
                if (!int.TryParse(m.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var min))
                    continue;
                if (!double.TryParse(m.Groups[2].Value.Replace(':', '.'), NumberStyles.Float,
                        CultureInfo.InvariantCulture, out var sec))
                    continue;
                var t = min * 60 + sec;
                if (t > last) last = t;
            }
            return last < 0 ? null : last;
        }

        /// <summary>How far the last cue runs past the end of the track; null when there is nothing to
        /// measure (no cues, or no duration to measure against). Negative means it lands inside.</summary>
        public static double? OverhangSec(string? lrc, double? durationSec)
        {
            if (durationSec is not double d || d <= 0) return null;
            return LastCueSec(lrc) is not double last ? null : last - d;
        }

        /// <summary>The overhang past which these cues cannot be this recording's.</summary>
        public static double MismatchThresholdSec(double durationSec) =>
            Math.Max(MismatchFloorSec, durationSec * MismatchFraction);

        /// <summary>
        /// True when the cues could belong to a track this long — i.e. nothing hangs off the end by
        /// more than the tolerance. This is the SMELL, useful for auditing and for choosing between
        /// candidates. It must not be the licence for a destructive write; use
        /// <see cref="IsVersionMismatch"/> for that.
        ///
        /// <para>An unknown or zero duration has nothing to judge against and passes — refusing those
        /// would throw away good lyrics to enforce a test that cannot run.</para>
        /// </summary>
        public static bool CuesFit(string? lrc, double? durationSec) =>
            OverhangSec(lrc, durationSec) is not double over || over <= ToleranceSec;

        /// <summary>
        /// True when the overhang is too large to be anything but a different recording — the only
        /// test allowed to authorise replacing or clearing a stored LRC.
        /// </summary>
        public static bool IsVersionMismatch(string? lrc, double? durationSec) =>
            durationSec is double d && d > 0
            && OverhangSec(lrc, d) is double over
            && over > MismatchThresholdSec(d);
    }
}
