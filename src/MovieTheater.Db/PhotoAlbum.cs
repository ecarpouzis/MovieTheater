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

        /// <summary>
        /// Which shelf's index this album appears on (§2.12, Phase 7). A
        /// <see cref="PhotoShelf.Timeline"/> album is a family album and shows on <c>/photos/albums</c>;
        /// a <see cref="PhotoShelf.Archive"/> album is a Gallery collection and shows on
        /// <c>/photos/gallery</c>.
        ///
        /// <para><b>The shelf decides the INDEX, never the contents.</b> An album page renders whatever
        /// it holds regardless of either shelf — showing a Gallery collection's artwork to a family
        /// member is the entire reason the section exists — and the detail URL stays
        /// <c>/photos/albums/{slug}</c> on both shelves, so every link ever sent keeps working.</para>
        /// </summary>
        public PhotoShelf Shelf { get; set; }

        /// <summary>
        /// The artist, when this album is an ARTIST COLLECTION (§2.12). Set on an archive album it makes
        /// the album a body of one person's work — the owner collects several — and the page is drawn
        /// as a museum wall: the artist's name as the headline, the album title beneath it as a
        /// subtitle when the two differ. Null is the ordinary case (memes, misc, every family album),
        /// which renders exactly as albums always have.
        ///
        /// <para>A NAME, not a foreign key: these are artists whose work is in the collection, not
        /// people in the photographs. <see cref="FamilyPerson"/> is the family's own people and joining
        /// the two would put strangers in the tag pickers.</para>
        /// </summary>
        [MaxLength(256)]
        public string? ArtistName { get; set; }

        public int? CreatedByUserId { get; set; }

        [ForeignKey(nameof(CreatedByUserId))]
        public User? CreatedByUser { get; set; }

        public DateTime CreatedUtc { get; set; }

        public ICollection<PhotoAlbumEntry> Entries { get; set; } = new List<PhotoAlbumEntry>();
    }
}
