using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace MovieTheater.Db
{
    /// <summary>
    /// One episode of a <see cref="Db.Series"/> (docs/metadata-enrichment-plan.md §3.3). Episodes
    /// are not movies — they carry lightweight metadata as columns rather than the full credit graph —
    /// and become streamable via their <see cref="Playable"/> (which a <see cref="MediaFile"/> attaches
    /// to). Unique on (SeriesId, SeasonNumber, EpisodeNumber).
    /// </summary>
    [Table("Episode")]
    public class Episode
    {
        [Key]
        public int Id { get; set; }

        /// <summary>The <see cref="Db.Series"/> this episode belongs to. (The old <c>SeriesMovieId</c>
        /// link to the series' Movie row was dropped at the Series-split flip 2026-06-17; the orphaned
        /// DB column is dropped in a later deploy.) Nullable in the DB but always populated.</summary>
        public int? SeriesId { get; set; }

        [ForeignKey(nameof(SeriesId))]
        public Series? Series { get; set; }

        /// <summary>Unique FK to the episode's <see cref="Playable"/> (Kind = Episode); null until a file/playable exists.</summary>
        public int? PlayableId { get; set; }

        [ForeignKey(nameof(PlayableId))]
        public Playable? Playable { get; set; }

        public int SeasonNumber { get; set; }

        public int EpisodeNumber { get; set; }

        [MaxLength(512)]
        public string? Title { get; set; }

        /// <summary>Episodes carry their own IMDB tt id.</summary>
        [MaxLength(16)]
        public string? ImdbId { get; set; }

        public DateTime? AirDate { get; set; }

        public int? RuntimeMinutes { get; set; }

        public string? Plot { get; set; }

        public decimal? ImdbRating { get; set; }

        /// <summary>Episode thumbnail; the UI falls back to the series poster when absent.</summary>
        public string? StillPath { get; set; }
    }
}
