using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace MovieTheater.Db
{
    /// <summary>One asset a <see cref="PhotoCurationBatch"/> proposes something about, stamped with the
    /// RULE that proposed it (docs/photos-plan.md §2.9). The stamp is per item on purpose: a reviewer
    /// decides on the rule and the count, and a rule that turns out to be wrong is then one rejectable
    /// cluster rather than scattered mistakes.</summary>
    [Table("PhotoCurationBatchItem")]
    public class PhotoCurationBatchItem
    {
        [Key]
        public int Id { get; set; }

        public int PhotoCurationBatchId { get; set; }

        [ForeignKey(nameof(PhotoCurationBatchId))]
        public PhotoCurationBatch PhotoCurationBatch { get; set; } = default!;

        public int PhotoAssetId { get; set; }

        [ForeignKey(nameof(PhotoAssetId))]
        public PhotoAsset PhotoAsset { get; set; } = default!;

        /// <summary>Root-relative path as it stood when proposed — context for the reviewer, and the
        /// fallback identity when the row's hash has not been computed yet (§2.5).</summary>
        [MaxLength(850)]
        public string Path { get; set; } = "";

        [MaxLength(64)]
        public string? Sha256 { get; set; }

        /// <summary>Which heuristic proposed it (<c>PhotoHideSuggestions</c>: screenshot-folder,
        /// screenshot-filename, misc-folder, tiny-image, non-photo-format).</summary>
        [MaxLength(64)]
        public string Rule { get; set; } = "";
    }
}
