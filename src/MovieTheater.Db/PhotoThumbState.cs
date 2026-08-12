namespace MovieTheater.Db
{
    /// <summary>
    /// What the thumb pass (photos-plan.md §2.5 phase 4) managed to emit for an asset. Recorded rather
    /// than inferred from the cache directory: the derivatives live on the gateway host, so "does a
    /// grid thumb exist" must be answerable by a query — otherwise the pass's own <c>remaining</c>
    /// count would cost a stat per row, and the UI could not decide what to render without one too.
    /// </summary>
    public enum PhotoThumbState
    {
        /// <summary>Not attempted yet — the thumb queue.</summary>
        Pending = 0,

        /// <summary>Derivatives written and named by <c>ThumbKey</c>.</summary>
        Ready = 1,

        /// <summary>Video. Deliberately deferred to Phase 5 (ffprobe/ffmpeg poster grabs); Phase 1
        /// carries videos as skeleton rows only, and this is the deterministic state the UI renders a
        /// film placeholder for rather than a broken image.</summary>
        VideoDeferred = 2,

        /// <summary>A still image in a container this build cannot DECODE (HEIC/HEIF/AVIF/RAW —
        /// MetadataExtractor reads their metadata, ImageSharp does not decode their pixels). The row is
        /// fully catalogued and hashed; only the derivatives are absent. Distinct from
        /// <see cref="Failed"/> because nothing went wrong — a decoder was simply not shipped.</summary>
        UnsupportedFormat = 3,

        /// <summary>The decode was attempted and threw (truncated/corrupt file). Stamped so the queue
        /// drains deterministically instead of retrying the same broken file forever; <c>IngestError</c>
        /// carries the reason and <c>--retry-errors</c> re-queues it.</summary>
        Failed = 4,
    }
}
