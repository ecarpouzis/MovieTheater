using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace MovieTheater.Db
{
    /// <summary>
    /// "This person is in this photo" (photos-plan.md §2.8). One row per (asset, person) pair —
    /// <see cref="Source"/> carries whether a human said it or a machine proposed it, so a suggestion
    /// and a confirmation are the same row transitioning, not two rows racing.
    ///
    /// <para>Tags attach to a dupe group's MASTER (§2.6): tagging any member redirects the write to the
    /// master, which is why resolving dupes is scheduled before mass tagging — one pass then covers
    /// every copy of the same print.</para>
    /// </summary>
    [Table("PhotoPersonTag")]
    public class PhotoPersonTag
    {
        [Key]
        public int Id { get; set; }

        public int PhotoAssetId { get; set; }

        [ForeignKey(nameof(PhotoAssetId))]
        public PhotoAsset PhotoAsset { get; set; } = default!;

        public int FamilyPersonId { get; set; }

        [ForeignKey(nameof(FamilyPersonId))]
        public FamilyPerson FamilyPerson { get; set; } = default!;

        public PhotoTagSource Source { get; set; }

        /// <summary>The recognizer's own confidence, when the row came from one. Ranking input for the
        /// tag queue only — it is never a threshold that auto-confirms anything.</summary>
        public double? Confidence { get; set; }

        // ── Face box, as FRACTIONS of the image (0..1), not pixels ──────────────────────────────
        // Fractions survive every derivative: the grid thumb, the 1600px view, the zoom copy and the
        // original are all the same box. Optional — a tag with no box is still a perfectly good tag.

        public double? BoxX { get; set; }

        public double? BoxY { get; set; }

        public double? BoxW { get; set; }

        public double? BoxH { get; set; }

        /// <summary>Which Immich cluster proposed this (§2.4). Provenance only — kept so a re-sync can
        /// recognize its own suggestions instead of duplicating them.</summary>
        [MaxLength(64)]
        public string? ImmichPersonId { get; set; }

        public DateTime CreatedUtc { get; set; }

        /// <summary>When a human accepted the suggestion. Null on a still-pending
        /// <see cref="PhotoTagSource.Suggested"/> row.</summary>
        public DateTime? ConfirmedUtc { get; set; }
    }
}
