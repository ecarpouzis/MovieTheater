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
        /// A ROUGH, inferred MPAA-equivalent for titles that have no real certificate (so the
        /// age-gate has something to work with instead of treating them as Unknown/blocked).
        /// Lowest-priority source: the effective rating resolves <see cref="MpaaRating"/> →
        /// legacy <see cref="Rating"/> → this. NEVER overwrites a real certificate.
        /// See <see cref="MovieTheater.Web.RatingGate"/> and the backfill command.
        /// </summary>
        public string? MpaaRatingInferred { get; set; }

        /// <summary>Provenance of <see cref="MpaaRatingInferred"/> (e.g. "omdb", "tmdb",
        /// "imdb-cache:region", "ai:intensity+genre", "inherit:parent") so an inferred guess is
        /// always distinguishable from a real cert and the backfill stays idempotent/re-runnable.</summary>
        public string? MpaaRatingInferredSource { get; set; }

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
        /// Absolute path of the movie's primary video file as the media library exposes it
        /// (e.g. <c>D:\Media\Movies\Title (Year)\Title.mkv</c>). Null when the movie has no
        /// mapped file. Seeded by the file-mapping pass; the Jellyfin sync translates this
        /// via path mappings (see docs/streaming-plan.md §5).
        /// </summary>
        [MaxLength(1024)]
        public string? FilePath { get; set; }

        // ── Rotten Tomatoes scores: scraped fresh from rottentomatoes.com. ──
        // The legacy OMDB-sourced <see cref="tomatoRating"/> (single critic score) is left
        // frozen; these carry today's Tomatometer (critics) and Popcornmeter (audience).

        /// <summary>Rotten Tomatoes Tomatometer (critics) percentage, 0–100.</summary>
        public int? RtTomatometer { get; set; }

        /// <summary>Rotten Tomatoes Popcornmeter (audience) percentage, 0–100.</summary>
        public int? RtPopcornmeter { get; set; }

        /// <summary>Resolved canonical RT movie page (e.g. https://www.rottentomatoes.com/m/the_matrix).</summary>
        public string? RtUrl { get; set; }

        /// <summary>When the RT scores were last scraped; null = not yet scraped (drives resume).</summary>
        public DateTime? RtScoresUpdatedDate { get; set; }

        /// <summary>True when RT search could not confidently match our title/year, so the scores are suspect.</summary>
        public bool RtNeedsReview { get; set; }

        /// <summary>Why the RT row was flagged for manual review.</summary>
        public string? RtReviewReason { get; set; }

        // ── Metadata enrichment (Phase A): data we previously fetched-and-discarded or never ──
        // fetched. Additive and nullable; the legacy snapshot columns above stay frozen. See
        // docs/metadata-enrichment-plan.md §3.2.

        /// <summary>TMDB id — the stable re-fetch key. Lets any future TMDB field be backfilled
        /// without re-running the TMDB→OMDB→IMDb→Google resolution cascade.</summary>
        public int? TmdbId { get; set; }

        /// <summary>Short marketing tagline (distinct from <see cref="Plot"/>). TMDB.</summary>
        public string? Tagline { get; set; }

        /// <summary>Original language (ISO-639-1 from TMDB; OMDB name as fallback). For "foreign films" filtering.</summary>
        public string? OriginalLanguage { get; set; }

        /// <summary>Country/countries of origin (OMDB <c>Country</c> / TMDB production countries).</summary>
        public string? Country { get; set; }

        /// <summary>Production budget in USD (TMDB).</summary>
        public long? BudgetUsd { get; set; }

        /// <summary>Box-office revenue in USD (TMDB worldwide; OMDB <c>BoxOffice</c> domestic as fallback).</summary>
        public long? RevenueUsd { get; set; }

        /// <summary>TMDB popularity score; handy as a default browse sort.</summary>
        public decimal? TmdbPopularity { get; set; }

        /// <summary>TMDB vote count (paired with the existing rating fields).</summary>
        public int? TmdbVoteCount { get; set; }

        /// <summary>Wide hero/backdrop image path (TMDB <c>backdrop_path</c>); poster lives in <see cref="MoviePosterDetails"/>.</summary>
        public string? BackdropPath { get; set; }

        /// <summary>YouTube key for the primary trailer (TMDB <c>videos</c>); pairs with the YouTube service.</summary>
        public string? TrailerKey { get; set; }

        /// <summary>
        /// What kind of title this is (IMDB-aware classification). <see cref="TitleType.Unknown"/>
        /// until the IMDB classification scrape sets it. Series stay here as
        /// <see cref="TitleType.TvSeries"/>; their episodes live in a separate table. See
        /// docs/metadata-enrichment-plan.md §3.2 / Phase C1.
        /// </summary>
        public TitleType TitleType { get; set; } = TitleType.Unknown;

        /// <summary>
        /// Coarse user-facing bucket (<see cref="NormalizedTitleType.Movies"/> / <see cref="NormalizedTitleType.Short"/>)
        /// the Browse "Type" filter groups by — a <b>persisted computed column</b> derived from
        /// <see cref="TitleType"/> in SQL, so it never needs app-side syncing and can be queried directly.
        /// A Movie row is only ever Movies or Short: series live in the <see cref="Series"/> table
        /// (always <see cref="NormalizedTitleType.Series"/>) and tt-less videos in MiscVideo
        /// (always <see cref="NormalizedTitleType.Misc"/>). See <see cref="TitleTypeExtensions.Normalize"/>.
        /// </summary>
        public NormalizedTitleType NormalizedTitleType { get; private set; }

        // ── Library-ingest review (transient) ──────────────────────────────────────
        // Set on every row the bulk library ingest creates so the whole batch can be
        // reviewed on the site before it's trusted, then cleared. Pending-review rows
        // (ReviewBatch != null) are quarantined from the public browse/random/odata
        // queries until approved. Distinct from ImdbNeedsReview / RtNeedsReview, which
        // flag scrape uncertainty (a different axis). See docs/metadata-enrichment-plan
        // and the library-ingest effort.

        /// <summary>Tag identifying the bulk-ingest batch this row came from (e.g.
        /// "library-ingest 2026-06-15"); null once reviewed/approved or for organically
        /// added rows. While set, the row is hidden from browse.</summary>
        [MaxLength(64)]
        public string? ReviewBatch { get; set; }

        /// <summary>How this row's <see cref="imdbID"/> was resolved:
        /// <c>finalsort-cache</c> | <c>suggestion-api</c> | <c>web-search</c>. Lets the
        /// review queue surface lowest-trust first.</summary>
        [MaxLength(32)]
        public string? ReviewProvenance { get; set; }

        /// <summary>Resolver confidence (<c>HIGH</c>/<c>MEDIUM</c>/<c>LOW</c>) at ingest time.</summary>
        [MaxLength(16)]
        public string? ReviewConfidence { get; set; }

        /// <summary>On-disk folder the title was ingested from — review context now and
        /// the seed for file mapping later (Phase 5).</summary>
        [MaxLength(1024)]
        public string? ReviewSourcePath { get; set; }

        /// <summary>When a reviewer explicitly acknowledged a <em>file oddity</em> (no playable file,
        /// missing file, no Primary, extras-only) on this otherwise-live title. While null, an
        /// odd live title keeps surfacing in the review tool's "oddities" scope; once stamped it
        /// drops off. Distinct from <see cref="ReviewBatch"/> (fresh-ingest quarantine).</summary>
        public DateTime? OddityAcknowledgedUtc { get; set; }

        [Key]
        public int id { get; set; }

        public List<Viewing> Viewings { get; set; } = default!;

        public ICollection<MovieCredit> Credits { get; set; } = [];

        public ICollection<MovieGenre> MovieGenres { get; set; } = [];

        public ICollection<MoviePlotSummary> PlotSummaries { get; set; } = [];

        /// <summary>
        /// Unique FK to this movie's <see cref="Playable"/> (Kind = Movie); set for every row by the
        /// Phase-4 cutover migration. Files, playback progress, and channel slots attach to the
        /// <see cref="Playable"/> now (so episodes can carry them too) — reach a movie's files via
        /// <c>Playable.Files</c>. See docs/metadata-enrichment-plan.md §3.1.
        /// </summary>
        public int? PlayableId { get; set; }

        [ForeignKey(nameof(PlayableId))]
        public Playable? Playable { get; set; }

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

        /// <summary>
        /// True when the movie has a streamable file (a <see cref="MovieFile"/> with a
        /// synced Jellyfin item id and not flagged missing). Populated by the API for the
        /// watch button; not persisted.
        /// </summary>
        [NotMapped]
        public bool HasFile { get; set; }
    }
}