using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace MovieTheater.Db
{
    /// <summary>
    /// A video file the Jellyfin sync found on disk that the DB doesn't track, persisted so the review
    /// tool can act on it instead of the finding dying in a report line. Two shapes: an
    /// <see cref="SyncCandidateKind.Upgrade"/> (evidence ties it to an existing movie whose file went
    /// missing — approving re-points that movie in place) and a <see cref="SyncCandidateKind.NewTitle"/>
    /// (its folder parses as a movie the library doesn't have — resolving details creates a quarantined
    /// <c>ReviewBatch</c> Movie row that flows through the normal review ingest). Rows are keyed by
    /// <see cref="Path"/> and upserted by each sync, so a candidate survives re-syncs without
    /// duplicating and a rejection is remembered.
    /// </summary>
    [Table("SyncCandidate")]
    public class SyncCandidate
    {
        [Key]
        public int Id { get; set; }

        public SyncCandidateKind Kind { get; set; }

        public SyncCandidateStatus Status { get; set; }

        /// <summary>DB-translated path of the untracked file (same form <see cref="MediaFile.Path"/> uses).</summary>
        [MaxLength(1024)]
        public string Path { get; set; } = default!;

        /// <summary>Jellyfin's item id for the file, refreshed each sync — what approval uses to pull
        /// full media detail without re-listing the library.</summary>
        [MaxLength(64)]
        public string? JellyfinItemId { get; set; }

        public long? SizeBytes { get; set; }

        // ── Upgrade evidence ──────────────────────────────────────────────────────────

        /// <summary>The movie this file appears to replace (Upgrade only).</summary>
        public int? TargetMovieId { get; set; }

        [ForeignKey(nameof(TargetMovieId))]
        public Movie? TargetMovie { get; set; }

        /// <summary>Which signal paired file and movie: "same-folder" | "same-size" | "title-match".</summary>
        [MaxLength(32)]
        public string? Signal { get; set; }

        /// <summary>The target movie's recorded (now dead) path at detection time, for display.</summary>
        [MaxLength(1024)]
        public string? OldPath { get; set; }

        // ── NewTitle parse + resolution ───────────────────────────────────────────────

        /// <summary>Title parsed from the movie folder name (quality tags and ordinals stripped).</summary>
        [MaxLength(512)]
        public string? ParsedTitle { get; set; }

        public int? ParsedYear { get; set; }

        /// <summary>IMDb id the detail-resolution pass settled on, when it found one.</summary>
        [MaxLength(16)]
        public string? ResolvedImdbId { get; set; }

        /// <summary>Why the last detail-resolution attempt failed, for the review UI; null when fine.</summary>
        [MaxLength(512)]
        public string? ResolutionError { get; set; }

        /// <summary>The quarantined Movie row a NewTitle resolution created (Status = Ingested).</summary>
        public int? CreatedMovieId { get; set; }

        [ForeignKey(nameof(CreatedMovieId))]
        public Movie? CreatedMovie { get; set; }

        /// <summary>Set when a reviewer hand-edited this row (retitle, pinned tt, reclassify). A
        /// pinned Pending row keeps its classification across syncs — the refresh would otherwise
        /// clobber the correction with the same machine parse that was wrong the first time.</summary>
        public bool PinnedByReviewer { get; set; }

        // ── Lifecycle ─────────────────────────────────────────────────────────────────

        public DateTime FirstSeenUtc { get; set; }

        /// <summary>Refreshed by every sync that still sees the file untracked.</summary>
        public DateTime LastSeenUtc { get; set; }

        /// <summary>When the row left Pending (approved / rejected / ingested / superseded).</summary>
        public DateTime? ResolvedUtc { get; set; }

        /// <summary>Username of the reviewer who approved/rejected, when a person did.</summary>
        [MaxLength(64)]
        public string? ResolvedBy { get; set; }
    }
}
