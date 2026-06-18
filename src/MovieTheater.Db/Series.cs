using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace MovieTheater.Db
{
    /// <summary>
    /// A television series / mini-series as a first-class title — a full peer of <see cref="Movie"/>,
    /// not a Movie row. It carries the same metadata graph a movie does (genres, credits, plot
    /// summaries, poster) plus series aggregates, and its <see cref="Episode"/>s hang off it.
    ///
    /// <para><b>Id reuse:</b> a series keeps the SAME numeric id it had as a <see cref="Movie"/> row
    /// (migrated via IDENTITY_INSERT). Poster image files are stored on disk as <c>{id}.png</c> and
    /// served by <c>/Image/{id}</c> with no DB lookup, so an unchanged id means posters, user viewings,
    /// episode FKs and MiscVideo relations all resolve without remapping or re-download. The shared id
    /// space (a value can be a Movie OR a Series) is disambiguated everywhere by a kind discriminator.</para>
    /// </summary>
    [Table("Series")]
    public class Series
    {
        [Key]
        public int Id { get; set; }

        // ── Core display metadata (mirrors Movie; legacy comma-separated columns kept frozen) ──
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

        // ── Normalized IMDB re-scrape data (new columns; legacy above stays frozen) ──
        public int? RuntimeMinutes { get; set; }
        public string? PlotFull { get; set; }
        public string? PlotSynopsis { get; set; }
        public string? MpaaRating { get; set; }
        public string? TopCast { get; set; }
        public DateTime? ImdbReleaseDate { get; set; }
        public decimal? ImdbRatingScraped { get; set; }
        public DateTime? ImdbVerifiedDate { get; set; }
        public string? ImdbScrapedTitle { get; set; }
        public bool ImdbNeedsReview { get; set; }
        public string? ImdbReviewReason { get; set; }

        // ── Rotten Tomatoes ──
        public int? RtTomatometer { get; set; }
        public int? RtPopcornmeter { get; set; }
        public string? RtUrl { get; set; }
        public DateTime? RtScoresUpdatedDate { get; set; }
        public bool RtNeedsReview { get; set; }
        public string? RtReviewReason { get; set; }

        // ── Metadata enrichment (TMDB) ──
        public int? TmdbId { get; set; }
        public string? Tagline { get; set; }
        public string? OriginalLanguage { get; set; }
        public string? Country { get; set; }
        public long? BudgetUsd { get; set; }
        public long? RevenueUsd { get; set; }
        public decimal? TmdbPopularity { get; set; }
        public int? TmdbVoteCount { get; set; }
        public string? BackdropPath { get; set; }
        public string? TrailerKey { get; set; }

        /// <summary>TvSeries or TvMiniSeries.</summary>
        public TitleType TitleType { get; set; } = TitleType.TvSeries;

        // ── Series-level aggregates (formerly the whole, aggregate-only Series table) ──
        public int? SeasonCount { get; set; }
        /// <summary>IMDB total episode count (may exceed how many episodes we hold a file for).</summary>
        public int? EpisodeCount { get; set; }
        public SeriesStatus Status { get; set; }
        public int? StartYear { get; set; }
        public int? EndYear { get; set; }
        [MaxLength(128)]
        public string? Network { get; set; }

        // ── Library-ingest review quarantine (same pattern as Movie) ──
        /// <summary>A scanned text dump of the series' on-disk folder (from <c>scan-series-folders</c>): every
        /// file present, sized, and flagged mapped/unmapped, so the review tool surfaces files the mapper
        /// missed. Snapshot — re-scan to refresh; null until scanned. Not shown in browse.</summary>
        public string? FolderListing { get; set; }

        [MaxLength(64)]
        public string? ReviewBatch { get; set; }
        [MaxLength(32)]
        public string? ReviewProvenance { get; set; }
        [MaxLength(16)]
        public string? ReviewConfidence { get; set; }
        [MaxLength(1024)]
        public string? ReviewSourcePath { get; set; }

        /// <summary>When a reviewer acknowledged a file oddity (episodes mapped but not streamable, missing
        /// episode files) on this otherwise-live series, so it stops surfacing in the review "oddities"
        /// scope. Distinct from <see cref="ReviewBatch"/>. Mirrors <see cref="Movie.OddityAcknowledgedUtc"/>.</summary>
        public DateTime? OddityAcknowledgedUtc { get; set; }

        // ── Navigation (peers of the Movie graph) ──
        public List<Viewing> Viewings { get; set; } = default!;
        public ICollection<SeriesCredit> Credits { get; set; } = [];
        public ICollection<SeriesGenre> SeriesGenres { get; set; } = [];
        public ICollection<SeriesPlotSummary> PlotSummaries { get; set; } = [];
        public ICollection<Episode> Episodes { get; set; } = [];

        public SeriesPosterDetails? PosterDetails { get; set; }

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
