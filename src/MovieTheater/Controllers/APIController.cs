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
    public class APIController : Controller
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
        public async Task<IActionResult> GetMoviesByIds([FromBody] List<int> ids, int page = 1, int pageSize = 0, string? sort = null)
        {
            if (ids == null || ids.Count == 0)
                return Ok(pageSize > 0 ? (object)EmptyPage(pageSize) : new List<MovieCardDto>());

            // ids share a space across the two tables — match both Movies and Series.
            var mq = (await GetBaseMovieQuery()).Where(m => ids.Contains(m.id));
            var sq = (await GetBaseSeriesQuery()).Where(s => ids.Contains(s.Id));

            // The infinite (Seen/Want) path honors the browse sort; the bare-array restore path keeps its
            // SimpleTitle order (the client reorders it by the remembered on-screen sequence anyway).
            if (pageSize > 0)
                return Ok(await PageMergedAsync(mq, sq, page, pageSize, NormalizeSort(sort)));

            var movies = await mq.Select(ToCardDto).ToListAsync();
            var series = await sq.Select(ToSeriesCardDto).ToListAsync();
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

        private async Task<int> GetAgeRestrictionAsync()
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
                    .FirstOrDefaultAsync(u => u.SettingKey == "AgeRestriction" && u.UserID == currentUserId.Value);
                if (setRestriction != null && int.TryParse(setRestriction.SettingValue, out int parsedRestriction))
                    result = parsedRestriction;
            }

            if (HttpContext != null)
                HttpContext.Items[cacheKey] = result;
            return result;
        }

        private async Task<IQueryable<Movie>> GetBaseMovieQuery()
        {
            int ageRestriction = await GetAgeRestrictionAsync();
            return movieDb.Movies
                .Include(m => m.PosterDetails)
                // Quarantine: hide rows still pending library-ingest review (ReviewBatch != null)
                // from every browse/odata path until they're approved.
                .Where(m => m.ReviewBatch == null)
                // Series live in their own table now; exclude series-typed Movie rows so a series
                // shows once (from Series), never doubled during the dual-existence window.
                .Where(m => m.TitleType != TitleType.TvSeries && m.TitleType != TitleType.TvMiniSeries)
                // Age gate on the EFFECTIVE rating (real cert → legacy → inferred), so freshly
                // scraped rows (MpaaRating only) and inferred rows gate correctly, not just legacy.
                .Where(Web.RatingGate.MovieVisibleAtAge(movieDb, ageRestriction));
        }

        // Series peer of GetBaseMovieQuery (same quarantine + age gate). Browse/search union the two.
        private async Task<IQueryable<Series>> GetBaseSeriesQuery()
        {
            int ageRestriction = await GetAgeRestrictionAsync();
            return movieDb.Series
                .Include(s => s.PosterDetails)
                .Where(s => s.ReviewBatch == null)
                // Effective-rating gate (see GetBaseMovieQuery). Critical for series: most carry only
                // a scraped MpaaRating, so gating on the legacy Rating alone leaked adult series.
                .Where(Web.RatingGate.SeriesVisibleAtAge(movieDb, ageRestriction));
        }

        // Merge movie + series cards into one SimpleTitle-ordered list (browse stays unified).
        private static List<MovieCardDto> MergeCards(IEnumerable<MovieCardDto> a, IEnumerable<MovieCardDto> b) =>
            a.Concat(b).OrderBy(c => c.SimpleTitle, StringComparer.OrdinalIgnoreCase).ToList();

        // ── Browse sort key ─────────────────────────────────────────────────────────────────────
        // The Browse grid can be ordered by SimpleTitle (the default, A→Z) or by one of three rating
        // metrics (highest first, unscored titles last). The accepted values are the ones the client
        // sends; everything else falls back to "alpha". Rating sorts always tiebreak by SimpleTitle so
        // the order is fully deterministic (stable across infinite-scroll page fetches).
        private static string NormalizeSort(string? sort) => (sort ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "added" or "recent" or "recently-added" => "added",
            "imdb" => "imdb",
            "rt" or "tomatometer" or "critics" => "rt",
            "popcorn" or "popcornmeter" or "audience" => "popcorn",
            _ => "alpha",
        };

        // Order a single-table movie query by the chosen sort. Rating sorts coalesce null → -1 so
        // unscored titles sort last under OrderByDescending.
        private static IQueryable<Movie> SortMovies(IQueryable<Movie> q, string sort) => sort switch
        {
            "added" => q.OrderByDescending(m => m.UploadedDate ?? DateTime.MinValue).ThenBy(m => m.SimpleTitle).ThenBy(m => m.id),
            "imdb" => q.OrderByDescending(m => (m.ImdbRatingScraped ?? m.imdbRating) ?? -1m).ThenBy(m => m.SimpleTitle).ThenBy(m => m.id),
            "rt" => q.OrderByDescending(m => ((decimal?)m.RtTomatometer) ?? -1m).ThenBy(m => m.SimpleTitle).ThenBy(m => m.id),
            "popcorn" => q.OrderByDescending(m => ((decimal?)m.RtPopcornmeter) ?? -1m).ThenBy(m => m.SimpleTitle).ThenBy(m => m.id),
            _ => q.OrderBy(m => m.SimpleTitle).ThenBy(m => m.id),
        };

        private static IQueryable<Series> SortSeries(IQueryable<Series> q, string sort) => sort switch
        {
            "added" => q.OrderByDescending(s => s.UploadedDate ?? DateTime.MinValue).ThenBy(s => s.SimpleTitle).ThenBy(s => s.Id),
            "imdb" => q.OrderByDescending(s => (s.ImdbRatingScraped ?? s.imdbRating) ?? -1m).ThenBy(s => s.SimpleTitle).ThenBy(s => s.Id),
            "rt" => q.OrderByDescending(s => ((decimal?)s.RtTomatometer) ?? -1m).ThenBy(s => s.SimpleTitle).ThenBy(s => s.Id),
            "popcorn" => q.OrderByDescending(s => ((decimal?)s.RtPopcornmeter) ?? -1m).ThenBy(s => s.SimpleTitle).ThenBy(s => s.Id),
            _ => q.OrderBy(s => s.SimpleTitle).ThenBy(s => s.Id),
        };

        // In-memory peer of SortMovies/SortSeries for already-materialized card lists (Misc-inclusive
        // browse, where the sources can't UNION at the DB).
        private static List<MovieCardDto> SortCards(IEnumerable<MovieCardDto> cards, string sort) => sort switch
        {
            "added" => cards.OrderByDescending(c => c.UploadedDate ?? DateTime.MinValue).ThenBy(c => c.SimpleTitle, StringComparer.OrdinalIgnoreCase).ThenBy(c => c.Kind).ThenBy(c => c.id).ToList(),
            "imdb" => cards.OrderByDescending(c => c.imdbRating ?? -1m).ThenBy(c => c.SimpleTitle, StringComparer.OrdinalIgnoreCase).ThenBy(c => c.Kind).ThenBy(c => c.id).ToList(),
            "rt" => cards.OrderByDescending(c => c.RtTomatometer ?? -1).ThenBy(c => c.SimpleTitle, StringComparer.OrdinalIgnoreCase).ThenBy(c => c.Kind).ThenBy(c => c.id).ToList(),
            "popcorn" => cards.OrderByDescending(c => c.RtPopcornmeter ?? -1).ThenBy(c => c.SimpleTitle, StringComparer.OrdinalIgnoreCase).ThenBy(c => c.Kind).ThenBy(c => c.id).ToList(),
            _ => cards.OrderBy(c => c.SimpleTitle ?? c.Title ?? string.Empty, StringComparer.OrdinalIgnoreCase).ThenBy(c => c.Kind).ThenBy(c => c.id).ToList(),
        };

        [HttpGet("/API/GetRandomMovies")]
        public async Task<IActionResult> GetRandomMovies(int take = 50)
        {
            var mq = await GetBaseMovieQuery();
            var sq = await GetBaseSeriesQuery();
            var movies = await mq.Where(m => !m.RemoveFromRandom).OrderBy(m => Guid.NewGuid()).Take(take).Select(ToCardDto).ToListAsync();
            // Sprinkle a proportional number of series into the random landing grid.
            var series = await sq.Where(s => !s.RemoveFromRandom).OrderBy(s => Guid.NewGuid()).Take(Math.Max(1, take / 10)).Select(ToSeriesCardDto).ToListAsync();
            var all = movies.Concat(series).ToList();
            var rng = new Random();
            for (int i = all.Count - 1; i > 0; i--) { int j = rng.Next(i + 1); (all[i], all[j]) = (all[j], all[i]); }
            return Ok(all.Take(take).ToList());
        }

        // Browse filtered by the coarse, normalized Title Type bucket(s). `type` is a comma-separated
        // list (OR across types, like the multi-select genre filter): a title shows if its bucket is any
        // selected. Movies/Short read Movie.NormalizedTitleType (a persisted computed column off
        // TitleType); Series come from the Series table; Misc from the MiscVideo table (tt-less library
        // videos — workprints, stage performances, shorts sets).
        // Returns the { movies, totalCount, page, pageSize } envelope so the client can infinite-scroll
        // instead of pulling the whole library at once. pageSize <= 0 returns everything (the old
        // behavior) for any caller that still wants the full list. Combos without Misc stay fully
        // DB-paged; only Misc-inclusive combos pay an in-memory merge (Misc is a materialized list).
        [HttpGet("/API/GetMoviesByType")]
        public async Task<IActionResult> GetMoviesByType(string type, int page = 1, int pageSize = 60, int? seed = null, string? sort = null)
        {
            var types = ParseTypeScope(type);
            if (types.Count == 0)
                return BadRequest(new { Message = $"Unknown title type '{type}'" });

            // A `sort` param drives the order (Alphabetical/IMDB/RT/Popcornmeter) and takes precedence
            // over `seed`. With neither, the legacy behavior stands: a seed shuffles deterministically,
            // and no seed is A→Z by title.
            bool hasSort = sort != null;
            string s = NormalizeSort(sort);

            bool wantSeries = types.Contains(NormalizedTitleType.Series);
            bool wantMisc = types.Contains(NormalizedTitleType.Misc);
            // Movies and Short both live in the Movie table, keyed by the NormalizedTitleType column.
            var movieBuckets = types.Where(t => t == NormalizedTitleType.Movies || t == NormalizedTitleType.Short).ToList();

            IQueryable<Movie>? mq = movieBuckets.Count > 0
                ? (await GetBaseMovieQuery()).Where(m => movieBuckets.Contains(m.NormalizedTitleType))
                : null;
            IQueryable<Series>? sq = wantSeries ? await GetBaseSeriesQuery() : null;

            // Misc alone: an explicit sort orders it; otherwise it keeps its curated collection ordering.
            if (wantMisc && mq == null && sq == null)
            {
                var misc = await GetMiscCards();
                return Ok(PageCards(hasSort ? SortCards(misc, s) : misc, page, pageSize));
            }

            if (!wantMisc)
            {
                // Pure-DB paths — no Misc, so everything pages at the database.
                if (mq != null && sq != null)
                    return Ok(await PageMergedAsync(mq, sq, page, pageSize, hasSort ? s : "alpha"));
                if (mq != null)
                {
                    // A sort wins. Otherwise a seed (the landing/discovery grid) shuffles deterministically:
                    // (id+seed)*C mod a prime (C a large constant coprime to it) is a permutation, so the
                    // order is stable across pages (infinite scroll stays consistent) yet different per
                    // seed. No seed and no sort → A→Z by title (the letter nav relies on it).
                    IQueryable<Movie> mo = hasSort
                        ? SortMovies(mq, s)
                        : seed is int sm
                            ? mq.OrderBy(m => ((long)m.id + sm) * 2654435761L % 2147483647L)
                            : mq.OrderBy(m => m.SimpleTitle).ThenBy(m => m.id);
                    return Ok(await PageCardsAsync(mo.Select(ToCardDto), page, pageSize));
                }
                IQueryable<Series> so = hasSort
                    ? SortSeries(sq!, s)
                    : seed is int ss
                        ? sq!.OrderBy(s2 => ((long)s2.Id + ss) * 2654435761L % 2147483647L)
                        : sq!.OrderBy(s2 => s2.SimpleTitle).ThenBy(s2 => s2.Id);
                return Ok(await PageCardsAsync(so.Select(ToSeriesCardDto), page, pageSize));
            }

            // Misc mixed with movies/series → merge all selected sources in memory, ordered uniformly by
            // the chosen sort (Misc's own table can't UNION with the Movie/Series queries).
            var cards = new List<MovieCardDto>();
            if (mq != null) cards.AddRange(await mq.Select(ToCardDto).ToListAsync());
            if (sq != null) cards.AddRange(await sq.Select(ToSeriesCardDto).ToListAsync());
            cards.AddRange(await GetMiscCards());
            return Ok(PageCards(SortCards(cards, hasSort ? s : "alpha"), page, pageSize));
        }

        // Parse the comma-separated Title-Type scope — the persistent Browse "Type" filter, applied as an
        // overarching scope across every browse mode. An empty result means no scope (all types).
        private static HashSet<NormalizedTitleType> ParseTypeScope(string? types) =>
            (types ?? string.Empty)
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(t => Enum.TryParse<NormalizedTitleType>(t, true, out var nt) ? nt : (NormalizedTitleType?)null)
                .Where(nt => nt.HasValue)
                .Select(nt => nt!.Value)
                .ToHashSet();

        // Narrow base movie/series queries to a Type scope. Movies/Short live in the Movie table (keyed by
        // NormalizedTitleType); Series is the whole Series table; Misc has no genre/cast/title-search
        // presence so it never participates here. An empty scope is a no-op (all types). A kind absent from
        // the scope is emptied so it contributes nothing to the merged result.
        private static (IQueryable<Movie> Movies, IQueryable<Series> Series) ApplyTypeScope(
            HashSet<NormalizedTitleType> scope, IQueryable<Movie> mq, IQueryable<Series> sq)
        {
            if (scope.Count == 0)
                return (mq, sq);
            var movieBuckets = scope.Where(t => t == NormalizedTitleType.Movies || t == NormalizedTitleType.Short).ToList();
            mq = movieBuckets.Count > 0 ? mq.Where(m => movieBuckets.Contains(m.NormalizedTitleType)) : mq.Where(m => false);
            sq = scope.Contains(NormalizedTitleType.Series) ? sq : sq.Where(s => false);
            return (mq, sq);
        }

        // Page a card query at the DB (SELECT just one page + a COUNT). The query MUST already
        // be ordered so Skip/Take is stable. pageSize <= 0 → return the whole set.
        private static async Task<object> PageCardsAsync(IQueryable<MovieCardDto> ordered, int page, int pageSize)
        {
            if (pageSize <= 0)
            {
                var all = await ordered.ToListAsync();
                return new { movies = all, totalCount = all.Count, page = 1, pageSize = all.Count };
            }
            if (page < 1) page = 1;
            // Only the first page's totalCount is consumed by the client — it sets the "Showing X of Y"
            // header and the infinite-scroll hasMore bound once, then ignores it on every subsequent
            // page fetch. So skip the COUNT round-trip on page > 1 (-1 = "not computed").
            var totalCount = page == 1 ? await ordered.CountAsync() : -1;
            var paged = await ordered.Skip((page - 1) * pageSize).Take(pageSize).ToListAsync();
            return new { movies = paged, totalCount, page, pageSize };
        }

        // In-memory peer of PageCardsAsync for already-materialized card lists (e.g. Misc).
        private static object PageCards(List<MovieCardDto> all, int page, int pageSize)
        {
            var totalCount = all.Count;
            if (pageSize <= 0)
                return new { movies = all, totalCount, page = 1, pageSize = totalCount };
            if (page < 1) page = 1;
            var paged = all.Skip((page - 1) * pageSize).Take(pageSize).ToList();
            return new { movies = paged, totalCount, page, pageSize };
        }

        // Lightweight ordering key for the merged movie+series browse modes. Projecting just these
        // scalars (no navigations) keeps the cross-table UNION trivially translatable.
        private class CardKey
        {
            public string Kind { get; set; } = "movie";
            public int Id { get; set; }
            public string? SimpleTitle { get; set; }
            /// <summary>The active rating metric for a rating sort (null for alpha sort or an unscored
            /// title). Kept as decimal so the int RT scores and the decimal IMDB rating share one column.</summary>
            public decimal? SortValue { get; set; }

            /// <summary>The library add-date for the "Recently Added" sort (null for other sorts / undated rows).</summary>
            public DateTime? SortDate { get; set; }
        }

        // The ordering-key UNION for the merged movie+series browse, projecting the active sort metric
        // into CardKey.SortValue. Branching per sort (rather than a parameterized CASE) keeps the
        // generated SQL clean — and the alpha case projects no rating column at all.
        private static IQueryable<CardKey> BuildCardKeys(IQueryable<Movie> mq, IQueryable<Series> sq, string sort) => sort switch
        {
            "imdb" => mq.Select(m => new CardKey { Kind = "movie", Id = m.id, SimpleTitle = m.SimpleTitle, SortValue = m.ImdbRatingScraped ?? m.imdbRating })
                .Concat(sq.Select(s => new CardKey { Kind = "series", Id = s.Id, SimpleTitle = s.SimpleTitle, SortValue = s.ImdbRatingScraped ?? s.imdbRating })),
            "rt" => mq.Select(m => new CardKey { Kind = "movie", Id = m.id, SimpleTitle = m.SimpleTitle, SortValue = (decimal?)m.RtTomatometer })
                .Concat(sq.Select(s => new CardKey { Kind = "series", Id = s.Id, SimpleTitle = s.SimpleTitle, SortValue = (decimal?)s.RtTomatometer })),
            "popcorn" => mq.Select(m => new CardKey { Kind = "movie", Id = m.id, SimpleTitle = m.SimpleTitle, SortValue = (decimal?)m.RtPopcornmeter })
                .Concat(sq.Select(s => new CardKey { Kind = "series", Id = s.Id, SimpleTitle = s.SimpleTitle, SortValue = (decimal?)s.RtPopcornmeter })),
            "added" => mq.Select(m => new CardKey { Kind = "movie", Id = m.id, SimpleTitle = m.SimpleTitle, SortDate = m.UploadedDate })
                .Concat(sq.Select(s => new CardKey { Kind = "series", Id = s.Id, SimpleTitle = s.SimpleTitle, SortDate = s.UploadedDate })),
            _ => mq.Select(m => new CardKey { Kind = "movie", Id = m.id, SimpleTitle = m.SimpleTitle })
                .Concat(sq.Select(s => new CardKey { Kind = "series", Id = s.Id, SimpleTitle = s.SimpleTitle })),
        };

        // Order merged keys by the chosen sort: rating sorts desc with unscored (null → -1) last, then
        // SimpleTitle/Kind/Id as a stable tiebreak; alpha is SimpleTitle/Kind/Id.
        private static IOrderedQueryable<CardKey> OrderCardKeys(IQueryable<CardKey> keys, string sort) => sort switch
        {
            "alpha" => keys.OrderBy(k => k.SimpleTitle).ThenBy(k => k.Kind).ThenBy(k => k.Id),
            "added" => keys.OrderByDescending(k => k.SortDate ?? DateTime.MinValue).ThenBy(k => k.SimpleTitle).ThenBy(k => k.Kind).ThenBy(k => k.Id),
            _ => keys.OrderByDescending(k => k.SortValue ?? -1m).ThenBy(k => k.SimpleTitle).ThenBy(k => k.Kind).ThenBy(k => k.Id),
        };

        // Page a merged movie+series browse result at the DB without pulling the whole filtered set
        // (two-phase, mirroring the MyBooks views-perf "band items" approach):
        //   1. UNION just the ordering keys (Kind/Id/SimpleTitle) across both tables and Skip/Take
        //      that — a cheap scalar set-operation. A stable secondary sort (Kind, Id) guarantees the
        //      page boundaries don't drift between fetches, so infinite scroll never dupes/skips.
        //   2. Materialize the full card DTOs for just the page's ids and restore the merged order.
        // pageSize <= 0 returns the whole merged set (back-compat).
        private static async Task<object> PageMergedAsync(IQueryable<Movie> mq, IQueryable<Series> sq, int page, int pageSize, string sort = "alpha")
        {
            var keys = BuildCardKeys(mq, sq, sort);

            if (pageSize <= 0)
            {
                var allMovies = await mq.Select(ToCardDto).ToListAsync();
                var allSeries = await sq.Select(ToSeriesCardDto).ToListAsync();
                var allMerged = SortCards(allMovies.Concat(allSeries), sort);
                return new { movies = allMerged, totalCount = allMerged.Count, page = 1, pageSize = allMerged.Count };
            }
            if (page < 1) page = 1;
            // Count only on the first page — the client reads totalCount once and discards it on every
            // later page fetch. Here that COUNT is a full UNION of both table scans, so skipping it on
            // page > 1 saves the most expensive query of the request (-1 = "not computed").
            var totalCount = page == 1 ? await keys.CountAsync() : -1;

            var pageKeys = await OrderCardKeys(keys, sort)
                .Skip((page - 1) * pageSize).Take(pageSize)
                .ToListAsync();

            var movieIds = pageKeys.Where(k => k.Kind == "movie").Select(k => k.Id).ToList();
            var seriesIds = pageKeys.Where(k => k.Kind == "series").Select(k => k.Id).ToList();
            var movieCards = movieIds.Count > 0
                ? await mq.Where(m => movieIds.Contains(m.id)).Select(ToCardDto).ToListAsync()
                : new List<MovieCardDto>();
            var seriesCards = seriesIds.Count > 0
                ? await sq.Where(s => seriesIds.Contains(s.Id)).Select(ToSeriesCardDto).ToListAsync()
                : new List<MovieCardDto>();

            var movieById = movieCards.ToDictionary(c => c.id);
            var seriesById = seriesCards.ToDictionary(c => c.id);
            var pageCards = pageKeys
                .Select(k => k.Kind == "movie"
                    ? (movieById.TryGetValue(k.Id, out var mc) ? mc : null)
                    : (seriesById.TryGetValue(k.Id, out var sc) ? sc : null))
                .Where(c => c != null)
                .ToList();

            return new { movies = pageCards, totalCount, page, pageSize };
        }

        // Empty merged result in the paginated envelope shape (for empty queries).
        private static object EmptyPage(int pageSize) =>
            new { movies = new List<MovieCardDto>(), totalCount = 0, page = 1, pageSize };

        // Approved (un-quarantined) MiscVideos as browse cards. They carry only an inferred rating
        // (a related misc inherits its parent's; a standalone one is judged on its own), gated the
        // same way every other title is. Poster (if any) is served from the separate /MiscImage
        // namespace; the card builds the poster URL off Kind="misc", not the shared id space.
        private async Task<List<MovieCardDto>> GetMiscCards()
        {
            int ageRestriction = await GetAgeRestrictionAsync();
            var raw = await movieDb.MiscVideos
                .Where(v => v.ReviewBatch == null)
                .Where(Web.RatingGate.MiscVisibleAtAge(movieDb, ageRestriction))
                .OrderBy(v => v.CollectionName ?? "")
                .ThenBy(v => v.SortOrder ?? int.MaxValue)
                .ThenBy(v => v.SimpleTitle ?? v.Title)
                .Select(v => new { v.Id, v.Title, v.SimpleTitle, v.Year, v.Description, v.Category, v.PlayableId, v.MpaaRatingInferred })
                .ToListAsync();
            return raw.Select(v => new MovieCardDto
            {
                id = v.Id,
                Kind = "misc",
                Title = v.Title,
                SimpleTitle = v.SimpleTitle,
                ReleaseDate = v.Year.HasValue ? new DateTime(v.Year.Value, 1, 1) : (DateTime?)null,
                Rating = v.MpaaRatingInferred,
                RatingEstimated = v.MpaaRatingInferred != null,
                Plot = v.Description,
                Category = v.Category,
                PlayableId = v.PlayableId,
            }).ToList();
        }

        // Misc-video cards for an explicit id set (the Rate page's misc bars). MiscVideo has its own id
        // space, so this is separate from GetMoviesByIds; same projection + age gate as GetMiscCards.
        [HttpPost("/API/GetMiscByIds")]
        public async Task<IActionResult> GetMiscByIds([FromBody] List<int> ids)
        {
            if (ids == null || ids.Count == 0)
                return Ok(new List<MovieCardDto>());

            int ageRestriction = await GetAgeRestrictionAsync();
            var raw = await movieDb.MiscVideos
                .Where(v => ids.Contains(v.Id) && v.ReviewBatch == null)
                .Where(Web.RatingGate.MiscVisibleAtAge(movieDb, ageRestriction))
                .Select(v => new { v.Id, v.Title, v.SimpleTitle, v.Year, v.Description, v.Category, v.PlayableId, v.MpaaRatingInferred })
                .ToListAsync();
            var cards = raw.Select(v => new MovieCardDto
            {
                id = v.Id,
                Kind = "misc",
                Title = v.Title,
                SimpleTitle = v.SimpleTitle,
                ReleaseDate = v.Year.HasValue ? new DateTime(v.Year.Value, 1, 1) : (DateTime?)null,
                Rating = v.MpaaRatingInferred,
                RatingEstimated = v.MpaaRatingInferred != null,
                Plot = v.Description,
                Category = v.Category,
                PlayableId = v.PlayableId,
            }).OrderBy(c => c.SimpleTitle ?? c.Title, StringComparer.OrdinalIgnoreCase).ToList();
            return Ok(cards);
        }

        // ── Unified search over movies + series (the frontend uses these instead of /odata/Movies) ──

        [HttpGet("/API/BrowseTitle")]
        public async Task<IActionResult> BrowseTitle(string q, int page = 1, int pageSize = 60, string? types = null, string? sort = null)
        {
            q = (q ?? "").Trim();
            if (q.Length == 0) return Ok(EmptyPage(pageSize));
            var mq = (await GetBaseMovieQuery()).Where(m => (m.SimpleTitle != null && m.SimpleTitle.Contains(q)) || (m.Title != null && m.Title.Contains(q)));
            var sq = (await GetBaseSeriesQuery()).Where(s => (s.SimpleTitle != null && s.SimpleTitle.Contains(q)) || (s.Title != null && s.Title.Contains(q)));
            (mq, sq) = ApplyTypeScope(ParseTypeScope(types), mq, sq);
            return Ok(await PageMergedAsync(mq, sq, page, pageSize, NormalizeSort(sort)));
        }

        [HttpGet("/API/BrowseLetter")]
        public async Task<IActionResult> BrowseLetter(string letter, int page = 1, int pageSize = 60, string? types = null, string? sort = null)
        {
            letter = (letter ?? "").Trim();
            if (letter.Length == 0) return Ok(EmptyPage(pageSize));
            var mq = await GetBaseMovieQuery();
            var sq = await GetBaseSeriesQuery();
            if (letter == "#")
            {
                mq = mq.Where(m => EF.Functions.Like(m.SimpleTitle, "[0-9]%"));
                sq = sq.Where(s => EF.Functions.Like(s.SimpleTitle, "[0-9]%"));
            }
            else
            {
                mq = mq.Where(m => m.SimpleTitle != null && m.SimpleTitle.StartsWith(letter));
                sq = sq.Where(s => s.SimpleTitle != null && s.SimpleTitle.StartsWith(letter));
            }
            (mq, sq) = ApplyTypeScope(ParseTypeScope(types), mq, sq);
            return Ok(await PageMergedAsync(mq, sq, page, pageSize, NormalizeSort(sort)));
        }

        [HttpGet("/API/BrowseGenre")]
        public async Task<IActionResult> BrowseGenre(string genres, int page = 1, int pageSize = 60, string? types = null, string? sort = null)
        {
            var list = (genres ?? "").Split(',').Select(g => g.Trim()).Where(g => g.Length > 0).ToList();
            if (list.Count == 0) return Ok(EmptyPage(pageSize));
            var mq = await GetBaseMovieQuery();
            var sq = await GetBaseSeriesQuery();
            foreach (var g in list)
            {
                var gg = g;
                mq = mq.Where(m => m.MovieGenres.Any(x => x.Genre.Name == gg) || (m.Genre != null && m.Genre.Contains(gg)));
                sq = sq.Where(s => s.SeriesGenres.Any(x => x.Genre.Name == gg) || (s.Genre != null && s.Genre.Contains(gg)));
            }
            (mq, sq) = ApplyTypeScope(ParseTypeScope(types), mq, sq);
            return Ok(await PageMergedAsync(mq, sq, page, pageSize, NormalizeSort(sort)));
        }

        // All titles the model tagged as part of a franchise / shared universe (TagCategory.Franchise),
        // e.g. "mcu", "studio-ghibli". The franchise value is the model's normalized tag (lowercase);
        // the detail modal's franchise chips pass it through verbatim.
        [HttpGet("/API/BrowseFranchise")]
        public async Task<IActionResult> BrowseFranchise(string franchise, int page = 1, int pageSize = 60, string? types = null, string? sort = null)
        {
            var fx = (franchise ?? "").Trim();
            if (fx.Length == 0) return Ok(EmptyPage(pageSize));
            var mq = await GetBaseMovieQuery();
            var sq = await GetBaseSeriesQuery();
            mq = mq.Where(m => movieDb.TitleTags.Any(t => t.Category == TagCategory.Franchise && t.Value == fx
                && movieDb.TitleInsights.Any(ti => ti.Id == t.TitleInsightId
                    && ti.SubjectKind == InsightSubjectKind.Movie && ti.SubjectId == m.id)));
            sq = sq.Where(s => movieDb.TitleTags.Any(t => t.Category == TagCategory.Franchise && t.Value == fx
                && movieDb.TitleInsights.Any(ti => ti.Id == t.TitleInsightId
                    && ti.SubjectKind == InsightSubjectKind.Series && ti.SubjectId == s.Id)));
            (mq, sq) = ApplyTypeScope(ParseTypeScope(types), mq, sq);
            return Ok(await PageMergedAsync(mq, sq, page, pageSize, NormalizeSort(sort)));
        }

        [HttpGet("/API/BrowsePerson")]
        public async Task<IActionResult> BrowsePerson(string q, int page = 1, int pageSize = 60, string? types = null, string? sort = null)
        {
            q = (q ?? "").Trim();
            if (q.Length == 0) return Ok(EmptyPage(pageSize));
            var mq = (await GetBaseMovieQuery()).Where(m => m.Credits.Any(c => c.Person.DisplayName.Contains(q))
                || (m.Actors != null && m.Actors.Contains(q)) || (m.Director != null && m.Director.Contains(q)) || (m.Writer != null && m.Writer.Contains(q)));
            var sq = (await GetBaseSeriesQuery()).Where(s => s.Credits.Any(c => c.Person.DisplayName.Contains(q))
                || (s.Actors != null && s.Actors.Contains(q)) || (s.Director != null && s.Director.Contains(q)) || (s.Writer != null && s.Writer.Contains(q)));
            (mq, sq) = ApplyTypeScope(ParseTypeScope(types), mq, sq);
            return Ok(await PageMergedAsync(mq, sq, page, pageSize, NormalizeSort(sort)));
        }

        // Series detail (mirror of GetMovie): the series + its normalized graph + seasons/episodes.
        [HttpGet("/API/GetSeries")]
        public async Task<IActionResult> GetSeries(int id)
        {
            int ageRestriction = await GetAgeRestrictionAsync();
            var series = await movieDb.Series.AsNoTracking().Include(s => s.PosterDetails).SingleOrDefaultAsync(s => s.Id == id);
            if (series == null) return BadRequest(new { Success = false, Message = "Series ID not found" });
            if (Web.RatingGate.EffectiveMpaRatingId(movieDb, series.MpaaRating, series.Rating, series.MpaaRatingInferred) > ageRestriction)
                return BadRequest(new { Success = false, Message = "Series ID not found" });
            var normalized = await GetNormalizedSeriesData(id, series);
            return Ok(new { Success = true, data = series, normalized });
        }

        public class SeriesUpdateDto
        {
            public int id { get; set; }
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
            public string? PosterLink { get; set; }
            public decimal? imdbRating { get; set; }
            public string? imdbID { get; set; }
            public int? RtTomatometer { get; set; }
            public int? RtPopcornmeter { get; set; }
            public bool RemoveFromRandom { get; set; }
        }

        // Edit a series in place (the modal's Edit form for series — the peer of UpdateMovie). Editor-gated;
        // a changed imdbID is conflict-checked, and a changed poster link is fetched. The richer normalized
        // graph (cast/genre FK rows) comes from a re-scrape/Re-fetch, not this scalar edit.
        [HttpPost("/API/UpdateSeries")]
        public async Task<IActionResult> UpdateSeries([FromBody] SeriesUpdateDto dto)
        {
            if (!await IsCurrentUserEditor()) return Forbid();
            if (dto == null || dto.id == 0) return BadRequest(new { Message = "Series ID is required", Success = false });

            var s = await movieDb.Series.Include(x => x.PosterDetails).SingleOrDefaultAsync(x => x.Id == dto.id);
            if (s == null) return NotFound(new { Message = "Series not found", Success = false });

            var newImdb = dto.imdbID?.Trim();
            if (!string.IsNullOrEmpty(newImdb) && !string.Equals(s.imdbID, newImdb, StringComparison.Ordinal))
            {
                if (!IsValidImdbId(newImdb)) return BadRequest(new { Message = $"'{newImdb}' is not a valid IMDb id", Success = false });
                if (await movieDb.Series.AnyAsync(x => x.imdbID == newImdb && x.Id != dto.id))
                    return Conflict(new { Message = $"Another series already has imdbID: {newImdb}", Success = false });
            }

            var posterLink = dto.PosterLink?.Trim();
            var posterChanged = !string.IsNullOrEmpty(posterLink) && !string.Equals(s.PosterDetails?.PosterLink, posterLink, StringComparison.Ordinal);

            s.Title = dto.Title?.Trim();
            s.SimpleTitle = dto.SimpleTitle?.Trim();
            s.Rating = dto.Rating?.Trim();
            s.ReleaseDate = dto.ReleaseDate;
            s.Runtime = dto.Runtime?.Trim();
            s.Genre = dto.Genre?.Trim();
            s.Director = dto.Director?.Trim();
            s.Writer = dto.Writer?.Trim();
            s.Actors = dto.Actors?.Trim();
            s.Plot = dto.Plot?.Trim();
            s.imdbRating = dto.imdbRating;
            s.imdbID = newImdb;
            s.RtTomatometer = dto.RtTomatometer;
            s.RtPopcornmeter = dto.RtPopcornmeter;
            s.RemoveFromRandom = dto.RemoveFromRandom;

            try { await movieDb.SaveChangesAsync(); }
            catch (Exception ex) { return Conflict(new { Message = $"Save failed: {ex.InnerException?.Message ?? ex.Message}", Success = false }); }

            if (posterChanged)
            {
                try { await DownloadAndSavePosterByIdAsync(s.Id, posterLink!, isSeries: true); } catch { /* poster best-effort */ }
            }

            var fresh = await movieDb.Series.Include(x => x.PosterDetails).SingleOrDefaultAsync(x => x.Id == dto.id);
            var normalized = await GetNormalizedSeriesData(dto.id, fresh!);
            return Ok(new { Success = true, data = fresh, normalized });
        }

        // Re-fetch IMDb data for a single title (movie or series) on demand — the modal's "Re-fetch from IMDb"
        // button. Re-resolves rating / certificate / year / plot / poster from the stored tt (OMDB → IMDb API,
        // never TMDB) and overwrites. Editor-gated. After a tt correction, this repopulates the metadata.
        [HttpPost("/API/RefetchTitle")]
        public async Task<IActionResult> RefetchTitle(int id, string kind = "movie")
        {
            if (!await IsCurrentUserEditor()) return Forbid();
            if (id == 0) return BadRequest(new { Success = false, Message = "id required" });
            bool isSeries = string.Equals(kind, "series", StringComparison.OrdinalIgnoreCase);
            var ok = await titleEnrichService.EnrichAsync(id, isSeries, force: true);
            if (!ok) return BadRequest(new { Success = false, Message = "Couldn't fetch IMDb data for this title (check the IMDb id)." });
            return Ok(new { Success = true });
        }

        private static string FileBaseName(string p) => (p ?? "").Replace('\\', '/').TrimEnd('/').Split('/')[^1];

        // Related misc videos (workprints, featurettes, shorts, specials) attached to a title via
        // RelatedMovieId/RelatedSeriesId — surfaced in the public modal's "Extras & Specials" section with
        // enough per-file info to play each. Pass the one relevant id; the other stays null.
        private async Task<List<object>> LoadModalMiscAsync(int? relatedMovieId, int? relatedSeriesId)
        {
            var rel = await movieDb.MiscVideos
                .Where(v => (relatedMovieId != null && v.RelatedMovieId == relatedMovieId)
                         || (relatedSeriesId != null && v.RelatedSeriesId == relatedSeriesId))
                .OrderBy(v => v.CollectionName).ThenBy(v => v.SortOrder).ThenBy(v => v.Title)
                .Select(v => new { v.Id, v.PlayableId, v.Title, v.Category, v.Year, v.CollectionName })
                .ToListAsync();
            if (rel.Count == 0) return new List<object>();
            var pids = rel.Select(v => v.PlayableId).ToList();
            var filesByPid = (await movieDb.MediaFiles.Where(f => pids.Contains(f.PlayableId))
                    .OrderBy(f => f.Role).ThenBy(f => f.PartNumber).ThenBy(f => f.Id)
                    .Select(f => new { f.Id, f.PlayableId, f.Path, Streamable = f.JellyfinItemId != null && f.MissingSinceUtc == null }).ToListAsync())
                .GroupBy(f => f.PlayableId)
                .ToDictionary(g => g.Key, g => g.Select(f => (object)new { mediaFileId = f.Id, name = FileBaseName(f.Path), isPlayable = f.Streamable }).ToList());
            return rel.Select(v => (object)new
            {
                title = v.Title, category = v.Category, year = v.Year, collectionName = v.CollectionName,
                files = filesByPid.TryGetValue(v.PlayableId, out var ff) ? ff : new List<object>(),
            }).ToList();
        }

        private async Task<object> GetNormalizedSeriesData(int id, Series series)
        {
            var genres = await movieDb.SeriesGenres.Where(g => g.SeriesId == id).OrderBy(g => g.Ordering).Select(g => g.Genre.Name).ToListAsync();
            var credits = await movieDb.SeriesCredits.Where(c => c.SeriesId == id).OrderBy(c => c.Ordering)
                .Select(c => new { c.Role, Nm = c.Person.ImdbNameId, Name = c.Person.DisplayName, c.Character }).ToListAsync();
            var summaries = await movieDb.SeriesPlotSummaries.Where(s => s.SeriesId == id).OrderBy(s => s.Ordering).Select(s => new { s.Author, s.Text }).ToListAsync();
            object People(CreditRole role) => credits.Where(c => c.Role == role).Select(c => new { nm = c.Nm, name = c.Name, character = c.Character }).ToList();

            var eps = await movieDb.Episodes.Where(e => e.SeriesId == id)
                .OrderBy(e => e.SeasonNumber).ThenBy(e => e.EpisodeNumber)
                .Select(e => new { e.SeasonNumber, e.EpisodeNumber, e.Title, e.ImdbId, e.RuntimeMinutes, e.PlayableId }).ToListAsync();
            var epPids = eps.Where(e => e.PlayableId != null).Select(e => e.PlayableId!.Value).ToList();
            // hasFile = mapping coverage (any MediaFile); isPlayable = Jellyfin-ready right now
            // (item synced + not gone missing) — the play button needs the stricter flag.
            var fileRows = await movieDb.MediaFiles.Where(f => epPids.Contains(f.PlayableId))
                .OrderBy(f => f.Role).ThenBy(f => f.PartNumber).ThenBy(f => f.Id)
                .Select(f => new { f.Id, f.PlayableId, f.Path, f.Role, f.Label, f.PartNumber, Streamable = f.JellyfinItemId != null && f.MissingSinceUtc == null }).ToListAsync();
            var withFile = fileRows.Select(f => f.PlayableId).Distinct().ToHashSet();
            var streamable = fileRows.Where(f => f.Streamable).Select(f => f.PlayableId).Distinct().ToHashSet();
            // Per-episode file list so the modal can surface multi-file episodes (segment Parts / Variants /
            // Extras), not just a single play button. Windows paths on a Linux host: split on both separators.
            static string BaseName(string p) => (p ?? "").Replace('\\', '/').TrimEnd('/').Split('/')[^1];
            var filesByPlayable = fileRows.GroupBy(f => f.PlayableId).ToDictionary(g => g.Key, g => g.Select(f => (object)new
            {
                mediaFileId = f.Id, role = f.Role.ToString(), label = f.Label, partNumber = f.PartNumber,
                isPlayable = f.Streamable, name = BaseName(f.Path),
            }).ToList());
            var noFiles = new List<object>();
            // The (S0,E0,"Extras") pseudo-episode is a holder for series/season-level extras, not a real
            // episode — pull it out of the season list and surface its files in the "Extras & Specials" section.
            var extrasHolder = eps.FirstOrDefault(e => e.SeasonNumber == 0 && e.EpisodeNumber == 0 && e.Title == "Extras");
            var seasonEps = extrasHolder == null ? eps : eps.Where(e => e != extrasHolder).ToList();
            var seriesExtras = (extrasHolder?.PlayableId != null && filesByPlayable.TryGetValue(extrasHolder.PlayableId.Value, out var xfl)) ? xfl : noFiles;
            var relatedMisc = await LoadModalMiscAsync(null, id);
            var seasons = seasonEps.GroupBy(e => e.SeasonNumber).OrderBy(g => g.Key).Select(g => new
            {
                season = g.Key,
                episodes = g.Select(e => new
                {
                    episode = e.EpisodeNumber,
                    title = e.Title,
                    imdbId = e.ImdbId,
                    runtimeMinutes = e.RuntimeMinutes,
                    playableId = e.PlayableId,
                    hasFile = e.PlayableId != null && withFile.Contains(e.PlayableId.Value),
                    isPlayable = e.PlayableId != null && streamable.Contains(e.PlayableId.Value),
                    files = (e.PlayableId != null && filesByPlayable.TryGetValue(e.PlayableId.Value, out var efl)) ? efl : noFiles,
                }).ToList(),
            }).ToList();

            return new
            {
                verified = series.ImdbVerifiedDate != null,
                needsReview = series.ImdbNeedsReview,
                titleType = series.TitleType.ToString(),
                runtimeMinutes = series.RuntimeMinutes,
                plotFull = series.PlotFull,
                plotSynopsis = series.PlotSynopsis,
                mpaaRating = series.MpaaRating,
                imdbReleaseDate = series.ImdbReleaseDate,
                imdbRating = series.ImdbRatingScraped,
                genres,
                cast = People(CreditRole.Actor),
                directors = People(CreditRole.Director),
                writers = People(CreditRole.Writer),
                summaries,
                insight = await LoadInsightAsync(InsightSubjectKind.Series, id),
                isSeries = true,
                seasons,
                seriesExtras,
                relatedMisc,
                seasonCount = series.SeasonCount,
                episodeCount = series.EpisodeCount,
                network = series.Network,
                startYear = series.StartYear,
                endYear = series.EndYear,
            };
        }

        [HttpPost("/API/InsertMovie")]
        public async Task<IActionResult> InsertMovie([FromBody] Movie movie)
        {
            if (!await IsCurrentUserEditor()) return Forbid();
            var checkMovie = await movieDb.Movies.AnyAsync(d => d.imdbID == movie.imdbID);

            if (checkMovie)
            {
                return Conflict(new { Message = $"Movie already Exists: {movie.Title}", Success = false });
            }

            movie.Title = movie.Title?.Trim();
            movie.SimpleTitle = movie.SimpleTitle?.Trim();
            movie.Rating = movie.Rating?.Trim();
            movie.Runtime = movie.Runtime?.Trim();
            movie.Genre = movie.Genre?.Trim();
            movie.Director = movie.Director?.Trim();
            movie.Writer = movie.Writer?.Trim();
            movie.Actors = movie.Actors?.Trim();
            movie.Plot = movie.Plot?.Trim();
            movie.PosterLink = movie.PosterLink?.Trim();
            movie.imdbID = movie.imdbID?.Trim();
            movie.UploadedDate = DateTime.Now;
            // Every movie gets a Playable (Phase-4 cutover) so files / progress / channel slots attach to it.
            movie.Playable = new Playable { Kind = PlayableKind.Movie };

            movieDb.Movies.Add(movie);
            try
            {
                movieDb.SaveChanges();
            }
            catch
            {
                return Conflict(new { Message = "Save failed", Success = false });
            }

            // Parse the submitted text fields into the normalized model (genres, runtime,
            // plot, rating, cast/crew). The movie stays unverified so the IMDB scrape can
            // later enrich it with nm-keyed cast, characters, and summaries.
            try
            {
                await MovieNormalizer.ApplyAllAsync(movieDb, movie);
            }
            catch
            {
                // Normalized parse failed; the movie itself is already saved.
            }

            if (!string.IsNullOrWhiteSpace(movie.PosterLink))
            {
                await DownloadAndSavePoster(movie, movie.PosterLink);
            }

            return Ok(new { Message = "Movie saved", Success = true });
        }

        public class MovieUpdateDto
        {
            public int id { get; set; }
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
            public string? PosterLink { get; set; }
            public decimal? imdbRating { get; set; }
            public string? imdbID { get; set; }
            public int? tomatoRating { get; set; }
            public int? RtTomatometer { get; set; }
            public int? RtPopcornmeter { get; set; }
            public bool RemoveFromRandom { get; set; }
        }

        [HttpPost("/API/UpdateMovie")]
        public async Task<IActionResult> UpdateMovie([FromBody] MovieUpdateDto dto)
        {
            if (!await IsCurrentUserEditor()) return Forbid();
            if (dto == null)
                return BadRequest(new { Message = "Invalid movie data", Success = false });

            if (dto.id == 0)
                return BadRequest(new { Message = "Movie ID is required", Success = false });

            var existing = await movieDb.Movies.Include(m => m.PosterDetails).SingleOrDefaultAsync(m => m.id == dto.id);
            if (existing == null)
                return NotFound(new { Message = "Movie not found", Success = false });

            dto.Title = dto.Title?.Trim();
            dto.SimpleTitle = dto.SimpleTitle?.Trim();
            dto.Rating = dto.Rating?.Trim();
            dto.Runtime = dto.Runtime?.Trim();
            dto.Genre = dto.Genre?.Trim();
            dto.Director = dto.Director?.Trim();
            dto.Writer = dto.Writer?.Trim();
            dto.Actors = dto.Actors?.Trim();
            dto.Plot = dto.Plot?.Trim();
            dto.PosterLink = dto.PosterLink?.Trim();
            dto.imdbID = dto.imdbID?.Trim();

            var posterLinkChanged = !string.Equals(existing.PosterDetails?.PosterLink, dto.PosterLink, StringComparison.Ordinal);

            if (!string.Equals(existing.imdbID, dto.imdbID, StringComparison.Ordinal) && !string.IsNullOrEmpty(dto.imdbID))
            {
                var imdbConflict = await movieDb.Movies.AnyAsync(m => m.imdbID == dto.imdbID && m.id != dto.id);
                if (imdbConflict)
                    return Conflict(new { Message = $"Another movie already has imdbID: {dto.imdbID}", Success = false });
            }

            // Detect which legacy text fields actually changed, so we re-parse only those into
            // the normalized tables (the user's edit wins for that field; unchanged fields keep
            // any richer scraped data).
            bool genreChanged = !string.Equals(existing.Genre, dto.Genre, StringComparison.Ordinal);
            bool runtimeChanged = !string.Equals(existing.Runtime, dto.Runtime, StringComparison.Ordinal);
            bool plotChanged = !string.Equals(existing.Plot, dto.Plot, StringComparison.Ordinal);
            bool ratingChanged = !string.Equals(existing.Rating, dto.Rating, StringComparison.Ordinal);
            bool directorChanged = !string.Equals(existing.Director, dto.Director, StringComparison.Ordinal);
            bool writerChanged = !string.Equals(existing.Writer, dto.Writer, StringComparison.Ordinal);
            bool actorsChanged = !string.Equals(existing.Actors, dto.Actors, StringComparison.Ordinal);

            existing.Title = dto.Title;
            existing.SimpleTitle = dto.SimpleTitle;
            existing.Rating = dto.Rating;
            existing.ReleaseDate = dto.ReleaseDate;
            existing.Runtime = dto.Runtime;
            existing.Genre = dto.Genre;
            existing.Director = dto.Director;
            existing.Writer = dto.Writer;
            existing.Actors = dto.Actors;
            existing.Plot = dto.Plot;
            existing.imdbRating = dto.imdbRating;
            existing.imdbID = dto.imdbID;
            existing.tomatoRating = dto.tomatoRating;
            existing.RtTomatometer = dto.RtTomatometer;
            existing.RtPopcornmeter = dto.RtPopcornmeter;
            existing.RemoveFromRandom = dto.RemoveFromRandom;

            try
            {
                await movieDb.SaveChangesAsync();
            }
            catch (Exception ex)
            {
                return Conflict(new { Message = $"Save failed: {ex.InnerException?.Message ?? ex.Message}", Success = false });
            }

            // Re-parse only the changed text fields into the normalized model.
            try
            {
                if (runtimeChanged) MovieNormalizer.ApplyRuntime(existing);
                if (plotChanged) MovieNormalizer.ApplyPlot(existing);
                if (ratingChanged) MovieNormalizer.ApplyRating(existing);
                if (genreChanged) await MovieNormalizer.ReplaceGenresAsync(movieDb, existing.id, existing.Genre);
                if (directorChanged) await MovieNormalizer.ReplaceRoleCreditsAsync(movieDb, existing.id, CreditRole.Director, existing.Director);
                if (writerChanged) await MovieNormalizer.ReplaceRoleCreditsAsync(movieDb, existing.id, CreditRole.Writer, existing.Writer);
                if (actorsChanged)
                {
                    await MovieNormalizer.ReplaceRoleCreditsAsync(movieDb, existing.id, CreditRole.Actor, existing.Actors);
                    MovieNormalizer.ApplyTopCast(existing);
                }
                if (genreChanged || runtimeChanged || plotChanged || ratingChanged || directorChanged || writerChanged || actorsChanged)
                    await movieDb.SaveChangesAsync();
            }
            catch
            {
                // Normalized re-parse failed; the legacy update is already saved.
            }

            string posterError = null;
            if (posterLinkChanged && !string.IsNullOrWhiteSpace(dto.PosterLink))
            {
                if (existing.PosterDetails == null)
                {
                    var pd = new MoviePosterDetails { MovieId = existing.id, PosterLink = dto.PosterLink };
                    movieDb.MoviePosterDetails.Add(pd);
                }
                else
                {
                    existing.PosterDetails.PosterLink = dto.PosterLink;
                }
                await movieDb.SaveChangesAsync();

                try
                {
                    await DownloadAndSavePoster(existing, dto.PosterLink, force: true);
                }
                catch (Exception ex)
                {
                    posterError = ex.Message;
                }
            }

            var message = posterError != null
                ? $"Movie updated, but poster download failed: {posterError}"
                : "Movie updated";
            return Ok(new { Message = message, Success = true, data = existing });
        }

        private async Task DownloadAndSavePoster(Movie movie, string posterLink, bool force = false)
        {
            var result = await httpClient.GetAsync(posterLink);
            result.EnsureSuccessStatusCode();
            var content = await result.Content.ReadAsByteArrayAsync();
            await imageRepo.SaveImage(movie.id, PosterImageVariant.Main, content);
            // The main image is already on disk; a thumbnail failure must not abort the save (which would
            // leave the title with a main poster but PosterVersion unbumped and no thumb — a blank card
            // even though /Image works). Isolate it like PosterFetchService does; BackfillThumbnails can
            // regenerate a missed thumb later from the on-disk main.
            try { await shrinkService.EnsurePosterThumnailExists(movie.id, force); }
            catch (Exception ex) { logger.LogWarning(ex, "Thumbnail generation failed for movie {Id}; saving poster without it", movie.id); }

            var thumbnailBytes = await imageRepo.GetImage(movie.id, PosterImageVariant.Thumbnail);
            var dominantColor = ComputeAverageColor(thumbnailBytes ?? content);

            var posterDetails = await movieDb.MoviePosterDetails.FindAsync(movie.id);
            if (posterDetails == null)
            {
                posterDetails = new MoviePosterDetails { MovieId = movie.id, PosterLink = posterLink, PosterVersion = 1, DominantColor = dominantColor };
                movieDb.MoviePosterDetails.Add(posterDetails);
            }
            else
            {
                posterDetails.PosterLink = posterLink;
                posterDetails.PosterVersion++;
                posterDetails.DominantColor = dominantColor;
            }
            await movieDb.SaveChangesAsync();
        }

        // Download a poster from a link and persist it for a title by id — movie or series. Bumps the
        // PosterVersion (cache-bust) and recomputes the dominant color, exactly like DownloadAndSavePoster
        // but addressable by id+table so the review tool can pull a poster for a pending row. Returns the
        // new version.
        private async Task<int> DownloadAndSavePosterByIdAsync(int id, string posterLink, bool isSeries)
        {
            var bucket = PosterBucket.ForTitle(isSeries);
            var result = await httpClient.GetAsync(posterLink);
            result.EnsureSuccessStatusCode();
            var content = await result.Content.ReadAsByteArrayAsync();
            await imageRepo.SaveImage(id, PosterImageVariant.Main, content, bucket);
            await shrinkService.EnsurePosterThumnailExists(id, true, bucket);
            var thumbnailBytes = await imageRepo.GetImage(id, PosterImageVariant.Thumbnail, bucket);
            var dominantColor = ComputeAverageColor(thumbnailBytes ?? content);

            if (isSeries)
            {
                var pd = await movieDb.SeriesPosterDetails.FindAsync(id);
                if (pd == null)
                {
                    pd = new SeriesPosterDetails { SeriesId = id, PosterLink = posterLink, PosterVersion = 1, DominantColor = dominantColor };
                    movieDb.SeriesPosterDetails.Add(pd);
                }
                else { pd.PosterLink = posterLink; pd.PosterVersion++; pd.DominantColor = dominantColor; }
                await movieDb.SaveChangesAsync();
                return pd.PosterVersion;
            }
            else
            {
                var pd = await movieDb.MoviePosterDetails.FindAsync(id);
                if (pd == null)
                {
                    pd = new MoviePosterDetails { MovieId = id, PosterLink = posterLink, PosterVersion = 1, DominantColor = dominantColor };
                    movieDb.MoviePosterDetails.Add(pd);
                }
                else { pd.PosterLink = posterLink; pd.PosterVersion++; pd.DominantColor = dominantColor; }
                await movieDb.SaveChangesAsync();
                return pd.PosterVersion;
            }
        }

        [HttpPost("/API/ScanPosterColors")]
        public async Task<IActionResult> ScanPosterColors(int batchSize = 50)
        {
            batchSize = Math.Clamp(batchSize, 1, 500);

            var batch = await movieDb.MoviePosterDetails
                .Where(pd => pd.DominantColor == null)
                .OrderBy(pd => pd.MovieId)
                .Take(batchSize)
                .ToListAsync();

            if (batch.Count == 0)
            {
                var total = await movieDb.MoviePosterDetails.CountAsync();
                return Ok(new { Processed = 0, Skipped = 0, Remaining = 0, Total = total, Errors = Array.Empty<string>() });
            }

            int processed = 0;
            int skipped = 0;
            var errors = new List<string>();

            foreach (var pd in batch)
            {
                try
                {
                    var hasThumb = await imageRepo.HasImage(pd.MovieId, PosterImageVariant.Thumbnail);
                    var variant = hasThumb ? PosterImageVariant.Thumbnail : PosterImageVariant.Main;
                    if (!hasThumb && !await imageRepo.HasImage(pd.MovieId, PosterImageVariant.Main))
                    {
                        skipped++;
                        continue;
                    }

                    var imageBytes = await imageRepo.GetImage(pd.MovieId, variant);
                    pd.DominantColor = ComputeAverageColor(imageBytes);
                    processed++;
                }
                catch (Exception ex)
                {
                    errors.Add($"Movie {pd.MovieId}: {ex.Message}");
                }
            }

            await movieDb.SaveChangesAsync();

            var remaining = await movieDb.MoviePosterDetails.CountAsync(pd => pd.DominantColor == null);

            return Ok(new { Processed = processed, Skipped = skipped, Remaining = remaining, Errors = errors });
        }

        private static string ComputeAverageColor(byte[] imageBytes)
        {
            using var image = Image.Load<Rgba32>(imageBytes);
            long totalR = 0, totalG = 0, totalB = 0, count = 0;

            image.ProcessPixelRows(accessor =>
            {
                for (int y = 0; y < accessor.Height; y++)
                {
                    var row = accessor.GetRowSpan(y);
                    for (int x = 0; x < row.Length; x++)
                    {
                        var pixel = row[x];
                        if (pixel.A < 128) continue;
                        totalR += pixel.R;
                        totalG += pixel.G;
                        totalB += pixel.B;
                        count++;
                    }
                }
            });

            if (count == 0)
                return "#000000";

            return $"#{totalR / count:X2}{totalG / count:X2}{totalB / count:X2}";
        }

        private static readonly PasswordHasher<User> passwordHasher = new();

        public class LoginRequest
        {
            public string Username { get; set; }
            public string Password { get; set; }
        }

        // Password comes in the JSON body, never the query string, so it can't leak into request logs.
        [HttpPost("/API/Login")]
        public async Task<IActionResult> Login([FromBody] LoginRequest request)
        {
            var givenUser = request?.Username?.Trim();

            if (string.IsNullOrEmpty(givenUser))
            {
                return NotFound();
            }

            var user = await movieDb.Users.SingleOrDefaultAsync(d => d.Username == givenUser);

            // Set when this session proved control of the account with a password — the
            // trust boundary for streaming (§3.1 of the streaming plan). Mere
            // authentication is not it: unknown usernames still auto-create accounts.
            bool passwordVerified = false;

            if (user == null)
            {
                user = new User()
                {
                    Username = givenUser
                };

                await movieDb.Users.AddAsync(user);
            }
            else if (user.PasswordHash != null)
            {
                if (string.IsNullOrEmpty(request.Password))
                {
                    return Unauthorized(new { requiresPassword = true, message = "This account is password-protected." });
                }

                var failKey = $"LoginFailures:{user.UserID}";
                if (memoryCache.TryGetValue(failKey, out int failures) && failures >= 5)
                {
                    return StatusCode(StatusCodes.Status429TooManyRequests,
                        new { requiresPassword = true, message = "Too many failed attempts. Try again in 15 minutes." });
                }

                var verification = passwordHasher.VerifyHashedPassword(user, user.PasswordHash, request.Password);
                if (verification == PasswordVerificationResult.Failed)
                {
                    memoryCache.Set(failKey, failures + 1, new MemoryCacheEntryOptions
                    {
                        AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(15),
                        Size = 1
                    });
                    return Unauthorized(new { requiresPassword = true, message = "Incorrect password." });
                }

                memoryCache.Remove(failKey);
                passwordVerified = true;

                if (verification == PasswordVerificationResult.SuccessRehashNeeded)
                {
                    user.PasswordHash = passwordHasher.HashPassword(user, request.Password);
                }
            }

            user.LastLogin = DateTime.UtcNow;
            await movieDb.SaveChangesAsync();

            await SignInWithSessionClaims(user, passwordVerified);

            return Json(await BuildUserPayload(user));
        }

        // Issues (or re-issues) the auth cookie. amr=pwd marks a password-verified
        // session; the StreamingUser policy keys off it (§3.1).
        private async Task SignInWithSessionClaims(User user, bool passwordVerified)
        {
            var claims = new List<Claim>
            {
                new Claim(ClaimTypes.NameIdentifier, user.UserID.ToString()),
                new Claim(ClaimTypes.Name, user.Username)
            };
            if (passwordVerified)
            {
                claims.Add(new Claim("amr", "pwd"));
            }

            var claimsIdentity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
            var authProperties = new Microsoft.AspNetCore.Authentication.AuthenticationProperties
            {
                IsPersistent = true,
                ExpiresUtc = DateTimeOffset.UtcNow.AddDays(30)
            };

            await HttpContext.SignInAsync(
                CookieAuthenticationDefaults.AuthenticationScheme,
                new ClaimsPrincipal(claimsIdentity),
                authProperties);
        }

        // Restores a session from the auth cookie without re-running login. Required for
        // password-protected accounts: the SPA can no longer silently re-login on page load.
        [HttpGet("/API/Me")]
        public async Task<IActionResult> Me()
        {
            var userId = GetCurrentUserId();
            if (!userId.HasValue)
            {
                return Unauthorized();
            }

            var user = await movieDb.Users.FindAsync(userId.Value);
            if (user == null)
            {
                return Unauthorized();
            }

            user.LastLogin = DateTime.UtcNow;
            await movieDb.SaveChangesAsync();

            // Never cache the session payload. A stale cached GET here once served a user an empty
            // ratings list while the server actually had 200+ — the Rate page then looked wiped.
            Response.Headers["Cache-Control"] = "no-store, no-cache, must-revalidate";
            Response.Headers["Pragma"] = "no-cache";

            return Json(await BuildUserPayload(user));
        }

        public class SetPasswordRequest
        {
            public string CurrentPassword { get; set; }
            public string NewPassword { get; set; }
        }

        [HttpPost("/API/SetPassword")]
        public async Task<IActionResult> SetPassword([FromBody] SetPasswordRequest request)
        {
            var userId = GetCurrentUserId();
            if (!userId.HasValue)
            {
                return Unauthorized();
            }

            var user = await movieDb.Users.FindAsync(userId.Value);
            if (user == null)
            {
                return Unauthorized();
            }

            if (user.PasswordHash != null)
            {
                if (string.IsNullOrEmpty(request?.CurrentPassword) ||
                    passwordHasher.VerifyHashedPassword(user, user.PasswordHash, request.CurrentPassword) == PasswordVerificationResult.Failed)
                {
                    return Unauthorized(new { success = false, message = "Current password is incorrect." });
                }
            }
            else if (!string.IsNullOrEmpty(request?.NewPassword) && !IsAdminUsername(user.Username))
            {
                // Creating a *first* password is restricted: streaming access is provisioned by an
                // admin, so a user can't self-grant it. (An account that already has a password can
                // still freely change or remove it above.) Config admins are the one exception, so
                // they can bootstrap their own password and unlock the admin tools.
                return StatusCode(StatusCodes.Status403Forbidden,
                    new { success = false, message = "An administrator must set your initial password." });
            }

            if (string.IsNullOrEmpty(request?.NewPassword))
            {
                // Empty new password removes the password, returning the account to passwordless login.
                user.PasswordHash = null;
            }
            else
            {
                if (request.NewPassword.Length < 8)
                {
                    return BadRequest(new { success = false, message = "Password must be at least 8 characters." });
                }

                user.PasswordHash = passwordHasher.HashPassword(user, request.NewPassword);
            }

            await movieDb.SaveChangesAsync();

            // Re-issue the cookie so the session's amr claim tracks the account state:
            // setting a password from this session proves account control (claim added);
            // removing the password drops streaming rights immediately for this session.
            await SignInWithSessionClaims(user, passwordVerified: user.PasswordHash != null);

            return Ok(new { success = true, hasPassword = user.PasswordHash != null });
        }

        private async Task<object> BuildUserPayload(User user)
        {
            // One round-trip for all of this user's viewings; the kinds are split in memory below
            // (previously four separate Viewings queries — Seen / Want / misc-Seen / Rated).
            var viewings = await movieDb.Viewings
                .Where(v => v.UserID == user.UserID)
                .Select(v => new { v.ViewingType, v.MovieID, v.SeriesId, v.MiscVideoId, v.ViewingData })
                .ToListAsync();

            // Seen / Want lists carry both movie and series ids (a viewing targets one or the other; the
            // shared id space + the card's Kind disambiguate). MovieID ?? SeriesId yields the id either way.
            var moviesSeen = viewings.Where(d => d.ViewingType == "Seen")
                .Select(d => d.MovieID ?? d.SeriesId).Where(x => x != null).Select(x => x!.Value).ToList();
            var moviesToWatch = viewings.Where(d => d.ViewingType == "WantToWatch")
                .Select(d => d.MovieID ?? d.SeriesId).Where(x => x != null).Select(x => x!.Value).ToList();

            // Watched MiscVideo ids (their own id space, so kept separate from moviesSeen). The Rate page
            // fetches their cards via GetMiscByIds.
            var miscSeen = viewings.Where(d => d.ViewingType == "Seen" && d.MiscVideoId != null)
                .Select(d => d.MiscVideoId!.Value).ToList();

            // User's own 0–100 ratings. Legacy + new ratings both live on Viewing as ViewingType=="Rated"
            // with the score in ViewingData. Keyed by a composite "{kind}:{id}" because MiscVideo has its own
            // id space that can collide with a movie id. Non-numeric / out-of-range values are treated as
            // unrated and skipped, so only real scores surface.
            var ratings = new Dictionary<string, int>();
            foreach (var r in viewings.Where(v => v.ViewingType == "Rated" && v.ViewingData != null))
            {
                if (!int.TryParse(r.ViewingData, out var score) || score < 0 || score > 100) continue;
                string? key = r.MovieID != null ? $"movie:{r.MovieID.Value}"
                            : r.SeriesId != null ? $"series:{r.SeriesId.Value}"
                            : r.MiscVideoId != null ? $"misc:{r.MiscVideoId.Value}"
                            : null;
                if (key != null) ratings[key] = score;
            }

            // One round-trip for all of this user's settings; each is picked by key in memory below
            // (previously ~8 separate UserSettings queries).
            var settings = await movieDb.UserSettings
                .Where(u => u.UserID == user.UserID)
                .Select(s => new { s.SettingKey, s.SettingValue })
                .ToListAsync();
            string? Setting(string key) => settings.FirstOrDefault(s => s.SettingKey == key)?.SettingValue;

            // Rate-page anchors — per-user JSON; parsed defensively. Bare JSON array [{ "id":"a1","value":30 }].
            System.Text.Json.JsonElement ratingAnchors;
            try
            {
                var anchorsRaw = Setting("RatingAnchors");
                ratingAnchors = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(
                    string.IsNullOrWhiteSpace(anchorsRaw) ? "[]" : anchorsRaw);
            }
            catch (System.Text.Json.JsonException)
            {
                ratingAnchors = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>("[]");
            }

            int? ageRestriction = int.TryParse(Setting("AgeRestriction"), out int parsedAgeRestriction) ? parsedAgeRestriction : (int?)null;
            var cardStyle = Setting("CardStyle") ?? "standard";
            var canEditMovies = Setting("CanEditMovies") == "true";
            bool enablePagination = bool.TryParse(Setting("EnablePagination"), out var parsedEnablePagination) && parsedEnablePagination;
            bool showBoardgameExpansions = bool.TryParse(Setting("ShowBoardgameExpansions"), out var parsedShowExpansions) && parsedShowExpansions;
            var comicSiteAccess = Setting("ComicSiteAccess");
            // Family photo album membership (photos-plan.md §2.1). Surfaced only so the nav can hide
            // /photos for non-members — the real gate is the RequireFamilyAlbum policy, re-checked
            // server-side on every /API/Photos request. Not self-grantable: the key is absent from
            // SelfServiceSettingKeys, so it can only be set through the admin surface.
            var familyAlbum = string.Equals(
                Setting(MovieTheater.Photos.FamilyAlbumGate.SettingKey),
                MovieTheater.Photos.FamilyAlbumGate.SettingValue,
                StringComparison.OrdinalIgnoreCase);

            // favorite channels — SettingValue is a JSON int array; parse defensively (empty on malformed)
            int[] favoriteChannels;
            try
            {
                var favRaw = Setting("FavoriteChannels");
                favoriteChannels = string.IsNullOrWhiteSpace(favRaw)
                    ? Array.Empty<int>()
                    : (System.Text.Json.JsonSerializer.Deserialize<int[]>(favRaw) ?? Array.Empty<int>());
            }
            catch (System.Text.Json.JsonException) { favoriteChannels = Array.Empty<int>(); }

            var hasPassword = user.PasswordHash != null;

            // Drives whether the SPA shows the admin tools. Mirrors the server gate: a config admin
            // who has a password (and so can become password-verified). A passwordless admin gets
            // false here, which is correct — they must set their password before they can administer.
            var isAdmin = IsAdminUsername(user.Username) && hasPassword;

            return new { user.Username, moviesSeen, moviesToWatch, miscSeen, ratings, ratingAnchors, ageRestriction, cardStyle, canEditMovies, enablePagination, showBoardgameExpansions, comicSiteAccess, favoriteChannels, hasPassword, isAdmin, familyAlbum };
        }

        [HttpPost("/API/Logout")]
        public async Task<IActionResult> Logout()
        {
            await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return Ok(new { Success = true });
        }

        [HttpGet("/API/ImdbApiLookupImdbID")]
        public async Task<Movie> ImdbApiLookupImdbID(string imdbID)
        {
            return await imdb.ImdbApiLookupImdbID(imdbID);
        }



        [HttpGet("/API/ImdbApiLookupName")]
        public async Task<Movie> ImdbApiLookupName(string name)
        {
            return await imdb.ImdbApiLookupName(name);
        }

        [HttpGet("/API/TMDBLookupImdbID")]
        public async Task<MovieDto> TmdbLookupImdbID(string imdbID)
        {
            return await tmdb.GetMovie(imdbID);
        }

        [HttpGet("/API/TMDBLookupName")]
        public async Task<MovieDto> TmdbLookupName(string name)
        {
            return await tmdb.GetMovieByName(name);
        }

        [HttpGet("/API/OMDBLookupName")]
        public async Task<Movie> OmdbLookupName(string name)
        {
            return await omdb.GetMovieByName(name);
        }

        [HttpGet("/API/OMDBLookupImdbID")]
        public async Task<Movie> OmdbLookupImdbID(string imdbID)
        {
            return await omdb.GetMovieByImdbId(imdbID);
        }

        [HttpPost("/API/SetViewingState")]
        public async Task<IActionResult> SetViewingState([FromBody] ViewingState viewingState)
        {
            if (viewingState == null)
            {
                return BadRequest(new { Success = false, Message = "No User Movie Data Provided." });
            }

            // Act on the authenticated cookie identity, never the client-supplied username —
            // otherwise anyone could edit a password-protected user's lists without the password.
            var currentUserId = GetCurrentUserId();
            if (!currentUserId.HasValue)
            {
                return Unauthorized(new { Success = false, Message = "Not logged in." });
            }

            var user = await movieDb.Users.FindAsync(currentUserId.Value);
            if (user == null)
            {
                return Unauthorized(new { Success = false, Message = "No User Found." });
            }

            var action = viewingState.Action == ViewingType.SetWatched ? "Seen" : "WantToWatch";
            // movie/series share an id space; misc videos have their own. The card's Kind says which
            // target the id refers to, and which typed FK on Viewing to read/write.
            bool isSeries = string.Equals(viewingState.Kind, "series", StringComparison.OrdinalIgnoreCase);
            bool isMisc = string.Equals(viewingState.Kind, "misc", StringComparison.OrdinalIgnoreCase);
            int id = viewingState.MovieID;

            if (isSeries)
            {
                if (!await movieDb.Series.AnyAsync(s => s.Id == id))
                    return BadRequest(new { Success = false, Message = "Invalid Series ID." });
            }
            else if (isMisc)
            {
                if (!await movieDb.MiscVideos.AnyAsync(mv => mv.Id == id))
                    return BadRequest(new { Success = false, Message = "Invalid MiscVideo ID." });
            }
            else if (!await movieDb.Movies.AnyAsync(m => m.id == id))
            {
                return BadRequest(new { Success = false, Message = "Invalid Movie ID." });
            }

            var existingViewing = isSeries
                ? await movieDb.Viewings.FirstOrDefaultAsync(e => e.UserID == user.UserID && e.SeriesId == id && e.ViewingType == action)
                : isMisc
                    ? await movieDb.Viewings.FirstOrDefaultAsync(e => e.UserID == user.UserID && e.MiscVideoId == id && e.ViewingType == action)
                    : await movieDb.Viewings.FirstOrDefaultAsync(e => e.UserID == user.UserID && e.MovieID == id && e.ViewingType == action);
            bool shouldCreateNew = existingViewing == null && viewingState.SetActive;
            bool shouldDeleteExisting = existingViewing != null && !viewingState.SetActive;

            if (shouldCreateNew)
            {
                var newViewing = new Viewing
                {
                    MovieID = (isSeries || isMisc) ? (int?)null : id,
                    SeriesId = isSeries ? id : (int?)null,
                    MiscVideoId = isMisc ? id : (int?)null,
                    UserID = user.UserID,
                    ViewingType = action,
                };
                await movieDb.Viewings.AddAsync(newViewing);
            }
            if (shouldDeleteExisting)
            {
                movieDb.Viewings.Remove(existingViewing);
            }

            await movieDb.SaveChangesAsync();
            return Ok(new { Success = true });
        }

        public class RatingItem
        {
            public int Id { get; set; }
            /// <summary>"movie" (default), "series", or "misc" — selects which typed FK on Viewing to write.</summary>
            public string Kind { get; set; } = "movie";
            /// <summary>0–100 score, or null to clear the rating (remove the row — "unranked").</summary>
            public int? Value { get; set; }
        }

        public class SetRatingsRequest
        {
            public List<RatingItem> Items { get; set; } = new();
        }

        // Upsert a user's own 0–100 ratings. Stored on Viewing as ViewingType=="Rated" with the score in
        // ViewingData (the same rows the legacy rating feature used). Mirrors SetViewingState's cookie-identity
        // and kind→FK dispatch. Bounded + idempotent: one capped chunk per call, writes only changed rows, and
        // re-sending the same value is a no-op — the Rate page's autosave drives the chunk loop to completion.
        [HttpPost("/API/SetRatings")]
        public async Task<IActionResult> SetRatings([FromBody] SetRatingsRequest request)
        {
            var items = request?.Items;
            if (items == null || items.Count == 0)
                return Ok(new { Success = true, updated = 0, skipped = 0, deleted = 0 });

            // Bounded write (project rule): the caller sends capped chunks and drives the loop to completion.
            if (items.Count > 200)
                return BadRequest(new { Success = false, Message = "Too many items; send at most 200 per call." });

            var currentUserId = GetCurrentUserId();
            if (!currentUserId.HasValue)
                return Unauthorized(new { Success = false, Message = "Not logged in." });
            int uid = currentUserId.Value;

            static string NormKind(string? k) =>
                string.Equals(k, "series", StringComparison.OrdinalIgnoreCase) ? "series"
                : string.Equals(k, "misc", StringComparison.OrdinalIgnoreCase) ? "misc"
                : "movie";

            var movieIds = items.Where(i => NormKind(i.Kind) == "movie").Select(i => i.Id).Distinct().ToList();
            var seriesIds = items.Where(i => NormKind(i.Kind) == "series").Select(i => i.Id).Distinct().ToList();
            var miscIds = items.Where(i => NormKind(i.Kind) == "misc").Select(i => i.Id).Distinct().ToList();

            // Validate targets exist (one set-load per kind).
            var validMovies = movieIds.Count == 0 ? new HashSet<int>()
                : (await movieDb.Movies.Where(m => movieIds.Contains(m.id)).Select(m => m.id).ToListAsync()).ToHashSet();
            var validSeries = seriesIds.Count == 0 ? new HashSet<int>()
                : (await movieDb.Series.Where(s => seriesIds.Contains(s.Id)).Select(s => s.Id).ToListAsync()).ToHashSet();
            var validMisc = miscIds.Count == 0 ? new HashSet<int>()
                : (await movieDb.MiscVideos.Where(mv => miscIds.Contains(mv.Id)).Select(mv => mv.Id).ToListAsync()).ToHashSet();

            // Load the user's existing "Rated" rows for just these targets (one query).
            var existingRows = await movieDb.Viewings
                .Where(v => v.UserID == uid && v.ViewingType == "Rated" &&
                    ((v.MovieID != null && movieIds.Contains(v.MovieID.Value)) ||
                     (v.SeriesId != null && seriesIds.Contains(v.SeriesId.Value)) ||
                     (v.MiscVideoId != null && miscIds.Contains(v.MiscVideoId.Value))))
                .ToListAsync();
            Viewing? Find(string kind, int id) => existingRows.FirstOrDefault(v =>
                kind == "series" ? v.SeriesId == id : kind == "misc" ? v.MiscVideoId == id : v.MovieID == id);

            int updated = 0, skipped = 0, deleted = 0;
            foreach (var item in items)
            {
                var kind = NormKind(item.Kind);
                bool exists = kind == "series" ? validSeries.Contains(item.Id)
                            : kind == "misc" ? validMisc.Contains(item.Id)
                            : validMovies.Contains(item.Id);
                if (!exists) { skipped++; continue; }

                var existing = Find(kind, item.Id);

                if (item.Value == null)
                {
                    if (existing != null) { movieDb.Viewings.Remove(existing); existingRows.Remove(existing); deleted++; }
                    else skipped++;
                    continue;
                }

                var data = Math.Clamp(item.Value.Value, 0, 100).ToString(System.Globalization.CultureInfo.InvariantCulture);
                if (existing == null)
                {
                    var row = new Viewing
                    {
                        MovieID = kind == "movie" ? item.Id : (int?)null,
                        SeriesId = kind == "series" ? item.Id : (int?)null,
                        MiscVideoId = kind == "misc" ? item.Id : (int?)null,
                        UserID = uid,
                        ViewingType = "Rated",
                        ViewingData = data,
                    };
                    await movieDb.Viewings.AddAsync(row);
                    existingRows.Add(row);
                    updated++;
                }
                else if (existing.ViewingData != data) { existing.ViewingData = data; updated++; }
                else skipped++;
            }

            await movieDb.SaveChangesAsync();
            return Ok(new { Success = true, updated, skipped, deleted });
        }

        [HttpGet("/API/API_UserList")]
        public IActionResult API_UserList()
        {
            var userList = movieDb.Users
                .OrderByDescending(u => u.LastLogin.HasValue)
                .ThenByDescending(u => u.LastLogin)
                .Select(d => d.Username)
                .ToList();
            return Json(userList);
        }

        public class search
        {
            public string Type { get; set; }
            public int? Count { get; set; }
            public string StartsWith { get; set; }
            public string Text { get; set; }
            public string Actor { get; set; }
            public string ReleaseYear { get; set; }
            public string UploadDate { get; set; }
        }

        [HttpPost("/API/API_Movies")]
        public async Task<IActionResult> API_Movies([FromBody] search search = null)
        {
            IQueryable<Movie> movies = movieDb.Movies.AsNoTracking().Where(m => m.ReviewBatch == null);
            if (search == null)
                return BadRequest(new { message = "No Search Data Provided" });

            if (!String.IsNullOrEmpty(search.Type))
                switch (search.Type)
                {
                    case "startsWith":
                        if (search.StartsWith == "#")
                        {
                            movies = movies.Where(m => char.IsDigit(m.SimpleTitle[0]));
                        }
                        else
                        {
                            movies = movies.Where(m => m.SimpleTitle.StartsWith(search.StartsWith));
                        }
                        break;

                    case "containsText":
                        if (!String.IsNullOrEmpty(search.Text))
                            movies = movies.Where(m => m.SimpleTitle.Contains(search.Text) || m.Title.Contains(search.Text));
                        break;

                    case "actorSearch":
                        if (!String.IsNullOrEmpty(search.Actor))
                            // Prefer the normalized cast (richer: full billed cast); fall back to
                            // the legacy Actors string for movies not yet scraped.
                            movies = movies.Where(m =>
                                m.Credits.Any(c => c.Role == CreditRole.Actor && c.Person.DisplayName.Contains(search.Actor))
                                || (m.Actors != null && m.Actors.Contains(search.Actor)));
                        break;

                    default:
                        break;
                }

            if (search.Count.HasValue)
                movies = movies.OrderBy(elem => Guid.NewGuid()).Take(search.Count.Value);

            // Hard ceiling so a broad search can't dump the whole library as one JSON body.
            const int MaxResults = 500;
            var movieList = await movies.OrderBy(m => m.SimpleTitle).Take(MaxResults).ToListAsync();
            return Json(movieList);
        }

        private static bool IsValidImdbId(string input)
        {
            if (string.IsNullOrWhiteSpace(input))
                return false;

            var id = input.Trim();
            // IMDB title IDs are typically "tt" followed by 7-9 digits (e.g., tt1234567)
            return System.Text.RegularExpressions.Regex.IsMatch(id, @"^tt\d{7,9}$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        }

        private static bool TryParseBggThingId(string input, out int bggThingId)
        {
            bggThingId = 0;
            if (string.IsNullOrWhiteSpace(input))
                return false;

            var trimmed = input.Trim();
            if (int.TryParse(trimmed, out bggThingId) && bggThingId > 0)
                return true;

            var match = System.Text.RegularExpressions.Regex.Match(trimmed, @"(?:boardgame|boardgameexpansion)/(\d+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (!match.Success)
                match = System.Text.RegularExpressions.Regex.Match(trimmed, @"\b(\d{3,})\b");

            return match.Success && int.TryParse(match.Groups[1].Value, out bggThingId) && bggThingId > 0;
        }

        [HttpGet("/API/GetMoviesByRating")]
        public async Task<IActionResult> GetMoviesByRating(string ratingIds, int page = 1, int pageSize = 60, string? types = null, string? sort = null)
        {
            int ageRestriction = 100;
            var currentUserId = GetCurrentUserId();
            if (currentUserId.HasValue)
            {
                var setRestriction = await movieDb.UserSettings
                    .FirstOrDefaultAsync(u => u.SettingKey == "AgeRestriction" && u.UserID == currentUserId.Value);
                if (setRestriction != null && int.TryParse(setRestriction.SettingValue, out int parsedRestriction))
                    ageRestriction = parsedRestriction;
            }

            // Browse by the rating ITSELF — clicking "PG-13" asks for the PG-13 movies, not "PG-13 and
            // everything tamer" (which is what this endpoint used to do, as a rating CAP). A title is
            // filed under the rating that actually gates it: real certificate → legacy → inferred
            // (RatingGate.MovieEffectiveBucketIn), so a movie shows up under one button and only one.
            //
            // A SET of buckets, not a single id, because one button can stand for more than one: NC-17
            // covers NC-17(5) and X(6), which are one certificate to anyone browsing.
            //
            // The age gate still applies on top: asking for a bucket above the viewer's restriction is
            // simply an empty grid — the two predicates intersect to nothing, no special-casing.
            // Order at the DB (nulls last, then collation — digit-titles sort before letters) and page
            // there, so the infinite-scroll client's repeated page fetches don't each re-materialize +
            // re-sort the whole rating set.
            var buckets = (ratingIds ?? "")
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(s => int.TryParse(s, out var id) ? id : 0)
                .Where(id => id > 0)
                .Distinct()
                .ToList();
            if (buckets.Count == 0)
                return Ok(await PageCardsAsync(movieDb.Movies.Where(m => false).Select(ToCardDto), page, pageSize));

            var baseQuery = movieDb.Movies
                .Where(m => m.ReviewBatch == null)
                .Where(Web.RatingGate.MovieVisibleAtAge(movieDb, ageRestriction))
                .Where(Web.RatingGate.MovieEffectiveBucketIn(movieDb, buckets));

            // Rating browse is movie-only, so apply just the Movie-bucket part of the Type scope.
            // (A scope without any movie bucket — e.g. Series-only — yields no rating results.)
            var scope = ParseTypeScope(types);
            if (scope.Count > 0)
            {
                var movieBuckets = scope.Where(t => t == NormalizedTitleType.Movies || t == NormalizedTitleType.Short).ToList();
                baseQuery = movieBuckets.Count > 0
                    ? baseQuery.Where(m => movieBuckets.Contains(m.NormalizedTitleType))
                    : baseQuery.Where(m => false);
            }

            // Order at the DB by the chosen sort, then page there.
            var query = SortMovies(baseQuery, NormalizeSort(sort)).Select(ToCardDto);

            return Ok(await PageCardsAsync(query, page, pageSize));
        }

        // Small, rarely-changing lookup tables (genres, MPA ratings, total count) fetched by every client
        // on load — cache briefly so they aren't re-queried per visit. Size 1 satisfies the cache's SizeLimit.
        private static readonly TimeSpan LookupCacheTtl = TimeSpan.FromMinutes(5);

        private async Task<T> GetOrCacheLookupAsync<T>(string key, Func<Task<T>> load)
        {
            if (memoryCache.TryGetValue(key, out T cached))
                return cached;
            var value = await load();
            memoryCache.Set(key, value, new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = LookupCacheTtl, Size = 1 });
            return value;
        }

        // Distinct genre names from the normalized Genre table, for the browse genre filter.
        [HttpGet("/API/GetGenres")]
        public async Task<IActionResult> GetGenres()
        {
            var genres = await GetOrCacheLookupAsync("lookup:genres", () => movieDb.Genres
                .OrderBy(g => g.Name)
                .Select(g => g.Name)
                .ToListAsync());
            return Ok(genres);
        }

        [HttpGet("/API/GetMPARatings")]
        public async Task<IActionResult> GetMPARatings()
        {
            var result = await GetOrCacheLookupAsync("lookup:mparatings", async () =>
            {
                var ratingIds = await movieDb.RatingMaps
                    .Select(rm => rm.MPARatingID)
                    .Distinct()
                    .OrderBy(id => id)
                    .ToListAsync();

                var mpaNames = await movieDb.RatingMpas
                    .ToDictionaryAsync(mpa => mpa.RatingID, mpa => mpa.MPAName);

                return ratingIds.Select(id => new
                {
                    id,
                    name = mpaNames.TryGetValue(id, out var n) && !string.IsNullOrEmpty(n) ? n : id.ToString()
                }).ToList();
            });

            return Ok(result);
        }

        public class UserSettingRequest
        {
            public string SettingKey { get; set; }
            public string SettingValue { get; set; }
        }

        // Keys a user is allowed to set on their own account through the self-service endpoint.
        // Default-deny: anything not listed here (notably the privileged access grants
        // "CanEditMovies" and "ComicSiteAccess") can only be set via /API/Admin/SetUserSetting,
        // which requires a password-verified config admin. Without this allow-list any logged-in
        // user could grant themselves editor rights by POSTing CanEditMovies=true.
        private static readonly HashSet<string> SelfServiceSettingKeys = new(StringComparer.OrdinalIgnoreCase)
        {
            "AgeRestriction",
            "CardStyle",
            "EnablePagination",
            "ShowBoardgameExpansions",
            "RatingAnchors",
            "FavoriteChannels",
        };

        [HttpPost("/API/SetUserSetting")]
        public async Task<IActionResult> SetUserSetting([FromBody] UserSettingRequest request)
        {
            var currentUserId = GetCurrentUserId();
            if (!currentUserId.HasValue)
                return Unauthorized(new { Success = false, Message = "Not logged in." });

            if (string.IsNullOrEmpty(request?.SettingKey))
                return BadRequest(new { Success = false, Message = "SettingKey is required." });

            if (!SelfServiceSettingKeys.Contains(request.SettingKey))
                return Forbid();

            var existing = await movieDb.UserSettings
                .FirstOrDefaultAsync(u => u.UserID == currentUserId.Value && u.SettingKey == request.SettingKey);

            if (request.SettingValue == null)
            {
                if (existing != null)
                {
                    movieDb.UserSettings.Remove(existing);
                    await movieDb.SaveChangesAsync();
                }
            }
            else
            {
                if (existing != null)
                {
                    existing.SettingValue = request.SettingValue;
                }
                else
                {
                    var newSetting = new MovieTheater.Db.UserSettings
                    {
                        UserID = currentUserId.Value,
                        SettingKey = request.SettingKey,
                        SettingValue = request.SettingValue,
                    };
                    await movieDb.UserSettings.AddAsync(newSetting);
                    movieDb.Entry(newSetting).Reference(s => s.User).IsModified = false;
                }
                await movieDb.SaveChangesAsync();
            }

            return Ok(new { Success = true });
        }

        // GET /PosterCollage
        // Optional query params:
        //   postersWide    – number of poster columns (default: 25)
        //   postersHigh    – target row count; all matching posters are shown, distributed evenly
        //                    across this many rows (last row may be shorter). Makes the image
        //                    as wide as needed rather than capping the poster count.
        //   maxPixelsWide  – derive column count from max image width instead of postersWide
        //   actor          – only include movies whose Actors field contains this value
        //   text           – only include movies whose SimpleTitle or Title contains this value
        //   startsWith     – only include movies whose SimpleTitle starts with this letter ('#' for digits)
        //   posterWidth    – width of each poster tile in pixels (default: 75)
        //   posterHeight   – height of each poster tile in pixels (default: 100)
        [HttpGet("/PosterCollage")]
        public async Task<IActionResult> PosterCollage(
            int? postersWide = null, int? postersHigh = null, int? maxPixelsWide = null,
            string actor = null, string text = null, string startsWith = null,
            int posterWidth = 75, int posterHeight = 100)
        {
            // Renders a full-library composite in memory — an editor/creative tool, not on any user path.
            if (!await IsCurrentUserEditor()) return Forbid();

            // Clamp caller-controlled tile size so a request can't demand a gigapixel canvas.
            posterWidth = Math.Clamp(posterWidth, 8, 300);
            posterHeight = Math.Clamp(posterHeight, 8, 400);

            var cacheKey = $"collage:a={actor}:t={text}:sw={startsWith}:pw={postersWide}:ph={postersHigh}:mpw={maxPixelsWide}:w={posterWidth}:h={posterHeight}";
            if (memoryCache.TryGetValue(cacheKey, out byte[] cachedPng))
            {
                HttpContext.Response.ContentType = "image/png";
                await HttpContext.Response.Body.WriteAsync(cachedPng);
                return new EmptyResult();
            }

            IQueryable<Movie> moviesQuery = movieDb.Movies.AsNoTracking().OrderBy(m => m.SimpleTitle);

            if (!string.IsNullOrEmpty(actor))
                moviesQuery = moviesQuery.Where(m =>
                    m.Credits.Any(c => c.Role == CreditRole.Actor && c.Person.DisplayName.Contains(actor))
                    || (m.Actors != null && m.Actors.Contains(actor)));

            if (!string.IsNullOrEmpty(text))
                moviesQuery = moviesQuery.Where(m => m.SimpleTitle.Contains(text) || m.Title.Contains(text));

            if (!string.IsNullOrEmpty(startsWith))
            {
                if (startsWith == "#")
                {
                    moviesQuery = moviesQuery.Where(m => char.IsDigit(m.SimpleTitle[0]));
                }
                else
                {
                    moviesQuery = moviesQuery.Where(m => m.SimpleTitle.StartsWith(startsWith));
                }
            }

            // Only the id is needed to load each poster — never materialize whole Movie entities here.
            const int MaxPosters = 5000;
            var movieIds = await moviesQuery.Select(m => m.id).Take(MaxPosters).ToListAsync();

            // Load posters with bounded concurrency (Task.WhenAll preserves array order, so draw order
            // is still deterministic) rather than firing thousands of simultaneous file reads.
            byte[][] allImageResults;
            using (var gate = new SemaphoreSlim(16))
            {
                allImageResults = await Task.WhenAll(movieIds.Select(async id =>
                {
                    await gate.WaitAsync();
                    try { return await imageRepo.GetImage(id, PosterImageVariant.Thumbnail); }
                    finally { gate.Release(); }
                }));
            }

            var posterImages = allImageResults.Where(b => b != null).ToList();

            int totalPosters = posterImages.Count;

            // postersHigh: distribute all posters into this many rows, making the image as wide as needed.
            // maxPixelsWide / postersWide: directly set column count regardless of poster count.
            int rowLength;
            if (postersHigh.HasValue)
                rowLength = Math.Max(1, (int)Math.Ceiling((double)totalPosters / postersHigh.Value));
            else if (maxPixelsWide.HasValue)
                rowLength = Math.Max(1, maxPixelsWide.Value / posterWidth);
            else
                rowLength = postersWide ?? 25;

            int rowCount = (int)Math.Ceiling((double)totalPosters / rowLength);
            int totalWidth = Math.Min(totalPosters, rowLength) * posterWidth;
            int totalHeight = rowCount * posterHeight;

            using var combinedImage = new Image<Rgba32>(totalWidth, totalHeight);

            int drawingX = 0;
            int drawingY = 0;
            int rowCounter = 0;

            foreach (var bytes in posterImages)
            {
                if (rowCounter == rowLength)
                {
                    rowCounter = 0;
                    drawingX = 0;
                    drawingY += posterHeight;
                }

                using var posterImg = Image.Load(bytes);
                posterImg.Mutate(x => x.Resize(posterWidth, posterHeight));
                combinedImage.Mutate(ctx => ctx.DrawImage(posterImg, new Point(drawingX, drawingY), 1f));

                drawingX += posterWidth;
                rowCounter++;
            }

            using var outputMs = new MemoryStream();
            await combinedImage.SaveAsPngAsync(outputMs);
            var pngBytes = outputMs.ToArray();

            memoryCache.Set(cacheKey, pngBytes, new MemoryCacheEntryOptions
            {
                SlidingExpiration = TimeSpan.FromHours(4),
                Size = pngBytes.Length,
            });

            HttpContext.Response.ContentType = "image/png";
            await HttpContext.Response.Body.WriteAsync(pngBytes);
            return new EmptyResult();
        }

        // POST /PosterMosaic
        // Accepts an uploaded image and creates a photo-mosaic where each tile is one of the stored posters.
        [HttpPost("/PosterMosaic")]
        public async Task<IActionResult> PosterMosaic(
            IFormFile imageFile,
            // Scale
            double tileScale = 1.0,
            double outputScale = 1.0,
            int maxOutputDimension = 0,
            // Color Matching
            int topK = 50,
            int excludeRadius = 2,
            double colorDecayFactor = 100.0,
            double adjacencyPenaltyBase = 0.1,
            // Output Format
            string format = "png",
            int quality = 85,
            int pngCompression = 6)
        {
            if (imageFile == null || imageFile.Length == 0)
                return BadRequest(new { Message = "No image uploaded", Success = false });

            byte[] sourceBytes;
            using (var ms = new MemoryStream())
            {
                await imageFile.CopyToAsync(ms);
                sourceBytes = ms.ToArray();
            }

            var options = BuildMosaicOptions(tileScale, outputScale, maxOutputDimension,
                topK, excludeRadius, colorDecayFactor, adjacencyPenaltyBase, format, quality, pngCompression);

            byte[] mosaicBytes;
            try
            {
                mosaicBytes = await posterMosaicService.BuildPosterMosaicBytes(sourceBytes, options);
            }
            catch (InvalidOperationException ex)
            {
                return BadRequest(new { Message = ex.Message, Success = false });
            }

            return File(mosaicBytes, GetMimeType(options.OutputFormat));
        }

        [HttpGet("/PosterMosaicFromUrl")]
        public async Task<IActionResult> PosterMosaicFromUrl(
            string imageUrl,
            // Scale
            double tileScale = 1.0,
            double outputScale = 1.0,
            int maxOutputDimension = 0,
            // Color Matching
            int topK = 50,
            int excludeRadius = 2,
            double colorDecayFactor = 100.0,
            double adjacencyPenaltyBase = 0.1,
            // Output Format
            string format = "png",
            int quality = 85,
            int pngCompression = 6)
        {
            if (!await IsCurrentUserEditor()) return Forbid();

            if (string.IsNullOrWhiteSpace(imageUrl))
                return BadRequest(new { Message = "imageUrl is required", Success = false });

            var (urlOk, urlError) = await MovieTheater.Web.ServerSideUrlGuard.ValidateAsync(imageUrl);
            if (!urlOk)
                return BadRequest(new { Message = urlError, Success = false });

            var options = BuildMosaicOptions(tileScale, outputScale, maxOutputDimension,
                topK, excludeRadius, colorDecayFactor, adjacencyPenaltyBase, format, quality, pngCompression);

            var cacheKey = $"mosaic:{imageUrl}:ts={tileScale}:os={outputScale}:max={maxOutputDimension}:k={topK}:er={excludeRadius}:cd={colorDecayFactor}:ap={adjacencyPenaltyBase}:fmt={format}:q={quality}:png={pngCompression}";
            if (memoryCache.TryGetValue(cacheKey, out byte[] cached))
                return File(cached, GetMimeType(options.OutputFormat));

            HttpResponseMessage result;
            try
            {
                result = await httpClient.GetAsync(imageUrl);
            }
            catch (Exception ex)
            {
                return BadRequest(new { Message = $"Failed to fetch image: {ex.Message}", Success = false });
            }

            if (!result.IsSuccessStatusCode)
                return BadRequest(new { Message = $"Failed to fetch image: {result.StatusCode}", Success = false });

            var sourceBytes = await result.Content.ReadAsByteArrayAsync();

            byte[] mosaicBytes;
            try
            {
                mosaicBytes = await posterMosaicService.BuildPosterMosaicBytes(sourceBytes, options);
            }
            catch (InvalidOperationException ex)
            {
                return BadRequest(new { Message = ex.Message, Success = false });
            }

            memoryCache.Set(cacheKey, mosaicBytes, new MemoryCacheEntryOptions
            {
                SlidingExpiration = TimeSpan.FromHours(4),
                Size = mosaicBytes.Length,
            });

            return File(mosaicBytes, GetMimeType(options.OutputFormat));
        }

        private static MosaicOptions BuildMosaicOptions(
            double tileScale, double outputScale, int maxOutputDimension,
            int topK, int excludeRadius, double colorDecayFactor, double adjacencyPenaltyBase,
            string format, int quality, int pngCompression)
        {
            return new MosaicOptions
            {
                TileScale = tileScale,
                OutputScale = outputScale,
                MaxOutputDimension = maxOutputDimension,
                TopK = topK,
                ExcludeRadius = excludeRadius,
                ColorDecayFactor = colorDecayFactor,
                AdjacencyPenaltyBase = adjacencyPenaltyBase,
                OutputFormat = format?.ToLowerInvariant() switch
                {
                    "jpeg" or "jpg" => MosaicOutputFormat.Jpeg,
                    "webp" => MosaicOutputFormat.WebP,
                    _ => MosaicOutputFormat.Png
                },
                Quality = quality,
                PngCompressionLevel = pngCompression switch
                {
                    1 => PngCompressionLevel.Level1,
                    2 => PngCompressionLevel.Level2,
                    3 => PngCompressionLevel.Level3,
                    4 => PngCompressionLevel.Level4,
                    5 => PngCompressionLevel.Level5,
                    6 => PngCompressionLevel.Level6,
                    7 => PngCompressionLevel.Level7,
                    8 => PngCompressionLevel.Level8,
                    9 => PngCompressionLevel.Level9,
                    _ => PngCompressionLevel.DefaultCompression
                }
            };
        }

        [HttpPost("/API/SyncBoardgameFromBgg")]
        public async Task<IActionResult> SyncBoardgameFromBgg(int bggThingId)
        {
            if (!await IsCurrentUserEditor()) return Forbid();
            if (bggThingId <= 0)
                return BadRequest(new { Success = false, Message = "bggThingId must be a positive integer" });

            try
            {
                var fromBgg = await boardGameGeekApi.GetBoardgame(bggThingId);
                if (fromBgg == null)
                    return NotFound(new { Success = false, Message = "Boardgame not found from BoardGameGeek" });

                return await SyncBoardgameInternal(fromBgg);
            }
            catch (HttpRequestException ex)
            {
                return StatusCode(502, new { Success = false, Message = "BoardGameGeek request failed", Error = ex.Message });
            }
        }

        [HttpPost("/API/SyncBoardgameFromBggByTitle")]
        public async Task<IActionResult> SyncBoardgameFromBggByTitle(string title)
        {
            if (!await IsCurrentUserEditor()) return Forbid();
            if (string.IsNullOrWhiteSpace(title))
                return BadRequest(new { Success = false, Message = "title is required" });

            try
            {
                var fromBgg = await boardGameGeekApi.GetBoardgameByTitle(title);
                if (fromBgg == null)
                    return NotFound(new { Success = false, Message = $"Boardgame '{title}' not found from BoardGameGeek" });

                return await SyncBoardgameInternal(fromBgg);
            }
            catch (HttpRequestException ex)
            {
                return StatusCode(502, new { Success = false, Message = "BoardGameGeek request failed", Error = ex.Message });
            }
        }

        private async Task<IActionResult> SyncBoardgameInternal(BoardgameBggResult fromBgg)
        {
            var fromBggBoardgame = fromBgg.Boardgame;
            var existing = await movieDb.Boardgames
                .Include(x => x.ImageDetails)
                .Include(x => x.ExtraDetails)
                .SingleOrDefaultAsync(x => x.BggThingId == fromBggBoardgame.BggThingId);

            if (existing == null)
            {
                movieDb.Boardgames.Add(fromBggBoardgame);
                await movieDb.SaveChangesAsync();
                fromBggBoardgame.BaseGameId = await ResolveBaseGameId(fromBggBoardgame.ExtraDetails?.LinksJson);
                if (fromBggBoardgame.BaseGameId.HasValue) await movieDb.SaveChangesAsync();
                await UpsertBoardgameImageUrls(fromBggBoardgame.id, fromBgg.ImageUrl, fromBgg.ThumbnailUrl);
                await DownloadAndSaveBoardgameImages(fromBggBoardgame);
                await movieDb.Entry(fromBggBoardgame).Reference(x => x.ImageDetails).LoadAsync();
                await boardgameSimilarityService.RebuildAsync(movieDb);
                return Ok(new { Success = true, Message = "Boardgame captured", data = fromBggBoardgame });
            }

            var imageUrlsChanged = !string.Equals(existing.ImageDetails?.ImageUrl, fromBgg.ImageUrl, StringComparison.Ordinal)
                || !string.Equals(existing.ImageDetails?.ThumbnailUrl, fromBgg.ThumbnailUrl, StringComparison.Ordinal);

            ApplyBoardgameSnapshot(existing, fromBggBoardgame);
            await movieDb.SaveChangesAsync();
            existing.BaseGameId = await ResolveBaseGameId(existing.ExtraDetails?.LinksJson);
            await movieDb.SaveChangesAsync();
            await UpsertBoardgameImageUrls(existing.id, fromBgg.ImageUrl, fromBgg.ThumbnailUrl);

            if (imageUrlsChanged)
                await DownloadAndSaveBoardgameImages(existing, force: true);

            if (existing.ImageDetails == null)
                await movieDb.Entry(existing).Reference(x => x.ImageDetails).LoadAsync();

            await boardgameSimilarityService.RebuildAsync(movieDb);
            return Ok(new { Success = true, Message = "Boardgame updated", data = existing });
        }

        public class UpdateBoardgameRequest
        {
            public int Id { get; set; }
            public string? Name { get; set; }
            public string? Description { get; set; }
            public int? YearPublished { get; set; }
            public int? MinPlayers { get; set; }
            public int? MaxPlayers { get; set; }
            public int? PlayingTime { get; set; }
            public int? MinAge { get; set; }
            public string? ImageUrl { get; set; }
            public int? BaseGameId { get; set; }
        }

        [HttpPost("/API/UpdateBoardgame")]
        public async Task<IActionResult> UpdateBoardgame([FromBody] UpdateBoardgameRequest req)
        {
            if (!await IsCurrentUserEditor()) return Forbid();
            if (req == null)
                return BadRequest(new { Success = false, Message = "No data provided." });

            var game = await movieDb.Boardgames.Include(b => b.ImageDetails).FirstOrDefaultAsync(x => x.id == req.Id);
            if (game == null)
                return NotFound(new { Success = false, Message = "Boardgame not found." });

            var imageUrlChanged = !string.Equals(game.ImageDetails?.ImageUrl, req.ImageUrl?.Trim(), StringComparison.Ordinal)
                                  && !string.IsNullOrWhiteSpace(req.ImageUrl);

            game.Name = req.Name;
            game.Description = req.Description;
            game.YearPublished = req.YearPublished;
            game.MinPlayers = req.MinPlayers;
            game.MaxPlayers = req.MaxPlayers;
            game.PlayingTime = req.PlayingTime;
            game.MinAge = req.MinAge;
            game.BaseGameId = req.BaseGameId;

            await movieDb.SaveChangesAsync();

            string? imageError = null;
            if (imageUrlChanged)
            {
                await UpsertBoardgameImageUrls(game.id, req.ImageUrl!.Trim(), null);
                try
                {
                    await DownloadAndSaveBoardgameImages(game, force: true);
                }
                catch (Exception ex)
                {
                    imageError = ex.Message;
                }
            }

            // Name/rating/image fields edited here surface in other games' similar-game
            // entries, so refresh the (persisted) similarity cache.
            await boardgameSimilarityService.RebuildAsync(movieDb);

            var msg = imageError != null ? $"Boardgame updated, but image download failed: {imageError}" : "Boardgame updated";
            return Ok(new { Success = true, Message = msg, data = game });
        }

        public class RematchBoardgameRequest
        {
            public int Id { get; set; }
            public int NewBggThingId { get; set; }
        }

        [HttpPost("/API/RematchBoardgame")]
        public async Task<IActionResult> RematchBoardgame([FromBody] RematchBoardgameRequest req)
        {
            if (!await IsCurrentUserEditor()) return Forbid();
            if (req == null || req.Id <= 0 || req.NewBggThingId <= 0)
                return BadRequest(new { Success = false, Message = "id and newBggThingId must be positive integers." });

            var game = await movieDb.Boardgames
                .Include(x => x.ImageDetails)
                .Include(x => x.ExtraDetails)
                .FirstOrDefaultAsync(x => x.id == req.Id);
            if (game == null)
                return NotFound(new { Success = false, Message = "Boardgame not found." });

            var conflict = await movieDb.Boardgames.FirstOrDefaultAsync(x => x.BggThingId == req.NewBggThingId && x.id != req.Id);
            if (conflict != null)
                return Conflict(new { Success = false, Message = $"BGG ID {req.NewBggThingId} is already used by '{conflict.Name}' (id #{conflict.id})." });

            try
            {
                var fromBgg = await boardGameGeekApi.GetBoardgame(req.NewBggThingId);
                if (fromBgg == null)
                    return NotFound(new { Success = false, Message = "Boardgame not found on BoardGameGeek." });

                var fromBggBoardgame = fromBgg.Boardgame;

                await boardgameImageRepo.DeleteImage(game.id, BoardgameImageVariant.Main);
                await boardgameImageRepo.DeleteImage(game.id, BoardgameImageVariant.Thumbnail);

                ApplyBoardgameSnapshot(game, fromBggBoardgame);
                game.BggThingId = req.NewBggThingId;

                await movieDb.SaveChangesAsync();
                await UpsertBoardgameImageUrls(game.id, fromBgg.ImageUrl, fromBgg.ThumbnailUrl);
                await DownloadAndSaveBoardgameImages(game, force: true);

                // ImageDetails is set by DownloadAndSaveBoardgameImages; load it if not already populated
                if (game.ImageDetails == null)
                    await movieDb.Entry(game).Reference(g => g.ImageDetails).LoadAsync();

                await boardgameSimilarityService.RebuildAsync(movieDb);
                return Ok(new { Success = true, data = game });
            }
            catch (HttpRequestException ex)
            {
                return StatusCode(502, new { Success = false, Message = "BoardGameGeek request failed", Error = ex.Message });
            }
        }

        [HttpGet("/API/GetBoardgame")]
        public async Task<IActionResult> GetBoardgame(int bggThingId)
        {
            if (bggThingId <= 0)
            {
                return BadRequest(new { Success = false, Message = "bggThingId must be a positive integer" });
            }

            var boardgame = await movieDb.Boardgames
                .Include(x => x.ImageDetails)
                .SingleOrDefaultAsync(x => x.BggThingId == bggThingId);
            if (boardgame == null)
            {
                return NotFound(new { Success = false, Message = "Boardgame not found" });
            }

            return Ok(new { Success = true, data = boardgame });
        }

        [EnableQuery]
        [HttpGet("/odata/Boardgames")]
        public IQueryable<Boardgame> GetBoardgames()
        {
            return movieDb.Boardgames.Include(b => b.ImageDetails);
        }

        [HttpGet("/API/SimilarBoardgames")]
        public IActionResult SimilarBoardgames(int id)
        {
            var similar = boardgameSimilarityService.GetSimilar(id);
            return Ok(new { success = true, data = similar });
        }

        [HttpPost("/API/BatchImportBoardgames")]
        [HttpPost("/API/BatchInsertBoardgames")]
        public async Task<IActionResult> BatchImportBoardgames([FromBody] List<string> gameNames, int delayMs = 2000)
        {
            if (!await IsCurrentUserEditor()) return Forbid();
            if (gameNames == null || gameNames.Count == 0)
            {
                return BadRequest(new { Success = false, Message = "gameNames array is required" });
            }

            var results = new List<object>();
            int successCount = 0;
            int failureCount = 0;
            int skippedCount = 0;

            for (int i = 0; i < gameNames.Count; i++)
            {
                var rawInput = gameNames[i]?.Trim();
                if (string.IsNullOrWhiteSpace(rawInput))
                {
                    results.Add(new { Index = i, Input = rawInput, Status = "Skipped", Reason = "Empty input" });
                    skippedCount++;
                    continue;
                }

                bool madeApiCall = false;
                try
                {
                    var isBggId = TryParseBggThingId(rawInput, out var bggThingId) && bggThingId > 0;

                    if (isBggId)
                    {
                        var existingById = await movieDb.Boardgames.SingleOrDefaultAsync(x => x.BggThingId == bggThingId);
                        if (existingById != null)
                        {
                            results.Add(new { Index = i, Input = rawInput, BggThingId = existingById.BggThingId, Status = "AlreadyExists", Name = existingById.Name });
                            skippedCount++;
                            continue;
                        }
                    }
                    else
                    {
                        var existingByName = await movieDb.Boardgames.FirstOrDefaultAsync(x => x.Name == rawInput);
                        if (existingByName != null)
                        {
                            results.Add(new { Index = i, Input = rawInput, BggThingId = existingByName.BggThingId, Status = "AlreadyExists", Name = existingByName.Name });
                            skippedCount++;
                            continue;
                        }
                    }

                    var fromBgg = isBggId
                        ? await boardGameGeekApi.GetBoardgame(bggThingId)
                        : await boardGameGeekApi.GetBoardgameByTitle(rawInput);
                    madeApiCall = true;

                    if (fromBgg == null)
                    {
                        results.Add(new { Index = i, Input = rawInput, Status = "NotFound", Message = "Not found on BGG" });
                        failureCount++;
                        continue;
                    }

                    var fromBggBoardgame = fromBgg.Boardgame;
                    var existing = await movieDb.Boardgames.SingleOrDefaultAsync(x => x.BggThingId == fromBggBoardgame.BggThingId);
                    if (existing == null)
                    {
                        movieDb.Boardgames.Add(fromBggBoardgame);
                        await movieDb.SaveChangesAsync();
                        await UpsertBoardgameImageUrls(fromBggBoardgame.id, fromBgg.ImageUrl, fromBgg.ThumbnailUrl);

                        // Download images after saving to database
                        await DownloadAndSaveBoardgameImages(fromBggBoardgame);

                        results.Add(new { Index = i, Input = rawInput, BggThingId = fromBggBoardgame.BggThingId, Status = "Created", Name = fromBggBoardgame.Name });
                        successCount++;
                    }
                    else
                    {
                        results.Add(new { Index = i, Input = rawInput, BggThingId = fromBggBoardgame.BggThingId, Status = "AlreadyExists", Name = existing.Name });
                        skippedCount++;
                    }
                }
                catch (HttpRequestException ex)
                {
                    results.Add(new { Index = i, Input = rawInput, Status = "Failed", Error = ex.Message });
                    failureCount++;
                }
                catch (Exception ex)
                {
                    results.Add(new { Index = i, Input = rawInput, Status = "Failed", Error = ex.Message });
                    failureCount++;
                }

                // Rate limiting: wait between BGG requests (default 2 seconds)
                if (madeApiCall && i < gameNames.Count - 1)
                {
                    await Task.Delay(delayMs);
                }
            }

            if (successCount > 0)
                await boardgameSimilarityService.RebuildAsync(movieDb);

            return Ok(new
            {
                Success = true,
                Summary = new { Total = gameNames.Count, Success = successCount, Failed = failureCount, Skipped = skippedCount },
                Results = results
            });
        }

        private async Task UpsertBoardgameImageUrls(int boardgameId, string? imageUrl, string? thumbnailUrl)
        {
            var details = await movieDb.BoardgameImageDetails.FindAsync(boardgameId);
            if (details == null)
                movieDb.BoardgameImageDetails.Add(new BoardgameImageDetails { BoardgameId = boardgameId, ImageVersion = 0, ImageUrl = imageUrl, ThumbnailUrl = thumbnailUrl });
            else
            {
                details.ImageUrl = imageUrl;
                details.ThumbnailUrl = thumbnailUrl;
            }
            await movieDb.SaveChangesAsync();
        }

        private async Task DownloadAndSaveBoardgameImages(Boardgame boardgame, bool force = false)
        {
            var details = boardgame.ImageDetails ?? await movieDb.BoardgameImageDetails.FindAsync(boardgame.id);
            var imageUrl = details?.ImageUrl;
            var thumbnailUrl = details?.ThumbnailUrl;

            bool hasMain = await boardgameImageRepo.HasImage(boardgame.id, BoardgameImageVariant.Main);
            bool hasThumb = await boardgameImageRepo.HasImage(boardgame.id, BoardgameImageVariant.Thumbnail);
            bool savedAny = false;

            // Defense-in-depth SSRF guard: these URLs are editor/BGG-supplied, but validate before
            // fetching so a stored internal URL can't turn this into a server-side proxy.
            if (!string.IsNullOrWhiteSpace(imageUrl) && !(await MovieTheater.Web.ServerSideUrlGuard.ValidateAsync(imageUrl)).ok)
                imageUrl = null;
            if (!string.IsNullOrWhiteSpace(thumbnailUrl) && !(await MovieTheater.Web.ServerSideUrlGuard.ValidateAsync(thumbnailUrl)).ok)
                thumbnailUrl = null;

            // Fire both HTTP requests before awaiting either so they download in parallel
            var mainFetchTask = (force || !hasMain) && !string.IsNullOrWhiteSpace(imageUrl)
                ? httpClient.GetAsync(imageUrl)
                : null;
            var thumbFetchTask = (force || !hasThumb) && !string.IsNullOrWhiteSpace(thumbnailUrl)
                ? httpClient.GetAsync(thumbnailUrl)
                : null;

            byte[]? mainBytes = null;
            if (mainFetchTask != null)
            {
                var imageResponse = await mainFetchTask;
                imageResponse.EnsureSuccessStatusCode();
                mainBytes = await imageResponse.Content.ReadAsByteArrayAsync();
                await boardgameImageRepo.SaveImage(boardgame.id, BoardgameImageVariant.Main, mainBytes);
                savedAny = true;
            }

            byte[]? thumbBytes = null;
            if (thumbFetchTask != null)
            {
                var thumbResponse = await thumbFetchTask;
                if (thumbResponse.IsSuccessStatusCode)
                    thumbBytes = await thumbResponse.Content.ReadAsByteArrayAsync();
            }

            if (thumbBytes == null && (force || !hasThumb))
            {
                mainBytes ??= await boardgameImageRepo.GetImage(boardgame.id, BoardgameImageVariant.Main);
                if (mainBytes != null)
                    thumbBytes = BuildBoardgameThumbnail(mainBytes);
            }

            if (thumbBytes != null)
            {
                await boardgameImageRepo.SaveImage(boardgame.id, BoardgameImageVariant.Thumbnail, thumbBytes);
                savedAny = true;
            }

            if (savedAny)
            {
                if (details == null)
                {
                    details = new BoardgameImageDetails { BoardgameId = boardgame.id, ImageVersion = 1, ImageUrl = imageUrl, ThumbnailUrl = thumbnailUrl };
                    movieDb.BoardgameImageDetails.Add(details);
                    boardgame.ImageDetails = details;
                }
                else
                {
                    details.ImageVersion++;
                    details.ImageUrl = imageUrl;
                    details.ThumbnailUrl = thumbnailUrl;
                }
                await movieDb.SaveChangesAsync();
            }
        }

        private static byte[] BuildBoardgameThumbnail(byte[] sourceImage)
        {
            using (var image = SixLabors.ImageSharp.Image.Load(sourceImage))
            {
                float originalHeight = image.Height;
                float originalWidth = image.Width;
                float calcHeight = 200f;
                int maxWidth = 150;
                float changedPerc = calcHeight / originalHeight;
                float calcWidth = changedPerc * originalWidth;
                int finalWidth = (int)Math.Round(calcWidth);
                int finalHeight = (int)Math.Round(calcHeight);
                if (finalWidth > maxWidth)
                {
                    finalWidth = maxWidth;
                }

                image.Mutate(x => x
                    .Resize(finalWidth, finalHeight, KnownResamplers.Lanczos2)
                    .GaussianSharpen(.5f));

                var png = new PngEncoder
                {
                    CompressionLevel = 0,
                    FilterMethod = PngFilterMethod.None
                };

                using (var ms = new MemoryStream())
                {
                    image.Save(ms, png);
                    return ms.ToArray();
                }
            }
        }

        private static void ApplyBoardgameSnapshot(Boardgame existing, Boardgame fromBgg)
        {
            existing.ThingType = fromBgg.ThingType;
            existing.Name = fromBgg.Name;
            existing.YearPublished = fromBgg.YearPublished;
            existing.MinPlayers = fromBgg.MinPlayers;
            existing.MaxPlayers = fromBgg.MaxPlayers;
            existing.PlayingTime = fromBgg.PlayingTime;
            existing.MinPlayTime = fromBgg.MinPlayTime;
            existing.MaxPlayTime = fromBgg.MaxPlayTime;
            existing.MinAge = fromBgg.MinAge;
            existing.Description = fromBgg.Description;
            existing.UsersRated = fromBgg.UsersRated;
            existing.AverageRating = fromBgg.AverageRating;
            existing.BayesAverageRating = fromBgg.BayesAverageRating;
            existing.StdDev = fromBgg.StdDev;
            existing.Median = fromBgg.Median;
            existing.Owned = fromBgg.Owned;
            existing.Trading = fromBgg.Trading;
            existing.Wanting = fromBgg.Wanting;
            existing.Wishing = fromBgg.Wishing;
            existing.NumComments = fromBgg.NumComments;
            existing.NumWeights = fromBgg.NumWeights;
            existing.AverageWeight = fromBgg.AverageWeight;
            existing.LastSyncedUtc = fromBgg.LastSyncedUtc;

            var src = fromBgg.ExtraDetails;
            if (src != null)
            {
                existing.ExtraDetails ??= new BoardgameExtraDetails { BoardgameId = existing.id };
                existing.ExtraDetails.AlternateNamesJson = src.AlternateNamesJson;
                existing.ExtraDetails.RanksJson = src.RanksJson;
                existing.ExtraDetails.LinksJson = src.LinksJson;
                existing.ExtraDetails.PollsJson = src.PollsJson;
                existing.ExtraDetails.VersionsXml = src.VersionsXml;
                existing.ExtraDetails.VideosJson = src.VideosJson;
                existing.ExtraDetails.MarketplaceXml = src.MarketplaceXml;
                existing.ExtraDetails.RawXml = src.RawXml;
            }
        }


        private async Task<int?> ResolveBaseGameId(string? linksJson)
        {
            if (string.IsNullOrWhiteSpace(linksJson)) return null;
            try
            {
                using var doc = JsonDocument.Parse(linksJson);
                if (doc.RootElement.ValueKind != JsonValueKind.Array) return null;
                // boardgameexpansion inbound:true = this game requires the linked game to play
                // boardgameimplementation inbound:true = design lineage only; still a standalone game, not an expansion
                foreach (var link in doc.RootElement.EnumerateArray())
                {
                    if (!link.TryGetProperty("type", out var typeProp)) continue;
                    var linkType = typeProp.GetString();
                    if (linkType != "boardgameexpansion") continue;
                    if (!link.TryGetProperty("inbound", out var inboundProp) || inboundProp.ValueKind != JsonValueKind.True) continue;
                    if (!link.TryGetProperty("id", out var idProp) || !idProp.TryGetInt32(out var bggBaseId)) continue;
                    var baseGame = await movieDb.Boardgames
                        .AsNoTracking()
                        .Where(b => b.BggThingId == bggBaseId)
                        .Select(b => new { b.id })
                        .FirstOrDefaultAsync();
                    if (baseGame != null) return baseGame.id;
                }
            }
            catch { /* malformed JSON */ }
            return null;
        }

        private static string GetMimeType(MosaicOutputFormat format) => format switch
        {
            MosaicOutputFormat.Jpeg => "image/jpeg",
            MosaicOutputFormat.WebP => "image/webp",
            _ => "image/png"
        };

        [HttpPost("/API/InsertBoardgameFromBgg")]
        public async Task<IActionResult> InsertBoardgameFromBgg(int bggThingId)
        {
            if (!await IsCurrentUserEditor()) return Forbid();
            if (bggThingId <= 0)
                return BadRequest(new { Success = false, Message = "bggThingId must be a positive integer" });

            var existing = await movieDb.Boardgames.SingleOrDefaultAsync(x => x.BggThingId == bggThingId);
            if (existing != null)
                return Conflict(new { Success = false, Message = $"Boardgame with BGG ID {bggThingId} already exists.", data = existing });

            try
            {
                var fromBgg = await boardGameGeekApi.GetBoardgame(bggThingId);
                if (fromBgg == null)
                    return NotFound(new { Success = false, Message = "Boardgame not found from BoardGameGeek" });

                var fromBggBoardgame = fromBgg.Boardgame;
                movieDb.Boardgames.Add(fromBggBoardgame);
                await movieDb.SaveChangesAsync();
                fromBggBoardgame.BaseGameId = await ResolveBaseGameId(fromBggBoardgame.ExtraDetails?.LinksJson);
                if (fromBggBoardgame.BaseGameId.HasValue) await movieDb.SaveChangesAsync();

                await UpsertBoardgameImageUrls(fromBggBoardgame.id, fromBgg.ImageUrl, fromBgg.ThumbnailUrl);
                await DownloadAndSaveBoardgameImages(fromBggBoardgame);
                await movieDb.Entry(fromBggBoardgame).Reference(x => x.ImageDetails).LoadAsync();
                await boardgameSimilarityService.RebuildAsync(movieDb);

                return Ok(new { Success = true, Message = "Boardgame inserted", data = fromBggBoardgame });
            }
            catch (HttpRequestException ex)
            {
                return StatusCode(502, new { Success = false, Message = "BoardGameGeek request failed", Error = ex.Message });
            }
        }

        [HttpPost("/API/GetBoardgamesFromInputs")]
        public async Task<IActionResult> GetBoardgamesFromInputs([FromBody] string[] inputs)
        {
            if (!await IsCurrentUserEditor()) return Forbid();
            if (inputs == null || inputs.Length == 0)
                return Ok(new List<object>());

            var results = new List<object>();

            foreach (var raw in inputs)
            {
                var input = raw?.Trim();
                if (string.IsNullOrWhiteSpace(input))
                {
                    results.Add(new { input = raw, found = false, message = "Empty input" });
                    continue;
                }

                try
                {
                    var isBggId = TryParseBggThingId(input, out var bggThingId) && bggThingId > 0;
                    var fromBgg = isBggId
                        ? await boardGameGeekApi.GetBoardgame(bggThingId)
                        : await boardGameGeekApi.GetBoardgameByTitle(input);

                    if (fromBgg == null)
                    {
                        results.Add(new { input, found = false, message = "Not found on BGG" });
                        continue;
                    }

                    var existing = await movieDb.Boardgames
                        .AsNoTracking()
                        .Include(x => x.ImageDetails)
                        .SingleOrDefaultAsync(x => x.BggThingId == fromBgg.Boardgame.BggThingId);

                    results.Add(new
                    {
                        input,
                        found = true,
                        exists = existing != null,
                        id = existing?.id,
                        bggThingId = fromBgg.Boardgame.BggThingId,
                        name = fromBgg.Boardgame.Name,
                        yearPublished = fromBgg.Boardgame.YearPublished,
                        minPlayers = fromBgg.Boardgame.MinPlayers,
                        maxPlayers = fromBgg.Boardgame.MaxPlayers,
                        playingTime = fromBgg.Boardgame.PlayingTime,
                        minAge = fromBgg.Boardgame.MinAge,
                        description = fromBgg.Boardgame.Description,
                        imageUrl = fromBgg.ImageUrl,
                        thumbnailUrl = fromBgg.ThumbnailUrl,
                        imageVersion = existing?.ImageDetails?.ImageVersion ?? 0
                    });
                }
                catch (HttpRequestException ex)
                {
                    results.Add(new { input, found = false, message = $"BGG request failed: {ex.Message}" });
                }
                catch (Exception ex)
                {
                    results.Add(new { input, found = false, message = ex.Message });
                }
            }

            return Ok(results);
        }

        // ─── Rules & Video Endpoints ─────────────────────────────────────────────

        [HttpPost("/API/DiscoverBoardgameRules")]
        public async Task<IActionResult> DiscoverBoardgameRules(int id)
        {
            if (!await IsCurrentUserEditor()) return Forbid();

            var game = await movieDb.Boardgames
                .Include(x => x.ExtraDetails)
                .FirstOrDefaultAsync(x => x.id == id);
            if (game == null) return NotFound(new { Success = false, Message = "Boardgame not found." });

            var (pdfCandidateUrls, videoUrls) = await boardgameRulesService.DiscoverAsync(game);

            if (pdfCandidateUrls.Count > 0)
                game.RulesPdfCandidateUrls = game.RulesPdfCandidateUrls.Union(pdfCandidateUrls).Distinct().ToList();
            if (videoUrls.Count > 0)
                game.HowToPlayVideoUrls = game.HowToPlayVideoUrls.Union(videoUrls).Distinct().ToList();

            await movieDb.SaveChangesAsync();
            return Ok(new { Success = true, data = new { rulesPdfCandidateUrls = game.RulesPdfCandidateUrls, howToPlayVideoUrls = game.HowToPlayVideoUrls } });
        }

        [HttpPost("/API/ApproveBoardgameRulesPdf")]
        public async Task<IActionResult> ApproveBoardgameRulesPdf(int id, [FromBody] ApprovePdfRequest req)
        {
            if (!await IsCurrentUserEditor()) return Forbid();
            if (string.IsNullOrWhiteSpace(req?.Url))
                return BadRequest(new { Success = false, Message = "No URL provided." });

            var game = await movieDb.Boardgames.FirstOrDefaultAsync(x => x.id == id);
            if (game == null) return NotFound(new { Success = false, Message = "Boardgame not found." });

            var pdfUrl = req.Url.Trim();
            var slot = game.RulesPdfUrls.Count;

            try
            {
                var response = await httpClient.GetAsync(pdfUrl);
                response.EnsureSuccessStatusCode();
                var bytes = await response.Content.ReadAsByteArrayAsync();
                await boardgamePdfRepository.SavePdfAsync(game.id, slot, bytes);
            }
            catch (Exception ex)
            {
                return StatusCode(502, new { Success = false, Message = $"Failed to download PDF: {ex.Message}" });
            }

            var approved = game.RulesPdfUrls;
            approved.Add(new RulesPdfEntry { Url = pdfUrl });
            game.RulesPdfUrls = approved;
            game.RulesPdfCandidateUrls = game.RulesPdfCandidateUrls.Where(u => u != pdfUrl).ToList();

            await movieDb.SaveChangesAsync();
            return Ok(new { Success = true, data = new { rulesPdfUrls = game.RulesPdfUrls.Select(e => new { url = e.Url, name = e.Name }), rulesPdfCandidateUrls = game.RulesPdfCandidateUrls, slot } });
        }

        [HttpPost("/API/RemoveBoardgameRulesPdf")]
        public async Task<IActionResult> RemoveBoardgameRulesPdf(int id, int slot)
        {
            if (!await IsCurrentUserEditor()) return Forbid();

            var game = await movieDb.Boardgames.FirstOrDefaultAsync(x => x.id == id);
            if (game == null) return NotFound(new { Success = false, Message = "Boardgame not found." });

            var urls = game.RulesPdfUrls;
            if (slot < 0 || slot >= urls.Count)
                return BadRequest(new { Success = false, Message = "Invalid slot." });

            boardgamePdfRepository.DeleteAndCompact(game.id, slot, urls.Count);
            urls.RemoveAt(slot);
            game.RulesPdfUrls = urls;

            await movieDb.SaveChangesAsync();
            return Ok(new { Success = true, data = new { rulesPdfUrls = game.RulesPdfUrls.Select(e => new { url = e.Url, name = e.Name }) } });
        }

        [HttpPost("/API/RemoveBoardgameRulesPdfCandidate")]
        public async Task<IActionResult> RemoveBoardgameRulesPdfCandidate(int id, [FromBody] ApprovePdfRequest req)
        {
            if (!await IsCurrentUserEditor()) return Forbid();
            if (string.IsNullOrWhiteSpace(req?.Url))
                return BadRequest(new { Success = false, Message = "No URL provided." });

            var game = await movieDb.Boardgames.FirstOrDefaultAsync(x => x.id == id);
            if (game == null) return NotFound(new { Success = false, Message = "Boardgame not found." });

            game.RulesPdfCandidateUrls = game.RulesPdfCandidateUrls.Where(u => u != req.Url.Trim()).ToList();
            await movieDb.SaveChangesAsync();
            return Ok(new { Success = true, data = new { rulesPdfCandidateUrls = game.RulesPdfCandidateUrls } });
        }

        public class ApprovePdfRequest { public string? Url { get; set; } }

        [HttpPost("/API/UploadBoardgameRulesPdf")]
        public async Task<IActionResult> UploadBoardgameRulesPdf(int id, IFormFile file)
        {
            if (!await IsCurrentUserEditor()) return Forbid();
            if (file == null || file.Length == 0)
                return BadRequest(new { Success = false, Message = "No file provided." });
            if (!file.ContentType.Contains("pdf", StringComparison.OrdinalIgnoreCase) &&
                !file.FileName.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
                return BadRequest(new { Success = false, Message = "Only PDF files are allowed." });

            var game = await movieDb.Boardgames.FirstOrDefaultAsync(x => x.id == id);
            if (game == null) return NotFound(new { Success = false, Message = "Boardgame not found." });

            var slot = game.RulesPdfUrls.Count;
            using var ms = new MemoryStream();
            await file.CopyToAsync(ms);
            await boardgamePdfRepository.SavePdfAsync(game.id, slot, ms.ToArray());

            var approved = game.RulesPdfUrls;
            var name = Path.GetFileNameWithoutExtension(file.FileName);
            approved.Add(new RulesPdfEntry { Url = $"/BoardgamePdf/{game.id}/{slot}", Name = name });
            game.RulesPdfUrls = approved;

            await movieDb.SaveChangesAsync();
            return Ok(new { Success = true, data = new { rulesPdfUrls = game.RulesPdfUrls.Select(e => new { url = e.Url, name = e.Name }), slot } });
        }

        [HttpPost("/API/BatchDiscoverBoardgameRules")]
        public async Task<IActionResult> BatchDiscoverBoardgameRules([FromBody] int[] ids)
        {
            if (!await IsCurrentUserEditor()) return Forbid();
            if (ids == null || ids.Length == 0) return BadRequest(new { Success = false, Message = "No ids provided." });

            var results = new List<object>();
            foreach (var gameId in ids)
            {
                var game = await movieDb.Boardgames
                    .Include(x => x.ExtraDetails)
                    .FirstOrDefaultAsync(x => x.id == gameId);
                if (game == null) { results.Add(new { id = gameId, success = false, message = "Not found" }); continue; }

                try
                {
                    var (pdfCandidateUrls, videoUrls) = await boardgameRulesService.DiscoverAsync(game);
                    if (pdfCandidateUrls.Count > 0)
                        game.RulesPdfCandidateUrls = game.RulesPdfCandidateUrls.Union(pdfCandidateUrls).Distinct().ToList();
                    if (videoUrls.Count > 0)
                        game.HowToPlayVideoUrls = game.HowToPlayVideoUrls.Union(videoUrls).Distinct().ToList();
                    var entries = game.HowToPlayVideoEntries;
                    if (await youTubeService.RefreshEntriesAsync(entries))
                        game.HowToPlayVideoEntries = entries;
                    await movieDb.SaveChangesAsync();
                    results.Add(new { id = gameId, success = true, rulesPdfCandidateUrls = game.RulesPdfCandidateUrls, howToPlayVideoUrls = game.HowToPlayVideoUrls });
                }
                catch (Exception ex)
                {
                    results.Add(new { id = gameId, success = false, message = ex.Message });
                }

                await Task.Delay(1000);
            }

            return Ok(new { Success = true, results });
        }

        [HttpPut("/API/UpdateBoardgameRules")]
        public async Task<IActionResult> UpdateBoardgameRules([FromBody] UpdateBoardgameRulesRequest req)
        {
            if (!await IsCurrentUserEditor()) return Forbid();
            if (req == null) return BadRequest(new { Success = false, Message = "No data provided." });

            var game = await movieDb.Boardgames.FirstOrDefaultAsync(x => x.id == req.Id);
            if (game == null) return NotFound(new { Success = false, Message = "Boardgame not found." });

            if (req.HowToPlayVideoUrls != null) game.HowToPlayVideoUrls = req.HowToPlayVideoUrls;
            if (req.RulesPdfUrls != null) game.RulesPdfUrls = req.RulesPdfUrls;

            if (req.HowToPlayVideoUrls != null)
            {
                var entries = game.HowToPlayVideoEntries;
                if (await youTubeService.RefreshEntriesAsync(entries))
                    game.HowToPlayVideoEntries = entries;
            }

            await movieDb.SaveChangesAsync();

            return Ok(new { Success = true, data = new {
                rulesPdfUrls = game.RulesPdfUrls.Select(e => new { url = e.Url, name = e.Name }),
                howToPlayVideoUrls = game.HowToPlayVideoUrls,
                howToPlayVideoUrlsJson = game.HowToPlayVideoUrlsJson,
            }});
        }

        public class UpdateBoardgameRulesRequest
        {
            public int Id { get; set; }
            public List<string>? HowToPlayVideoUrls { get; set; }
            public List<RulesPdfEntry>? RulesPdfUrls { get; set; }
        }

        private async Task<bool> IsCurrentUserEditor()
        {
            var userId = GetCurrentUserId();
            if (!userId.HasValue) return false;
            var settings = await movieDb.UserSettings.FirstOrDefaultAsync(s => s.UserID == userId.Value && s.SettingKey == "CanEditMovies");
            return settings != null && string.Equals(settings.SettingValue, "true", StringComparison.OrdinalIgnoreCase);
        }

        // ── Library-ingest review (editor-gated) ─────────────────────────────────────
        // Surfaces the rows the bulk library ingest created (ReviewBatch != null) — still
        // quarantined from browse — so they can be Approved (un-quarantined into the
        // library), Rejected (deleted), or corrected before they're trusted. The whole
        // batch is reversible: every ingested row carries its ReviewBatch tag.

        public class IngestReviewItemDto
        {
            public int id { get; set; }
            // Which table this id lives in: "movie" | "series" | "misc". MiscVideo has its own id
            // sequence, so every detail/approve/reject must carry this — a bare id is ambiguous.
            public string Kind { get; set; } = "movie";
            public string? Title { get; set; }
            public string? SimpleTitle { get; set; }
            public string? imdbID { get; set; }
            public string? TitleType { get; set; }
            /// <summary>Resolved release year — compared to the on-disk folder year to confirm a match.</summary>
            public int? Year { get; set; }
            /// <summary>Authoritative IMDb title from the last scrape/enrich. Lets the card show the IMDb
            /// cross-check from stored data — no per-card live OMDB lookup on page load.</summary>
            public string? ImdbScrapedTitle { get; set; }
            /// <summary>Current stored poster link (pre-fills the editable Poster URL field).</summary>
            public string? PosterLink { get; set; }
            public string? ReviewBatch { get; set; }
            public string? ReviewProvenance { get; set; }
            public string? ReviewConfidence { get; set; }
            public string? ReviewSourcePath { get; set; }
            public bool IsSeries { get; set; }
            public int FileCount { get; set; }      // movie-shaped / misc: mapped media files
            public int PlayableCount { get; set; }  // …of those, Jellyfin-ready right now (streamable)
            public int MissingCount { get; set; }   // …of those, flagged gone by a sync (MissingSinceUtc)
            public int EpisodeTotal { get; set; }   // series: total episodes
            public int EpisodeHave { get; set; }    // series: episodes that have a file ("have X of Y")
            public int EpisodePlayable { get; set; }// series: episodes with a Jellyfin-ready file
            // Scraper's own uncertainty flag (wrong-looking match, ambiguous title, etc.).
            public bool ImdbNeedsReview { get; set; }
            public string? ImdbReviewReason { get; set; }
            // ── misc-video only ──
            public string? Category { get; set; }
            public string? RelatedTitle { get; set; }
            public string? CollectionName { get; set; }
        }

        [HttpGet("/API/Admin/IngestReview/List")]
        public async Task<IActionResult> IngestReviewList(string scope = "batch")
        {
            if (!await IsCurrentUserEditor()) return Forbid();
            bool gapsScope = string.Equals(scope, "gaps", StringComparison.OrdinalIgnoreCase);

            // File / episode summaries so each card shows "N files" / "have X of Y" and, crucially,
            // whether those files are actually *streamable* now (synced to Jellyfin, not gone missing)
            // — an unplayable title is a concern the reviewer must see. Computed first so the "oddities"
            // scope below can select live titles whose files aren't streamable.
            var fileByPlayable = (await movieDb.MediaFiles.GroupBy(f => f.PlayableId)
                .Select(g => new
                {
                    g.Key,
                    n = g.Count(),
                    playable = g.Count(f => f.JellyfinItemId != null && f.MissingSinceUtc == null),
                    missing = g.Count(f => f.MissingSinceUtc != null),
                    primary = g.Count(f => f.Role == MovieFileRole.Primary),
                }).ToListAsync()).ToDictionary(x => x.Key, x => x);
            var epTotal = await movieDb.Episodes.Where(e => e.SeriesId != null).GroupBy(e => e.SeriesId!.Value)
                .Select(g => new { g.Key, n = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.n);
            var epHave = await movieDb.Episodes
                .Where(e => e.SeriesId != null && e.PlayableId != null && movieDb.MediaFiles.Any(f => f.PlayableId == e.PlayableId))
                .GroupBy(e => e.SeriesId!.Value).Select(g => new { g.Key, n = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.n);
            var epPlayable = await movieDb.Episodes
                .Where(e => e.SeriesId != null && e.PlayableId != null
                    && movieDb.MediaFiles.Any(f => f.PlayableId == e.PlayableId && f.JellyfinItemId != null && f.MissingSinceUtc == null))
                .GroupBy(e => e.SeriesId!.Value).Select(g => new { g.Key, n = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.n);

            // "oddities" scope additionally surfaces LIVE (already-approved, ReviewBatch == null) titles
            // with a file oddity — files present but none streamable, a file gone missing, or no Primary
            // — that haven't been explicitly acknowledged (OddityAcknowledgedUtc). A live title with no
            // files at all is a different concern (a gap), not surfaced here.
            bool oddScope = string.Equals(scope, "oddities", StringComparison.OrdinalIgnoreCase);
            var oddPlayableIds = oddScope
                ? fileByPlayable.Where(kv => kv.Value.n > 0 && (kv.Value.playable == 0 || kv.Value.missing > 0 || kv.Value.primary == 0))
                    .Select(kv => kv.Key).ToHashSet()
                : new HashSet<int>();

            // Movies only — series-typed rows now live in the Series table (added below). A row still
            // in a ReviewBatch is the exception: a title filed as a movie that IMDb calls a
            // mini-series is exactly what a reviewer has to rule on, and excluding it by type would
            // quarantine it into invisibility — present in the DB, absent from the queue, hidden from
            // browse. The exclusion applies to LIVE rows, which is where it came from.
            var raw = await movieDb.Movies
                .Where(m => (m.ReviewBatch != null
                        || (m.TitleType != TitleType.TvSeries && m.TitleType != TitleType.TvMiniSeries))
                    && (m.ReviewBatch != null
                        || (oddScope && m.ReviewBatch == null && m.OddityAcknowledgedUtc == null
                            && m.PlayableId != null && oddPlayableIds.Contains(m.PlayableId.Value))))
                .Select(m => new { m.id, m.Title, m.SimpleTitle, m.imdbID, m.TitleType, m.PlayableId, m.ReviewBatch, m.ReviewProvenance, m.ReviewConfidence, m.ReviewSourcePath, m.ImdbNeedsReview, m.ImdbReviewReason, m.ReleaseDate, m.ImdbReleaseDate, m.ImdbScrapedTitle, PosterLink = m.PosterDetails != null ? m.PosterDetails.PosterLink : null })
                .ToListAsync();

            // Lowest-trust first so the riskiest resolutions get eyeballed before the easy bulk.
            static int ConfRank(string? c) => (c ?? "").ToUpperInvariant() switch { "LOW" => 0, "MEDIUM" => 1, "NONE" => 0, "HIGH" => 2, _ => 3 };
            static int ProvRank(string? p) => p switch { "manual" => -1, "web-search" => 0, "suggestion-api" => 1, "finalsort-cache" => 2, _ => 3 };

            var items = raw
                .Select(m => new IngestReviewItemDto
                {
                    id = m.id,
                    Kind = "movie",
                    Title = m.Title,
                    SimpleTitle = m.SimpleTitle,
                    imdbID = m.imdbID,
                    TitleType = m.TitleType.ToString(),
                    Year = m.ReleaseDate != null ? m.ReleaseDate.Value.Year : (m.ImdbReleaseDate != null ? m.ImdbReleaseDate.Value.Year : (int?)null),
                    ImdbScrapedTitle = m.ImdbScrapedTitle,
                    PosterLink = m.PosterLink,
                    ReviewBatch = m.ReviewBatch,
                    ReviewProvenance = m.ReviewProvenance,
                    ReviewConfidence = m.ReviewConfidence,
                    ReviewSourcePath = m.ReviewSourcePath,
                    ImdbNeedsReview = m.ImdbNeedsReview,
                    ImdbReviewReason = m.ImdbReviewReason,
                    IsSeries = false,
                    FileCount = (m.PlayableId != null && fileByPlayable.TryGetValue(m.PlayableId.Value, out var fc)) ? fc.n : 0,
                    PlayableCount = (m.PlayableId != null && fileByPlayable.TryGetValue(m.PlayableId.Value, out var pc)) ? pc.playable : 0,
                    MissingCount = (m.PlayableId != null && fileByPlayable.TryGetValue(m.PlayableId.Value, out var mc)) ? mc.missing : 0,
                })
                .ToList();

            // Series (their own table now), with "have X of Y" episode summaries via SeriesId. In "gaps"
            // scope we ALSO surface series that have episodes not yet streamable (epPlayable < total) even
            // if already approved (ReviewBatch == null), so they can be hand-mapped.
            var gapSeriesIds = gapsScope
                ? epTotal.Where(kv => (epPlayable.TryGetValue(kv.Key, out var p) ? p : 0) < kv.Value).Select(kv => kv.Key).ToHashSet()
                : new HashSet<int>();
            // A series oddity: episodes are mapped but some aren't streamable (file missing / not synced) —
            // epHave > epPlayable. (Plain unmapped gaps belong to the "gaps" scope, not here.)
            var oddSeriesIds = oddScope
                ? epHave.Where(kv => kv.Value > (epPlayable.TryGetValue(kv.Key, out var p) ? p : 0)).Select(kv => kv.Key).ToHashSet()
                : new HashSet<int>();
            // A gap/oddity on an already-approved series is flagged ONCE for review; once the reviewer
            // acknowledges it (OddityAcknowledgedUtc), the known gap must not keep re-surfacing. Pending
            // (ReviewBatch != null) rows always show regardless.
            var seriesRaw = await movieDb.Series
                .Where(s => s.ReviewBatch != null
                    || (gapSeriesIds.Contains(s.Id) && s.OddityAcknowledgedUtc == null)
                    || (oddScope && oddSeriesIds.Contains(s.Id) && s.OddityAcknowledgedUtc == null))
                .Select(s => new { s.Id, s.Title, s.SimpleTitle, s.imdbID, s.TitleType, s.ReviewBatch, s.ReviewProvenance, s.ReviewConfidence, s.ReviewSourcePath, s.ImdbNeedsReview, s.ImdbReviewReason, s.ReleaseDate, s.ImdbReleaseDate, s.StartYear, s.ImdbScrapedTitle, PosterLink = s.PosterDetails != null ? s.PosterDetails.PosterLink : null })
                .ToListAsync();
            items.AddRange(seriesRaw.Select(s => new IngestReviewItemDto
            {
                id = s.Id,
                Kind = "series",
                Title = s.Title,
                SimpleTitle = s.SimpleTitle,
                imdbID = s.imdbID,
                TitleType = s.TitleType.ToString(),
                Year = s.ReleaseDate != null ? s.ReleaseDate.Value.Year : (s.ImdbReleaseDate != null ? s.ImdbReleaseDate.Value.Year : s.StartYear),
                ImdbScrapedTitle = s.ImdbScrapedTitle,
                PosterLink = s.PosterLink,
                ReviewBatch = s.ReviewBatch,
                ReviewProvenance = s.ReviewProvenance,
                ReviewConfidence = s.ReviewConfidence,
                ReviewSourcePath = s.ReviewSourcePath,
                ImdbNeedsReview = s.ImdbNeedsReview,
                ImdbReviewReason = s.ImdbReviewReason,
                IsSeries = true,
                EpisodeTotal = epTotal.TryGetValue(s.Id, out var et) ? et : 0,
                EpisodeHave = epHave.TryGetValue(s.Id, out var eh) ? eh : 0,
                EpisodePlayable = epPlayable.TryGetValue(s.Id, out var ep) ? ep : 0,
            }));

            // Lowest-trust first, then group franchises/related titles by the canonical sort key
            // (SimpleTitle — same ordering as Browse, so e.g. Star Trek 1/2/3/4 and Dragon Ball Z/Kai/GT/
            // Super sit together) for a coherent review pass; Title is the fallback.
            items = items
                .OrderBy(i => ConfRank(i.ReviewConfidence))
                .ThenBy(i => ProvRank(i.ReviewProvenance))
                .ThenBy(i => string.IsNullOrEmpty(i.SimpleTitle) ? i.Title : i.SimpleTitle, StringComparer.OrdinalIgnoreCase)
                .ToList();

            // MiscVideos (no own tt: workprints, stage performances, instructional/shorts sets) carry
            // Kind="misc". Their related title resolves through Movie (RelatedMovieId) OR Series (RelatedSeriesId).
            // A misc that's ATTACHED to a title AND has no standalone Description is episodic-extra content
            // (an OP/ED, music video, featurette) — it gets NO card of its own; it's surfaced on its parent's
            // card (relatedMisc) and approved/rejected along with that parent. Only standalone misc (unrelated)
            // or a related misc that carries its own Description earns a review card here.
            var miscRaw = await movieDb.MiscVideos
                .Where(v => v.ReviewBatch != null
                    && ((v.RelatedMovieId == null && v.RelatedSeriesId == null)
                        || (v.Description != null && v.Description != "")))
                .Select(v => new { v.Id, v.PlayableId, v.Title, v.SimpleTitle, v.Year, v.Category, v.CollectionName, v.RelatedMovieId, v.RelatedSeriesId, v.ReviewBatch, v.ReviewProvenance, v.ReviewSourcePath })
                .ToListAsync();
            if (miscRaw.Count > 0)
            {
                var relMovieIds = miscRaw.Where(v => v.RelatedMovieId != null).Select(v => v.RelatedMovieId!.Value).Distinct().ToList();
                var relSeriesIds = miscRaw.Where(v => v.RelatedSeriesId != null).Select(v => v.RelatedSeriesId!.Value).Distinct().ToList();
                var relMovieTitles = await movieDb.Movies.Where(m => relMovieIds.Contains(m.id)).Select(m => new { m.id, m.Title }).ToDictionaryAsync(x => x.id, x => x.Title);
                var relSeriesTitles = await movieDb.Series.Where(s => relSeriesIds.Contains(s.Id)).Select(s => new { s.Id, s.Title }).ToDictionaryAsync(x => x.Id, x => x.Title);
                items.AddRange(miscRaw
                    .Select(v => new IngestReviewItemDto
                    {
                        id = v.Id,
                        Kind = "misc",
                        Title = v.Title,
                        SimpleTitle = v.SimpleTitle,
                        Year = v.Year,
                        TitleType = "MiscVideo",
                        Category = v.Category,
                        CollectionName = v.CollectionName,
                        RelatedTitle = (v.RelatedMovieId != null && relMovieTitles.TryGetValue(v.RelatedMovieId.Value, out var rmt)) ? rmt
                                     : (v.RelatedSeriesId != null && relSeriesTitles.TryGetValue(v.RelatedSeriesId.Value, out var rst)) ? rst : null,
                        ReviewBatch = v.ReviewBatch,
                        ReviewProvenance = v.ReviewProvenance,
                        ReviewSourcePath = v.ReviewSourcePath,
                        IsSeries = false,
                        FileCount = fileByPlayable.TryGetValue(v.PlayableId, out var mfc) ? mfc.n : 0,
                        PlayableCount = fileByPlayable.TryGetValue(v.PlayableId, out var mpc) ? mpc.playable : 0,
                        MissingCount = fileByPlayable.TryGetValue(v.PlayableId, out var mmc) ? mmc.missing : 0,
                    })
                    .OrderBy(i => i.CollectionName ?? "")
                    .ThenBy(i => i.Title, StringComparer.OrdinalIgnoreCase));
            }

            var batches = items.GroupBy(i => i.ReviewBatch).Select(g => new { batch = g.Key, count = g.Count() }).ToList();
            var byType = items.GroupBy(i => i.TitleType).Select(g => new { type = g.Key, count = g.Count() }).OrderByDescending(x => x.count).ToList();
            var byConfidence = items.GroupBy(i => i.ReviewConfidence ?? "?").Select(g => new { confidence = g.Key, count = g.Count() }).ToList();

            return Ok(new { total = items.Count, batches, byType, byConfidence, items });
        }

        public class AcknowledgeOddityRequest
        {
            public int Id { get; set; }
            public string Kind { get; set; } = "movie";   // "movie" | "series"
        }

        // Mark a live title's file oddity as reviewed so it stops surfacing in the "oddities" scope.
        // Does NOT touch files or ReviewBatch — purely "I've seen this, it's fine / I'll handle it".
        [HttpPost("/API/Admin/IngestReview/AcknowledgeOddity")]
        public async Task<IActionResult> AcknowledgeOddity([FromBody] AcknowledgeOddityRequest req)
        {
            if (!await IsCurrentUserEditor()) return Forbid();
            if (req == null) return BadRequest(new { Message = "Invalid request." });
            var now = DateTime.UtcNow;
            if (string.Equals(req.Kind, "series", StringComparison.OrdinalIgnoreCase))
            {
                var s = await movieDb.Series.FirstOrDefaultAsync(x => x.Id == req.Id);
                if (s == null) return NotFound(new { Message = "Series not found" });
                s.OddityAcknowledgedUtc = now;
            }
            else
            {
                var m = await movieDb.Movies.FirstOrDefaultAsync(x => x.id == req.Id);
                if (m == null) return NotFound(new { Message = "Movie not found" });
                m.OddityAcknowledgedUtc = now;
            }
            await movieDb.SaveChangesAsync();
            return Ok(new { Success = true });
        }

        // Per-title detail for the review tool: a movie's media files, or a series' episodes grouped by
        // season with the file mapped to each and the match strategy (MediaFile.Label "match:<strategy>")
        // so the position-based matches (absolute/combined/title) can be scrutinized. Lazy-loaded per card.
        [HttpGet("/API/Admin/IngestReview/Detail")]
        public async Task<IActionResult> IngestReviewDetail(int id, string kind = "movie")
        {
            if (!await IsCurrentUserEditor()) return Forbid();

            if (string.Equals(kind, "misc", StringComparison.OrdinalIgnoreCase))
            {
                var mv = await movieDb.MiscVideos.FirstOrDefaultAsync(v => v.Id == id);
                if (mv == null) return NotFound(new { Message = "Not found" });
                var miscFiles = await movieDb.MediaFiles.Where(f => f.PlayableId == mv.PlayableId)
                    .OrderBy(f => f.Role).ThenBy(f => f.PartNumber).ThenBy(f => f.Id)
                    .Select(f => (object)new { path = f.Path, role = f.Role.ToString(), label = f.Label })
                    .ToListAsync();
                string? relTitle = null, relKind = null;
                if (mv.RelatedMovieId != null)
                {
                    relTitle = await movieDb.Movies.Where(m => m.id == mv.RelatedMovieId).Select(m => m.Title).FirstOrDefaultAsync();
                    relKind = "movie";
                }
                else if (mv.RelatedSeriesId != null)
                {
                    relTitle = await movieDb.Series.Where(s => s.Id == mv.RelatedSeriesId).Select(s => s.Title).FirstOrDefaultAsync();
                    relKind = "series";
                }
                return Ok(new { kind = "misc", category = mv.Category, collectionName = mv.CollectionName, relatedTitle = relTitle, relatedKind = relKind, description = mv.Description, files = miscFiles });
            }

            // ── series (its own table): episodes by SeriesId, grouped by season, with mapped files + strategy ──
            if (string.Equals(kind, "series", StringComparison.OrdinalIgnoreCase))
            {
                var ser = await movieDb.Series.FirstOrDefaultAsync(s => s.Id == id);
                if (ser == null) return NotFound(new { Message = "Not found" });
                var allEps = await movieDb.Episodes.Where(e => e.SeriesId == id)
                    .OrderBy(e => e.SeasonNumber).ThenBy(e => e.EpisodeNumber)
                    .Select(e => new { e.Id, e.SeasonNumber, e.EpisodeNumber, e.Title, e.PlayableId })
                    .ToListAsync();
                // The (Season 0, Ep 0, "Extras") pseudo-episode is the holder for series/season-level Extra
                // files (not a real episode) — pull it out and surface its files separately.
                static bool IsExtrasHolder(int s, int e, string? t) => s == 0 && e == 0 && t == "Extras";
                var extrasHolder = allEps.FirstOrDefault(e => IsExtrasHolder(e.SeasonNumber, e.EpisodeNumber, e.Title));
                var seps = allEps.Where(e => !IsExtrasHolder(e.SeasonNumber, e.EpisodeNumber, e.Title)).ToList();
                var sPlayableIds = allEps.Where(e => e.PlayableId != null).Select(e => e.PlayableId!.Value).ToList();
                var sFilesByPlayable = (await movieDb.MediaFiles.Where(f => sPlayableIds.Contains(f.PlayableId))
                        .Select(f => new { f.Id, f.PlayableId, f.Path, f.Label, f.Role }).ToListAsync())
                    .GroupBy(f => f.PlayableId).ToDictionary(g => g.Key, g => g.ToList());
                var emptyFiles = new List<object>().Select(_ => new { mediaFileId = 0, path = (string)null, role = (string)null, label = (string)null }).ToList();
                var sSeasons = seps.GroupBy(e => e.SeasonNumber).OrderBy(g => g.Key).Select(g => new
                {
                    season = g.Key,
                    episodes = g.Select(e => new
                    {
                        episodeId = e.Id,
                        episode = e.EpisodeNumber,
                        title = e.Title,
                        files = (e.PlayableId != null && sFilesByPlayable.TryGetValue(e.PlayableId.Value, out var fl))
                            ? fl.Select(f => new { mediaFileId = f.Id, path = f.Path, role = f.Role.ToString(), label = f.Label }).ToList()
                            : emptyFiles,
                    }).ToList(),
                }).ToList();
                var seriesExtras = (extrasHolder?.PlayableId != null && sFilesByPlayable.TryGetValue(extrasHolder.PlayableId.Value, out var xf))
                    ? xf.Select(f => new { mediaFileId = f.Id, path = f.Path, role = f.Role.ToString(), label = f.Label }).ToList()
                    : emptyFiles;
                var sGenres = await movieDb.SeriesGenres.Where(g => g.SeriesId == id).OrderBy(g => g.Ordering).Select(g => g.Genre.Name).ToListAsync();
                var sCredits = await movieDb.SeriesCredits.Where(cr => cr.SeriesId == id).OrderBy(cr => cr.Ordering)
                    .Select(cr => new { cr.Role, Name = cr.Person.DisplayName }).ToListAsync();
                var sPlot = await movieDb.SeriesPlotSummaries.Where(p => p.SeriesId == id).OrderBy(p => p.Ordering).Select(p => p.Text).FirstOrDefaultAsync();
                string[] SNames(CreditRole r, int take) => sCredits.Where(x => x.Role == r && x.Name != null).Select(x => x.Name!).Distinct().Take(take).ToArray();
                // Re-mark the cached folder dump against CURRENT mappings: scan-series-folders bakes the
                // [OK]/[??] flags at scan time, so files mapped AFTER the last scan wrongly show [??].
                // Match by filename against the series' live MediaFile names and recompute the header counts.
                string liveFolderListing = ser.FolderListing;
                if (!string.IsNullOrEmpty(liveFolderListing))
                {
                    // Runs on the Linux server, but the stored paths are Windows ("L:\...\file.mkv") — so
                    // System.IO.Path.GetFileName would NOT strip them (backslash isn't a Linux separator).
                    // Split on BOTH separators to get the bare filename regardless of host OS.
                    static string BaseName(string p) => (p ?? "").Replace('\\', '/').TrimEnd('/').Split('/')[^1];
                    // Mark a line [OK] if its filename is captured by ANY title, not just this series.
                    // Co-located series share one folder dump (e.g. the 2003 micro-series and the 2008
                    // series both live under "Star Wars - The Clone Wars (2003-2020)"), so a file mapped
                    // to a SIBLING title would otherwise show a misleading "[??] NOT captured" here.
                    var mappedNames = (await movieDb.MediaFiles.Select(f => f.Path).ToListAsync())
                        .Select(p => BaseName(p).Trim().ToLowerInvariant())
                        .Where(n => !string.IsNullOrEmpty(n)).ToHashSet();
                    var lineRx = new System.Text.RegularExpressions.Regex(@"^(\[OK\]|\[\?\?\]) (.*?)(    \S+ [KMG]B)\s*$");
                    int okN = 0, noN = 0;
                    var outLines = liveFolderListing.Replace("\r", "").Split('\n').Select(line =>
                    {
                        var m = lineRx.Match(line);
                        if (!m.Success) return line;
                        var rel = m.Groups[2].Value;
                        var name = BaseName(rel).Trim().ToLowerInvariant();
                        bool ok = !string.IsNullOrEmpty(name) && mappedNames.Contains(name);
                        if (ok) okN++; else noN++;
                        return (ok ? "[OK]" : "[??]") + " " + rel + m.Groups[3].Value;
                    }).ToList();
                    for (int i = 0; i < outLines.Count; i++)
                        outLines[i] = System.Text.RegularExpressions.Regex.Replace(outLines[i],
                            @"\(\[OK\] mapped \d+ / \[\?\?\] NOT captured \d+\)",
                            $"([OK] mapped {okN} / [??] NOT captured {noN})");
                    liveFolderListing = string.Join("\n", outLines);
                }
                var seriesRelatedMisc = await LoadRelatedMiscAsync(null, id);
                return Ok(new
                {
                    kind = "series",
                    episodeTotal = seps.Count,
                    episodeHave = seps.Count(e => e.PlayableId != null && sFilesByPlayable.ContainsKey(e.PlayableId.Value)),
                    seasons = sSeasons,
                    seriesExtras,
                    relatedMisc = seriesRelatedMisc,
                    folderListing = liveFolderListing,   // re-marked live vs current mappings (scan-time flags go stale)
                    meta = new
                    {
                        plot = sPlot ?? ser.Plot,
                        genres = sGenres,
                        directors = SNames(CreditRole.Director, 5),
                        writers = SNames(CreditRole.Writer, 5),
                        cast = SNames(CreditRole.Actor, 10),
                        runtime = ser.Runtime,
                        runtimeMinutes = ser.RuntimeMinutes,
                        imdbRating = ser.ImdbRatingScraped ?? ser.imdbRating,
                        rtTomatometer = ser.RtTomatometer,
                        rtPopcornmeter = ser.RtPopcornmeter,
                        mpaa = ser.MpaaRating,
                        tagline = ser.Tagline,
                        year = ser.ReleaseDate != null ? ser.ReleaseDate.Value.Year : (ser.ImdbReleaseDate != null ? ser.ImdbReleaseDate.Value.Year : (int?)null),
                    },
                });
            }

            // ── movie ──
            var movie = await movieDb.Movies.FirstOrDefaultAsync(m => m.id == id);
            if (movie == null) return NotFound(new { Message = "Not found" });
            var files = movie.PlayableId == null
                ? new List<object>()
                : await movieDb.MediaFiles.Where(f => f.PlayableId == movie.PlayableId)
                    .OrderBy(f => f.Role).ThenBy(f => f.PartNumber).ThenBy(f => f.Id)
                    .Select(f => (object)new { mediaFileId = f.Id, path = f.Path, role = f.Role.ToString(), label = f.Label, partNumber = f.PartNumber,
                        isPlayable = f.JellyfinItemId != null && f.MissingSinceUtc == null, missing = f.MissingSinceUtc != null })
                    .ToListAsync();
            // Cached IMDb/TMDB metadata (normalized tables) so the review card can show what's being approved
            // — plot / genres / director / cast / ratings — with no live lookup.
            var mGenres = await movieDb.MovieGenres.Where(g => g.MovieID == id).OrderBy(g => g.Ordering).Select(g => g.Genre.Name).ToListAsync();
            var mCredits = await movieDb.MovieCredits.Where(cr => cr.MovieID == id).OrderBy(cr => cr.Ordering)
                .Select(cr => new { cr.Role, Name = cr.Person.DisplayName }).ToListAsync();
            var mPlot = await movieDb.MoviePlotSummaries.Where(p => p.MovieID == id).OrderBy(p => p.Ordering).Select(p => p.Text).FirstOrDefaultAsync();
            string[] MNames(CreditRole r, int take) => mCredits.Where(x => x.Role == r && x.Name != null).Select(x => x.Name!).Distinct().Take(take).ToArray();
            var movieRelatedMisc = await LoadRelatedMiscAsync(id, null);
            return Ok(new
            {
                kind = "movie",
                files,
                relatedMisc = movieRelatedMisc,
                meta = new
                {
                    plot = mPlot ?? movie.Plot,
                    genres = mGenres,
                    directors = MNames(CreditRole.Director, 5),
                    writers = MNames(CreditRole.Writer, 5),
                    cast = MNames(CreditRole.Actor, 10),
                    runtime = movie.Runtime,
                    runtimeMinutes = movie.RuntimeMinutes,
                    imdbRating = movie.ImdbRatingScraped ?? movie.imdbRating,
                    rtTomatometer = movie.RtTomatometer,
                    rtPopcornmeter = movie.RtPopcornmeter,
                    mpaa = movie.MpaaRating,
                    tagline = movie.Tagline,
                    year = movie.ReleaseDate != null ? movie.ReleaseDate.Value.Year : (movie.ImdbReleaseDate != null ? movie.ImdbReleaseDate.Value.Year : (int?)null),
                }
            });
        }

        // Extras (MiscVideos) that point AT a title via RelatedMovieId/RelatedSeriesId — surfaced on the
        // movie/series review card so you can see what's attached without hunting the misc queue. Pass the
        // one relevant id; the other stays null.
        private async Task<List<object>> LoadRelatedMiscAsync(int? relatedMovieId, int? relatedSeriesId)
        {
            var rel = await movieDb.MiscVideos
                .Where(v => (relatedMovieId != null && v.RelatedMovieId == relatedMovieId)
                         || (relatedSeriesId != null && v.RelatedSeriesId == relatedSeriesId))
                .OrderBy(v => v.CollectionName).ThenBy(v => v.SortOrder).ThenBy(v => v.Title)
                .Select(v => new { v.Id, v.PlayableId, v.Title, v.Category, v.Year, v.CollectionName, Pending = v.ReviewBatch != null })
                .ToListAsync();
            if (rel.Count == 0) return new List<object>();
            var pids = rel.Select(v => v.PlayableId).ToList();
            var filesByPid = (await movieDb.MediaFiles.Where(f => pids.Contains(f.PlayableId))
                    .OrderBy(f => f.Role).ThenBy(f => f.PartNumber).ThenBy(f => f.Id)
                    .Select(f => new { f.PlayableId, f.Path, f.Role }).ToListAsync())
                .GroupBy(f => f.PlayableId)
                .ToDictionary(g => g.Key, g => g.Select(f => (object)new { path = f.Path, role = f.Role.ToString() }).ToList());
            return rel.Select(v => (object)new
            {
                id = v.Id,
                title = v.Title,
                category = v.Category,
                year = v.Year,
                collectionName = v.CollectionName,
                pending = v.Pending,
                files = filesByPid.TryGetValue(v.PlayableId, out var ff) ? ff : new List<object>(),
            }).ToList();
        }

        // A hand-mapped path must be the FULL on-disk path: Jellyfin matches by path, so a bare filename (or
        // any non-rooted value) looks "mapped" yet never streams (JellyfinItemId stays null). Accept a rooted
        // Windows/UNC path as-is; otherwise resolve a bare filename against the series' scanned FolderListing
        // snapshot (the prod web app can't read the NAS, but it has that snapshot) by unique filename. Returns
        // false with a reason when it can't be resolved, so the caller rejects rather than stores garbage.
        private static bool TryResolveMappedPath(string? submitted, string? folderListing, out string resolved, out string error)
        {
            resolved = (submitted ?? "").Trim();
            error = "";
            if (resolved.Length == 0) { error = "Path required"; return false; }

            bool rooted = System.Text.RegularExpressions.Regex.IsMatch(resolved, @"^[A-Za-z]:[\\/]") || resolved.StartsWith(@"\\");
            if (rooted) return true;

            var fileName = LastPathSegment(resolved);
            if (string.IsNullOrWhiteSpace(folderListing))
            {
                error = $"'{resolved}' isn't a full path and there's no folder scan to resolve it — paste the full L:\\ path (or run scan-series-folders).";
                return false;
            }
            var matches = ParseFolderListingFullPaths(folderListing)
                .Where(full => string.Equals(LastPathSegment(full), fileName, StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (matches.Count == 1) { resolved = matches[0]; return true; }
            error = matches.Count == 0
                ? $"Couldn't find '{fileName}' in this series' scanned folder — paste the full L:\\ path."
                : $"'{fileName}' matches {matches.Count} files in the folder — paste the full L:\\ path to disambiguate.";
            return false;
        }

        private static string LastPathSegment(string p)
        {
            var s = (p ?? "").Replace('/', '\\');
            var i = s.LastIndexOf('\\');
            return i >= 0 ? s.Substring(i + 1) : s;
        }

        // Reconstruct full paths from a Series.FolderListing snapshot (see ScanSeriesFoldersCommand): line 0 is
        // the folder root, then after a "----" separator each line is "<4-char flag> <relative path>    <size>".
        private static IEnumerable<string> ParseFolderListingFullPaths(string listing)
        {
            var lines = listing.Replace("\r\n", "\n").Split('\n');
            if (lines.Length == 0) yield break;
            var root = lines[0].Trim().TrimEnd('\\', '/');
            if (root.Length == 0) yield break;
            bool past = false;
            foreach (var raw in lines.Skip(1))
            {
                if (!past) { if (raw.StartsWith("----")) past = true; continue; }
                if (raw.Length < 6) continue;
                var rel = raw.Substring(5);              // drop the 4-char flag and the space after it
                var sep = rel.LastIndexOf("    ");        // strip the trailing "    <size>"
                if (sep > 0) rel = rel.Substring(0, sep);
                rel = rel.Trim();
                if (rel.Length == 0) continue;
                yield return root + "\\" + rel.Replace('/', '\\');
            }
        }

        public class SetEpisodeFileRequest { public int EpisodeId { get; set; } public string? Path { get; set; } }

        // Manually point a series episode at the correct on-disk file (chosen from the folder dump). Ensures
        // the episode has a Playable and sets/replaces its Primary MediaFile (Label "match:manual"); an empty
        // path clears it. Editor-gated. The file becomes streamable after the next Jellyfin sync (matched by
        // path). Disk files are untouched — this only records the mapping.
        [HttpPost("/API/Admin/IngestReview/SetEpisodeFile")]
        public async Task<IActionResult> IngestReviewSetEpisodeFile([FromBody] SetEpisodeFileRequest req)
        {
            if (!await IsCurrentUserEditor()) return Forbid();
            if (req == null || req.EpisodeId == 0) return BadRequest(new { Message = "EpisodeId required" });

            var ep = await movieDb.Episodes.FirstOrDefaultAsync(e => e.Id == req.EpisodeId);
            if (ep == null) return NotFound(new { Message = "Episode not found" });

            if (ep.PlayableId == null)
            {
                ep.Playable = new Playable { Kind = PlayableKind.Episode };
                await movieDb.SaveChangesAsync();   // assigns ep.PlayableId
            }
            var playableId = ep.PlayableId!.Value;

            var path = req.Path?.Trim();
            if (string.IsNullOrWhiteSpace(path))
            {
                var existing = await movieDb.MediaFiles.Where(f => f.PlayableId == playableId).ToListAsync();
                movieDb.MediaFiles.RemoveRange(existing);
                await movieDb.SaveChangesAsync();
                return Ok(new { Success = true, cleared = existing.Count });
            }

            // Resolve to a FULL path (reject a bare filename that would map but never stream).
            var listing = ep.SeriesId != null
                ? await movieDb.Series.Where(s => s.Id == ep.SeriesId.Value).Select(s => s.FolderListing).FirstOrDefaultAsync()
                : null;
            if (!TryResolveMappedPath(path, listing, out var fullPath, out var resolveErr))
                return BadRequest(new { Message = resolveErr });
            path = fullPath;

            // Replace any current Primary with the chosen file.
            var prior = await movieDb.MediaFiles.Where(f => f.PlayableId == playableId && f.Role == MovieFileRole.Primary).ToListAsync();
            movieDb.MediaFiles.RemoveRange(prior);
            movieDb.MediaFiles.Add(new MediaFile { PlayableId = playableId, Path = path, Role = MovieFileRole.Primary, Label = "match:manual" });
            await movieDb.SaveChangesAsync();
            return Ok(new { Success = true });
        }

        // Generalized hand-map: assign a file as Primary or Extra, to an episode OR to the series' Extras
        // holder (a Season-0 / Ep-0 "Extras" pseudo-episode that carries series/season-level extras).
        public class SetFileRequest
        {
            public string TargetType { get; set; } = "episode";   // "episode" | "series"
            public int TargetId { get; set; }                      // episodeId, or seriesId for "series"
            public int? SeasonNumber { get; set; }                 // optional: scope a series Extra to a season
            public string Role { get; set; } = "Primary";          // "Primary" | "Extra"
            public string? Path { get; set; }
        }

        [HttpPost("/API/Admin/IngestReview/SetFile")]
        public async Task<IActionResult> IngestReviewSetFile([FromBody] SetFileRequest req)
        {
            if (!await IsCurrentUserEditor()) return Forbid();
            if (req == null) return BadRequest(new { Message = "Body required" });
            var path = req.Path?.Trim();
            if (string.IsNullOrWhiteSpace(path)) return BadRequest(new { Message = "Path required" });

            bool toSeries = string.Equals(req.TargetType, "series", StringComparison.OrdinalIgnoreCase);
            bool toMovie = string.Equals(req.TargetType, "movie", StringComparison.OrdinalIgnoreCase);

            // Resolve to a FULL path before storing (a bare filename maps but never streams via Jellyfin).
            // A movie has no scanned FolderListing, so its paths must be pasted fully rooted (listing = null).
            int? listingSeriesId = toMovie ? (int?)null
                : toSeries ? req.TargetId
                : await movieDb.Episodes.Where(e => e.Id == req.TargetId).Select(e => e.SeriesId).FirstOrDefaultAsync();
            var listing = listingSeriesId != null
                ? await movieDb.Series.Where(s => s.Id == listingSeriesId.Value).Select(s => s.FolderListing).FirstOrDefaultAsync()
                : null;
            if (!TryResolveMappedPath(path, listing, out var fullPath, out var resolveErr))
                return BadRequest(new { Message = resolveErr });
            path = fullPath;
            // A series target is always an Extra (it has no episode of its own); a movie/episode target honors the role.
            var role = (toSeries || string.Equals(req.Role, "Extra", StringComparison.OrdinalIgnoreCase))
                ? MovieFileRole.Extra : MovieFileRole.Primary;

            int playableId;
            if (toMovie)
            {
                var mov = await movieDb.Movies.FirstOrDefaultAsync(m => m.id == req.TargetId);
                if (mov == null) return NotFound(new { Message = "Movie not found" });
                if (mov.PlayableId == null)
                {
                    mov.Playable = new Playable { Kind = PlayableKind.Movie };
                    await movieDb.SaveChangesAsync();
                }
                playableId = mov.PlayableId!.Value;
            }
            else if (toSeries)
            {
                // Find/create the (Season 0, Ep 0, "Extras") holder for this series.
                var holder = await movieDb.Episodes.FirstOrDefaultAsync(e =>
                    e.SeriesId == req.TargetId && e.SeasonNumber == 0 && e.EpisodeNumber == 0 && e.Title == "Extras");
                if (holder == null)
                {
                    holder = new Episode { SeriesId = req.TargetId, SeasonNumber = 0, EpisodeNumber = 0, Title = "Extras" };
                    movieDb.Episodes.Add(holder);
                    await movieDb.SaveChangesAsync();
                }
                if (holder.PlayableId == null)
                {
                    holder.Playable = new Playable { Kind = PlayableKind.Episode };
                    await movieDb.SaveChangesAsync();
                }
                playableId = holder.PlayableId!.Value;
            }
            else
            {
                var ep = await movieDb.Episodes.FirstOrDefaultAsync(e => e.Id == req.TargetId);
                if (ep == null) return NotFound(new { Message = "Episode not found" });
                if (ep.PlayableId == null)
                {
                    ep.Playable = new Playable { Kind = PlayableKind.Episode };
                    await movieDb.SaveChangesAsync();
                }
                playableId = ep.PlayableId!.Value;
            }

            // Primary replaces the existing Primary; an Extra is added alongside (multiple allowed).
            if (role == MovieFileRole.Primary)
                movieDb.MediaFiles.RemoveRange(
                    await movieDb.MediaFiles.Where(f => f.PlayableId == playableId && f.Role == MovieFileRole.Primary).ToListAsync());

            if (!await movieDb.MediaFiles.AnyAsync(f => f.PlayableId == playableId && f.Path == path))
            {
                var label = role == MovieFileRole.Extra
                    ? (req.SeasonNumber != null ? $"manual:extra:s{req.SeasonNumber}" : "manual:extra")
                    : "match:manual";
                movieDb.MediaFiles.Add(new MediaFile { PlayableId = playableId, Path = path, Role = role, Label = label });
                await movieDb.SaveChangesAsync();
            }
            return Ok(new { Success = true });
        }

        public class MoveFileRequest { public int MediaFileId { get; set; } public string Action { get; set; } = "primary"; }

        // Reorder a title's files within its "feature sequence" (the Primary + ordered Parts of one playable).
        //   action "primary" → make this file the Primary (promotes a Part, Variant, or Extra; the old Primary
        //                       becomes the next Part). "up"/"down" → shift a Part/Primary one slot in the order.
        // After any move the sequence is renumbered: first = Primary (Part 1), the rest = Parts 2..N. A lone
        // file keeps PartNumber NULL. Variants/Extras not pulled in stay as they are. Editor-gated.
        [HttpPost("/API/Admin/IngestReview/MoveFile")]
        public async Task<IActionResult> IngestReviewMoveFile([FromBody] MoveFileRequest req)
        {
            if (!await IsCurrentUserEditor()) return Forbid();
            if (req == null || req.MediaFileId == 0) return BadRequest(new { Message = "MediaFileId required" });
            var mf = await movieDb.MediaFiles.FirstOrDefaultAsync(x => x.Id == req.MediaFileId);
            if (mf == null) return NotFound(new { Message = "File not found" });

            var all = await movieDb.MediaFiles.Where(x => x.PlayableId == mf.PlayableId).ToListAsync();
            // The feature sequence in current display order; Variants/Extras live outside it.
            var seq = all.Where(x => x.Role == MovieFileRole.Primary || x.Role == MovieFileRole.Part)
                .OrderBy(x => x.Role).ThenBy(x => x.PartNumber ?? int.MaxValue).ThenBy(x => x.Id).ToList();

            var action = (req.Action ?? "").Trim().ToLowerInvariant();
            if (action == "primary")
            {
                seq.RemoveAll(x => x.Id == mf.Id);   // a Part/Variant/Extra is pulled into the sequence at the front
                seq.Insert(0, mf);
            }
            else if (action == "up" || action == "down")
            {
                var idx = seq.FindIndex(x => x.Id == mf.Id);
                if (idx < 0) return BadRequest(new { Message = "Only a primary or part can be shifted." });
                var swap = action == "up" ? idx - 1 : idx + 1;
                if (swap < 0 || swap >= seq.Count) return Ok(new { Success = true });   // already at the edge
                (seq[idx], seq[swap]) = (seq[swap], seq[idx]);
            }
            else return BadRequest(new { Message = "Action must be primary, up, or down." });

            for (int i = 0; i < seq.Count; i++)
            {
                seq[i].Role = i == 0 ? MovieFileRole.Primary : MovieFileRole.Part;
                seq[i].PartNumber = seq.Count == 1 ? (int?)null : i + 1;
            }
            await movieDb.SaveChangesAsync();
            return Ok(new { Success = true });
        }

        public class RemoveFileRequest { public int MediaFileId { get; set; } }

        [HttpPost("/API/Admin/IngestReview/RemoveFile")]
        public async Task<IActionResult> IngestReviewRemoveFile([FromBody] RemoveFileRequest req)
        {
            if (!await IsCurrentUserEditor()) return Forbid();
            var f = await movieDb.MediaFiles.FirstOrDefaultAsync(x => x.Id == req.MediaFileId);
            if (f == null) return NotFound(new { Message = "File not found" });
            var playableId = f.PlayableId;
            var wasCreatedEp = (f.Label ?? "").StartsWith("match:created-ep", StringComparison.OrdinalIgnoreCase);
            movieDb.MediaFiles.Remove(f);
            await movieDb.SaveChangesAsync();

            var phantomRemoved = await CleanupEmptyCreatedEpPhantomAsync(playableId, wasCreatedEp);
            return Ok(new { Success = true, phantomEpisodeRemoved = phantomRemoved });
        }

        // A "created-ep phantom" is an Episode the bulk mapper fabricated from a filename (no ImdbId, title
        // taken from the file) purely to hold a file it couldn't match to a real episode — its MediaFile
        // carries Label "match:created-ep" (data/_create_missing_eps.py). When that file is later remapped to
        // the correct real episode and removed here, the fabricated episode is left behind as an empty "0/1"
        // gap that can never be filled (no such episode exists — often a typo/duplicate of a real one). If
        // removing a created-ep file empties such a phantom, delete the episode + its now-unreferenced
        // playable so it stops surfacing. Guarded tight: only a created-ep file, only an ImdbId-NULL episode,
        // and only once no files remain — a real (scraped) episode or one that still has files is never touched.
        private async Task<bool> CleanupEmptyCreatedEpPhantomAsync(int playableId, bool removedWasCreatedEp)
        {
            if (!removedWasCreatedEp) return false;
            if (await movieDb.MediaFiles.AnyAsync(m => m.PlayableId == playableId)) return false;
            var ep = await movieDb.Episodes.FirstOrDefaultAsync(e => e.PlayableId == playableId);
            if (ep == null || ep.ImdbId != null) return false;
            movieDb.Episodes.Remove(ep);   // episode first (its FK to Playable is Restrict), then the playable
            var pl = await movieDb.Playables.FirstOrDefaultAsync(p => p.Id == playableId);
            if (pl != null) movieDb.Playables.Remove(pl);
            await movieDb.SaveChangesAsync();
            return true;
        }

        // Movie ids in Ids, series ids in SeriesIds, misc-video ids in MiscIds (separate id sequences — see Kind).
        public class IngestReviewIdsRequest { public List<int> Ids { get; set; } = new(); public List<int> SeriesIds { get; set; } = new(); public List<int> MiscIds { get; set; } = new(); }

        // Apply the library's leading-"The" sort convention at the approve gate — PrepMovieTitle runs only
        // on manual insert, so ingested rows arrive un-inverted ("The Cube" instead of "Cube, The"). Preserve
        // a hand-curated SimpleTitle that isn't itself an article form (e.g. franchise numbering).
        private static void ApplyArticleConvention(Movie m)
        {
            var inv = MovieTheater.Ingest.TitleNorm.InvertLeadingThe(m.Title);
            if (string.Equals(inv, m.Title, StringComparison.Ordinal)) return;
            if (string.IsNullOrEmpty(m.SimpleTitle) || string.Equals(m.SimpleTitle, m.Title, StringComparison.Ordinal))
                m.SimpleTitle = inv;
            else if (m.SimpleTitle.StartsWith("The ", StringComparison.OrdinalIgnoreCase))
                m.SimpleTitle = MovieTheater.Ingest.TitleNorm.InvertLeadingThe(m.SimpleTitle);
            m.Title = inv;
        }
        private static void ApplyArticleConvention(Series s)
        {
            var inv = MovieTheater.Ingest.TitleNorm.InvertLeadingThe(s.Title);
            if (string.Equals(inv, s.Title, StringComparison.Ordinal)) return;
            if (string.IsNullOrEmpty(s.SimpleTitle) || string.Equals(s.SimpleTitle, s.Title, StringComparison.Ordinal))
                s.SimpleTitle = inv;
            else if (s.SimpleTitle.StartsWith("The ", StringComparison.OrdinalIgnoreCase))
                s.SimpleTitle = MovieTheater.Ingest.TitleNorm.InvertLeadingThe(s.SimpleTitle);
            s.Title = inv;
        }

        // Approve = clear the quarantine flag so the row joins the library (idempotent;
        // re-approving an already-cleared id is a no-op). ReviewSourcePath is kept — the
        // file-mapping pass (Phase 5) needs it.
        [HttpPost("/API/Admin/IngestReview/Approve")]
        public async Task<IActionResult> IngestReviewApprove([FromBody] IngestReviewIdsRequest req)
        {
            if (!await IsCurrentUserEditor()) return Forbid();
            if (req == null) return Ok(new { approved = 0 });

            var rows = req.Ids.Count == 0 ? new List<Movie>()
                : await movieDb.Movies.Where(m => req.Ids.Contains(m.id) && m.ReviewBatch != null).ToListAsync();
            foreach (var m in rows)
            {
                // IMDb's scraped year wins over ours when they differ — the scrape is the reliable source
                // (project rule), so persist it onto the canonical ReleaseDate at approve/save time.
                if (m.ImdbReleaseDate.HasValue && (m.ReleaseDate == null || m.ReleaseDate.Value.Year != m.ImdbReleaseDate.Value.Year))
                    m.ReleaseDate = m.ImdbReleaseDate;
                ApplyArticleConvention(m);
                m.ReviewBatch = null; m.ReviewProvenance = null; m.ReviewConfidence = null;
            }

            var seriesRows = req.SeriesIds.Count == 0 ? new List<Series>()
                : await movieDb.Series.Where(s => req.SeriesIds.Contains(s.Id) && s.ReviewBatch != null).ToListAsync();
            foreach (var s in seriesRows)
            {
                if (s.ImdbReleaseDate.HasValue && (s.ReleaseDate == null || s.ReleaseDate.Value.Year != s.ImdbReleaseDate.Value.Year))
                { s.ReleaseDate = s.ImdbReleaseDate; s.StartYear = s.ImdbReleaseDate.Value.Year; }
                ApplyArticleConvention(s);
                s.ReviewBatch = null; s.ReviewProvenance = null; s.ReviewConfidence = null;
            }

            var miscRows = req.MiscIds.Count == 0 ? new List<MiscVideo>()
                : await movieDb.MiscVideos.Where(v => req.MiscIds.Contains(v.Id) && v.ReviewBatch != null).ToListAsync();
            foreach (var v in miscRows) { v.ReviewBatch = null; v.ReviewProvenance = null; }

            // Episodic-extra misc (attached, no standalone Description) have no card of their own — approve
            // them WITH the parent series/movie being approved here, so they go live together.
            var approvedMovieIds = rows.Select(m => m.id).ToList();
            var approvedSeriesIds = seriesRows.Select(s => s.Id).ToList();
            var childMisc = (approvedMovieIds.Count == 0 && approvedSeriesIds.Count == 0) ? new List<MiscVideo>()
                : await movieDb.MiscVideos.Where(v => v.ReviewBatch != null
                    && (v.Description == null || v.Description == "")
                    && ((v.RelatedMovieId != null && approvedMovieIds.Contains(v.RelatedMovieId.Value))
                     || (v.RelatedSeriesId != null && approvedSeriesIds.Contains(v.RelatedSeriesId.Value)))).ToListAsync();
            foreach (var v in childMisc) { v.ReviewBatch = null; v.ReviewProvenance = null; }

            await movieDb.SaveChangesAsync();

            // A newly-approved title should carry a poster — fetch one (from IMDb via OMDB) for any movie /
            // series that lacks it. EnsurePosterAsync no-ops when a poster already exists; bounded
            // parallelism keeps a big "approve all" responsive, and a failed fetch never blocks approval.
            var posterTargets = rows.Select(m => (id: m.id, tt: m.imdbID, series: false))
                .Concat(seriesRows.Select(s => (id: s.Id, tt: s.imdbID, series: true)))
                .ToList();
            if (posterTargets.Count > 0)
                await Parallel.ForEachAsync(posterTargets, new ParallelOptions { MaxDegreeOfParallelism = 6 },
                    async (t, _) => await posterFetchService.EnsurePosterAsync(t.id, t.tt, t.series));

            return Ok(new { approved = rows.Count + seriesRows.Count + miscRows.Count + childMisc.Count });
        }

        // Fetch posters for already-approved movies/series that have none (e.g. the auto-approved series).
        // Runs in the web app so it writes to the live image store — the CLI backfill can't from a dev box.
        // Editor-gated; idempotent (EnsurePosterAsync no-ops where a poster exists).
        [HttpPost("/API/Admin/IngestReview/BackfillPosters")]
        public async Task<IActionResult> IngestReviewBackfillPosters([FromQuery] int minId = 0)
        {
            if (!await IsCurrentUserEditor()) return Forbid();
            // Target titles with no PosterDetails row AND those whose row never got an image downloaded
            // (PosterVersion == 0 — e.g. the scrape recorded a URL but the fetch failed). EnsurePosterAsync
            // no-ops where an on-disk image already exists, so this stays safe for legacy rows.
            // minId scopes the pass to recent ids (the buggy ingest era) so a run need not iterate the
            // whole legacy library — pass e.g. minId=9001 to target only recently-ingested titles.
            // Pending-review rows are INCLUDED, deliberately. They are the ones a person is about to
            // look at, and a review card without art is the hardest kind to judge; excluding them
            // meant the only titles that could be given a poster were the ones already approved.
            var series = await movieDb.Series.Where(s => s.imdbID != null && s.Id >= minId
                    && (s.PosterDetails == null || s.PosterDetails.PosterVersion == 0))
                .Select(s => new { s.Id, s.imdbID }).ToListAsync();
            var movies = await movieDb.Movies.Where(m => m.imdbID != null && m.id >= minId
                    && (m.PosterDetails == null || m.PosterDetails.PosterVersion == 0)
                    && m.TitleType != TitleType.TvSeries && m.TitleType != TitleType.TvMiniSeries)
                .Select(m => new { m.id, m.imdbID }).ToListAsync();
            var targets = series.Select(s => (id: s.Id, tt: s.imdbID, isSeries: true))
                .Concat(movies.Select(m => (id: m.id, tt: m.imdbID, isSeries: false))).ToList();

            int got = 0;
            if (targets.Count > 0)
                await Parallel.ForEachAsync(targets, new ParallelOptions { MaxDegreeOfParallelism = 6 },
                    async (t, _) => { if (await posterFetchService.EnsurePosterAsync(t.id, t.tt, t.isSeries)) System.Threading.Interlocked.Increment(ref got); });

            return Ok(new { attempted = targets.Count, got, minId });
        }

        // ── "Sync from Jellyfin" admin button ────────────────────────────────────────────────────────
        // The periodic Jellyfin library scan is disabled (NAS health), so making freshly-mapped content
        // streamable takes two steps: tell Jellyfin to scan the disk, then run the sync that stamps
        // JellyfinItemId onto our MediaFile rows. BOTH, and the sequencing between them, belong to the
        // server: RunSync starts one background job that does the whole thing and SyncStatus reports
        // where it is. The browser is a spectator.
        //
        // It used to chain the phases itself, and that was the bug — a tab closed during the twelve
        // minute scan stranded the run silently (2026-08-15: the scan completed at 23:18 and the sync
        // was simply never asked for; nothing in the DB or the UI said so, and the operator reasonably
        // believed they had synced). TriggerScan and ScanStatus remain for diagnosing Jellyfin itself,
        // no longer as steps anyone has to chain.

        // Ask Jellyfin to scan, without running a sync. Diagnostic; the normal path is RunSync.
        [HttpPost("/API/Admin/Jellyfin/TriggerScan")]
        public async Task<IActionResult> JellyfinTriggerScan()
        {
            if (!await IsCurrentUserEditor()) return Forbid();
            try
            {
                await jellyfinApi.TriggerLibraryScanAsync();
                return Ok(new { triggered = true });
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Jellyfin scan trigger failed");
                return StatusCode(502, new { triggered = false, message = "Could not reach Jellyfin to start a scan: " + ex.Message });
            }
        }

        // The library-scan task's raw state. Diagnostic; the job watches this itself now.
        // { running, progress (0-100 or null), found, state }.
        [HttpGet("/API/Admin/Jellyfin/ScanStatus")]
        public async Task<IActionResult> JellyfinScanStatus()
        {
            if (!await IsCurrentUserEditor()) return Forbid();
            try
            {
                var st = await jellyfinApi.GetScanTaskStateAsync();
                return Ok(new { running = st.IsRunning, progress = st.Progress, found = st.Found, state = st.State });
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Jellyfin scan-status read failed");
                return StatusCode(502, new { message = "Could not reach Jellyfin to read scan status: " + ex.Message });
            }
        }

        // START the whole operation as ONE server-side background job — scan, wait, sync — and return
        // immediately. Nothing about the outcome depends on the caller's connection surviving: the
        // job's state lives on the server (JellyfinSyncRunner) and SyncStatus reports it. Single-
        // flight: a second click while one runs just follows the run in flight.
        // scan=false syncs against the library as it stands, for when a scan has only just finished.
        [HttpPost("/API/Admin/Jellyfin/RunSync")]
        public async Task<IActionResult> JellyfinRunSync([FromQuery] bool scan = true)
        {
            if (!await IsCurrentUserEditor()) return Forbid();
            var started = jellyfinSyncRunner.TryStart(User.Identity?.Name, withScan: scan);
            var snap = jellyfinSyncRunner.Snapshot();
            // startedUtc lets the follower see WHICH run it's following — an in-flight run that began
            // earlier may predate whatever the caller was hoping to pick up.
            return Ok(new { started, alreadyRunning = !started, startedUtc = snap.StartedUtc, phase = snap.Phase });
        }

        // The job's state. { running, phase } while in flight; then { done, summary } or
        // { done, error }. A pod restart forgets the last run — reported honestly as
        // { done: false, running: false } rather than inventing an outcome.
        [HttpGet("/API/Admin/Jellyfin/SyncStatus")]
        public async Task<IActionResult> JellyfinSyncStatus()
        {
            if (!await IsCurrentUserEditor()) return Forbid();
            var (running, startedUtc, finishedUtc, report, error, phase) = jellyfinSyncRunner.Snapshot();
            if (running) return Ok(new { running = true, startedUtc, phase });
            if (error != null) return Ok(new { running = false, done = true, startedUtc, finishedUtc, error });
            if (report != null) return Ok(new { running = false, done = true, startedUtc, finishedUtc, summary = SyncSummary(report) });
            return Ok(new { running = false, done = false });
        }

        private static object SyncSummary(MovieTheater.Services.Jellyfin.JellyfinSyncReport rep)
        {
            static List<string> Sample(IReadOnlyList<string> xs, int n = 20) =>
                xs.Count <= n ? new List<string>(xs) : new List<string>(xs).GetRange(0, n);

            return new
            {
                server = rep.ServerName,
                version = rep.Version,
                moviesMatched = rep.MoviesMatched,
                moviesTotal = rep.MoviesTotal,
                created = rep.Created,
                updated = rep.Updated,
                repointed = rep.Repointed.Count,
                extrasAttached = rep.ExtrasAttached,
                extrasUnplaced = rep.ExtrasUnplaced,
                supersededOrphans = rep.SupersededOrphans,
                possibleRenames = rep.PossibleRenames.Count,
                moviesMissing = rep.MissingMovies.Count,
                epMatched = rep.EpMatched,
                epTotal = rep.EpTotal,
                untracked = rep.Untracked.Count,
                untranslatable = rep.Untranslatable.Count,
                imdbFallbacks = rep.ImdbFallbacks.Count,
                candidateUpgrades = rep.CandidateUpgrades,
                candidateNewTitles = rep.CandidateNewTitles,
                candidateSeriesEpisodes = rep.CandidateSeriesEpisodes,
                candidateSeriesGroups = rep.CandidateSeriesGroups,
                candidateUnclassified = rep.CandidateUnclassified,
                candidatesSuperseded = rep.CandidatesSuperseded,
                candidateError = rep.CandidateError,
                keyframeError = rep.KeyframeError,
                scanNote = rep.ScanNote,
                resolveError = rep.ResolveError,
                resolution = rep.Resolution == null ? null : new
                {
                    moviesCreated = rep.Resolution.MoviesCreated,
                    moviesConvertedToUpgrade = rep.Resolution.MoviesConvertedToUpgrade,
                    seriesIdentified = rep.Resolution.SeriesIdentified,
                    seriesEnriched = rep.Resolution.SeriesEnriched,
                    episodesCatalogued = rep.Resolution.EpisodesCatalogued,
                    episodeFilesMapped = rep.Resolution.EpisodeFilesMapped,
                    needsAttention = rep.Resolution.NeedsAttention,
                    notes = rep.Resolution.Notes,
                },
                samples = new
                {
                    repointed = Sample(rep.Repointed),
                    possibleRenames = Sample(rep.PossibleRenames),
                    missingTitles = Sample(rep.MissingMovies),
                    imdbFallbacks = Sample(rep.ImdbFallbacks),
                },
            };
        }

        // ── Sync-scan candidates (the sync's untracked findings, made actionable) ─────────────────────
        // A sync run classifies every untracked file into SyncCandidate rows (upgrade of an existing
        // movie / new title / unclassified). These endpoints drive the review surface: list them,
        // apply an upgrade (re-point in place), resolve new titles into quarantined ReviewBatch rows
        // that flow through the normal ingest review, correct a wrong classification, or reject.
        // Resolution is CHUNKED (a few folders per call, the UI loops) — each folder costs external
        // metadata lookups, so no single request is ever asked to survive the whole pile.

        [HttpGet("/API/Admin/IngestReview/SyncCandidates")]
        public async Task<IActionResult> SyncCandidatesList()
        {
            if (!await IsCurrentUserEditor()) return Forbid();
            var all = await movieDb.SyncCandidates
                .Where(c => c.Status == SyncCandidateStatus.Pending)
                .OrderBy(c => c.Kind).ThenBy(c => c.Path)
                .ToListAsync();
            var ingestedCount = await movieDb.SyncCandidates.CountAsync(c => c.Status == SyncCandidateStatus.Ingested);

            // Episode candidates never appear as loose rows — they fold into one card per show below.
            var episodeCands = all.Where(c => c.Kind == SyncCandidateKind.SeriesEpisode).ToList();
            var pending = all.Where(c => c.Kind != SyncCandidateKind.SeriesEpisode).ToList();
            var seriesGroups = await BuildSyncSeriesGroupsAsync(episodeCands);

            var targetIds = pending.Where(c => c.TargetMovieId != null).Select(c => c.TargetMovieId!.Value).Distinct().ToList();
            var targets = await movieDb.Movies.Where(m => targetIds.Contains(m.id))
                .Select(m => new
                {
                    m.id,
                    m.Title,
                    Year = m.ReleaseDate != null ? m.ReleaseDate.Value.Year : (m.ImdbReleaseDate != null ? m.ImdbReleaseDate.Value.Year : (int?)null),
                    m.PlayableId,
                    // Old file already dead = the safest kind of upgrade; still-live = replacing a working copy.
                    OldFileMissing = m.PlayableId == null || !movieDb.MediaFiles.Any(f =>
                        f.PlayableId == m.PlayableId && f.Role == MovieFileRole.Primary
                        && f.JellyfinItemId != null && f.MissingSinceUtc == null),
                })
                .ToDictionaryAsync(m => m.id);

            return Ok(new
            {
                counts = new
                {
                    upgrades = pending.Count(c => c.Kind == SyncCandidateKind.Upgrade),
                    newTitles = pending.Count(c => c.Kind == SyncCandidateKind.NewTitle),
                    unclassified = pending.Count(c => c.Kind == SyncCandidateKind.Unclassified),
                    ingested = ingestedCount,
                    seriesGroups = seriesGroups.Count,
                    seriesEpisodeFiles = episodeCands.Count,
                    // How many shows still need work before their card is complete — the number the
                    // "Resolve series" button loops over, and the honest "how much is left".
                    seriesUnresolved = seriesGroups.Count(g => !g.Complete),
                },
                seriesGroups,
                items = pending.Select(c => new
                {
                    id = c.Id,
                    kind = c.Kind == SyncCandidateKind.Upgrade ? "upgrade" : c.Kind == SyncCandidateKind.NewTitle ? "new" : "unclassified",
                    path = c.Path,
                    sizeBytes = c.SizeBytes,
                    signal = c.Signal,
                    oldPath = c.OldPath,
                    targetMovieId = c.TargetMovieId,
                    targetTitle = c.TargetMovieId != null && targets.TryGetValue(c.TargetMovieId.Value, out var t) ? t.Title : null,
                    targetYear = c.TargetMovieId != null && targets.TryGetValue(c.TargetMovieId.Value, out var t2) ? t2.Year : null,
                    oldFileMissing = c.TargetMovieId != null && targets.TryGetValue(c.TargetMovieId.Value, out var t3) ? t3.OldFileMissing : (bool?)null,
                    parsedTitle = c.ParsedTitle,
                    parsedYear = c.ParsedYear,
                    resolvedImdbId = c.ResolvedImdbId,
                    resolutionError = c.ResolutionError,
                    firstSeenUtc = c.FirstSeenUtc,
                    lastSeenUtc = c.LastSeenUtc,
                }),
            });
        }

        // ── Series-episode candidate groups ───────────────────────────────────────────────────────────
        // One show = one card, however many episode files it brought. The card carries the show's
        // identity (matched series or a parse of the folder), what the resolver still owes it
        // (identify → enumerate episodes → map files), and the per-file episode list so the reviewer
        // sees S01E07 → "Episode 7" rather than a wall of release names.

        public class SyncSeriesGroupDto
        {
            public string Folder { get; set; } = default!;
            public string? Title { get; set; }
            public int? Year { get; set; }
            public string? Signal { get; set; }
            public int? SeriesId { get; set; }
            public string? SeriesTitle { get; set; }
            public string? SeriesImdbId { get; set; }
            /// <summary>The pending series card is still quarantined in this batch (null once approved).</summary>
            public string? SeriesReviewBatch { get; set; }
            public bool SeriesHasPoster { get; set; }
            public int EpisodeRowsKnown { get; set; }
            public int FileCount { get; set; }
            public int SeasonCount { get; set; }
            public List<int> Seasons { get; set; } = new();
            /// <summary>Files whose (season, episode) has no Episode row yet — the numbering
            /// disagreements a reviewer must see rather than have guessed at.</summary>
            public int UnmatchedFiles { get; set; }
            public string? Error { get; set; }
            /// <summary>How the disk's season numbering disagrees with the catalogue's, in words;
            /// null when they agree. While this is set, NOTHING maps by number — so the card must
            /// report every file as unmatched rather than showing the by-number lookup's answer,
            /// which is precisely the answer that would be wrong.</summary>
            public string? ShapeMismatch { get; set; }
            /// <summary>The disk and the catalogue hold the same NUMBER of episodes but split them
            /// into seasons differently, and nothing is mapped yet — the one situation where mapping
            /// in absolute order is meaningful, offered to the reviewer as an explicit choice.</summary>
            public bool CanMapAbsolute { get; set; }
            /// <summary>Nothing left for the resolver: the show is identified, its episodes are
            /// enumerated, and every file has been mapped (so its candidates left Pending).</summary>
            public bool Complete { get; set; }
            /// <summary>What the resolver would do next — shown on the card so the loop is legible.</summary>
            public string NextStep { get; set; } = default!;
            public List<SyncSeriesFileDto> Files { get; set; } = new();
        }

        private sealed record SeriesLite(int Id, string? Title, string? ImdbId, string? ReviewBatch, bool HasPoster);

        public class SyncSeriesFileDto
        {
            public int Id { get; set; }
            public string Path { get; set; } = default!;
            public long? SizeBytes { get; set; }
            public int? Season { get; set; }
            public int? Episode { get; set; }
            public int? SpansToEpisode { get; set; }
            public string? EpisodeTitle { get; set; }
            public bool Matched { get; set; }
        }

        /// <summary>
        /// Folds pending episode candidates into one DTO per show and works out, for each, what the
        /// resolver still owes it. Deliberately read-only and side-effect free — the same computation
        /// drives both the card and <see cref="SyncCandidatesResolveSeries"/>'s work queue, so the
        /// progress the reviewer sees is the progress the loop is actually making.
        /// </summary>
        private async Task<List<SyncSeriesGroupDto>> BuildSyncSeriesGroupsAsync(List<SyncCandidate> episodeCands)
        {
            if (episodeCands.Count == 0) return new List<SyncSeriesGroupDto>();

            var seriesIds = episodeCands.Where(c => c.TargetSeriesId != null)
                .Select(c => c.TargetSeriesId!.Value).Distinct().ToList();
            var seriesById = seriesIds.Count == 0
                ? new Dictionary<int, SeriesLite>()
                : (await movieDb.Series.Where(s => seriesIds.Contains(s.Id))
                    .Select(s => new SeriesLite(s.Id, s.Title, s.imdbID, s.ReviewBatch,
                        s.PosterDetails != null && s.PosterDetails.PosterVersion > 0))
                    .ToListAsync()).ToDictionary(s => s.Id);
            // (season, episode) → title, for every series any group points at.
            var epRows = seriesIds.Count == 0
                ? new List<(int SeriesId, int Season, int Episode, string? Title)>()
                : (await movieDb.Episodes.Where(e => e.SeriesId != null && seriesIds.Contains(e.SeriesId!.Value))
                    .Select(e => new { SeriesId = e.SeriesId!.Value, e.SeasonNumber, e.EpisodeNumber, e.Title })
                    .ToListAsync())
                    .Select(e => (SeriesId: e.SeriesId, Season: e.SeasonNumber, Episode: e.EpisodeNumber, Title: e.Title)).ToList();
            var epLookup = epRows.ToDictionary(e => (e.SeriesId, e.Season, e.Episode), e => e.Title);
            // The same Episode rows the mapper's shape check reads, grouped per series.
            var epRowsBySeries = epRows
                .GroupBy(e => e.SeriesId)
                .ToDictionary(g => g.Key, g => (IReadOnlyCollection<Episode>)g
                    .Select(e => new Episode { SeasonNumber = e.Season, EpisodeNumber = e.Episode, Title = e.Title })
                    .ToList());
            var epCountBySeries = epRows.GroupBy(e => e.SeriesId).ToDictionary(g => g.Key, g => g.Count());
            // Episodes of those series that ALREADY hold a file — absolute-order mapping is only
            // meaningful on a show where nothing is mapped yet.
            var mappedEpBySeries = seriesIds.Count == 0
                ? new Dictionary<int, int>()
                : (await movieDb.Episodes
                    .Where(e => e.SeriesId != null && seriesIds.Contains(e.SeriesId!.Value) && e.PlayableId != null
                        && movieDb.MediaFiles.Any(f => f.PlayableId == e.PlayableId))
                    .GroupBy(e => e.SeriesId!.Value).Select(g => new { g.Key, n = g.Count() })
                    .ToListAsync()).ToDictionary(x => x.Key, x => x.n);

            var groups = new List<SyncSeriesGroupDto>();
            foreach (var g in episodeCands
                .GroupBy(c => c.SeriesFolder ?? ParentDirOfPath(c.Path) ?? c.Path, StringComparer.OrdinalIgnoreCase)
                .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
            {
                var members = g.OrderBy(c => c.SeasonNumber ?? 0).ThenBy(c => c.EpisodeNumber ?? 0).ThenBy(c => c.Path).ToList();
                var head = members[0];
                var sid = members.Select(c => c.TargetSeriesId).FirstOrDefault(x => x != null);
                SeriesLite? s = sid != null && seriesById.TryGetValue(sid.Value, out var sv) ? sv : null;

                var dto = new SyncSeriesGroupDto
                {
                    Folder = g.Key,
                    Title = head.ParsedTitle,
                    Year = head.ParsedYear,
                    Signal = head.Signal,
                    SeriesId = sid,
                    SeriesTitle = s?.Title,
                    SeriesImdbId = s?.ImdbId,
                    SeriesReviewBatch = s?.ReviewBatch,
                    SeriesHasPoster = s?.HasPoster == true,
                    EpisodeRowsKnown = sid != null && epCountBySeries.TryGetValue(sid.Value, out var ec) ? ec : 0,
                    FileCount = members.Count,
                    Seasons = members.Where(c => c.SeasonNumber != null).Select(c => c.SeasonNumber!.Value).Distinct().OrderBy(n => n).ToList(),
                    Error = members.Select(c => c.ResolutionError).FirstOrDefault(e => e != null),
                };
                dto.SeasonCount = dto.Seasons.Count;
                dto.Files = members.Select(c => new SyncSeriesFileDto
                {
                    Id = c.Id,
                    Path = c.Path,
                    SizeBytes = c.SizeBytes,
                    Season = c.SeasonNumber,
                    Episode = c.EpisodeNumber,
                    SpansToEpisode = c.SpansToEpisode,
                    EpisodeTitle = sid != null && c.SeasonNumber != null && c.EpisodeNumber != null
                        && epLookup.TryGetValue((sid.Value, c.SeasonNumber.Value, c.EpisodeNumber.Value), out var et) ? et : null,
                    Matched = sid != null && c.SeasonNumber != null && c.EpisodeNumber != null
                        && epLookup.ContainsKey((sid.Value, c.SeasonNumber.Value, c.EpisodeNumber.Value)),
                }).ToList();
                // The card must agree with the mapper. When the season shapes disagree, mapping by
                // number is refused wholesale — so reporting the by-number lookup's 83-of-84 here
                // would tell the reviewer the show is nearly done when in fact nothing will attach.
                if (sid != null && epRowsBySeries.TryGetValue(sid.Value, out var catalogue))
                {
                    dto.ShapeMismatch = MovieTheater.Services.Series.SyncSeriesMatcher
                        .SeasonShapeMismatch(members, catalogue);
                    if (dto.ShapeMismatch != null)
                        foreach (var f in dto.Files) f.Matched = false;
                }
                dto.UnmatchedFiles = dto.Files.Count(f => !f.Matched);
                dto.CanMapAbsolute =
                    sid != null
                    && dto.EpisodeRowsKnown > 0
                    && dto.UnmatchedFiles > 0
                    && (mappedEpBySeries.TryGetValue(sid.Value, out var already) ? already : 0) == 0
                    && dto.Files.Count(f => f.Season != null && f.Episode != null) == dto.EpisodeRowsKnown;

                dto.NextStep =
                    sid == null ? "identify the show"
                    : !dto.SeriesHasPoster ? "enrich the show (poster, plot, rating)"
                    : dto.EpisodeRowsKnown == 0 ? "enumerate its episodes"
                    : dto.UnmatchedFiles == dto.FileCount ? "enumerate the missing seasons"
                    : "map the files to episodes";
                // These candidates are Pending by definition (the query filters on it), so a group that
                // still exists always has files left to map — "complete" is about the SHOW being ready,
                // which is what tells the reviewer the resolver has nothing more to do here.
                dto.Complete = sid != null && dto.EpisodeRowsKnown > 0 && dto.UnmatchedFiles == 0;
                if (dto.Complete) dto.NextStep = "map the files to episodes";
                groups.Add(dto);
            }
            return groups;
        }

        private static string? ParentDirOfPath(string? p)
        {
            if (string.IsNullOrEmpty(p)) return null;
            var s = p.Replace('/', '\\').TrimEnd('\\');
            var i = s.LastIndexOf('\\');
            return i <= 0 ? null : s.Substring(0, i);
        }

        public class SyncCandidateIdRequest { public int Id { get; set; } }
        public class SyncCandidateIdsRequest { public List<int> Ids { get; set; } = new(); }

        [HttpPost("/API/Admin/IngestReview/SyncCandidates/ApplyUpgrade")]
        public async Task<IActionResult> SyncCandidateApplyUpgrade([FromBody] SyncCandidateIdRequest req)
        {
            if (!await IsCurrentUserEditor()) return Forbid();
            var res = await jellyfinSyncService.ApplyUpgradeCandidateAsync(req.Id, TruncCol(User.Identity?.Name, 64));
            return Ok(new { success = res.Ok, message = res.Message, movieTitle = res.MovieTitle, newPath = res.NewPath, nowStreamable = res.NowStreamable, extrasAttached = res.ExtrasAttached, partsAttached = res.PartsAttached });
        }

        [HttpPost("/API/Admin/IngestReview/SyncCandidates/Reject")]
        public async Task<IActionResult> SyncCandidatesReject([FromBody] SyncCandidateIdsRequest req)
        {
            if (!await IsCurrentUserEditor()) return Forbid();
            var rows = await movieDb.SyncCandidates
                .Where(c => req.Ids.Contains(c.Id) && c.Status == SyncCandidateStatus.Pending)
                .ToListAsync();
            var now = DateTime.UtcNow;
            foreach (var c in rows)
            {
                c.Status = SyncCandidateStatus.Rejected;
                c.ResolvedUtc = now;
                c.ResolvedBy = TruncCol(User.Identity?.Name, 64);
            }
            await movieDb.SaveChangesAsync();
            return Ok(new { rejected = rows.Count });
        }

        public class SyncCandidateUpdateRequest
        {
            public int Id { get; set; }
            /// <summary>"upgrade" | "new" | "unclassified" | "series" — omit to keep the current kind.</summary>
            public string? Kind { get; set; }
            public string? Title { get; set; }
            public int? Year { get; set; }
            /// <summary>Hand-picked IMDb id; short-circuits name resolution for this candidate.</summary>
            public string? ImdbId { get; set; }
            /// <summary>Upgrade target when reclassifying to "upgrade" by hand.</summary>
            public int? TargetMovieId { get; set; }
            /// <summary>The show an episode candidate belongs to; also settable on a whole group.</summary>
            public int? TargetSeriesId { get; set; }
            /// <summary>Apply this edit to EVERY pending candidate sharing the row's SeriesFolder. A
            /// correction to a show ("this is the wrong series", "here's the right tt") is a statement
            /// about the show, and fixing it one file at a time across 84 rows is not review, it's
            /// data entry.</summary>
            public bool ApplyToGroup { get; set; }
        }

        // Correct a wrong classification or parse on a still-pending candidate: retitle a NewTitle,
        // pin its IMDb id, or re-point an upgrade at the right movie. The row stays Pending — this
        // only changes what Approve/Resolve will do with it.
        [HttpPost("/API/Admin/IngestReview/SyncCandidates/Update")]
        public async Task<IActionResult> SyncCandidateUpdate([FromBody] SyncCandidateUpdateRequest req)
        {
            if (!await IsCurrentUserEditor()) return Forbid();
            var c = await movieDb.SyncCandidates.FirstOrDefaultAsync(x => x.Id == req.Id);
            if (c == null) return NotFound(new { success = false, message = "Candidate not found." });
            if (c.Status != SyncCandidateStatus.Pending)
                return BadRequest(new { success = false, message = $"Candidate is {c.Status}, not Pending." });

            // An edit aimed at a SHOW applies to every pending file of that show — see ApplyToGroup.
            var affected = new List<SyncCandidate> { c };
            if (req.ApplyToGroup && !string.IsNullOrEmpty(c.SeriesFolder))
                affected = await movieDb.SyncCandidates
                    .Where(x => x.Status == SyncCandidateStatus.Pending && x.SeriesFolder == c.SeriesFolder)
                    .ToListAsync();

            if (req.TargetSeriesId != null)
            {
                var ts = await movieDb.Series.FirstOrDefaultAsync(s => s.Id == req.TargetSeriesId);
                if (ts == null) return BadRequest(new { success = false, message = $"No series {req.TargetSeriesId}." });
                foreach (var x in affected) x.TargetSeriesId = ts.Id;
            }

            // ImdbId: null = leave alone, "" = clear a pin (a rejected resolution must be un-pinnable
            // or re-resolving recreates the same wrong movie), non-empty = validate + pin.
            if (req.ImdbId != null)
            {
                if (req.ImdbId.Length == 0) foreach (var x in affected) x.ResolvedImdbId = null;
                else if (!IsValidImdbId(req.ImdbId)) return BadRequest(new { success = false, message = $"'{req.ImdbId}' is not a valid IMDb id." });
                else foreach (var x in affected) x.ResolvedImdbId = req.ImdbId;
            }
            if (req.Title != null) foreach (var x in affected) x.ParsedTitle = TruncCol(req.Title.Trim(), 512);
            if (req.Year != null) foreach (var x in affected) x.ParsedYear = req.Year;

            if (!string.IsNullOrEmpty(req.Kind))
            {
                switch (req.Kind.ToLowerInvariant())
                {
                    case "series":
                        // Rescue an episode file the classifier left unclassified (an odd file name, a
                        // folder with no SxxExx): give it a series folder to group under so it joins the
                        // show's card instead of sitting alone forever.
                        foreach (var x in affected)
                        {
                            x.Kind = SyncCandidateKind.SeriesEpisode;
                            x.TargetMovieId = null; x.OldPath = null;
                            x.Signal = "manual";
                            x.SeriesFolder ??= TruncCol(ParentDirOfPath(x.Path), 1024);
                            if (x.SeasonNumber == null || x.EpisodeNumber == null)
                            {
                                var ep = MovieTheater.Services.Jellyfin.MovieFolderParser.ParseEpisode(
                                    MovieTheater.Services.Jellyfin.MovieFolderParser.SeriesFolderLeaf(x.Path));
                                if (ep != null)
                                {
                                    x.SeasonNumber = ep.Value.Season;
                                    x.EpisodeNumber = ep.Value.Episode;
                                    x.SpansToEpisode = ep.Value.Spans != ep.Value.Episode ? ep.Value.Spans : null;
                                }
                            }
                        }
                        break;
                    case "new":
                        if (string.IsNullOrWhiteSpace(c.ParsedTitle) && string.IsNullOrEmpty(c.ResolvedImdbId))
                            return BadRequest(new { success = false, message = "A new-title candidate needs a title or an IMDb id." });
                        foreach (var x in affected)
                        {
                            x.Kind = SyncCandidateKind.NewTitle;
                            x.TargetMovieId = null; x.Signal = null; x.OldPath = null;
                            x.TargetSeriesId = null; x.SeriesFolder = null; x.SeriesListOwned = false;
                        }
                        break;
                    case "upgrade":
                        var target = req.TargetMovieId != null
                            ? await movieDb.Movies.FirstOrDefaultAsync(m => m.id == req.TargetMovieId)
                            : null;
                        if (target == null) return BadRequest(new { success = false, message = "An upgrade candidate needs a valid TargetMovieId." });
                        // An upgrade is a statement about ONE file replacing ONE movie — never fanned
                        // out over a group, which would point every episode at the same movie.
                        c.Kind = SyncCandidateKind.Upgrade;
                        c.TargetMovieId = target.id; c.Signal = "manual"; c.OldPath = target.FilePath;
                        c.TargetSeriesId = null; c.SeriesFolder = null; c.SeriesListOwned = false;
                        affected = new List<SyncCandidate> { c };
                        break;
                    case "unclassified":
                        foreach (var x in affected)
                        {
                            x.Kind = SyncCandidateKind.Unclassified;
                            x.TargetMovieId = null; x.Signal = null; x.OldPath = null;
                            x.TargetSeriesId = null; x.SeriesFolder = null; x.SeriesListOwned = false;
                        }
                        break;
                    default:
                        return BadRequest(new { success = false, message = $"Unknown kind '{req.Kind}'." });
                }
            }
            // Any hand edit pins the row: the next sync's refresh must not clobber a reviewer's
            // correction with the same machine classification that was wrong the first time. Clearing
            // the error is what puts a blocked group back in the resolver's queue.
            foreach (var x in affected)
            {
                x.ResolutionError = null;
                x.PinnedByReviewer = true;
            }
            await movieDb.SaveChangesAsync();
            return Ok(new { success = true, updated = affected.Count });
        }


        // ── Candidate resolution ──────────────────────────────────────────────────────────────────────
        // The sync job now runs all of this itself the moment classification finishes, so a completed
        // sync leaves a finished review queue rather than a pile of candidates. These endpoints stay
        // for re-running a piece BY HAND after a correction — fix a title, clear an error, resolve
        // again — which is the only reason a person should ever need to press them.

        [HttpPost("/API/Admin/IngestReview/SyncCandidates/Resolve")]
        public async Task<IActionResult> SyncCandidatesResolve([FromQuery] int limit = 3)
        {
            if (!await IsCurrentUserEditor()) return Forbid();
            var r = await candidateResolver.ResolveNewTitlesChunkAsync(Math.Clamp(limit, 1, 10), TruncCol(User.Identity?.Name, 64));
            return Ok(new { processed = r.Processed, created = r.Created, converted = r.Converted, failed = r.Failed, remaining = r.Remaining, done = r.Done });
        }

        [HttpPost("/API/Admin/IngestReview/SyncCandidates/ResolveSeries")]
        public async Task<IActionResult> SyncCandidatesResolveSeries([FromQuery] int limit = 4)
        {
            if (!await IsCurrentUserEditor()) return Forbid();
            var r = await candidateResolver.ResolveSeriesChunkAsync(Math.Clamp(limit, 1, 10), TruncCol(User.Identity?.Name, 64));
            return Ok(new
            {
                processed = r.Processed, identified = r.Identified, enriched = r.Enriched,
                seasonsEnumerated = r.SeasonsEnumerated, episodesAdded = r.EpisodesAdded,
                filesMapped = r.FilesMapped, failed = r.Failed, remaining = r.Remaining,
                blocked = r.Blocked, done = r.Done, log = r.Log,
            });
        }

        [HttpPost("/API/Admin/IngestReview/SyncCandidates/MapSeriesAbsolute")]
        public async Task<IActionResult> SyncCandidatesMapSeriesAbsolute([FromBody] SyncCandidateIdRequest req)
        {
            if (!await IsCurrentUserEditor()) return Forbid();
            var r = await candidateResolver.MapSeriesAbsoluteAsync(req.Id, TruncCol(User.Identity?.Name, 64));
            if (r.Message != null) return BadRequest(new { success = false, message = r.Message });
            return Ok(new { success = r.Success, mapped = r.Mapped, unmatched = r.Unmatched, total = r.Total });
        }

        // The batch-insert page's name→details cascade; one implementation, shared with the resolver.
        [HttpPost("/API/GetMoviesFromNames")]
        public async Task<List<Movie>> GetMoviesFromNames([FromBody] string[] movieNames, bool forceBackupLogic = false) =>
            await candidateResolver.GetMoviesFromNames(movieNames, forceBackupLogic);

        // ── Per-movie "Re-link files from disk" (movie edit page) ─────────────────────────────────────
        // When a movie's video file is replaced on disk (new rip, old file deleted, folder renamed), its DB
        // path goes stale and the watch button breaks. These two endpoints re-associate the NEW file to the
        // SAME movie row IN PLACE — every rating/viewing/poster/tag is kept — without a full-library scan.
        // Split so neither call has to outlive a proxy timeout: RelinkRefresh kicks a SCOPED re-scan of just
        // this title's shelf; Relink is a single idempotent probe the UI polls until the file is re-pointed.

        [HttpPost("/API/Admin/Movie/RelinkRefresh")]
        public async Task<IActionResult> MovieRelinkRefresh(int movieId)
        {
            if (!await IsCurrentUserEditor()) return Forbid();
            try
            {
                var r = await jellyfinSyncService.TriggerMovieFolderRefreshAsync(movieId);
                return r.Ok
                    ? Ok(new { ok = true, message = r.Message, shelfItemId = r.ShelfItemId })
                    : BadRequest(new { ok = false, message = r.Message });
            }
            catch (Exception ex)
            {
                return StatusCode(502, new { ok = false, message = "Could not reach Jellyfin to start a re-scan: " + ex.Message });
            }
        }

        [HttpPost("/API/Admin/Movie/Relink")]
        public async Task<IActionResult> MovieRelink(int movieId, string? shelfItemId = null)
        {
            if (!await IsCurrentUserEditor()) return Forbid();
            try
            {
                var r = await jellyfinSyncService.TryRelinkMovieFilesAsync(movieId, shelfItemId);
                return Ok(new
                {
                    done = r.Done,
                    scanning = r.Scanning,
                    primaryRepointed = r.PrimaryRepointed,
                    nowStreamable = r.NowStreamable,
                    oldPath = r.OldPath,
                    newPath = r.NewPath,
                    extrasAdded = r.ExtrasAdded,
                    message = r.Message,
                });
            }
            catch (Exception ex)
            {
                return StatusCode(502, new { done = false, message = "Re-link failed: " + ex.Message });
            }
        }

        // ── Subtitle picker (movie modal) ──────────────────────────────────────────────────────────
        // Find/download subtitles for a movie through Jellyfin's subtitle provider (the OpenSubtitles
        // plugin). Libraries are set to NOT save subtitles with media, so downloads land in Jellyfin's
        // metadata dir, never the read-only NAS. Editor-gated.

        // Resolve a movie to the Jellyfin item id of its streamable Primary file (null if not synced).
        private async Task<string?> GetMovieJellyfinItemId(int movieId)
        {
            var playableId = (await movieDb.Movies.Where(m => m.id == movieId).Select(m => m.PlayableId).FirstOrDefaultAsync());
            if (playableId == null) return null;
            return await movieDb.MediaFiles
                .Where(f => f.PlayableId == playableId && f.JellyfinItemId != null && f.MissingSinceUtc == null)
                .OrderBy(f => f.Role)
                .Select(f => f.JellyfinItemId)
                .FirstOrDefaultAsync();
        }

        // The subtitle tracks currently attached to the movie + whether it's synced to Jellyfin at all.
        [HttpGet("/API/Admin/Jellyfin/Subtitles")]
        public async Task<IActionResult> JellyfinSubtitlesList(int movieId)
        {
            if (!await IsCurrentUserEditor()) return Forbid();
            var itemId = await GetMovieJellyfinItemId(movieId);
            if (itemId == null) return Ok(new { synced = false, current = Array.Empty<object>() });
            try
            {
                var subs = await jellyfinApi.GetItemSubtitleStreamsAsync(itemId);
                return Ok(new { synced = true, current = subs.Select(s => new { index = s.Index, language = s.Language, title = s.Title, codec = s.Codec, external = s.IsExternal }) });
            }
            catch (Exception ex) { return StatusCode(502, new { message = "Could not read subtitles from Jellyfin: " + ex.Message }); }
        }

        // Search providers; returns candidates ranked hash-match-first (made for THIS exact file → in sync),
        // then most-downloaded, then highest community rating.
        [HttpPost("/API/Admin/Jellyfin/Subtitles/Search")]
        public async Task<IActionResult> JellyfinSubtitlesSearch(int movieId, string language = "eng")
        {
            if (!await IsCurrentUserEditor()) return Forbid();
            var lang = string.IsNullOrWhiteSpace(language) ? "eng" : language;

            // Preferred path: search OpenSubtitles.com directly by the IMDb id our DB holds. Jellyfin's
            // items are metadata-less homevideos (no IMDb id), so its own RemoteSearch can't match — and
            // its plugin's shared key is rate-limited besides.
            if (openSubtitles.IsConfigured)
            {
                var imdbId = await movieDb.Movies.Where(m => m.id == movieId).Select(m => m.imdbID).FirstOrDefaultAsync();
                if (string.IsNullOrWhiteSpace(imdbId))
                    return BadRequest(new { message = "This movie has no IMDb id on file, so subtitles can't be matched. Set its IMDb id, then search." });
                try
                {
                    var subs = await openSubtitles.SearchAsync(imdbId, lang);
                    var ranked = subs
                        .OrderByDescending(s => s.HashMatch)        // made for THIS exact file → already in sync
                        .ThenBy(s => s.AiTranslated)                 // demote machine/AI translations
                        .ThenByDescending(s => s.FromTrusted)
                        .ThenByDescending(s => s.DownloadCount ?? 0)
                        .ThenByDescending(s => s.Rating ?? 0)
                        .Select(s => new
                        {
                            id = s.FileId.ToString(),
                            provider = "OpenSubtitles",
                            name = s.Name,
                            language = s.Language,
                            downloads = s.DownloadCount,
                            rating = s.Rating,
                            hashMatch = s.HashMatch,
                            hearingImpaired = s.HearingImpaired,
                            trusted = s.FromTrusted,
                            aiTranslated = s.AiTranslated,
                            uploader = s.Uploader,
                        })
                        .ToList();
                    return Ok(new { count = ranked.Count, results = ranked });
                }
                catch (Exception ex) { return StatusCode(502, new { message = "Subtitle search failed: " + ex.Message }); }
            }

            // Fallback: the legacy Jellyfin plugin search (only when OpenSubtitles isn't configured).
            var itemId = await GetMovieJellyfinItemId(movieId);
            if (itemId == null) return BadRequest(new { message = "This movie isn't synced to Jellyfin yet — run \"Sync from Jellyfin\" first." });
            try
            {
                var subs = await jellyfinApi.SearchRemoteSubtitlesAsync(itemId, lang);
                var ranked = subs
                    .OrderByDescending(s => s.IsHashMatch)
                    .ThenByDescending(s => s.DownloadCount ?? 0)
                    .ThenByDescending(s => s.CommunityRating ?? 0)
                    .Select(s => new
                    {
                        id = s.Id,
                        provider = s.ProviderName,
                        name = s.Name,
                        format = s.Format,
                        author = s.Author,
                        comment = s.Comment,
                        language = s.ThreeLetterISOLanguageName,
                        downloads = s.DownloadCount,
                        hashMatch = s.IsHashMatch,
                        rating = s.CommunityRating,
                    })
                    .ToList();
                return Ok(new { count = ranked.Count, results = ranked });
            }
            catch (Exception ex) { return StatusCode(502, new { message = "Subtitle search failed (is a provider configured and signed in?): " + ex.Message }); }
        }

        // Download a chosen candidate (subtitleId from a prior search) and attach it to the movie.
        [HttpPost("/API/Admin/Jellyfin/Subtitles/Download")]
        public async Task<IActionResult> JellyfinSubtitlesDownload(int movieId, string subtitleId, string language = "eng")
        {
            if (!await IsCurrentUserEditor()) return Forbid();
            if (string.IsNullOrWhiteSpace(subtitleId)) return BadRequest(new { message = "subtitleId is required." });
            var itemId = await GetMovieJellyfinItemId(movieId);
            if (itemId == null) return BadRequest(new { message = "Movie isn't synced to Jellyfin." });
            try
            {
                // OpenSubtitles path: the id is a numeric file_id — download the text and attach it to the
                // Jellyfin item as an external sidecar (so the streaming path then serves it as WebVTT).
                if (openSubtitles.IsConfigured && int.TryParse(subtitleId, out var fileId))
                {
                    var (content, _) = await openSubtitles.DownloadAsync(fileId);
                    await jellyfinApi.UploadSubtitleAsync(
                        itemId, string.IsNullOrWhiteSpace(language) ? "eng" : language, "srt",
                        isForced: false, isHearingImpaired: false, System.Text.Encoding.UTF8.GetBytes(content));
                    return Ok(new { downloaded = true });
                }

                await jellyfinApi.DownloadRemoteSubtitleAsync(itemId, subtitleId);
                return Ok(new { downloaded = true });
            }
            catch (Exception ex) { return StatusCode(502, new { downloaded = false, message = "Download failed: " + ex.Message }); }
        }

        // Remove a downloaded subtitle (to swap for another). Guarded to EXTERNAL tracks only — never an
        // embedded subtitle inside the on-disk video — and the read-only NAS mount is the hard backstop.
        [HttpPost("/API/Admin/Jellyfin/Subtitles/Delete")]
        public async Task<IActionResult> JellyfinSubtitlesDelete(int movieId, int index)
        {
            if (!await IsCurrentUserEditor()) return Forbid();
            var itemId = await GetMovieJellyfinItemId(movieId);
            if (itemId == null) return BadRequest(new { message = "Movie isn't synced to Jellyfin." });
            try
            {
                var target = (await jellyfinApi.GetItemSubtitleStreamsAsync(itemId)).FirstOrDefault(s => s.Index == index);
                if (target == null) return NotFound(new { message = "No subtitle at that index." });
                if (!target.IsExternal) return BadRequest(new { message = "That subtitle is embedded in the video file — only downloaded (external) subtitles can be removed." });
                await jellyfinApi.DeleteSubtitleAsync(itemId, index);
                return Ok(new { deleted = true });
            }
            catch (Exception ex) { return StatusCode(502, new { deleted = false, message = "Remove failed: " + ex.Message }); }
        }

        // One-shot repair for the Movie/Series poster-namespace collision. Posters are on-disk files keyed
        // by id, and Movie & Series ids are NOT disjoint, so before series got their own ("series") bucket a
        // same-id Movie and Series shared "{id}.png" — a series showed the movie's poster.
        //
        // CHUNKED so it can never time out: each call handles the next `limit` series after `afterId`,
        // in parallel, and returns the cursor + whether more remain — the UI drives it to completion.
        // For each series it puts a poster in the series bucket (copying the existing "{id}.png" when the id
        // is the series' alone, else re-fetching the series' real poster from its tt).
        //
        // STRICTLY NON-DESTRUCTIVE to movie posters: it only READS the default ("{id}.png") namespace and
        // WRITES the series bucket; it NEVER deletes or overwrites a movie poster. As a courtesy it also
        // restores a colliding movie's poster *only when that movie has no poster file at all* (e.g. one an
        // earlier buggy run removed) by fetching the movie's OWN poster — again, never overwriting an
        // existing file. Runs in the web app so it writes the live image store (a dev box can't).
        // Editor-gated; idempotent (a series already in the bucket is skipped unless force=true).
        [HttpPost("/API/Admin/IngestReview/MigrateSeriesPosters")]
        public async Task<IActionResult> MigrateSeriesPosters(int afterId = 0, int limit = 40, bool force = false)
        {
            if (!await IsCurrentUserEditor()) return Forbid();
            limit = Math.Clamp(limit, 1, 200);

            var batch = await movieDb.Series.Where(s => s.Id > afterId)
                .OrderBy(s => s.Id).Take(limit)
                .Select(s => new { s.Id, s.imdbID }).ToListAsync();

            if (batch.Count == 0)
                return Ok(new { done = true, processed = 0, nextAfterId = afterId, copied = 0, refetched = 0, skipped = 0, movieRestored = 0, failed = 0, remaining = 0 });

            var batchIds = batch.Select(b => b.Id).ToList();
            // Precompute (no DbContext use inside the parallel body — MovieDb isn't thread-safe): which ids in
            // this chunk are also movies, and those movies' tts (to restore a movie that lost its poster).
            var collidingMovieTt = await movieDb.Movies.Where(m => batchIds.Contains(m.id))
                .Select(m => new { m.id, m.imdbID }).ToDictionaryAsync(x => x.id, x => x.imdbID);

            int copied = 0, refetched = 0, skipped = 0, movieRestored = 0, failed = 0;

            await Parallel.ForEachAsync(batch, new ParallelOptions { MaxDegreeOfParallelism = 6 }, async (s, _) =>
            {
                bool colliding = collidingMovieTt.ContainsKey(s.Id);
                try
                {
                    if (!force && await imageRepo.HasImage(s.Id, PosterImageVariant.Main, PosterBucket.Series))
                    {
                        Interlocked.Increment(ref skipped);
                    }
                    else if (!colliding && await imageRepo.HasImage(s.Id, PosterImageVariant.Main))
                    {
                        // The id is the series' alone, so the existing "{id}.png" is genuinely the series'
                        // poster — carry it (both variants) into the bucket without a network round-trip.
                        await CopyPosterImagesAsync(s.Id, null, s.Id, PosterBucket.Series);
                        Interlocked.Increment(ref copied);
                    }
                    else if (await posterFetchService.EnsurePosterAsync(s.Id, s.imdbID, isSeries: true, force: true))
                    {
                        // Colliding (can't trust "{id}.png" — it may be the movie's), or no source file:
                        // fetch the series' own poster straight into the series bucket.
                        Interlocked.Increment(ref refetched);
                    }
                    else
                    {
                        Interlocked.Increment(ref failed);
                    }
                }
                catch { Interlocked.Increment(ref failed); }

                // Courtesy movie restore: ONLY when the colliding movie has no poster file at all (absent),
                // fetch the movie's own poster. force:false guarantees we never overwrite an existing file.
                if (colliding && !string.IsNullOrWhiteSpace(collidingMovieTt[s.Id]))
                {
                    try
                    {
                        if (!await imageRepo.HasImage(s.Id, PosterImageVariant.Main)
                            && await posterFetchService.EnsurePosterAsync(s.Id, collidingMovieTt[s.Id], isSeries: false, force: false))
                            Interlocked.Increment(ref movieRestored);
                    }
                    catch { /* movie restore is best-effort; never fails the chunk */ }
                }
            });

            var nextAfterId = batchIds.Max();
            var remaining = await movieDb.Series.CountAsync(s => s.Id > nextAfterId);
            return Ok(new { done = remaining == 0, processed = batch.Count, nextAfterId, copied, refetched, skipped, movieRestored, failed, remaining });
        }

        // Backfill missing poster THUMBNAILS for movies: legacy rows whose main "{id}.png" exists on disk
        // but the "{id}_s.png" thumbnail was never generated — so /ImageThumb 404s and the card shows no
        // thumbnail even though the modal's /Image works. EnsurePosterThumnailExists shrinks the existing
        // on-disk main poster (no network fetch); we also (re)compute the dominant color while we hold the
        // bytes, since these legacy rows typically lack it too. CHUNKED by movie-id cursor so it can't time
        // out — the caller drives it to completion. Editor-gated; runs in the web app (writes the live image
        // store; a dev box can't). Idempotent: a movie that already has a thumbnail (or no main poster) is
        // skipped, so re-running only fills the gaps.
        [HttpPost("/API/Admin/IngestReview/BackfillThumbnails")]
        public async Task<IActionResult> BackfillThumbnails(int afterId = 0, int limit = 200)
        {
            if (!await IsCurrentUserEditor()) return Forbid();
            limit = Math.Clamp(limit, 1, 1000);

            var batch = await movieDb.Movies.Where(m => m.id > afterId)
                .OrderBy(m => m.id).Take(limit)
                .Select(m => m.id).ToListAsync();

            if (batch.Count == 0)
                return Ok(new { done = true, processed = 0, nextAfterId = afterId, generated = 0, coloured = 0, failed = 0, remaining = 0 });

            int generated = 0, coloured = 0;
            var failedIds = new List<int>();
            foreach (var id in batch)
            {
                try
                {
                    if (!await imageRepo.HasImage(id, PosterImageVariant.Main)) continue;        // no poster at all
                    if (await imageRepo.HasImage(id, PosterImageVariant.Thumbnail)) continue;     // already has a thumb
                    await shrinkService.EnsurePosterThumnailExists(id);
                    generated++;

                    var pd = await movieDb.MoviePosterDetails.FindAsync(id);
                    if (pd != null && pd.DominantColor == null)
                    {
                        var thumb = await imageRepo.GetImage(id, PosterImageVariant.Thumbnail);
                        if (thumb != null) { pd.DominantColor = ComputeAverageColor(thumb); coloured++; }
                    }
                }
                catch (Exception ex)
                {
                    // Don't let one bad poster sink the batch, but make the skip visible — a silently
                    // swallowed failure here is exactly how a title ends up stuck with a main image and
                    // no thumb (a blank card). Log it and report the ids back to the caller.
                    failedIds.Add(id);
                    logger.LogWarning(ex, "BackfillThumbnails: thumbnail generation failed for movie {Id}", id);
                }
            }
            await movieDb.SaveChangesAsync();

            var nextAfterId = batch.Max();
            var remaining = await movieDb.Movies.CountAsync(m => m.id > nextAfterId);
            return Ok(new { done = remaining == 0, processed = batch.Count, nextAfterId, generated, coloured, failed = failedIds.Count, failedIds, remaining });
        }

        // Generate (or refresh) the thumbnail for a SINGLE title from its existing on-disk main poster.
        // Used by the movie/series edit modal's "Generate thumbnail" button, which appears when a title has
        // a full poster but no "{id}_s.png" thumbnail (card shows a broken placeholder). No network fetch —
        // it shrinks the on-disk main poster — and refreshes the dominant color. Editor-gated; prod-only
        // (a dev box's image repo can't write).
        [HttpPost("/API/GenerateThumbnail")]
        public async Task<IActionResult> GenerateThumbnail(int id, bool isSeries = false)
        {
            if (!await IsCurrentUserEditor()) return Forbid();
            var bucket = PosterBucket.ForTitle(isSeries);
            if (!await imageRepo.HasImage(id, PosterImageVariant.Main, bucket))
                return BadRequest(new { success = false, message = "This title has no poster to make a thumbnail from." });

            await shrinkService.EnsurePosterThumnailExists(id, force: true, bucket);

            var thumb = await imageRepo.GetImage(id, PosterImageVariant.Thumbnail, bucket);
            if (thumb != null)
            {
                if (isSeries)
                {
                    var pd = await movieDb.SeriesPosterDetails.FindAsync(id);
                    if (pd != null) { pd.DominantColor = ComputeAverageColor(thumb); await movieDb.SaveChangesAsync(); }
                }
                else
                {
                    var pd = await movieDb.MoviePosterDetails.FindAsync(id);
                    if (pd != null) { pd.DominantColor = ComputeAverageColor(thumb); await movieDb.SaveChangesAsync(); }
                }
            }
            return Ok(new { success = true });
        }

        // Reject = delete the ingested row entirely. Guarded to pending-review rows so this can never
        // remove an established library entry. A series takes its episodes (+ their Playables/files) and
        // satellite graph with it; a misc video takes its Playable + files.
        [HttpPost("/API/Admin/IngestReview/Reject")]
        public async Task<IActionResult> IngestReviewReject([FromBody] IngestReviewIdsRequest req)
        {
            if (!await IsCurrentUserEditor()) return Forbid();
            if (req == null) return Ok(new { rejected = 0 });

            var rows = req.Ids.Count == 0 ? new List<Movie>()
                : await movieDb.Movies.Where(m => req.Ids.Contains(m.id) && m.ReviewBatch != null).ToListAsync();

            // Related misc must be cleared off a parent before it can be deleted (MiscVideo->Movie/Series FKs
            // are NO_ACTION). An episodic-extra misc (attached, no standalone Description) has no card and no
            // independent existence — delete it (+ its Playable/files) with the parent. A related misc that DOES
            // carry a Description is substantive — DETACH it (it lives on as a standalone pending misc with its
            // own card) rather than destroy it.
            var rejMovieIds = rows.Select(m => m.id).ToList();
            var rejSeriesIds = req.SeriesIds.Count == 0 ? new List<int>()
                : await movieDb.Series.Where(s => req.SeriesIds.Contains(s.Id) && s.ReviewBatch != null).Select(s => s.Id).ToListAsync();
            if (rejMovieIds.Count > 0 || rejSeriesIds.Count > 0)
            {
                var related = await movieDb.MiscVideos.Where(v =>
                    (v.RelatedMovieId != null && rejMovieIds.Contains(v.RelatedMovieId.Value))
                 || (v.RelatedSeriesId != null && rejSeriesIds.Contains(v.RelatedSeriesId.Value))).ToListAsync();
                var extra = related.Where(v => string.IsNullOrEmpty(v.Description)).ToList();
                if (extra.Count > 0)
                {
                    var cpids = extra.Select(v => v.PlayableId).ToList();
                    movieDb.MediaFiles.RemoveRange(await movieDb.MediaFiles.Where(f => cpids.Contains(f.PlayableId)).ToListAsync());
                    movieDb.MiscVideos.RemoveRange(extra);
                    movieDb.Playables.RemoveRange(await movieDb.Playables.Where(p => cpids.Contains(p.Id)).ToListAsync());
                }
                foreach (var v in related.Where(v => !string.IsNullOrEmpty(v.Description)))
                    { v.RelatedMovieId = null; v.RelatedSeriesId = null; }
                await movieDb.SaveChangesAsync();   // release the NO_ACTION FK before deleting the parents
            }

            // Use the full subtree delete: a plain Movies.RemoveRange leaves the movie's Playable+files
            // orphaned and — fatally — trips the NO_ACTION MoviePosterDetails FK when the row got a poster
            // during enrichment (that's the "Reject Failed" on enriched rows). DeleteMovieSubtreeAsync drops
            // poster details + playable/files + credit/genre/plot, then the Movie.
            foreach (var m in rows)
                await DeleteMovieSubtreeAsync(m);

            int seriesCount = 0;
            if (req.SeriesIds.Count > 0)
            {
                var seriesRows = await movieDb.Series.Where(s => req.SeriesIds.Contains(s.Id) && s.ReviewBatch != null).ToListAsync();
                var sids = seriesRows.Select(s => s.Id).ToList();
                var eps = await movieDb.Episodes.Where(e => e.SeriesId != null && sids.Contains(e.SeriesId.Value)).ToListAsync();
                var epPids = eps.Where(e => e.PlayableId != null).Select(e => e.PlayableId!.Value).ToList();
                var epFiles = await movieDb.MediaFiles.Where(f => epPids.Contains(f.PlayableId)).ToListAsync();
                var epPlayables = await movieDb.Playables.Where(p => epPids.Contains(p.Id)).ToListAsync();
                movieDb.MediaFiles.RemoveRange(epFiles);     // episode files…
                movieDb.Episodes.RemoveRange(eps);           // …episodes (releases Episode→Playable Restrict)…
                movieDb.Playables.RemoveRange(epPlayables);  // …their Playables…
                movieDb.Series.RemoveRange(seriesRows);      // …the series (cascades its genre/credit/plot/poster).
                seriesCount = seriesRows.Count;
            }

            int miscCount = 0;
            if (req.MiscIds.Count > 0)
            {
                var miscRows = await movieDb.MiscVideos.Where(v => req.MiscIds.Contains(v.Id) && v.ReviewBatch != null).ToListAsync();
                var pids = miscRows.Select(v => v.PlayableId).ToList();
                var files = await movieDb.MediaFiles.Where(f => pids.Contains(f.PlayableId)).ToListAsync();
                var playables = await movieDb.Playables.Where(p => pids.Contains(p.Id)).ToListAsync();
                movieDb.MediaFiles.RemoveRange(files);
                movieDb.MiscVideos.RemoveRange(miscRows);
                movieDb.Playables.RemoveRange(playables);
                miscCount = miscRows.Count;
            }

            await movieDb.SaveChangesAsync();
            return Ok(new { rejected = rows.Count + seriesCount + miscCount });
        }

        public class IngestReviewUpdateRequest
        {
            public int id { get; set; }
            public string Kind { get; set; } = "movie";   // "movie" | "series"
            public string? Title { get; set; }
            public string? SimpleTitle { get; set; }
            public int? Year { get; set; }
            public string? imdbID { get; set; }
            public string? TitleType { get; set; }
            /// <summary>A poster URL to fetch + persist for this row (so the approved title carries it).</summary>
            public string? PosterLink { get; set; }
        }

        // Correct a pending row in place before approval — title / simple title / year / imdbID / type, and
        // the poster (a provided PosterLink that differs from what's stored is downloaded + saved by id).
        // These are the exact values that go live on Approve. The row stays pending. A corrected imdbID is
        // validated and must not collide. Returns the new posterVersion when a poster was fetched.
        [HttpPost("/API/Admin/IngestReview/Update")]
        public async Task<IActionResult> IngestReviewUpdate([FromBody] IngestReviewUpdateRequest req)
        {
            if (!await IsCurrentUserEditor()) return Forbid();
            if (req == null || req.id == 0) return BadRequest(new { Message = "id required" });

            if (string.Equals(req.Kind, "series", StringComparison.OrdinalIgnoreCase))
            {
                var s = await movieDb.Series.FirstOrDefaultAsync(x => x.Id == req.id && x.ReviewBatch != null);
                if (s == null) return NotFound(new { Message = "Not a pending-review series" });
                if (!string.IsNullOrWhiteSpace(req.Title)) s.Title = req.Title.Trim();
                if (req.SimpleTitle != null) s.SimpleTitle = req.SimpleTitle.Trim();
                if (req.Year != null && (s.ReleaseDate == null || s.ReleaseDate.Value.Year != req.Year.Value))
                {
                    s.ReleaseDate = new DateTime(req.Year.Value, 1, 1);
                    s.StartYear = req.Year.Value;
                }
                if (req.imdbID != null)
                {
                    var newId = req.imdbID.Trim();
                    if (newId.Length > 0 && !string.Equals(newId, s.imdbID, StringComparison.OrdinalIgnoreCase))
                    {
                        if (!IsValidImdbId(newId)) return BadRequest(new { Message = $"'{newId}' is not a valid IMDb id" });
                        if (await movieDb.Series.AnyAsync(x => x.Id != s.Id && x.imdbID == newId))
                            return Conflict(new { Message = $"Another series already has {newId}" });
                        s.imdbID = newId; s.ReviewProvenance = "manual"; s.ReviewConfidence = "HIGH";
                    }
                }
                if (!string.IsNullOrWhiteSpace(req.TitleType) && Enum.TryParse<TitleType>(req.TitleType, true, out var stt)
                    && (stt == TitleType.TvSeries || stt == TitleType.TvMiniSeries))
                    s.TitleType = stt;
                int? sVer = await ApplyReviewPosterAsync(s.Id, req.PosterLink, isSeries: true);
                if (sVer == -1) return BadRequest(new { Success = false, Message = "Poster download failed." });
                await movieDb.SaveChangesAsync();
                return Ok(new { Success = true, posterVersion = sVer });
            }

            var m = await movieDb.Movies.FirstOrDefaultAsync(x => x.id == req.id && x.ReviewBatch != null);
            if (m == null) return NotFound(new { Message = "Not a pending-review movie" });

            if (!string.IsNullOrWhiteSpace(req.Title)) m.Title = req.Title.Trim();
            if (req.SimpleTitle != null) m.SimpleTitle = req.SimpleTitle.Trim();
            if (req.Year != null && (m.ReleaseDate == null || m.ReleaseDate.Value.Year != req.Year.Value))
                m.ReleaseDate = new DateTime(req.Year.Value, 1, 1);

            if (req.imdbID != null)
            {
                var newId = req.imdbID.Trim();
                if (newId.Length > 0 && !string.Equals(newId, m.imdbID, StringComparison.OrdinalIgnoreCase))
                {
                    if (!IsValidImdbId(newId))
                        return BadRequest(new { Message = $"'{newId}' is not a valid IMDb id" });
                    if (await movieDb.Movies.AnyAsync(x => x.id != m.id && x.imdbID == newId))
                        return Conflict(new { Message = $"Another movie already has {newId}" });
                    m.imdbID = newId;
                    m.ReviewProvenance = "manual";
                    m.ReviewConfidence = "HIGH";
                }
            }

            if (!string.IsNullOrWhiteSpace(req.TitleType) && Enum.TryParse<TitleType>(req.TitleType, true, out var tt))
                m.TitleType = tt;

            int? ver = await ApplyReviewPosterAsync(m.id, req.PosterLink, isSeries: false);
            if (ver == -1) return BadRequest(new { Success = false, Message = "Poster download failed." });
            await movieDb.SaveChangesAsync();
            return Ok(new { Success = true, posterVersion = ver });
        }

        // Fetch + persist a poster for a review row when a new/changed link is supplied. Returns the new
        // PosterVersion, null when no fetch was needed, or -1 on download failure (caller surfaces it).
        private async Task<int?> ApplyReviewPosterAsync(int id, string? posterLink, bool isSeries)
        {
            if (string.IsNullOrWhiteSpace(posterLink)) return null;
            var link = posterLink.Trim();
            var existing = isSeries
                ? await movieDb.SeriesPosterDetails.Where(p => p.SeriesId == id).Select(p => p.PosterLink).FirstOrDefaultAsync()
                : await movieDb.MoviePosterDetails.Where(p => p.MovieId == id).Select(p => p.PosterLink).FirstOrDefaultAsync();
            if (string.Equals(existing, link, StringComparison.OrdinalIgnoreCase)) return null;  // already have this exact poster
            try { return await DownloadAndSavePosterByIdAsync(id, link, isSeries); }
            catch { return -1; }   // bad URL / unreachable — caller surfaces a friendly message
        }

        public class IngestReviewReclassifyRequest
        {
            public int id { get; set; }
            // Both are "movie" | "series" | "misc". A bare id is ambiguous (separate id sequences),
            // so the caller states where the row lives now and where it should go.
            public string FromKind { get; set; } = "movie";
            public string ToKind { get; set; } = "misc";
            public string? Category { get; set; }
            public string? CollectionName { get; set; }
            public int? RelatedMovieId { get; set; }
            public int? RelatedSeriesId { get; set; }
        }

        // Reclassify a pending-review row among movie / series / misc. Movies and series each have their
        // own table now, so every direction (bar the no-op) is a real cross-table move: the title's own
        // metadata — and, for movie↔series, its genre / credit / plot / poster graph — is carried to the
        // destination table, the poster image is copied by id (PosterLink also carries, for re-download),
        // and structural children that don't fit the new shape are dropped cleanly. Dropping touches only
        // DB rows (mappings/episodes), never the files on disk; the reviewer re-scrapes / re-maps for the
        // corrected kind. The row stays review-pending so it can be Approved afterward. Pending-only.
        [HttpPost("/API/Admin/IngestReview/Reclassify")]
        public async Task<IActionResult> IngestReviewReclassify([FromBody] IngestReviewReclassifyRequest req)
        {
            if (!await IsCurrentUserEditor()) return Forbid();
            if (req == null || req.id == 0) return BadRequest(new { Message = "id required" });
            var from = (req.FromKind ?? "").Trim().ToLowerInvariant();
            var to = (req.ToKind ?? "").Trim().ToLowerInvariant();
            if (from == to) return Ok(new { Success = true, kind = to });

            string? cat = string.IsNullOrWhiteSpace(req.Category) ? null : req.Category.Trim();
            string? coll = string.IsNullOrWhiteSpace(req.CollectionName) ? null : req.CollectionName.Trim();

            // (A) Movie -> Series : carry metadata + the genre/credit/plot/poster graph to the Series
            // table. The movie's own Playable/files are dropped — a series streams through its episodes,
            // which a re-scrape will create and map (no episodes exist yet).
            if (from == "movie" && to == "series")
            {
                var m = await movieDb.Movies.FirstOrDefaultAsync(x => x.id == req.id && x.ReviewBatch != null);
                if (m == null) return NotFound(new { Message = "Not a pending-review movie" });

                var s = new Series();
                CopyTitleScalars(m, s);
                if (s.TitleType != TitleType.TvSeries && s.TitleType != TitleType.TvMiniSeries) s.TitleType = TitleType.TvSeries;
                s.ReviewProvenance = "reclassified in review";
                movieDb.Series.Add(s);
                await movieDb.SaveChangesAsync();   // assigns s.Id

                movieDb.SeriesGenres.AddRange((await movieDb.MovieGenres.Where(g => g.MovieID == m.id).ToListAsync())
                    .Select(g => new SeriesGenre { SeriesId = s.Id, GenreId = g.GenreId, Ordering = g.Ordering }));
                movieDb.SeriesCredits.AddRange((await movieDb.MovieCredits.Where(c => c.MovieID == m.id).ToListAsync())
                    .Select(c => new SeriesCredit { SeriesId = s.Id, PersonId = c.PersonId, Role = c.Role, Ordering = c.Ordering, Character = c.Character }));
                movieDb.SeriesPlotSummaries.AddRange((await movieDb.MoviePlotSummaries.Where(p => p.MovieID == m.id).ToListAsync())
                    .Select(p => new SeriesPlotSummary { SeriesId = s.Id, Ordering = p.Ordering, Author = p.Author, Text = p.Text }));
                var pd = await movieDb.MoviePosterDetails.FirstOrDefaultAsync(x => x.MovieId == m.id);
                if (pd != null) movieDb.SeriesPosterDetails.Add(new SeriesPosterDetails { SeriesId = s.Id, PosterLink = pd.PosterLink, PosterVersion = pd.PosterVersion, DominantColor = pd.DominantColor });

                await CopyPosterImagesAsync(m.id, null, s.Id, PosterBucket.Series);
                await DeleteMovieSubtreeAsync(m);
                await movieDb.SaveChangesAsync();
                return Ok(new { Success = true, kind = "series", id = s.Id });
            }

            // (A') Series -> Movie : the reverse — metadata + graph back into Movie (with a fresh
            // Playable); the series' episodes + their Playables/files are dropped (a movie has none).
            if (from == "series" && to == "movie")
            {
                var s = await movieDb.Series.FirstOrDefaultAsync(x => x.Id == req.id && x.ReviewBatch != null);
                if (s == null) return NotFound(new { Message = "Not a pending-review series" });

                var m = new Movie { Playable = new Playable { Kind = PlayableKind.Movie } };
                CopyTitleScalars(s, m);
                if (m.TitleType == TitleType.TvSeries || m.TitleType == TitleType.TvMiniSeries) m.TitleType = TitleType.Movie;
                m.ReviewProvenance = "reclassified in review";
                movieDb.Movies.Add(m);
                await movieDb.SaveChangesAsync();   // assigns m.id

                movieDb.MovieGenres.AddRange((await movieDb.SeriesGenres.Where(g => g.SeriesId == s.Id).ToListAsync())
                    .Select(g => new MovieGenre { MovieID = m.id, GenreId = g.GenreId, Ordering = g.Ordering }));
                movieDb.MovieCredits.AddRange((await movieDb.SeriesCredits.Where(c => c.SeriesId == s.Id).ToListAsync())
                    .Select(c => new MovieCredit { MovieID = m.id, PersonId = c.PersonId, Role = c.Role, Ordering = c.Ordering, Character = c.Character }));
                movieDb.MoviePlotSummaries.AddRange((await movieDb.SeriesPlotSummaries.Where(p => p.SeriesId == s.Id).ToListAsync())
                    .Select(p => new MoviePlotSummary { MovieID = m.id, Ordering = p.Ordering, Author = p.Author, Text = p.Text }));
                var pd = await movieDb.SeriesPosterDetails.FirstOrDefaultAsync(x => x.SeriesId == s.Id);
                if (pd != null) movieDb.MoviePosterDetails.Add(new MoviePosterDetails { MovieId = m.id, PosterLink = pd.PosterLink, PosterVersion = pd.PosterVersion, DominantColor = pd.DominantColor });

                await CopyPosterImagesAsync(s.Id, PosterBucket.Series, m.id, null);
                await DeleteSeriesSubtreeAsync(s);
                await movieDb.SaveChangesAsync();
                return Ok(new { Success = true, kind = "movie", id = m.id });
            }

            // (A'') Series -> MiscVideo : keep the title as a misc collection (fresh Playable); drop the
            // series' episodes + their Playables/files.
            if (from == "series" && to == "misc")
            {
                var s = await movieDb.Series.FirstOrDefaultAsync(x => x.Id == req.id && x.ReviewBatch != null);
                if (s == null) return NotFound(new { Message = "Not a pending-review series" });

                var p = new Playable { Kind = PlayableKind.MiscVideo };
                movieDb.Playables.Add(p);
                await movieDb.SaveChangesAsync();
                movieDb.MiscVideos.Add(new MiscVideo
                {
                    PlayableId = p.Id,
                    Title = s.Title ?? "(untitled)",
                    SimpleTitle = s.SimpleTitle,
                    Year = s.ReleaseDate?.Year ?? s.StartYear,
                    Category = cat,
                    CollectionName = coll,
                    RelatedMovieId = req.RelatedMovieId,
                    RelatedSeriesId = req.RelatedSeriesId,
                    ReviewBatch = s.ReviewBatch,
                    ReviewProvenance = "reclassified in review",
                    ReviewSourcePath = s.ReviewSourcePath,
                });
                await DeleteSeriesSubtreeAsync(s);
                await movieDb.SaveChangesAsync();
                return Ok(new { Success = true, kind = "misc" });
            }

            // (A''') MiscVideo -> Series : create the Series shell (reviewer fills tt + re-scrapes
            // episodes); drop the misc's Playable + files.
            if (from == "misc" && to == "series")
            {
                var mv = await movieDb.MiscVideos.FirstOrDefaultAsync(v => v.Id == req.id && v.ReviewBatch != null);
                if (mv == null) return NotFound(new { Message = "Not a pending-review misc video" });

                var s = new Series
                {
                    Title = mv.Title,
                    SimpleTitle = string.IsNullOrEmpty(mv.SimpleTitle) ? mv.Title : mv.SimpleTitle,
                    ReleaseDate = mv.Year != null ? new DateTime(mv.Year.Value, 1, 1) : null,
                    StartYear = mv.Year,
                    TitleType = TitleType.TvSeries,
                    ReviewBatch = mv.ReviewBatch,
                    ReviewProvenance = "reclassified in review",
                    ReviewConfidence = "NONE",
                    ReviewSourcePath = mv.ReviewSourcePath,
                };
                movieDb.Series.Add(s);
                await DeleteMiscSubtreeAsync(mv);
                await movieDb.SaveChangesAsync();
                return Ok(new { Success = true, kind = "series", id = s.Id });
            }

            // (B) Movie -> MiscVideo  (cross-table move; Playable + files come along)
            if (from == "movie" && to == "misc")
            {
                var m = await movieDb.Movies.FirstOrDefaultAsync(x => x.id == req.id && x.ReviewBatch != null);
                if (m == null) return NotFound(new { Message = "Not a pending-review movie" });

                int playableId;
                if (m.PlayableId != null) playableId = m.PlayableId.Value;
                else
                {
                    var p = new Playable { Kind = PlayableKind.MiscVideo };
                    movieDb.Playables.Add(p);
                    await movieDb.SaveChangesAsync();
                    playableId = p.Id;
                }

                movieDb.MiscVideos.Add(new MiscVideo
                {
                    PlayableId = playableId,
                    Title = m.Title ?? "(untitled)",
                    SimpleTitle = m.SimpleTitle,
                    Year = m.ReleaseDate?.Year ?? m.ImdbReleaseDate?.Year,
                    Category = cat,
                    CollectionName = coll,
                    RelatedMovieId = req.RelatedMovieId,
                    RelatedSeriesId = req.RelatedSeriesId,
                    ReviewBatch = m.ReviewBatch,
                    ReviewProvenance = "reclassified in review",
                    ReviewSourcePath = m.ReviewSourcePath,
                });

                var pl = await movieDb.Playables.FirstOrDefaultAsync(p => p.Id == playableId);
                if (pl != null) pl.Kind = PlayableKind.MiscVideo;

                // Drop the (often wrong-tt) credit/genre/plot graph explicitly — the live FKs can't be
                // assumed to cascade — then the Movie row. Files stay on the Playable.
                movieDb.MovieCredits.RemoveRange(await movieDb.MovieCredits.Where(c => c.MovieID == m.id).ToListAsync());
                movieDb.MovieGenres.RemoveRange(await movieDb.MovieGenres.Where(g => g.MovieID == m.id).ToListAsync());
                movieDb.MoviePlotSummaries.RemoveRange(await movieDb.MoviePlotSummaries.Where(s => s.MovieID == m.id).ToListAsync());
                m.PlayableId = null;
                await ClearSyncCandidateRefsAsync(m);   // NO ACTION FKs — see the helper
                movieDb.Movies.Remove(m);
                await movieDb.SaveChangesAsync();
                return Ok(new { Success = true, kind = "misc" });
            }

            // (C) MiscVideo -> Movie  (cross-table move back; reviewer adds the tt via Update)
            if (from == "misc" && to == "movie")
            {
                var mv = await movieDb.MiscVideos.FirstOrDefaultAsync(v => v.Id == req.id && v.ReviewBatch != null);
                if (mv == null) return NotFound(new { Message = "Not a pending-review misc video" });

                var movie = new Movie
                {
                    Title = mv.Title,
                    SimpleTitle = string.IsNullOrEmpty(mv.SimpleTitle) ? mv.Title : mv.SimpleTitle,
                    ReleaseDate = mv.Year != null ? new DateTime(mv.Year.Value, 1, 1) : null,
                    TitleType = TitleType.Movie,
                    PlayableId = mv.PlayableId,
                    ReviewBatch = mv.ReviewBatch,
                    ReviewProvenance = "reclassified in review",
                    ReviewConfidence = "NONE",
                    ReviewSourcePath = mv.ReviewSourcePath,
                };
                movieDb.Movies.Add(movie);

                var pl = await movieDb.Playables.FirstOrDefaultAsync(p => p.Id == mv.PlayableId);
                if (pl != null) pl.Kind = PlayableKind.Movie;

                movieDb.MiscVideos.Remove(mv);
                await movieDb.SaveChangesAsync();
                return Ok(new { Success = true, kind = "movie", id = movie.id });
            }

            return BadRequest(new { Message = $"Unsupported reclassify {req.FromKind} -> {req.ToKind}" });
        }

        // Copy every shared scalar column (string / value-type) from one title entity to another
        // (Movie ⇄ Series). Skips keys, the NotMapped PosterLink passthrough, and any nav/collection —
        // so it auto-carries new metadata columns as the schema grows ("no data left behind").
        private static readonly HashSet<string> TitleScalarSkip = new(StringComparer.Ordinal) { "id", "Id", "PosterLink" };
        private static void CopyTitleScalars(object src, object dst)
        {
            var srcType = src.GetType();
            foreach (var dp in dst.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (!dp.CanWrite || TitleScalarSkip.Contains(dp.Name)) continue;
                if (!(dp.PropertyType == typeof(string) || dp.PropertyType.IsValueType)) continue;  // scalars only — skips navs/collections
                var sp = srcType.GetProperty(dp.Name, BindingFlags.Public | BindingFlags.Instance);
                if (sp == null || !sp.CanRead || sp.PropertyType != dp.PropertyType) continue;
                dp.SetValue(dst, sp.GetValue(src));
            }
        }

        // Carry the poster image to a new id on a cross-table move (posters are on-disk files keyed by id,
        // served with no DB lookup). Best-effort: a missing source or write failure is fine — the copied
        // PosterLink lets enrichment re-download for the new id.
        private async Task CopyPosterImagesAsync(int fromId, string? fromBucket, int toId, string? toBucket)
        {
            if (fromId == toId && string.Equals(fromBucket, toBucket, StringComparison.Ordinal)) return;
            foreach (var variant in new[] { PosterImageVariant.Main, PosterImageVariant.Thumbnail })
            {
                try
                {
                    var bytes = await imageRepo.GetImage(fromId, variant, fromBucket);
                    if (bytes != null && bytes.Length > 0) await imageRepo.SaveImage(toId, variant, bytes, toBucket);
                }
                catch { /* best-effort */ }
            }
        }

        // Delete a movie and everything that hangs off it (Playable + files, credit/genre/plot/poster) —
        // the live FKs can't be assumed to cascade, so each is removed explicitly. Used when its metadata
        // has already been carried elsewhere (movie → series).
        private async Task DeleteMovieSubtreeAsync(Movie m)
        {
            if (m.PlayableId != null)
            {
                var pid = m.PlayableId.Value;
                movieDb.MediaFiles.RemoveRange(await movieDb.MediaFiles.Where(f => f.PlayableId == pid).ToListAsync());
                var pl = await movieDb.Playables.FirstOrDefaultAsync(p => p.Id == pid);
                m.PlayableId = null;
                if (pl != null) movieDb.Playables.Remove(pl);
            }
            movieDb.MovieCredits.RemoveRange(await movieDb.MovieCredits.Where(c => c.MovieID == m.id).ToListAsync());
            movieDb.MovieGenres.RemoveRange(await movieDb.MovieGenres.Where(g => g.MovieID == m.id).ToListAsync());
            movieDb.MoviePlotSummaries.RemoveRange(await movieDb.MoviePlotSummaries.Where(s => s.MovieID == m.id).ToListAsync());
            var pd = await movieDb.MoviePosterDetails.FirstOrDefaultAsync(x => x.MovieId == m.id);
            if (pd != null) movieDb.MoviePosterDetails.Remove(pd);
            await ClearSyncCandidateRefsAsync(m);
            movieDb.Movies.Remove(m);
        }

        /// <summary>
        /// Clears <see cref="SyncCandidate"/> references before a Movie row is removed — the FKs are
        /// NO ACTION in the DB (SQL Server refuses two SET NULL paths into Movie from one table), so
        /// EVERY path that deletes a movie must call this or the delete throws. A candidate whose
        /// CREATED movie is going away reverts to Pending with the reason visible AND its pinned tt
        /// cleared — the pin produced a movie the reviewer just rejected, and keeping it would make a
        /// re-resolve deterministically recreate the same wrong row. A candidate that merely TARGETED
        /// the movie loses its pairing and drops to Unclassified.
        /// </summary>
        private async Task ClearSyncCandidateRefsAsync(Movie m)
        {
            foreach (var c in await movieDb.SyncCandidates
                .Where(c => c.TargetMovieId == m.id || c.CreatedMovieId == m.id).ToListAsync())
            {
                if (c.CreatedMovieId == m.id && c.Status == SyncCandidateStatus.Ingested)
                {
                    c.Status = SyncCandidateStatus.Pending;
                    c.ResolvedImdbId = null;
                    c.ResolutionError = TruncCol($"The resolved movie '{m.Title}' was rejected/deleted — re-resolve or dismiss.", 512);
                    c.ResolvedUtc = null;
                }
                if (c.TargetMovieId == m.id)
                {
                    c.TargetMovieId = null; c.Signal = null; c.OldPath = null;
                    if (c.Status == SyncCandidateStatus.Pending) c.Kind = SyncCandidateKind.Unclassified;
                }
                if (c.CreatedMovieId == m.id) c.CreatedMovieId = null;
            }
        }

        /// <summary>Bound a string to a column's MaxLength — the write that records a failure must
        /// never itself fail on 'string or binary data would be truncated'.</summary>
        private static string? TruncCol(string? s, int max) => s != null && s.Length > max ? s.Substring(0, max) : s;

        // Delete a series subtree: episodes + their Playables/files, then the Series row (which cascades
        // its genre/credit/plot/poster). Mirrors the Reject path. Used when the title moves to movie/misc.
        private async Task DeleteSeriesSubtreeAsync(Series s)
        {
            var eps = await movieDb.Episodes.Where(e => e.SeriesId == s.Id).ToListAsync();
            var epPids = eps.Where(e => e.PlayableId != null).Select(e => e.PlayableId!.Value).ToList();
            movieDb.MediaFiles.RemoveRange(await movieDb.MediaFiles.Where(f => epPids.Contains(f.PlayableId)).ToListAsync());
            movieDb.Episodes.RemoveRange(eps);
            movieDb.Playables.RemoveRange(await movieDb.Playables.Where(p => epPids.Contains(p.Id)).ToListAsync());
            await ClearSyncCandidateSeriesRefsAsync(s);
            movieDb.Series.Remove(s);
        }

        /// <summary>
        /// The <see cref="ClearSyncCandidateRefsAsync"/> counterpart for series: <c>TargetSeriesId</c> is
        /// NO ACTION in the DB, so every path that removes a Series must clear it or the delete throws.
        /// Episode candidates that were mapped into the show being deleted come BACK as Pending — their
        /// files exist and are untracked again the moment the episodes go away, and silently losing them
        /// would make a rejected series erase the evidence that its files are on disk. The pinned tt is
        /// cleared too: it produced a series the reviewer just rejected, so a re-resolve must not
        /// deterministically recreate it.
        /// </summary>
        private async Task ClearSyncCandidateSeriesRefsAsync(Series s)
        {
            foreach (var c in await movieDb.SyncCandidates.Where(c => c.TargetSeriesId == s.Id).ToListAsync())
            {
                c.TargetSeriesId = null;
                c.ResolvedImdbId = null;
                // The episode list went with the series, so the next resolve is free to build a new
                // one — the ownership marker must not outlive the list it described.
                c.SeriesListOwned = false;
                c.ResolutionError = TruncCol($"The resolved series '{s.Title}' was rejected/deleted — re-resolve or dismiss.", 512);
                if (c.Status == SyncCandidateStatus.Ingested)
                {
                    c.Status = SyncCandidateStatus.Pending;
                    c.ResolvedUtc = null;
                    c.ResolvedBy = null;
                }
            }
        }

        // Delete a misc video + its Playable/files. Used when the title moves to series.
        private async Task DeleteMiscSubtreeAsync(MiscVideo mv)
        {
            movieDb.MediaFiles.RemoveRange(await movieDb.MediaFiles.Where(f => f.PlayableId == mv.PlayableId).ToListAsync());
            var pl = await movieDb.Playables.FirstOrDefaultAsync(p => p.Id == mv.PlayableId);
            movieDb.MiscVideos.Remove(mv);
            if (pl != null) movieDb.Playables.Remove(pl);
        }

        // Scrapes YouTube video metadata for all boardgame videos that are missing or stale (>30 days,
        // per YouTube Developer Policies §4.D). Stores results directly in HowToPlayVideoUrlsJson.
        [HttpPost("/API/ScrapeYouTubeVideoDetails")]
        public async Task<IActionResult> ScrapeYouTubeVideoDetails()
        {
            if (!await IsCurrentUserEditor()) return Forbid();

            var games = await movieDb.Boardgames
                .Where(b => b.HowToPlayVideoUrlsJson != null)
                .ToListAsync();

            int scraped = 0, total = 0;
            foreach (var game in games)
            {
                var entries = game.HowToPlayVideoEntries;
                if (entries.Count == 0) continue;
                total += entries.Count;
                if (await youTubeService.RefreshEntriesAsync(entries))
                {
                    game.HowToPlayVideoEntries = entries;
                    scraped++;
                }
            }

            if (scraped > 0) await movieDb.SaveChangesAsync();
            return Ok(new { message = $"Updated {scraped} boardgame(s).", scraped, total });
        }
    }
}
