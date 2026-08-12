using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace MovieTheater.Db
{
    /// <summary>
    /// One batch of curation waiting on (or settled by) a human — a suggested-hide proposal (§2.9) or an
    /// ingest marker's approval into the timeline (§2.5).
    ///
    /// <para><b>Why this is a table now.</b> Phase 2 kept this state as JSON under
    /// <c>PhotosReportDir</c>, which required the CLI host and the site to resolve that setting to the
    /// SAME directory. In production they cannot: the site pods have no path to the host that can read
    /// the collection, so every JSON-backed review surface renders empty there — the failure the Phase 2
    /// addendum predicted in writing ("a future <c>PhotoCurationBatch</c> table would remove that
    /// requirement"). The state now lives where both halves already agree, and
    /// <c>PhotosReportDir</c> keeps only the artifacts nobody has to read across a host boundary:
    /// ambiguous-pairing reports and exports.</para>
    ///
    /// <para>Nothing here is a file operation (§6). Accepting a hide proposal writes
    /// <see cref="PhotoAsset.Hidden"/>; approving an ingest writes nothing at all beyond this row.</para>
    /// </summary>
    [Table("PhotoCurationBatch")]
    public class PhotoCurationBatch
    {
        [Key]
        public int Id { get; set; }

        public PhotoCurationBatchKind Kind { get; set; }

        /// <summary>The batch's name in its own lane: the <c>IngestBatch</c> marker for an approval, the
        /// proposal id for a hide batch, and the empty string for the single
        /// <see cref="PhotoCurationBatchKind.IngestBaseline"/> row. Unique per kind.</summary>
        [MaxLength(128)]
        public string BatchId { get; set; } = "";

        public PhotoCurationBatchStatus Status { get; set; }

        public DateTime CreatedUtc { get; set; }

        public DateTime? DecidedUtc { get; set; }

        /// <summary>Who decided. Restrict-deleted like every other curation edge: a review record must
        /// outlive account housekeeping (§2.11).</summary>
        public int? DecidedByUserId { get; set; }

        [ForeignKey(nameof(DecidedByUserId))]
        public User? DecidedByUser { get; set; }

        /// <summary>How many rows an accept actually flipped. Lower than the item count is normal and
        /// not a fault: an asset already hidden, or gone since the proposal was written, is skipped.</summary>
        public int AppliedCount { get; set; }

        /// <summary>Resume marker for the chunked pass that fills this batch — a killed run continues
        /// from the batch rather than re-examining the collection from row 1.</summary>
        [MaxLength(128)]
        public string? Cursor { get; set; }

        /// <summary>Whether the pass drained the collection. A half-written proposal is reviewable — it
        /// just does not claim to be everything.</summary>
        public bool Complete { get; set; }

        public ICollection<PhotoCurationBatchItem> Items { get; set; } = new List<PhotoCurationBatchItem>();
    }
}
