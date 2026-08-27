using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.OData.Query;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using MovieTheater.Db;
using MovieTheater.Models;
using MovieTheater.Normalization;
using MovieTheater.Services;
using MovieTheater.Services.ImdbApi;
using MovieTheater.Services.Poster;
using MovieTheater.Services.BoardgameImage;
using MovieTheater.Services.Tmdb;
using MovieTheater.Services.Omdb;
using MovieTheater.Services.Google;
using MovieTheater.Services.Bgg;

namespace MovieTheater.Controllers
{
    public partial class APIController : Controller
    {
        private readonly MovieDb movieDb;
        private readonly TmdbApi tmdb;
        private readonly OmdbApi omdb;
        private readonly ImdbApiClient imdb;
        private readonly HttpClient httpClient;
        private readonly IPosterImageRepository imageRepo;
        private readonly IBoardgameImageRepository boardgameImageRepo;
        private readonly ImageShrinkService shrinkService;
        private readonly GoogleSearchService googleSearchService;
        private readonly IMDBApiService imdbApiService;
        private readonly BoardGameGeekApi boardGameGeekApi;
        private readonly PosterMosaicService posterMosaicService;
        private readonly BoardgameRulesService boardgameRulesService;
        private readonly BoardgamePdfRepository boardgamePdfRepository;
        private readonly IConfiguration configuration;
        private readonly YouTubeService youTubeService;
        private readonly IMemoryCache memoryCache;
        private readonly BoardgameSimilarityService boardgameSimilarityService;
        private readonly PosterFetchService posterFetchService;
        private readonly TitleEnrichService titleEnrichService;
        private readonly MovieTheater.Services.Jellyfin.JellyfinApi jellyfinApi;
        private readonly MovieTheater.Services.OpenSubtitles.OpenSubtitlesApi openSubtitles;
        private readonly MovieTheater.Services.Jellyfin.JellyfinSyncService jellyfinSyncService;
        private readonly MovieTheater.Services.Jellyfin.JellyfinSyncRunner jellyfinSyncRunner;
        private readonly MovieTheater.Services.Series.SeriesEpisodeCatalog episodeCatalog;
        private readonly MovieTheater.Ingest.SyncCandidateResolver candidateResolver;
        private readonly ILogger<APIController> logger;

        public APIController(MovieDb movieDb, TmdbApi tmdb, OmdbApi omdb, ImdbApiClient imdb, HttpClient httpClient, IPosterImageRepository imageRepo,
            IBoardgameImageRepository boardgameImageRepo, ImageShrinkService shrinkService, GoogleSearchService googleSearchService, IMDBApiService imdbApiService,
            BoardGameGeekApi boardGameGeekApi, PosterMosaicService posterMosaicService,
            BoardgameRulesService boardgameRulesService, BoardgamePdfRepository boardgamePdfRepository,
            IConfiguration configuration, YouTubeService youTubeService, IMemoryCache memoryCache,
            BoardgameSimilarityService boardgameSimilarityService, PosterFetchService posterFetchService, TitleEnrichService titleEnrichService,
            MovieTheater.Services.Jellyfin.JellyfinApi jellyfinApi, MovieTheater.Services.Jellyfin.JellyfinSyncService jellyfinSyncService,
            MovieTheater.Services.Jellyfin.JellyfinSyncRunner jellyfinSyncRunner,
            MovieTheater.Services.OpenSubtitles.OpenSubtitlesApi openSubtitles,
            MovieTheater.Services.Series.SeriesEpisodeCatalog episodeCatalog,
            MovieTheater.Ingest.SyncCandidateResolver candidateResolver,
            ILogger<APIController> logger)
        {
            this.episodeCatalog = episodeCatalog;
            this.candidateResolver = candidateResolver;
            this.movieDb = movieDb;
            this.tmdb = tmdb;
            this.omdb = omdb;
            this.imdb = imdb;
            this.httpClient = httpClient;
            this.imageRepo = imageRepo;
            this.boardgameImageRepo = boardgameImageRepo;
            this.shrinkService = shrinkService;
            this.googleSearchService = googleSearchService;
            this.imdbApiService = imdbApiService;
            this.boardGameGeekApi = boardGameGeekApi;
            this.posterMosaicService = posterMosaicService;
            this.boardgameRulesService = boardgameRulesService;
            this.boardgamePdfRepository = boardgamePdfRepository;
            this.configuration = configuration;
            this.youTubeService = youTubeService;
            this.memoryCache = memoryCache;
            this.boardgameSimilarityService = boardgameSimilarityService;
            this.posterFetchService = posterFetchService;
            this.titleEnrichService = titleEnrichService;
            this.jellyfinApi = jellyfinApi;
            this.jellyfinSyncService = jellyfinSyncService;
            this.jellyfinSyncRunner = jellyfinSyncRunner;
            this.openSubtitles = openSubtitles;
            this.logger = logger;
        }

        private int? GetCurrentUserId()
        {
            var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier);
            if (userIdClaim != null && int.TryParse(userIdClaim.Value, out int userId))
            {
                return userId;
            }
            return null;
        }

        // Admins are defined solely by the AdminUsernames config list (case-insensitive); the
        // dedicated AdminController is the root of trust. Here it gates the one self-service
        // exception — a config admin may set their own first password (to become password-verified
        // and unlock the admin tools), whereas ordinary users cannot create a first password.
        private bool IsAdminUsername(string? username)
        {
            if (string.IsNullOrEmpty(username)) return false;
            var admins = configuration.GetSection("AdminUsernames").Get<string[]>() ?? Array.Empty<string>();
            return admins.Any(a => string.Equals(a, username, StringComparison.OrdinalIgnoreCase));
        }

        // Delegates to the shared gate so the browse and streaming paths can't drift.
        // Single-field overload kept for callers that only hold one rating string.
        private int GetMPARatingFromMovieRating(string movieRating) =>
            Web.RatingGate.MpaRatingIdFor(movieDb, movieRating);

        [HttpGet("/API/GetMovie")]
        public async Task<IActionResult> GetMovie(int id)
        {
            int ageRestriction = 100;
            var currentUserId = GetCurrentUserId();
            if (currentUserId.HasValue)
            {
                var setRestriction = await movieDb.UserSettings
                    .FirstOrDefaultAsync(u => u.SettingKey == "AgeRestriction" && u.UserID == currentUserId.Value);
                if (setRestriction != null && int.TryParse(setRestriction.SettingValue, out int parsedRestriction))
                {
                    ageRestriction = parsedRestriction;
                }
            }

            var movie = await movieDb.Movies.AsNoTracking().Include(m => m.PosterDetails).SingleOrDefaultAsync(m => m.id == id);
            if (movie == null)
                return BadRequest(new { Success = false, Message = "Movie ID not found" });
            var rating = Web.RatingGate.EffectiveMpaRatingId(movieDb, movie.MpaaRating, movie.Rating, movie.MpaaRatingInferred);
            if (rating <= ageRestriction)
            {
                // Surface whether the movie is streamable so the UI can show the Watch button.
                movie.HasFile = await movieDb.MediaFiles
                    .AnyAsync(f => f.PlayableId == movie.PlayableId && f.JellyfinItemId != null && f.MissingSinceUtc == null);
                var normalized = await GetNormalizedMovieData(id, movie);
                return Ok(new { Success = true, data = movie, normalized });
            }
            return BadRequest(new { Success = false, Message = "Movie ID not found" });
        }

        // Normalized IMDB data (from the FK tables / new columns) for a single movie.
        // Returned alongside the legacy entity so the UI can prefer it and fall back
        // to the legacy comma-separated columns for rows not yet scraped.
        private async Task<object> GetNormalizedMovieData(int id, Movie movie)
        {
            var genres = await movieDb.MovieGenres
                .Where(mg => mg.MovieID == id)
                .OrderBy(mg => mg.Ordering)
                .Select(mg => mg.Genre.Name)
                .ToListAsync();

            var credits = await movieDb.MovieCredits
                .Where(c => c.MovieID == id)
                .OrderBy(c => c.Ordering)
                .Select(c => new { c.Role, Nm = c.Person.ImdbNameId, Name = c.Person.DisplayName, c.Character })
                .ToListAsync();

            var summaries = await movieDb.MoviePlotSummaries
                .Where(s => s.MovieID == id)
                .OrderBy(s => s.Ordering)
                .Select(s => new { s.Author, s.Text })
                .ToListAsync();

            object People(CreditRole role) => credits
                .Where(c => c.Role == role)
                .Select(c => new { nm = c.Nm, name = c.Name, character = c.Character })
                .ToList();

            // Media files for this title's Playable — drives the multi-file UI (Primary + any
            // Part / Variant / Extra). Ordered Primary-first, then split parts in order.
            // Editors get the full on-disk path (the file-mapping tools need it); everyone else gets
            // only the filename so the internal NAS directory layout isn't exposed to ordinary users.
            var isEditor = await IsCurrentUserEditor();
            var files = movie.PlayableId == null
                ? new List<object>()
                : (await movieDb.MediaFiles.Where(f => f.PlayableId == movie.PlayableId)
                    .OrderBy(f => f.Role).ThenBy(f => f.PartNumber).ThenBy(f => f.Id)
                    .Select(f => new { f.Id, f.Path, f.Role, f.Label, f.PartNumber, f.DurationTicks, Streamable = f.JellyfinItemId != null && f.MissingSinceUtc == null })
                    .ToListAsync())
                    // mediaFileId + isPlayable let the modal offer a play button per file (the Primary
                    // plays via the movie id; a specific Part/Variant/Extra plays by its mediaFileId).
                    // durationTicks lets the watch page stitch a multi-part movie into one virtual timeline.
                    .Select(f => (object)new { mediaFileId = f.Id, path = isEditor ? f.Path : FileBaseName(f.Path), role = f.Role.ToString(), label = f.Label, partNumber = f.PartNumber, durationTicks = f.DurationTicks, isPlayable = f.Streamable })
                    .ToList();

            // Series are their own table now (see GetSeries); a Movie is never a series here.
            return new
            {
                verified = movie.ImdbVerifiedDate != null,
                needsReview = movie.ImdbNeedsReview,
                titleType = movie.TitleType.ToString(),
                runtimeMinutes = movie.RuntimeMinutes,
                plotFull = movie.PlotFull,
                plotSynopsis = movie.PlotSynopsis,
                mpaaRating = movie.MpaaRating,
                imdbReleaseDate = movie.ImdbReleaseDate,
                imdbRating = movie.ImdbRatingScraped,
                genres,
                cast = People(CreditRole.Actor),
                directors = People(CreditRole.Director),
                writers = People(CreditRole.Writer),
                summaries,
                files,
                relatedMisc = await LoadModalMiscAsync(movie.id, null),
                insight = await LoadInsightAsync(InsightSubjectKind.Movie, id),
                isSeries = false,
                seasons = (object?)null,
            };
        }

        // Model-inferred discovery metadata for the detail modal: the "why interesting" pitch, the
        // "watch if you liked …" comparisons, and the franchise(s) the title belongs to. Reads the
        // CURRENT (newest) insight for the subject; returns null when there's nothing worth showing
        // so the UI can omit the section entirely.
        private async Task<object?> LoadInsightAsync(InsightSubjectKind kind, int id)
        {
            var insight = await movieDb.TitleInsights
                .Where(ti => ti.SubjectKind == kind && ti.SubjectId == id)
                .OrderByDescending(ti => ti.GeneratedUtc)
                .Include(ti => ti.Tags)
                .FirstOrDefaultAsync();
            if (insight == null) return null;

            string[] Vals(TagCategory c) => insight.Tags
                .Where(t => t.Category == c && !string.IsNullOrWhiteSpace(t.Value))
                .OrderByDescending(t => t.Weight ?? 0)
                .Select(t => t.Value!)
                .ToArray();

            var franchises = Vals(TagCategory.Franchise);
            var compTitles = Vals(TagCategory.CompTitle);

            if (string.IsNullOrWhiteSpace(insight.Vibe) && string.IsNullOrWhiteSpace(insight.WhyInteresting)
                && string.IsNullOrWhiteSpace(insight.WatchIfYouLiked) && franchises.Length == 0 && compTitles.Length == 0)
                return null;

            return new
            {
                vibe = insight.Vibe,
                whyInteresting = insight.WhyInteresting,
                watchIfYouLiked = insight.WatchIfYouLiked,
                franchises,
                compTitles,
            };
        }

        // Drives the modal's "franchise rail": the title's franchise(s), each as an ordered (by release
        // date) strip of fellow members so you can see what comes next/before. Franchise membership comes
        // from the newest TitleInsight's Franchise tags (newest-wins, so a superseded tag doesn't count);
        // members are age-gated with the same rule as the rest of the browse path. Returns one entry per
        // franchise the title belongs to (the UI toggles between them) plus the most-specific default —
        // fewest members, so a title tagged both `godzilla` + `monsterverse` sequences within the latter.
        [HttpGet("/API/GetFranchiseRail")]
        public async Task<IActionResult> GetFranchiseRail(int id, string kind = "movie")
        {
            var subjectKind = string.Equals(kind, "series", StringComparison.OrdinalIgnoreCase)
                ? InsightSubjectKind.Series : InsightSubjectKind.Movie;
            int ageRestriction = await GetAgeRestrictionAsync();
            object Empty() => new { defaultFranchise = (string?)null, franchises = Array.Empty<object>() };

            // 1. This title's franchise tags (from its newest insight), with their weights.
            var myInsight = await movieDb.TitleInsights
                .Where(ti => ti.SubjectKind == subjectKind && ti.SubjectId == id)
                .OrderByDescending(ti => ti.GeneratedUtc)
                .Include(ti => ti.Tags)
                .FirstOrDefaultAsync();
            var myFranchises = (myInsight?.Tags ?? new List<TitleTag>())
                .Where(t => t.Category == TagCategory.Franchise && !string.IsNullOrWhiteSpace(t.Value))
                .Select(t => t.Value).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (myFranchises.Count == 0) return Ok(Empty());
            var myFrSet = new HashSet<string>(myFranchises, StringComparer.OrdinalIgnoreCase);
            var myWeights = (myInsight?.Tags ?? new List<TitleTag>())
                .Where(t => t.Category == TagCategory.Franchise && !string.IsNullOrWhiteSpace(t.Value))
                .GroupBy(t => t.Value, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.Max(t => t.Weight ?? 0), StringComparer.OrdinalIgnoreCase);

            // 2. Candidate subjects carrying any of those franchise values (on any insight)…
            var candidates = await movieDb.TitleTags
                .Where(t => t.Category == TagCategory.Franchise && t.Value != null && myFranchises.Contains(t.Value))
                .Select(t => new { t.Insight.SubjectKind, t.Insight.SubjectId })
                .Distinct().ToListAsync();
            var candMovieIds = candidates.Where(c => c.SubjectKind == InsightSubjectKind.Movie).Select(c => c.SubjectId).Distinct().ToList();
            var candSeriesIds = candidates.Where(c => c.SubjectKind == InsightSubjectKind.Series).Select(c => c.SubjectId).Distinct().ToList();

            // 3. …confirmed against each candidate's NEWEST insight, so a stale (superseded) tag doesn't count.
            var candInsights = await movieDb.TitleInsights
                .Where(ti => (ti.SubjectKind == InsightSubjectKind.Movie && candMovieIds.Contains(ti.SubjectId))
                          || (ti.SubjectKind == InsightSubjectKind.Series && candSeriesIds.Contains(ti.SubjectId)))
                .Include(ti => ti.Tags)
                .ToListAsync();
            var members = candInsights
                .GroupBy(ti => new { ti.SubjectKind, ti.SubjectId })
                .Select(g => g.OrderByDescending(ti => ti.GeneratedUtc).First())
                .Select(ti => new
                {
                    ti.SubjectKind,
                    ti.SubjectId,
                    Franchises = ti.Tags.Where(t => t.Category == TagCategory.Franchise && t.Value != null)
                        .GroupBy(t => t.Value, StringComparer.OrdinalIgnoreCase)
                        .ToDictionary(g => g.Key, g => g.Max(t => t.Weight ?? 0), StringComparer.OrdinalIgnoreCase),
                })
                .Where(m => m.Franchises.Keys.Any(myFrSet.Contains))
                .ToList();

            // 4. Pull the display rows for the member subjects, age-gated. Movie + Series share one shape
            //    (startYear is series-only) so they can be concatenated and keyed together.
            var memMovieIds = members.Where(m => m.SubjectKind == InsightSubjectKind.Movie).Select(m => m.SubjectId).ToList();
            var memSeriesIds = members.Where(m => m.SubjectKind == InsightSubjectKind.Series).Select(m => m.SubjectId).ToList();
            var movieRows = await movieDb.Movies
                .Where(m => memMovieIds.Contains(m.id))
                .Where(Web.RatingGate.MovieVisibleAtAge(movieDb, ageRestriction))
                .Select(m => new
                {
                    kind = "movie",
                    id = m.id,
                    title = m.Title,
                    release = m.ImdbReleaseDate ?? m.ReleaseDate,
                    startYear = (int?)null,
                    posterVersion = m.PosterDetails != null ? m.PosterDetails.PosterVersion : 0,
                    streamable = m.PlayableId != null && movieDb.MediaFiles.Any(f => f.PlayableId == m.PlayableId && f.JellyfinItemId != null && f.MissingSinceUtc == null),
                }).ToListAsync();
            var seriesRows = await movieDb.Series
                .Where(s => memSeriesIds.Contains(s.Id))
                .Where(Web.RatingGate.SeriesVisibleAtAge(movieDb, ageRestriction))
                .Select(s => new
                {
                    kind = "series",
                    id = s.Id,
                    title = s.Title,
                    release = s.ImdbReleaseDate ?? s.ReleaseDate,
                    startYear = s.StartYear,
                    posterVersion = s.PosterDetails != null ? s.PosterDetails.PosterVersion : 0,
                    streamable = movieDb.Episodes.Any(e => e.SeriesId == s.Id && e.PlayableId != null
                        && movieDb.MediaFiles.Any(f => f.PlayableId == e.PlayableId && f.JellyfinItemId != null && f.MissingSinceUtc == null)),
                }).ToListAsync();
            var rowByKey = movieRows.Concat(seriesRows).ToDictionary(r => (r.kind, r.id));

            // 5. Assemble one ordered rail per franchise; drop franchises with < 2 visible members.
            string KindStr(InsightSubjectKind k) => k == InsightSubjectKind.Series ? "series" : "movie";
            var built = new List<(string value, int count, List<object> items)>();
            foreach (var f in myFranchises)
            {
                var rows = new List<(DateTime sort, object item)>();
                foreach (var m in members)
                {
                    if (!m.Franchises.ContainsKey(f)) continue;
                    if (!rowByKey.TryGetValue((KindStr(m.SubjectKind), m.SubjectId), out var r)) continue; // age-gated out
                    var year = r.release?.Year ?? r.startYear;
                    var sort = r.release ?? (r.startYear != null ? new DateTime(r.startYear.Value, 1, 1) : DateTime.MaxValue);
                    rows.Add((sort, new
                    {
                        r.id,
                        r.kind,
                        r.title,
                        year,
                        r.posterVersion,
                        r.streamable,
                        isCurrent = m.SubjectKind == subjectKind && m.SubjectId == id,
                    }));
                }
                if (rows.Count < 2) continue;
                built.Add((f, rows.Count, rows.OrderBy(x => x.sort).Select(x => x.item).ToList()));
            }
            if (built.Count == 0) return Ok(Empty());

            // Default to the most specific franchise (fewest members); ties → this title's higher tag weight.
            var defaultFranchise = built
                .OrderBy(b => b.count)
                .ThenByDescending(b => myWeights.TryGetValue(b.value, out var wv) ? wv : 0)
                .First().value;

            return Ok(new
            {
                defaultFranchise,
                franchises = built.Select(b => new { value = b.value, count = b.count, items = b.items }).ToList(),
            });
        }

        [HttpGet("/API/GetTotalMovieCount")]
        public async Task<IActionResult> GetTotalMovieCount()
        {
            try
            {
                var count = await GetOrCacheLookupAsync("lookup:totalMovieCount",
                    () => movieDb.Movies.CountAsync(m => m.ReviewBatch == null));
                return Ok(new { totalCount = count, success = true });
            }
            catch (Exception ex)
            {
                return BadRequest(new { totalCount = 0, success = false, error = ex.Message });
            }
        }

        // Rich queryable movie feed. Not used by the SPA (browse goes through the paged /API/Browse*
        // endpoints), so it's gated to logged-in users and hard-bounded: without a page ceiling a single
        // anonymous GET would materialize the whole Movie table (every heavy text column + FilePath) as
        // one tracked JSON dump.
        [Authorize]
        [EnableQuery(PageSize = 100, MaxTop = 200, MaxExpansionDepth = 2)]
        [HttpGet("/odata/Movies")]
        public async Task<IQueryable<Movie>> GetMovies()
        {
            return (await GetBaseMovieQuery()).AsNoTracking();
        }

        // Cards for an explicit id set (the Seen / Want lists, and the back-nav restore list).
        // pageSize > 0 streams the list as the paginated envelope (Seen/Want infinite scroll);
        // pageSize <= 0 (default) returns the full merged list as a bare array, which the restore
        // path needs so it can reorder client-side by its remembered on-screen order.
        [HttpPost("/API/GetMoviesByIds")]
        public async Task<IActionResult> GetMoviesByIds([FromBody] List<int> ids, int page = 1, int pageSize = 0, string? sort = null, int seed = 0, CancellationToken ct = default)
        {
            if (ids == null || ids.Count == 0)
                return Ok(pageSize > 0 ? (object)EmptyPage(pageSize) : new List<MovieCardDto>());

            // ids share a space across the two tables — match both Movies and Series.
            var mq = (await GetBaseMovieQuery(ct)).Where(m => ids.Contains(m.id));
            var sq = (await GetBaseSeriesQuery(ct)).Where(s => ids.Contains(s.Id));

            // The infinite (Seen/Want) path honors the browse sort; the bare-array restore path keeps its
            // SimpleTitle order (the client reorders it by the remembered on-screen sequence anyway).
            if (pageSize > 0)
                return Ok(await PageMergedAsync(mq, sq, page, pageSize, NormalizeSort(sort), seed, ct));

            var movies = await mq.Select(ToCardDto).ToListAsync(ct);
            var series = await sq.Select(ToSeriesCardDto).ToListAsync(ct);
            return Ok(movies.Concat(series).OrderBy(c => c.SimpleTitle, StringComparer.OrdinalIgnoreCase).ToList());
        }

        // Slim row shape for browse cards. Carries only the columns CardList /
        // SimpleCardList actually render — deliberately omitting the heavy
        // PlotSynopsis / scrape-audit columns and the full PosterDetails object so
        // the list payload (and the DB read) stays small. The modal fetches the full
        // record on its own when opened.
        public class MovieCardDto
        {
            public int id { get; set; }
            /// <summary>"movie" or "series" — the id space is shared, so a card states its table.</summary>
            public string Kind { get; set; } = "movie";
            public string? Title { get; set; }
            public string? SimpleTitle { get; set; }
            public DateTime? ReleaseDate { get; set; }
            public string? Rating { get; set; }
            /// <summary>True when <see cref="Rating"/> is a rough inferred estimate (no real
            /// certificate exists), so the UI can mark it (e.g. "PG ~").</summary>
            public bool RatingEstimated { get; set; }
            public string? Runtime { get; set; }
            public decimal? imdbRating { get; set; }
            /// <summary>Rotten Tomatoes Tomatometer (critics), 0–100; null when unscored. Used for sort.</summary>
            public int? RtTomatometer { get; set; }
            /// <summary>Rotten Tomatoes Popcornmeter (audience), 0–100; null when unscored. Used for sort.</summary>
            public int? RtPopcornmeter { get; set; }
            /// <summary>When the row was added to the library; drives the "Recently Added" sort. Null on misc/legacy rows.</summary>
            public DateTime? UploadedDate { get; set; }
            public string? PlotFull { get; set; }
            public string? Plot { get; set; }
            public string? TopCast { get; set; }
            public string? Actors { get; set; }
            public int PosterVersion { get; set; }

            // ── Misc-only (Kind="misc"); null for movie/series cards. ──
            /// <summary>Free-text MiscVideo bucket ("Workprint", "Stage Performance"…) shown as the card badge.</summary>
            public string? Category { get; set; }
            /// <summary>The misc video's Playable id — the stream target for a future play action.</summary>
            public int? PlayableId { get; set; }
        }

        // Shared EF projection so every card-feeding endpoint emits the same slim
        // shape and translates to a SELECT of just these columns.
        // Prefer the scraped IMDb data (the modal already does), falling back to the frozen legacy
        // columns — so freshly-ingested rows, which only have the new columns filled, still show a
        // rating / certificate / year on their card.
        private static readonly System.Linq.Expressions.Expression<Func<Movie, MovieCardDto>> ToCardDto = m => new MovieCardDto
        {
            id = m.id,
            Kind = "movie",
            Title = m.Title,
            SimpleTitle = m.SimpleTitle,
            ReleaseDate = m.ReleaseDate ?? m.ImdbReleaseDate,
            Rating = m.MpaaRating ?? m.Rating ?? m.MpaaRatingInferred,
            RatingEstimated = m.MpaaRating == null && m.Rating == null && m.MpaaRatingInferred != null,
            Runtime = m.Runtime,
            imdbRating = m.ImdbRatingScraped ?? m.imdbRating,
            RtTomatometer = m.RtTomatometer,
            RtPopcornmeter = m.RtPopcornmeter,
            PlotFull = m.PlotFull,
            Plot = m.Plot,
            TopCast = m.TopCast,
            Actors = m.Actors,
            PosterVersion = m.PosterDetails != null ? m.PosterDetails.PosterVersion : 0,
            UploadedDate = m.UploadedDate,
        };

        // Same slim card shape, projected from a Series — so browse/search can interleave series with movies.
        private static readonly System.Linq.Expressions.Expression<Func<Series, MovieCardDto>> ToSeriesCardDto = s => new MovieCardDto
        {
            id = s.Id,
            Kind = "series",
            Title = s.Title,
            SimpleTitle = s.SimpleTitle,
            ReleaseDate = s.ReleaseDate ?? s.ImdbReleaseDate,
            Rating = s.MpaaRating ?? s.Rating ?? s.MpaaRatingInferred,
            RatingEstimated = s.MpaaRating == null && s.Rating == null && s.MpaaRatingInferred != null,
            Runtime = s.Runtime,
            imdbRating = s.ImdbRatingScraped ?? s.imdbRating,
            RtTomatometer = s.RtTomatometer,
            RtPopcornmeter = s.RtPopcornmeter,
            PlotFull = s.PlotFull,
            Plot = s.Plot,
            TopCast = s.TopCast,
            Actors = s.Actors,
            PosterVersion = s.PosterDetails != null ? s.PosterDetails.PosterVersion : 0,
            UploadedDate = s.UploadedDate,
        };

        private async Task<int> GetAgeRestrictionAsync(CancellationToken ct = default)
        {
            // Memoize per request: a single browse request builds the movie, series and misc queries,
            // each of which needs the age restriction — without this that's the same UserSettings
            // round-trip 2-3× per request.
            const string cacheKey = "__ageRestriction";
            if (HttpContext != null && HttpContext.Items.TryGetValue(cacheKey, out var cached) && cached is int cachedAge)
                return cachedAge;

            int result = 100;
            var currentUserId = GetCurrentUserId();
            if (currentUserId.HasValue)
            {
                var setRestriction = await movieDb.UserSettings
                    .FirstOrDefaultAsync(u => u.SettingKey == "AgeRestriction" && u.UserID == currentUserId.Value, ct);
                if (setRestriction != null && int.TryParse(setRestriction.SettingValue, out int parsedRestriction))
                    result = parsedRestriction;
            }

            if (HttpContext != null)
                HttpContext.Items[cacheKey] = result;
            return result;
        }

        private async Task<IQueryable<Movie>> GetBaseMovieQuery(CancellationToken ct = default)
        {
            int ageRestriction = await GetAgeRestrictionAsync(ct);
            // The predicate itself lives in Web/CatalogQueries so the out-of-request catalog warmer
            // builds the SAME set (quarantine + the series de-duplication + the effective-rating gate).
            return Web.CatalogQueries.BaseMovies(movieDb, ageRestriction);
        }

        // Series peer of GetBaseMovieQuery (same quarantine + age gate). Browse/search union the two.
        private async Task<IQueryable<Series>> GetBaseSeriesQuery(CancellationToken ct = default)
        {
            int ageRestriction = await GetAgeRestrictionAsync(ct);
            return Web.CatalogQueries.BaseSeries(movieDb, ageRestriction);
        }

        // Merge movie + series cards into one SimpleTitle-ordered list (browse stays unified).
        private static List<MovieCardDto> MergeCards(IEnumerable<MovieCardDto> a, IEnumerable<MovieCardDto> b) =>
            a.Concat(b).OrderBy(c => c.SimpleTitle, StringComparer.OrdinalIgnoreCase).ToList();

        // ── Browse sort key ─────────────────────────────────────────────────────────────────────
        // The Browse grid can be ordered by SimpleTitle (A→Z), by one of three rating metrics (highest
        // first, unscored titles last), by library add-date, or SHUFFLED ("random" — the client's
        // default, and what the site's discovery grid is made of). The accepted values are the ones the
        // client sends; everything else falls back to "alpha". Every sort tiebreaks down to a unique
        // key, so the order is fully deterministic — stable across infinite-scroll page fetches.
        private static string NormalizeSort(string? sort) => (sort ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "added" or "recent" or "recently-added" => "added",
            "imdb" => "imdb",
            "rt" or "tomatometer" or "critics" => "rt",
            "popcorn" or "popcornmeter" or "audience" => "popcorn",
            "random" or "shuffle" => "random",
            _ => "alpha",
        };

        // ── The random sort's shuffle ───────────────────────────────────────────────────────────
        // (id + salt + seed) * C mod a large prime is a PERMUTATION of the id space: every title gets a
        // distinct key, so one seed reproduces exactly one order on every page fetch — infinite scroll
        // never dupes or skips a card the way ORDER BY NEWID() would — while a new seed reshuffles the
        // whole grid. The client mints one seed per page load, so a reload is a fresh shuffle.
        //
        // The salt separates the id spaces, which OVERLAP (Series.Id shares the historic Movie value
        // space; MiscVideo is its own): without it a movie and the same-numbered series compute the
        // same key and pair up next to each other in every shuffle.
        private const long ShuffleMul = 2654435761L;
        private const long ShuffleMod = 2147483647L;
        private const long SeriesShuffleSalt = 1000003L;
        private const long MiscShuffleSalt = 2000029L;

        /// <summary>In-memory peer of the shuffle expression the DB paths inline (EF can't call a method
        /// inside an expression tree, so the queries spell it out and this mirrors them exactly).</summary>
        private static long ShuffleKeyOf(int id, string? kind, int seed)
        {
            long salt = kind switch { "series" => SeriesShuffleSalt, "misc" => MiscShuffleSalt, _ => 0L };
            return ((long)id + seed + salt) * ShuffleMul % ShuffleMod;
        }

        // Order a single-table movie query by the chosen sort. Rating sorts coalesce null → -1 so
        // unscored titles sort last under OrderByDescending. `seed` matters only to the random sort.
        private static IQueryable<Movie> SortMovies(IQueryable<Movie> q, string sort, int seed = 0) => sort switch
        {
            "added" => q.OrderByDescending(m => m.UploadedDate ?? DateTime.MinValue).ThenBy(m => m.SimpleTitle).ThenBy(m => m.id),
            "imdb" => q.OrderByDescending(m => (m.ImdbRatingScraped ?? m.imdbRating) ?? -1m).ThenBy(m => m.SimpleTitle).ThenBy(m => m.id),
            "rt" => q.OrderByDescending(m => ((decimal?)m.RtTomatometer) ?? -1m).ThenBy(m => m.SimpleTitle).ThenBy(m => m.id),
            "popcorn" => q.OrderByDescending(m => ((decimal?)m.RtPopcornmeter) ?? -1m).ThenBy(m => m.SimpleTitle).ThenBy(m => m.id),
            "random" => q.OrderBy(m => ((long)m.id + seed) * ShuffleMul % ShuffleMod).ThenBy(m => m.id),
            _ => q.OrderBy(m => m.SimpleTitle).ThenBy(m => m.id),
        };

        private static IQueryable<Series> SortSeries(IQueryable<Series> q, string sort, int seed = 0) => sort switch
        {
            "added" => q.OrderByDescending(s => s.UploadedDate ?? DateTime.MinValue).ThenBy(s => s.SimpleTitle).ThenBy(s => s.Id),
            "imdb" => q.OrderByDescending(s => (s.ImdbRatingScraped ?? s.imdbRating) ?? -1m).ThenBy(s => s.SimpleTitle).ThenBy(s => s.Id),
            "rt" => q.OrderByDescending(s => ((decimal?)s.RtTomatometer) ?? -1m).ThenBy(s => s.SimpleTitle).ThenBy(s => s.Id),
            "popcorn" => q.OrderByDescending(s => ((decimal?)s.RtPopcornmeter) ?? -1m).ThenBy(s => s.SimpleTitle).ThenBy(s => s.Id),
            "random" => q.OrderBy(s => ((long)s.Id + seed + SeriesShuffleSalt) * ShuffleMul % ShuffleMod).ThenBy(s => s.Id),
            _ => q.OrderBy(s => s.SimpleTitle).ThenBy(s => s.Id),
        };

        // In-memory peer of SortMovies/SortSeries for already-materialized card lists (Misc-inclusive
        // browse, where the sources can't UNION at the DB). Its random branch reproduces the same
        // permutation the DB paths use, so a Misc-inclusive shuffle interleaves the way they would.
        private static List<MovieCardDto> SortCards(IEnumerable<MovieCardDto> cards, string sort, int seed = 0) => sort switch
        {
            "added" => cards.OrderByDescending(c => c.UploadedDate ?? DateTime.MinValue).ThenBy(c => c.SimpleTitle, StringComparer.OrdinalIgnoreCase).ThenBy(c => c.Kind).ThenBy(c => c.id).ToList(),
            "imdb" => cards.OrderByDescending(c => c.imdbRating ?? -1m).ThenBy(c => c.SimpleTitle, StringComparer.OrdinalIgnoreCase).ThenBy(c => c.Kind).ThenBy(c => c.id).ToList(),
            "rt" => cards.OrderByDescending(c => c.RtTomatometer ?? -1).ThenBy(c => c.SimpleTitle, StringComparer.OrdinalIgnoreCase).ThenBy(c => c.Kind).ThenBy(c => c.id).ToList(),
            "popcorn" => cards.OrderByDescending(c => c.RtPopcornmeter ?? -1).ThenBy(c => c.SimpleTitle, StringComparer.OrdinalIgnoreCase).ThenBy(c => c.Kind).ThenBy(c => c.id).ToList(),
            "random" => cards.OrderBy(c => ShuffleKeyOf(c.id, c.Kind, seed)).ThenBy(c => c.Kind).ThenBy(c => c.id).ToList(),
            _ => cards.OrderBy(c => c.SimpleTitle ?? c.Title ?? string.Empty, StringComparer.OrdinalIgnoreCase).ThenBy(c => c.Kind).ThenBy(c => c.id).ToList(),
        };
    }
}
