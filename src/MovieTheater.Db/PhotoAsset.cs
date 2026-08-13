using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace MovieTheater.Db
{
    /// <summary>
    /// One file in the family photo collection — photo or video (photos-plan.md §3). This table is the
    /// spine of the vertical: person tags, hand-set dates, album membership and dupe master picks all
    /// hang off these ids, and per §2.11 that curation is irreplaceable human labor.
    ///
    /// <para><b>Identity rule (§2.5): content is identity, path is location.</b> <see cref="Path"/> is
    /// unique but MUTABLE — when the inventory walk finds a path gone and an unfamiliar path present in
    /// the same run, it re-pairs them by content (<see cref="Sha256"/>, falling back to filename+size
    /// before hashes exist) and re-points <see cref="Path"/> on the EXISTING row, preserving the id and
    /// everything attached to it. A row is born only when no missing row matches its content, and
    /// ambiguous pairings go to a review list rather than being applied. <see cref="Sha256"/> is
    /// therefore nullable: skeleton rows exist from the walk, and the hash pass fills it later.</para>
    ///
    /// <para><b>Nothing here is ever a file operation.</b> Hiding, master picks, dupe merges and
    /// curation are rows and flags; the pipeline never writes, renames, moves or deletes under the
    /// collection root (§6). A vanished file gets <see cref="MissingSinceUtc"/>, never a DELETE —
    /// the same stance as <see cref="MediaFile"/> and <see cref="MusicTrack"/>.</para>
    ///
    /// <para><b>Privacy invariant (§6): this table joins nothing global.</b> No OData entity set, no
    /// site-wide search index, no AI-insight/recommendation/channel input, no poster-mosaic or landing
    /// surface. It is reachable only through the family-gated <c>/API/Photos</c> routes.</para>
    /// </summary>
    [Table("PhotoAsset")]
    public class PhotoAsset
    {
        [Key]
        public int Id { get; set; }

        /// <summary>
        /// Path relative to the configured photo root, forward slashes — the walk's upsert key and what
        /// a capability token carries (§2.2), so the gateway joins it onto its OWN mount of the same
        /// share and no drive-letter specifics leak into the DB (the <see cref="MusicTrack.RelativePath"/>
        /// precedent). Mutable by design; see the class remarks.
        /// </summary>
        /// <remarks>Capped at 850 characters because this column carries a UNIQUE index and SQL Server's
        /// nonclustered index key tops out at 1700 bytes — nvarchar(850) is exactly that budget.</remarks>
        [MaxLength(850)]
        public string Path { get; set; } = default!;

        public long SizeBytes { get; set; }

        /// <summary>Last-write time from the filesystem. Recorded for change detection (path+size+mtime
        /// unchanged ⇒ the walk short-circuits), NEVER trusted as a taken-date — copies reset it (§2.7).</summary>
        public DateTime FileModifiedUtc { get; set; }

        /// <summary>Photo vs. video, decided by extension during the cheap inventory pass.</summary>
        public PhotoAssetKind Kind { get; set; }

        /// <summary>Durable content identity (§2.5) and the exact-dupe key (§2.6); also the Google mesh's
        /// first-choice matcher (§2.10). Null until the hash pass — its own queue, because it re-reads
        /// every byte off the NAS.</summary>
        [MaxLength(64)]
        public string? Sha256 { get; set; }

        /// <summary>Perceptual hash (photos: ImageSharp; videos: a mid-point frame). Near-dupe grouping
        /// buckets on its prefix and compares by Hamming distance (§2.6). Null until the hash pass.</summary>
        public long? PHash { get; set; }

        /// <summary>Difference hash — the cheaper second opinion beside <see cref="PHash"/>.</summary>
        public long? DHash { get; set; }

        public int? Width { get; set; }

        public int? Height { get; set; }

        /// <summary>Videos only.</summary>
        public double? DurationSec { get; set; }

        /// <summary>
        /// When the photo was taken, as NAIVE LOCAL WALL-CLOCK (§2.7) — no offset, deliberately. EXIF
        /// carries no timezone, and a family timeline must group by the wall clock the moment happened
        /// on ("Christmas morning" must not land on Dec 24 through UTC math). Null = date unknown, which
        /// the timeline renders as its own shelf rather than scattering at epoch 0.
        /// </summary>
        public DateTime? TakenAt { get; set; }

        /// <summary>The true UTC instant when a source supplied one (Takeout's photoTakenTime, video
        /// container timestamps) BEFORE conversion to wall-clock. Kept so the conversion — GPS timezone
        /// when present, else the configured home timezone — stays revisitable. The two representations
        /// are never mixed into one column.</summary>
        public DateTime? TakenAtUtcRaw { get; set; }

        /// <summary>How much <see cref="TakenAt"/> is worth (§2.7).</summary>
        public TakenAtSource TakenAtSource { get; set; }

        /// <summary>Circa bounds for undated scans ("late 80s"). Set with
        /// <see cref="TakenAtSource.Estimated"/>; a tagged person's birth year only ever HINTS these
        /// bounds to the human, it never writes them.</summary>
        public int? YearMin { get; set; }

        public int? YearMax { get; set; }

        public double? GpsLat { get; set; }

        public double? GpsLon { get; set; }

        /// <summary>Human-readable place ("city, state") from offline reverse geocoding (§2.4).</summary>
        [MaxLength(256)]
        public string? LocationLabel { get; set; }

        public PhotoLocationSource LocationSource { get; set; }

        [MaxLength(128)]
        public string? CameraMake { get; set; }

        [MaxLength(128)]
        public string? CameraModel { get; set; }

        /// <summary>
        /// Whether a browser can display the ORIGINAL file (JPEG/PNG/WebP/GIF yes; HEIC/TIFF/RAW no).
        /// One column, decided at ingest, that the token minter reads (§2.2): renderable originals get
        /// deep-zoom straight from <c>PhotoOriginal</c>, the rest get a third <c>zoom</c> derivative and
        /// <c>PhotoOriginal</c> stays download-only.
        /// </summary>
        public bool OriginalRenderable { get; set; }

        /// <summary>The RAW EXIF/ffprobe readout, verbatim. Derived scalars above can always be
        /// recomputed from this; re-reading the file off the NAS to get it back cannot be made cheap,
        /// so the measurement is persisted rather than thrown away (§2.5).</summary>
        public string? RawMetadataJson { get; set; }

        /// <summary>Curation flag (§2.9): excluded from timeline and albums, still present in the folder
        /// view. Auto-SUGGESTED for screenshot/misc piles at ingest, confirmed by a human batch-wise.
        /// Also what collapses a dupe group's non-masters out of browse.</summary>
        public bool Hidden { get; set; }

        /// <summary>
        /// Which shelf this asset lives on (§2.12, Phase 7). <see cref="PhotoShelf.Timeline"/> is the
        /// family record; <see cref="PhotoShelf.Archive"/> is the Gallery — art and memes, off the
        /// timeline but browsable by every family member.
        ///
        /// <para>Orthogonal to <see cref="Hidden"/> and composed with it: shelf decides WHICH SECTION,
        /// hidden decides WHETHER A NON-ADMIN MAY SEE IT AT ALL. Hidden beats everything, so an
        /// archived-and-hidden asset is admin-only wherever it appears.</para>
        ///
        /// <para><b>Moves are group-coherent</b> (§2.12): shelving any member of a settled duplicate
        /// group shelves the whole group, because a collapsed group is ONE photograph on the browse
        /// surfaces and half of it changing section would make the card vanish from both.</para>
        /// </summary>
        public PhotoShelf Shelf { get; set; }

        /// <summary>Marks the ingest run that created this row (the <see cref="Movie.ReviewBatch"/>
        /// convention): bulk inserts stay reviewable, and the timeline can quarantine a run until it
        /// is approved.</summary>
        [MaxLength(128)]
        public string? IngestBatch { get; set; }

        /// <summary>Item id in the DEDICATED family Jellyfin library (§2.3), stamped by
        /// <c>photos-sync-jellyfin</c>. The movie-side sync excludes that library entirely, so a family
        /// video can never reach a movie-site surface.</summary>
        [MaxLength(64)]
        public string? JellyfinItemId { get; set; }

        /// <summary>Mapping id into the disposable Immich sidecar (§2.4). Re-derivable by path: the
        /// Immich database can be dropped and rebuilt without losing anything of ours.</summary>
        [MaxLength(64)]
        public string? ImmichAssetId { get; set; }

        public DateTime FirstSeenUtc { get; set; }

        /// <summary>Set when the walk can no longer find the file; cleared if it reappears (or if the
        /// row is re-paired to a new path). Never a deletion.</summary>
        public DateTime? MissingSinceUtc { get; set; }

        // ── Ingest queue bookkeeping (§2.5). Each pass is its own resumable queue, and a queue needs a
        // predicate the database can answer: "rows this pass has not stamped yet". The stamp is written
        // whether the pass succeeded or failed (with the reason in <see cref="IngestError"/>), because a
        // queue whose failures stay in it is an infinite retry rather than a job that terminates.
        // Re-running a failure is an explicit --retry-errors, never the default.

        /// <summary>When the metadata pass last read this file. Null ⇒ it is in the metadata queue.</summary>
        public DateTime? MetadataUpdatedUtc { get; set; }

        /// <summary>When the hash pass last read this file's bytes. Null ⇒ it is in the hash queue.</summary>
        public DateTime? HashUpdatedUtc { get; set; }

        /// <summary>When the thumb pass last ran for this row. Null ⇒ it is in the thumb queue.</summary>
        public DateTime? ThumbsUpdatedUtc { get; set; }

        /// <summary>Outcome of the thumb pass; see <see cref="PhotoThumbState"/>.</summary>
        public PhotoThumbState ThumbState { get; set; }

        /// <summary>
        /// The content key the emitted derivatives are named with — cache paths are
        /// <c>{id/1000}/{id}-{ThumbKey}-{size}.webp</c> (§2.2: "keyed by asset id + content hash"). Held
        /// on the row so a token can be minted without touching the cache directory, and so a re-ingest
        /// of changed bytes produces a DIFFERENT name: the browser's copy of the old URL cannot then be
        /// served for the new file.
        /// </summary>
        [MaxLength(32)]
        public string? ThumbKey { get; set; }

        /// <summary>Which derivatives exist for <see cref="ThumbKey"/>, comma-separated
        /// (<c>grid,view</c> or <c>grid,view,zoom</c>). The zoom derivative is emitted only when
        /// <see cref="OriginalRenderable"/> is false (§2.2), so the minter reads this rather than
        /// re-deriving the rule.</summary>
        [MaxLength(64)]
        public string? ThumbVariants { get; set; }

        /// <summary>Last ingest failure for this row (pass name + message, truncated). Cleared by a
        /// successful pass. Surfaced by the admin ingest-status endpoint so a stalled tail is visible
        /// rather than silently skipped.</summary>
        [MaxLength(512)]
        public string? IngestError { get; set; }
    }
}
