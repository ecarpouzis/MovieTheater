namespace MovieTheater.Streaming
{
    /// <summary>
    /// When an HLS session may let Jellyfin stream-copy the video, and when it must be forced to a real
    /// encode instead (docs/transcode-restart-freeze-plan.md).
    ///
    /// <para>Kept free of ASP.NET/EF dependencies so the test suite can link it directly — the decision
    /// governs the quality and GPU cost of most of the library, so its truth table is worth pinning.</para>
    /// </summary>
    public static class HlsCopySafety
    {
        /// <summary>
        /// The HLS segment length of the COPY path: every <c>-codec:v:0 copy</c> session in Jellyfin's
        /// FFmpeg logs runs <c>-hls_time 6</c>. Its <i>encode</i> sessions use 3, so don't "confirm" this
        /// against one of those. A source whose keyframes fall further apart than this cannot be safely
        /// copied across a restart.
        /// </summary>
        public const double CopySegmentSeconds = 6.0;

        /// <summary>
        /// Whether <c>StreamController.Start</c> must force a real video encode instead of letting
        /// Jellyfin stream-copy.
        ///
        /// <para>A copied stream can only be cut on the SOURCE's keyframes, so when those fall further
        /// apart than the segment length Jellyfin's <c>-start_number</c> bookkeeping (derived from assumed
        /// durations) disagrees with the segments ffmpeg actually wrote — differently on each mid-session
        /// restart. The restarted encoder renumbers underneath a playing decoder and the picture freezes
        /// while audio drains. A real encode emits its own keyframes on the segment boundary, so numbering
        /// and reality agree and there is nothing to renumber.</para>
        ///
        /// <para><b>Why <paramref name="joinsMidFile"/> gates it:</b> long spacing is only the
        /// precondition — what actually hurts is not a restart but a self-sustaining STORM of them.
        /// A from-the-start session does still get restarted at the playhead (measured in Jellyfin's
        /// FFmpeg logs: 4 restarts across 72 min on Muppet Treasure Island, 7 on James and the Giant
        /// Peach, both ~10 s GOP on the copy path) — it just recovers, because nothing is fighting the
        /// timeline. A mid-file join does not recover: the join lands up to one GOP early, the /tv drift
        /// corrector reads that as lag and seeks, each seek restarts the encoder at a new wrong offset,
        /// and it never converges — 58 restarts on NIMH, which is the freeze users actually reported.
        /// So the storm needs a mid-file join, and that is what this gates on.</para>
        ///
        /// <para>Ungated the rule fires on ~67% of h264 files, trading a large share of the library's
        /// quality and the GPU's headroom to suppress restarts that recover on their own. The accepted
        /// cost of the gate: a from-the-start session on a long-GOP source can still hiccup at each of
        /// those handful of restarts — which is exactly the behaviour that predates this fix and that
        /// nobody reported. The client's <c>ForceTranscode</c> escalation reopens the session as an
        /// encode if it ever does degenerate. Deliberately not keyed to /tv vs Watch: what matters is
        /// the start offset, and both can join mid-file (resume).</para>
        ///
        /// <para>Null <paramref name="keyframeIntervalSeconds"/> = never probed → don't force; the same
        /// client escalation is the backstop there.</para>
        /// </summary>
        public static bool ShouldForceEncode(
            bool forceTranscodeRequested, bool wouldCopy, bool joinsMidFile, double? keyframeIntervalSeconds)
            => forceTranscodeRequested
                || (wouldCopy && joinsMidFile && keyframeIntervalSeconds > CopySegmentSeconds);
    }
}
