using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace MovieTheater.Db
{
    /// <summary>
    /// One episode of a tvSeries <see cref="Movie"/> (docs/metadata-enrichment-plan.md §3.3). Episodes
    /// are not movies — they carry lightweight metadata as columns rather than the full credit graph —
    /// and become streamable via their <see cref="Playable"/> (which a <see cref="MediaFile"/> attaches
    /// to). Unique on (SeriesMovieId, SeasonNumber, EpisodeNumber).
    /// </summary>
    [Table("Episode")]
    public class Episode
    {
        [Key]
        public int Id { get; set; }

        /// <summary>Legacy link to the tvSeries <see cref="Movie"/> row this episode belonged to,
        /// back when series lived in the Movie table. Dropped at the Series-split flip; use
        /// <see cref="SeriesId"/> / <see cref="Series"/> instead.</summary>
        public int SeriesMovieId { get; set; }

        [ForeignKey(nameof(SeriesMovieId))]
        public Movie SeriesMovie { get; set; } = default!;

        /// <summary>The <see cref="Db.Series"/> this episode belongs to (canonical after the split;
        /// equals the old <see cref="SeriesMovieId"/> value — series keep their id). Nullable during
        /// the dual-existence migration window.</summary>
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
