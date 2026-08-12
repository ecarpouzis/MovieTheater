namespace MovieTheater.Db
{
    /// <summary>
    /// Where <see cref="PhotoAsset.TakenAt"/> came from (photos-plan.md §2.7). The value is kept
    /// beside the date because a scanned print dated from its folder name and a phone photo dated
    /// from EXIF are not the same claim, and the curation UI must be able to say which it is holding.
    ///
    /// <para>File mtime is deliberately absent: copies reset it, so it is recorded on the row
    /// (<see cref="PhotoAsset.FileModifiedUtc"/>) but never promoted to a taken-date.</para>
    /// </summary>
    public enum TakenAtSource
    {
        /// <summary>No date established yet — the timeline's date-unknown shelf (§2.7).</summary>
        Unknown = 0,

        /// <summary>EXIF DateTimeOriginal. ⚠ Never assigned to an image identified as a scan: scanners
        /// stamp their own capture date, which would date a 1980s print to the day it was digitized.</summary>
        Exif = 1,

        /// <summary>Google Takeout's per-item sidecar photoTakenTime, converted to wall-clock (§2.10).</summary>
        GoogleSidecar = 2,

        /// <summary>Parsed out of the file name (IMG_20140312_*, "… 7-4-2010").</summary>
        FilenameParsed = 3,

        /// <summary>Inferred from a year/date hint in the containing folder name.</summary>
        FolderInferred = 4,

        /// <summary>A human typed it. Outranks every automatic source and is never overwritten by one.</summary>
        Manual = 5,

        /// <summary>A human's circa range rather than a date — read with
        /// <see cref="PhotoAsset.YearMin"/>/<see cref="PhotoAsset.YearMax"/>.</summary>
        Estimated = 6,

        /// <summary>
        /// A video container's <c>creation_time</c>, read by ffprobe (§2.3). Like GPS it is a TRUE UTC
        /// instant, so it takes §2.7's conversion path: the raw instant is kept in
        /// <see cref="PhotoAsset.TakenAtUtcRaw"/> and <see cref="PhotoAsset.TakenAt"/> holds the wall
        /// clock derived from it.
        ///
        /// <para>Its own value rather than <see cref="Exif"/> because it is a different claim with a
        /// different failure mode: containers routinely carry an UNSET field that surfaces as the
        /// QuickTime 1904 epoch, and a reader deciding whether to trust a date needs to know which kind
        /// of stamp produced it. Adding a value to an existing int column is a schema change of exactly
        /// zero bytes (the Phase 4 precedent) against a database shared with production.</para>
        /// </summary>
        VideoContainer = 7,
    }
}
