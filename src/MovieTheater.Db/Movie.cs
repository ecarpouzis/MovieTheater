using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace MovieTheater.Db
{
    [Table("Movie")]
    public class Movie
    {
        public string? Title { get; set; }
        public string? SimpleTitle { get; set; }
        public string? Rating { get; set; }
        public DateTime? ReleaseDate { get; set; }
        public string? Runtime { get; set; }
        public string? Genre { get; set; }
        public string? Director { get; set; }
        public string? Writer { get; set; }
        public string? Actors { get; set; }
        public string? Plot { get; set; }
        public decimal? imdbRating { get; set; }
        public string? imdbID { get; set; }
        public int? tomatoRating { get; set; }
        public DateTime? UploadedDate { get; set; }
        public bool RemoveFromRandom { get; set; }

        // ── IMDB re-scrape: normalized/corrected data written into NEW columns. ──
        // Legacy columns above (Runtime, Plot, Rating, ReleaseDate, imdbRating,
        // Genre, Actors, Director, Writer) are intentionally left frozen as the
        // pre-scrape snapshot and are never overwritten by the scrape.

        /// <summary>Runtime normalized to whole minutes (legacy <see cref="Runtime"/> kept as text).</summary>
        public int? RuntimeMinutes { get; set; }

        /// <summary>Concise, complete IMDB plot outline (legacy <see cref="Plot"/> kept; it was often truncated).</summary>
        public string? PlotFull { get; set; }

        /// <summary>The long, single IMDB synopsis (spoilers); paired with <see cref="PlotSummaries"/>.</summary>
        public string? PlotSynopsis { get; set; }

        /// <summary>Normalized MPAA certificate from IMDB (legacy <see cref="Rating"/> kept).</summary>
        public string? MpaaRating { get; set; }

        /// <summary>
        /// Top-billed actor names (comma-separated) derived from the <see cref="MovieCredit"/>
        /// FK cast — a lightweight read cache so browse cards can show cast without expanding
        /// the credit graph. Source of truth remains <see cref="Credits"/>; kept in sync when
        /// credits are written. Lets the legacy <see cref="Actors"/> column be retired.
        /// </summary>
        public string? TopCast { get; set; }

        /// <summary>Canonical release date from IMDB (releaseinfo / datePublished).</summary>
        public DateTime? ImdbReleaseDate { get; set; }

        /// <summary>Fresh IMDB aggregate rating captured during the scrape.</summary>
        public decimal? ImdbRatingScraped { get; set; }

        /// <summary>When this row was last verified against IMDB; null = not yet scraped.</summary>
        public DateTime? ImdbVerifiedDate { get; set; }

        /// <summary>Title IMDB reported for our imdbID, used for mismatch detection.</summary>
        public string? ImdbScrapedTitle { get; set; }

        /// <summary>True when the scrape could not confidently confirm our imdbID.</summary>
        public bool ImdbNeedsReview { get; set; }

        /// <summary>Why the row was flagged for manual review.</summary>
        public string? ImdbReviewReason { get; set; }

        /// <summary>
        /// Absolute path of the movie's primary video file on the NAS, as the share exposes it
        /// (e.g. <c>L:\1 - Movies\G\Goodfellas (1990) 2160p\Goodfellas.mkv</c>). Null when the
        /// movie has no mapped file. Seeded by the NAS mapping pass; the future Jellyfin sync
        /// translates this via path mappings (see docs/streaming-plan.md §5).
        /// </summary>
        [MaxLength(1024)]
        public string? FilePath { get; set; }

        [Key]
        public int id { get; set; }

        public List<Viewing> Viewings { get; set; } = default!;

        public ICollection<MovieCredit> Credits { get; set; } = [];

        public ICollection<MovieGenre> MovieGenres { get; set; } = [];

        public ICollection<MoviePlotSummary> PlotSummaries { get; set; } = [];

        public MoviePosterDetails? PosterDetails { get; set; }

        private string? _posterLink;
        [NotMapped]
        public string? PosterLink
        {
            get => _posterLink ?? PosterDetails?.PosterLink;
            set => _posterLink = value;
        }

        [NotMapped]
        public int PosterVersion => PosterDetails?.PosterVersion ?? 0;
    }
}