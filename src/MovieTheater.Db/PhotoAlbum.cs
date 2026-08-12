using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace MovieTheater.Db
{
    /// <summary>
    /// A curated collection of assets (photos-plan.md §2.9). An album is DB rows, never a folder: the
    /// tree contains device dumps and misc piles that are not albums, so folder-as-album is a browse
    /// view and a seeding shortcut ("make an album from this folder" copies membership into rows), not
    /// the model. The folder itself is never the album's identity, which leaves the disk layout free to
    /// stay as ugly as it is.
    /// </summary>
    [Table("PhotoAlbum")]
    public class PhotoAlbum
    {
        [Key]
        public int Id { get; set; }

        [MaxLength(300)]
        public string Title { get; set; } = default!;

        /// <summary>URL key, unique. Stable across retitles once minted.</summary>
        [MaxLength(200)]
        public string Slug { get; set; } = default!;

        public string? Description { get; set; }

        public int? CoverAssetId { get; set; }

        [ForeignKey(nameof(CoverAssetId))]
        public PhotoAsset? CoverAsset { get; set; }

        /// <summary>Hand-set display range, for albums whose members are undated scans or whose real
        /// span reads badly from the data ("Summer 1994"). Independent of member dates on purpose.</summary>
        public DateTime? RangeStart { get; set; }

        public DateTime? RangeEnd { get; set; }

        /// <summary>Order among albums on the album index.</summary>
        public int SortOrder { get; set; }

        public int? CreatedByUserId { get; set; }

        [ForeignKey(nameof(CreatedByUserId))]
        public User? CreatedByUser { get; set; }

        public DateTime CreatedUtc { get; set; }

        public ICollection<PhotoAlbumEntry> Entries { get; set; } = new List<PhotoAlbumEntry>();
    }
}
