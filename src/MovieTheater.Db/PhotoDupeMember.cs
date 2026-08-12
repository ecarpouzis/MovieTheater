using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace MovieTheater.Db
{
    /// <summary>One asset's membership in a <see cref="PhotoDupeGroup"/> (photos-plan.md §2.6). Exactly
    /// one member per group carries <see cref="IsMaster"/> — enforced by a filtered unique index rather
    /// than a master column on the group, so the master is discovered with the members in one read.</summary>
    [Table("PhotoDupeMember")]
    public class PhotoDupeMember
    {
        [Key]
        public int Id { get; set; }

        public int PhotoDupeGroupId { get; set; }

        [ForeignKey(nameof(PhotoDupeGroupId))]
        public PhotoDupeGroup PhotoDupeGroup { get; set; } = default!;

        public int PhotoAssetId { get; set; }

        [ForeignKey(nameof(PhotoAssetId))]
        public PhotoAsset PhotoAsset { get; set; } = default!;

        /// <summary>The copy that represents the group everywhere. Default heuristic is highest
        /// resolution → largest file → EXIF-bearing; for a <see cref="PhotoDupeGroupKind.Variant"/>
        /// group the display half (the JPEG/photo) takes it automatically and no human is asked.</summary>
        public bool IsMaster { get; set; }

        /// <summary>How alike this member is to the group (pHash distance normalized, or the CLIP score
        /// an Immich candidate arrived with). Null for an Exact group — equality has no degree.</summary>
        public double? Similarity { get; set; }
    }
}
