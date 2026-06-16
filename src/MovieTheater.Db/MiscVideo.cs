using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace MovieTheater.Db
{
    /// <summary>
    /// A library video that is neither a movie nor a series/episode and has no IMDb id of its own:
    /// stage performances, workprints, instructional sets (e.g. "Book of Cool"), preview reels,
    /// short-film grab-bags, one-off documentary shorts. It becomes streamable via its
    /// <see cref="Playable"/> (Kind = <see cref="PlayableKind.MiscVideo"/>), which one or more
    /// <see cref="MediaFile"/>s attach to — exactly like a movie or episode does.
    ///
    /// A misc video may RELATE to an existing title (a workprint to its film, a Carl Sagan short to a
    /// documentary series) via <see cref="RelatedMovieId"/> OR <see cref="RelatedSeriesId"/>. These are
    /// two TYPED foreign keys on purpose: a single bare id would be ambiguous, because today a series
    /// still lives as a <see cref="Movie"/> row but is slated to move to <see cref="Series"/> only.
    /// Keeping the two columns separate means a series relation survives that move without reshaping
    /// this table. At most one is set; both null means the misc video stands alone (grouped, if part of
    /// a set, by <see cref="CollectionName"/>).
    /// </summary>
    [Table("MiscVideo")]
    public class MiscVideo
    {
        [Key]
        public int Id { get; set; }

        /// <summary>Unique FK to this misc video's <see cref="Playable"/> (Kind = MiscVideo).</summary>
        public int PlayableId { get; set; }

        [ForeignKey(nameof(PlayableId))]
        public Playable Playable { get; set; } = default!;

        [MaxLength(512)]
        public string Title { get; set; } = default!;

        /// <summary>Sort/search key (same role as <see cref="Movie.SimpleTitle"/>); never displayed.</summary>
        [MaxLength(512)]
        public string? SimpleTitle { get; set; }

        public int? Year { get; set; }

        /// <summary>Free-text bucket: "Stage Performance", "Workprint", "Alternate Cut", "Shorts Collection", "Instructional", "Previews"…</summary>
        [MaxLength(64)]
        public string? Category { get; set; }

        public string? Description { get; set; }

        // ── Optional relation to an existing title (at most one set; both null = standalone) ──

        /// <summary>The <see cref="Movie"/> this misc video belongs to (e.g. a workprint of a film).</summary>
        public int? RelatedMovieId { get; set; }

        [ForeignKey(nameof(RelatedMovieId))]
        public Movie? RelatedMovie { get; set; }

        /// <summary>The <see cref="Series"/> this misc video belongs to (e.g. a Carl Sagan short under Cosmos).</summary>
        public int? RelatedSeriesId { get; set; }

        [ForeignKey(nameof(RelatedSeriesId))]
        public Series? RelatedSeries { get; set; }

        /// <summary>Groups a multi-item set filed together (e.g. "Book of Cool" → Disc 1, Disc 2).</summary>
        [MaxLength(256)]
        public string? CollectionName { get; set; }

        /// <summary>Order within a <see cref="CollectionName"/> (Disc 1 before Disc 2); null otherwise.</summary>
        public int? SortOrder { get; set; }

        // ── Review gate (same quarantine pattern as Movie's ReviewBatch) ──

        [MaxLength(128)]
        public string? ReviewBatch { get; set; }

        [MaxLength(128)]
        public string? ReviewProvenance { get; set; }

        [MaxLength(1024)]
        public string? ReviewSourcePath { get; set; }
    }
}
