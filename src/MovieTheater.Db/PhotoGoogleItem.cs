using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace MovieTheater.Db
{
    /// <summary>
    /// One item seen in a Google Takeout archive, and where it landed against the local library
    /// (photos-plan.md §2.10). Takeout is the only lane left: the Photos Library API lost third-party
    /// read access in 2025, so there is no API-driven mesh to build — archives arrive on a schedule and
    /// this table is what makes re-running over the next one idempotent.
    ///
    /// <para><b>Identity is (file name, taken time, size)</b> because Takeout sidecars carry no stable
    /// Google id. That triple is the upsert key, so next quarter's archive updates these rows instead
    /// of duplicating them, and an item once matched stays matched.</para>
    ///
    /// <para>The sidecar is worth keeping even for photos we already own: Google's own metadata is
    /// often RICHER than the media file's EXIF (some upload/download paths strip it). Backfills onto a
    /// matched asset follow the flag-but-write convention on conflict.</para>
    /// </summary>
    [Table("PhotoGoogleItem")]
    public class PhotoGoogleItem
    {
        [Key]
        public int Id { get; set; }

        /// <summary>The media file's name inside the archive — one third of the identity triple.</summary>
        [MaxLength(400)]
        public string TakeoutFileName { get; set; } = default!;

        /// <summary>Where it sat in the archive (album folder and all), for the review list to show
        /// context. Not part of identity: Takeout reshuffles its own layout between exports.</summary>
        [MaxLength(850)]
        public string? TakeoutRelativePath { get; set; }

        /// <summary>The sidecar's photoTakenTime as the true UTC instant it is — the second third of
        /// the identity triple. Converted to wall-clock only when written onto an asset (§2.7).</summary>
        public DateTime? TakenAtUtc { get; set; }

        /// <summary>Final third of the identity triple.</summary>
        public long? SizeBytes { get; set; }

        /// <summary>The per-item sidecar, verbatim — description, GPS, people, the lot. Parsed columns
        /// are re-derivable from it; a second pass must never need the archive back.</summary>
        public string? SidecarJson { get; set; }

        public int? MatchedPhotoAssetId { get; set; }

        [ForeignKey(nameof(MatchedPhotoAssetId))]
        public PhotoAsset? MatchedPhotoAsset { get; set; }

        public PhotoGoogleItemStatus Status { get; set; }

        /// <summary>Which rung of the cascade matched: "name+size", "sha256", "phash". Google re-encodes
        /// some media, so pixel similarity is the safety net — and knowing a match came from that rung
        /// rather than a hash is what makes a wrong match findable later.</summary>
        [MaxLength(32)]
        public string? MatchMethod { get; set; }

        /// <summary>
        /// The pHash Hamming distance a <c>phash</c> match was accepted at — null on every other rung,
        /// because a hash match has no distance (Phase 6).
        ///
        /// <para>Its presence IS the lower-confidence marker: the first two rungs prove identity (a name
        /// and a size, or 256 bits of it), while this one says "these look like the same picture to
        /// within N of 64 bits". A wrong pixel-similarity match is the only kind this pass can make that
        /// a human would ever have to undo, so the number that produced it is kept rather than the
        /// verdict alone.</para>
        /// </summary>
        public int? MatchDistance { get; set; }

        /// <summary>
        /// Comma-separated field names where the sidecar DISAGREED with the local row (Phase 6, §2.10's
        /// flag-but-write convention). Two shapes, and the difference matters:
        /// <list type="bullet">
        /// <item><c>takenAt-overwritten:&lt;source&gt;</c> — the sidecar WON (the local date came from a
        /// strictly weaker source, §2.7) and was written over; the flag records what it displaced.</item>
        /// <item><c>takenAt:&lt;source&gt;</c>, <c>gps</c>, <c>locationLabel</c> — the sidecar LOST to an
        /// equal-or-stronger local value; nothing was written and the disagreement is recorded here so
        /// it can be counted and reviewed.</item>
        /// </list>
        /// <para>A string rather than a table: the whole point is a countable review surface, and a
        /// GROUP BY over a short token list answers "how many dates does Google disagree with" without
        /// a join. Re-running the mesh rewrites it wholesale, so a resolved disagreement disappears on
        /// the next pass rather than accumulating forever.</para>
        /// </summary>
        [MaxLength(256)]
        public string? Disagreements { get; set; }

        /// <summary>
        /// Where the (opt-in, additive, non-overwriting) download lane copied this item — the ONE NAS
        /// write in the whole vertical (§2.10). Null until <see cref="PhotoGoogleItemStatus.Downloaded"/>.
        ///
        /// <para>Recorded rather than re-derived: the destination is a function of a config value the
        /// site's pods cannot see and of the sidecar's date, so "which file on disk is this Google item
        /// now" would otherwise be answerable only by re-reading the archive that produced it.</para>
        /// </summary>
        [MaxLength(850)]
        public string? DownloadedPath { get; set; }

        public DateTime FirstSeenUtc { get; set; }

        /// <summary>Bumped every time an archive re-presents this item; how a stale row from an old
        /// export is told apart from one the current archive still carries.</summary>
        public DateTime LastSeenUtc { get; set; }
    }
}
