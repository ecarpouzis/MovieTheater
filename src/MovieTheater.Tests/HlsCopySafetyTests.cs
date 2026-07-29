using MovieTheater.Streaming;
using Xunit;

namespace MovieTheater.Tests
{
    /// <summary>
    /// The copy-vs-force-encode decision (docs/transcode-restart-freeze-plan.md). This governs the quality
    /// and GPU cost of most of the library — ungated, the spacing rule alone fires on ~67% of h264 files —
    /// so the truth table is pinned here. Loosening any of these is a deliberate decision, not a refactor.
    ///
    /// Since 2026-07-29 the patched Jellyfin on Ziggy segments a video-copied session at every source
    /// keyframe (anchor-free, restart-exact) — but only for files whose complete ffprobe keyframe list
    /// has been backfilled (MediaFile.JfKeyframesUtc stamped). Stamped files are exempt from the
    /// force-encode entirely; the spacing rule remains for the unstamped rest, whose playlists still lie.
    /// </summary>
    public class HlsCopySafetyTests
    {
        private const double LongGop = 8.6;    // measured on the Blu-ray remux that exposed the bug
        private const double ShortGop = 2.0;   // typical encode, comfortably inside the 6s copy segment

        // The bug: a mid-file join on a long-GOP source that Jellyfin would copy, in a container whose
        // playlist is a uniform-grid guess. A restart writes segments that disagree with the playlist
        // and the picture freezes while audio drains.
        [Fact]
        public void ForcesEncode_OnMidFileJoin_WhenSpacingExceedsTheCopySegment()
            => Assert.True(HlsCopySafety.ShouldForceEncode(false, wouldCopy: true, joinsMidFile: true, LongGop, hasExactCopySegmentation: false));

        // The exemption: a backfilled file gets a per-keyframe (ffprobe-complete) playlist and hls_time 0
        // cutting, so any restart reproduces identical segments — long GOPs and mid-file joins included.
        // Copy stays lossless and GPU-free.
        [Fact]
        public void AllowsCopy_WhenExactlySegmented_EvenOnMidFileJoinWithLongSpacing()
            => Assert.False(HlsCopySafety.ShouldForceEncode(false, wouldCopy: true, joinsMidFile: true, LongGop, hasExactCopySegmentation: true));

        // The gate. A from-the-start session does still get restarted at the playhead, but it recovers —
        // measured 4 and 7 restarts on ~10s-GOP copies with no complaint, against 58 on the mid-file join
        // that stormed. Copy is lossless and costs no GPU, so those few recoverable restarts are the
        // cheaper side of the trade.
        [Fact]
        public void AllowsCopy_FromTheStart_EvenWithLongSpacing()
            => Assert.False(HlsCopySafety.ShouldForceEncode(false, wouldCopy: true, joinsMidFile: false, LongGop, hasExactCopySegmentation: false));

        // Spacing inside the segment length is safe to copy however the session opened.
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void AllowsCopy_WhenSpacingFitsTheSegment(bool joinsMidFile)
            => Assert.False(HlsCopySafety.ShouldForceEncode(false, wouldCopy: true, joinsMidFile, ShortGop, hasExactCopySegmentation: false));

        // Never probed. Forcing on null would sweep in the whole unprobed backlog; the client's
        // ForceTranscode escalation is the backstop instead.
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void AllowsCopy_WhenSpacingIsUnknown(bool joinsMidFile)
            => Assert.False(HlsCopySafety.ShouldForceEncode(false, wouldCopy: true, joinsMidFile, null, hasExactCopySegmentation: false));

        // Jellyfin was already going to encode (codec/bitrate/subtitle burn-in); the rule adds nothing.
        [Fact]
        public void DoesNotForce_WhenTheSessionWasNeverGoingToCopy()
            => Assert.False(HlsCopySafety.ShouldForceEncode(false, wouldCopy: false, joinsMidFile: true, LongGop, hasExactCopySegmentation: false));

        // The client escalation must still win outright — it's the backstop for every case the server
        // can't see, including unprobed files, a seek on a from-the-start copy session, and anything
        // odd a backfilled file still manages to do.
        [Theory]
        [InlineData(true, true, LongGop, true)]
        [InlineData(true, true, LongGop, false)]
        [InlineData(false, false, ShortGop, false)]
        [InlineData(false, false, null, true)]
        public void ClientEscalation_ForcesEncode_Regardless(bool wouldCopy, bool joinsMidFile, double? spacing, bool hasExactCopySegmentation)
            => Assert.True(HlsCopySafety.ShouldForceEncode(true, wouldCopy, joinsMidFile, spacing, hasExactCopySegmentation));

        // Exactly at the boundary is copyable: segments are cut at 6s and a keyframe lands on each one.
        [Fact]
        public void AllowsCopy_AtExactlyTheSegmentLength()
            => Assert.False(HlsCopySafety.ShouldForceEncode(
                false, wouldCopy: true, joinsMidFile: true, HlsCopySafety.CopySegmentSeconds, hasExactCopySegmentation: false));

        // The constant is load-bearing: it must stay the COPY path's -hls_time 6, not the encode path's 3.
        // (On the patched server a backfilled copy runs -hls_time 0, but this constant only gates the
        // unstamped files, which still run 6.)
        [Fact]
        public void CopySegmentLength_IsTheCopyPathsHlsTime()
            => Assert.Equal(6.0, HlsCopySafety.CopySegmentSeconds);
    }
}
