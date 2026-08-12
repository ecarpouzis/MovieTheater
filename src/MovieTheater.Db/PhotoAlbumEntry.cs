using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace MovieTheater.Db
{
    /// <summary>One asset's place in a <see cref="PhotoAlbum"/> (photos-plan.md §2.9). Membership is
    /// many-to-many by design — the same photo belongs in "Wedding" and in a person's album at once —
    /// and albums may hold videos as freely as photos.</summary>
    [Table("PhotoAlbumEntry")]
    public class PhotoAlbumEntry
    {
        [Key]
        public int Id { get; set; }

        public int PhotoAlbumId { get; set; }

        [ForeignKey(nameof(PhotoAlbumId))]
        public PhotoAlbum PhotoAlbum { get; set; } = default!;

        public int PhotoAssetId { get; set; }

        [ForeignKey(nameof(PhotoAssetId))]
        public PhotoAsset PhotoAsset { get; set; } = default!;

        /// <summary>Manual order within the album; the UI falls back to taken-date when unset.</summary>
        public int SortOrder { get; set; }

        /// <summary>Per-album caption — the same photo can be captioned differently in two albums,
        /// which is why this lives on the entry rather than on the asset.</summary>
        [MaxLength(1000)]
        public string? Caption { get; set; }
    }
}
