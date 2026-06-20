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

        public APIController(MovieDb movieDb, TmdbApi tmdb, OmdbApi omdb, ImdbApiClient imdb, HttpClient httpClient, IPosterImageRepository imageRepo,
            IBoardgameImageRepository boardgameImageRepo, ImageShrinkService shrinkService, GoogleSearchService googleSearchService, IMDBApiService imdbApiService,
            BoardGameGeekApi boardGameGeekApi, PosterMosaicService posterMosaicService,
            BoardgameRulesService boardgameRulesService, BoardgamePdfRepository boardgamePdfRepository,
            IConfiguration configuration, YouTubeService youTubeService, IMemoryCache memoryCache,
            BoardgameSimilarityService boardgameSimilarityService, PosterFetchService posterFetchService, TitleEnrichService titleEnrichService)
        {
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

            var movie = await movieDb.Movies.Include(m => m.PosterDetails).SingleOrDefaultAsync(m => m.id == id);
            if (movie == null)
                return BadRequest(new { Success = false, Message = "Movie ID not found" });
            var rating = GetMPARatingFromMovieRating(movie.Rating);
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
            var files = movie.PlayableId == null
                ? new List<object>()
                : await movieDb.MediaFiles.Where(f => f.PlayableId == movie.PlayableId)
                    .OrderBy(f => f.Role).ThenBy(f => f.PartNumber).ThenBy(f => f.Id)
                    // mediaFileId + isPlayable let the modal offer a play button per file (the Primary
                    // plays via the movie id; a specific Part/Variant/Extra plays by its mediaFileId).
                    .Select(f => (object)new { mediaFileId = f.Id, path = f.Path, role = f.Role.ToString(), label = f.Label, partNumber = f.PartNumber, isPlayable = f.JellyfinItemId != null && f.MissingSinceUtc == null })
                    .ToListAsync();

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
                isSeries = false,
                seasons = (object?)null,
            };
        }

        [HttpGet("/API/GetTotalMovieCount")]
        public async Task<IActionResult> GetTotalMovieCount()
        {
            try
            {
                var count = await movieDb.Movies.CountAsync(m => m.ReviewBatch == null);
                return Ok(new { totalCount = count, success = true });
            }
            catch (Exception ex)
            {
                return BadRequest(new { totalCount = 0, success = false, error = ex.Message });
            }
        }

        [EnableQuery]
        [HttpGet("/odata/Movies")]
        public async Task<IQueryable<Movie>> GetMovies()
        {
            return await GetBaseMovieQuery();
        }

        // Cards for an explicit id set (the Seen / Want lists, and the back-nav restore list).
        // pageSize > 0 streams the list as the paginated envelope (Seen/Want infinite scroll);
        // pageSize <= 0 (default) returns the full merged list as a bare array, which the restore
        // path needs so it can reorder client-side by its remembered on-screen order.
        [HttpPost("/API/GetMoviesByIds")]
        public async Task<IActionResult> GetMoviesByIds([FromBody] List<int> ids, int page = 1, int pageSize = 0)
        {
            if (ids == null || ids.Count == 0)
                return Ok(pageSize > 0 ? (object)EmptyPage(pageSize) : new List<MovieCardDto>());

            // ids share a space across the two tables — match both Movies and Series.
            var mq = (await GetBaseMovieQuery()).Where(m => ids.Contains(m.id));
            var sq = (await GetBaseSeriesQuery()).Where(s => ids.Contains(s.Id));

            if (pageSize > 0)
                return Ok(await PageMergedAsync(mq, sq, page, pageSize));

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
            public string? Runtime { get; set; }
            public decimal? imdbRating { get; set; }
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
            Rating = m.MpaaRating ?? m.Rating,
            Runtime = m.Runtime,
            imdbRating = m.ImdbRatingScraped ?? m.imdbRating,
            PlotFull = m.PlotFull,
            Plot = m.Plot,
            TopCast = m.TopCast,
            Actors = m.Actors,
            PosterVersion = m.PosterDetails != null ? m.PosterDetails.PosterVersion : 0,
        };

        // Same slim card shape, projected from a Series — so browse/search can interleave series with movies.
        private static readonly System.Linq.Expressions.Expression<Func<Series, MovieCardDto>> ToSeriesCardDto = s => new MovieCardDto
        {
            id = s.Id,
            Kind = "series",
            Title = s.Title,
            SimpleTitle = s.SimpleTitle,
            ReleaseDate = s.ReleaseDate ?? s.ImdbReleaseDate,
            Rating = s.MpaaRating ?? s.Rating,
            Runtime = s.Runtime,
            imdbRating = s.ImdbRatingScraped ?? s.imdbRating,
            PlotFull = s.PlotFull,
            Plot = s.Plot,
            TopCast = s.TopCast,
            Actors = s.Actors,
            PosterVersion = s.PosterDetails != null ? s.PosterDetails.PosterVersion : 0,
        };

        private async Task<int> GetAgeRestrictionAsync()
        {
            var currentUserId = GetCurrentUserId();
            if (currentUserId.HasValue)
            {
                var setRestriction = await movieDb.UserSettings
                    .FirstOrDefaultAsync(u => u.SettingKey == "AgeRestriction" && u.UserID == currentUserId.Value);
                if (setRestriction != null && int.TryParse(setRestriction.SettingValue, out int parsedRestriction))
                    return parsedRestriction;
            }
            return 100;
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
                .Where(m => !movieDb.RatingMaps.Any(rm => rm.MovieRating == m.Rating && rm.MPARatingID > ageRestriction));
        }

        // Series peer of GetBaseMovieQuery (same quarantine + age gate). Browse/search union the two.
        private async Task<IQueryable<Series>> GetBaseSeriesQuery()
        {
            int ageRestriction = await GetAgeRestrictionAsync();
            return movieDb.Series
                .Include(s => s.PosterDetails)
                .Where(s => s.ReviewBatch == null)
                .Where(s => !movieDb.RatingMaps.Any(rm => rm.MovieRating == s.Rating && rm.MPARatingID > ageRestriction));
        }

        // Merge movie + series cards into one SimpleTitle-ordered list (browse stays unified).
        private static List<MovieCardDto> MergeCards(IEnumerable<MovieCardDto> a, IEnumerable<MovieCardDto> b) =>
            a.Concat(b).OrderBy(c => c.SimpleTitle, StringComparer.OrdinalIgnoreCase).ToList();

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

        // Browse filtered by the coarse, normalized Title Type bucket (Movies / Series / Short / Misc).
        // Series come from the Series table; Movies/Short read Movie.NormalizedTitleType (a persisted
        // computed column off TitleType); Misc reads the MiscVideo table (tt-less library videos —
        // workprints, stage performances, shorts sets).
        // Paginated browse by title type. Returns the { movies, totalCount, page, pageSize }
        // envelope so the client can infinite-scroll instead of pulling the whole library at
        // once (type=Movies is the entire Movie table). pageSize <= 0 returns everything (the
        // old behavior) for any caller that still wants the full list.
        [HttpGet("/API/GetMoviesByType")]
        public async Task<IActionResult> GetMoviesByType(string type, int page = 1, int pageSize = 60)
        {
            if (string.IsNullOrWhiteSpace(type) || !Enum.TryParse<NormalizedTitleType>(type, true, out var nt))
                return BadRequest(new { Message = $"Unknown title type '{type}'" });
            if (nt == NormalizedTitleType.Series)
            {
                var sq = await GetBaseSeriesQuery();
                return Ok(await PageCardsAsync(sq.OrderBy(s => s.SimpleTitle).ThenBy(s => s.Id).Select(ToSeriesCardDto), page, pageSize));
            }
            if (nt == NormalizedTitleType.Misc)
                return Ok(PageCards(await GetMiscCards(), page, pageSize));
            var baseQuery = await GetBaseMovieQuery();
            return Ok(await PageCardsAsync(baseQuery.Where(m => m.NormalizedTitleType == nt).OrderBy(m => m.SimpleTitle).ThenBy(m => m.id).Select(ToCardDto), page, pageSize));
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
        }

        // Page a merged movie+series browse result at the DB without pulling the whole filtered set
        // (two-phase, mirroring the MyBooks views-perf "band items" approach):
        //   1. UNION just the ordering keys (Kind/Id/SimpleTitle) across both tables and Skip/Take
        //      that — a cheap scalar set-operation. A stable secondary sort (Kind, Id) guarantees the
        //      page boundaries don't drift between fetches, so infinite scroll never dupes/skips.
        //   2. Materialize the full card DTOs for just the page's ids and restore the merged order.
        // pageSize <= 0 returns the whole merged set (back-compat).
        private static async Task<object> PageMergedAsync(IQueryable<Movie> mq, IQueryable<Series> sq, int page, int pageSize)
        {
            var keys = mq.Select(m => new CardKey { Kind = "movie", Id = m.id, SimpleTitle = m.SimpleTitle })
                .Concat(sq.Select(s => new CardKey { Kind = "series", Id = s.Id, SimpleTitle = s.SimpleTitle }));

            if (pageSize <= 0)
            {
                var allMovies = await mq.Select(ToCardDto).ToListAsync();
                var allSeries = await sq.Select(ToSeriesCardDto).ToListAsync();
                var allMerged = MergeCards(allMovies, allSeries);
                return new { movies = allMerged, totalCount = allMerged.Count, page = 1, pageSize = allMerged.Count };
            }
            if (page < 1) page = 1;
            // Count only on the first page — the client reads totalCount once and discards it on every
            // later page fetch. Here that COUNT is a full UNION of both table scans, so skipping it on
            // page > 1 saves the most expensive query of the request (-1 = "not computed").
            var totalCount = page == 1 ? await keys.CountAsync() : -1;

            var pageKeys = await keys
                .OrderBy(k => k.SimpleTitle).ThenBy(k => k.Kind).ThenBy(k => k.Id)
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

        // Approved (un-quarantined) MiscVideos as browse cards. They carry no Rating, so the age gate
        // does not apply; poster (if any) is served from the separate /MiscImage namespace. The card
        // builds the poster URL off Kind="misc", not the shared id space.
        private async Task<List<MovieCardDto>> GetMiscCards()
        {
            var raw = await movieDb.MiscVideos
                .Where(v => v.ReviewBatch == null)
                .OrderBy(v => v.CollectionName ?? "")
                .ThenBy(v => v.SortOrder ?? int.MaxValue)
                .ThenBy(v => v.SimpleTitle ?? v.Title)
                .Select(v => new { v.Id, v.Title, v.SimpleTitle, v.Year, v.Description, v.Category, v.PlayableId })
                .ToListAsync();
            return raw.Select(v => new MovieCardDto
            {
                id = v.Id,
                Kind = "misc",
                Title = v.Title,
                SimpleTitle = v.SimpleTitle,
                ReleaseDate = v.Year.HasValue ? new DateTime(v.Year.Value, 1, 1) : (DateTime?)null,
                Plot = v.Description,
                Category = v.Category,
                PlayableId = v.PlayableId,
            }).ToList();
        }

        // ── Unified search over movies + series (the frontend uses these instead of /odata/Movies) ──

        [HttpGet("/API/BrowseTitle")]
        public async Task<IActionResult> BrowseTitle(string q, int page = 1, int pageSize = 60)
        {
            q = (q ?? "").Trim();
            if (q.Length == 0) return Ok(EmptyPage(pageSize));
            var mq = (await GetBaseMovieQuery()).Where(m => (m.SimpleTitle != null && m.SimpleTitle.Contains(q)) || (m.Title != null && m.Title.Contains(q)));
            var sq = (await GetBaseSeriesQuery()).Where(s => (s.SimpleTitle != null && s.SimpleTitle.Contains(q)) || (s.Title != null && s.Title.Contains(q)));
            return Ok(await PageMergedAsync(mq, sq, page, pageSize));
        }

        [HttpGet("/API/BrowseLetter")]
        public async Task<IActionResult> BrowseLetter(string letter, int page = 1, int pageSize = 60)
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
            return Ok(await PageMergedAsync(mq, sq, page, pageSize));
        }

        [HttpGet("/API/BrowseGenre")]
        public async Task<IActionResult> BrowseGenre(string genres, int page = 1, int pageSize = 60)
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
            return Ok(await PageMergedAsync(mq, sq, page, pageSize));
        }

        [HttpGet("/API/BrowsePerson")]
        public async Task<IActionResult> BrowsePerson(string q, int page = 1, int pageSize = 60)
        {
            q = (q ?? "").Trim();
            if (q.Length == 0) return Ok(EmptyPage(pageSize));
            var mq = (await GetBaseMovieQuery()).Where(m => m.Credits.Any(c => c.Person.DisplayName.Contains(q))
                || (m.Actors != null && m.Actors.Contains(q)) || (m.Director != null && m.Director.Contains(q)) || (m.Writer != null && m.Writer.Contains(q)));
            var sq = (await GetBaseSeriesQuery()).Where(s => s.Credits.Any(c => c.Person.DisplayName.Contains(q))
                || (s.Actors != null && s.Actors.Contains(q)) || (s.Director != null && s.Director.Contains(q)) || (s.Writer != null && s.Writer.Contains(q)));
            return Ok(await PageMergedAsync(mq, sq, page, pageSize));
        }

        // Series detail (mirror of GetMovie): the series + its normalized graph + seasons/episodes.
        [HttpGet("/API/GetSeries")]
        public async Task<IActionResult> GetSeries(int id)
        {
            int ageRestriction = await GetAgeRestrictionAsync();
            var series = await movieDb.Series.Include(s => s.PosterDetails).SingleOrDefaultAsync(s => s.Id == id);
            if (series == null) return BadRequest(new { Success = false, Message = "Series ID not found" });
            if (GetMPARatingFromMovieRating(series.Rating) > ageRestriction)
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
                .Select(f => new { f.PlayableId, Streamable = f.JellyfinItemId != null && f.MissingSinceUtc == null }).ToListAsync();
            var withFile = fileRows.Select(f => f.PlayableId).Distinct().ToHashSet();
            var streamable = fileRows.Where(f => f.Streamable).Select(f => f.PlayableId).Distinct().ToHashSet();
            var seasons = eps.GroupBy(e => e.SeasonNumber).OrderBy(g => g.Key).Select(g => new
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
                isSeries = true,
                seasons,
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
            await shrinkService.EnsurePosterThumnailExists(movie.id, force);

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
            var result = await httpClient.GetAsync(posterLink);
            result.EnsureSuccessStatusCode();
            var content = await result.Content.ReadAsByteArrayAsync();
            await imageRepo.SaveImage(id, PosterImageVariant.Main, content);
            await shrinkService.EnsurePosterThumnailExists(id, true);
            var thumbnailBytes = await imageRepo.GetImage(id, PosterImageVariant.Thumbnail);
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
            // Seen / Want lists carry both movie and series ids (a viewing targets one or the other;
            // the shared id space + the card's Kind disambiguate). MovieID ?? SeriesId yields the id either way.
            var moviesSeen = (await movieDb.Viewings.Where(d => d.UserID == user.UserID && d.ViewingType == "Seen")
                .Select(d => d.MovieID ?? d.SeriesId).ToListAsync()).Where(x => x != null).Select(x => x!.Value).ToList();

            var moviesToWatch = (await movieDb.Viewings.Where(d => d.UserID == user.UserID && d.ViewingType == "WantToWatch")
                .Select(d => d.MovieID ?? d.SeriesId).ToListAsync()).Where(x => x != null).Select(x => x!.Value).ToList();

            //age restriction
            int? ageRestriction = null;
            var ageSetting = await movieDb.UserSettings
                .FirstOrDefaultAsync(u => u.SettingKey == "AgeRestriction" && u.UserID == user.UserID);
            if (ageSetting != null && int.TryParse(ageSetting.SettingValue, out int parsedAgeRestriction))
            {
                ageRestriction = parsedAgeRestriction;
            }

            //card style
            var cardStyleSetting = await movieDb.UserSettings
                .FirstOrDefaultAsync(u => u.SettingKey == "CardStyle" && u.UserID == user.UserID);
            var cardStyle = cardStyleSetting?.SettingValue ?? "standard";

            //can edit movies
            var canEditSetting = await movieDb.UserSettings
                .FirstOrDefaultAsync(u => u.SettingKey == "CanEditMovies" && u.UserID == user.UserID);
            var canEditMovies = canEditSetting?.SettingValue == "true";

            // enable pagination
            var enablePaginationSetting = await movieDb.UserSettings
                .FirstOrDefaultAsync(u => u.SettingKey == "EnablePagination" && u.UserID == user.UserID);
            bool enablePagination = false;
            if (enablePaginationSetting != null && bool.TryParse(enablePaginationSetting.SettingValue, out var parsedEnablePagination))
            {
                enablePagination = parsedEnablePagination;
            }

            // show boardgame expansions
            var showExpansionsSetting = await movieDb.UserSettings
                .FirstOrDefaultAsync(u => u.SettingKey == "ShowBoardgameExpansions" && u.UserID == user.UserID);
            bool showBoardgameExpansions = false;
            if (showExpansionsSetting != null && bool.TryParse(showExpansionsSetting.SettingValue, out var parsedShowExpansions))
            {
                showBoardgameExpansions = parsedShowExpansions;
            }

            // comic site access — SettingValue is the URL; null means no access
            var comicSiteAccessSetting = await movieDb.UserSettings
                .FirstOrDefaultAsync(u => u.SettingKey == "ComicSiteAccess" && u.UserID == user.UserID);
            var comicSiteAccess = comicSiteAccessSetting?.SettingValue;

            var hasPassword = user.PasswordHash != null;

            // Drives whether the SPA shows the admin tools. Mirrors the server gate: a config admin
            // who has a password (and so can become password-verified). A passwordless admin gets
            // false here, which is correct — they must set their password before they can administer.
            var isAdmin = IsAdminUsername(user.Username) && hasPassword;

            return new { user.Username, moviesSeen, moviesToWatch, ageRestriction, cardStyle, canEditMovies, enablePagination, showBoardgameExpansions, comicSiteAccess, hasPassword, isAdmin };
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


        [HttpPost("/API/GetMoviesFromNames")]
        public async Task<List<Movie>> GetMoviesFromNames([FromBody] string[] movieNames, bool forceBackupLogic = false)
        {
            List<Movie> movies = new List<Movie>();
            foreach (var givenTitle in movieNames)
            {
                Movie movie = null;
                string Name = ParseName(givenTitle);
                string Year = ParseYear(givenTitle);
                var imdbID = "";

                //First check if the input is already an IMDBID
                if (IsValidImdbId(givenTitle))
                    imdbID = givenTitle;

                //If we're forcing backup logic, perform backup IMDB search before anything else.
                if (forceBackupLogic)
                    imdbID = await googleSearchService.FindImdbIdFromMovieName($"{Name} ({Year})");

                //We don't have a valid IMDBId, Search.
                if (!IsValidImdbId(imdbID))
                {
                    //The input is not an IMDBID, check to see if we can retrieve the movie by Name and Year
                    movie = await omdb.GetMovieByNameAndYear(Name, Year);

                    //If that fails, try to find the IMDBID via other services
                    if (movie == null)
                    {
                        //  OMDB lookup-by-title is very inconsistent
                        //  Google search is best, but Google has been unreliable to search using HttpClient
                        //  ImdbApi seems reliable, but has been down at times
                        if (string.IsNullOrEmpty(imdbID))
                            imdbID = await imdbApiService.FindImdbIdFromMovieName(Name);
                        if (string.IsNullOrEmpty(imdbID))
                            imdbID = await googleSearchService.FindImdbIdFromMovieName(Name);
                    }
                }

                //If we have an IMDBID but not yet retrieved a movie, try to get the movie by the ID
                if (!string.IsNullOrEmpty(imdbID) && movie == null)
                    movie = await omdb.GetMovieByImdbId(imdbID);

                movie = await PrepMovieTitle(movie);

                movies.Add(movie);
            }
            return movies;
        }

        private async Task<Movie> PrepMovieTitle(Movie movie)
        {
            var trimmedTitle = movie.Title.Trim();
            if (trimmedTitle.StartsWith("The ", StringComparison.OrdinalIgnoreCase) &&
                        !trimmedTitle.EndsWith(", The", StringComparison.OrdinalIgnoreCase))
            {
                var withoutArticle = trimmedTitle.Substring(4).Trim(); // remove leading "The "

                // If removing the article leaves an empty string, keep original to avoid producing ", The"
                if (!string.IsNullOrEmpty(withoutArticle))
                {
                    movie.Title = $"{withoutArticle}, The";
                    movie.SimpleTitle = $"{withoutArticle}, The";
                }
            }

            //Check if we've already got a copy of this movie
            var checkMovie = await movieDb.Movies.AnyAsync(d => d.imdbID == movie.imdbID);

            if (checkMovie)
                movie.Title = "!DUPLICATE DETECTED! - " + movie.Title;

            return movie;
        }


        /*
         1. If givenName is null/whitespace -> return empty string.
         2. Trim surrounding whitespace.
         3. Find the first parenthetical group that contains a 4-digit year (supports ranges like (2012-2013) or (2012–2013)).
            - Use a regex that matches a parenthesis group with a 4-digit year.
            - Use Match to locate the first occurrence; this returns the index of that parenthesis.
         4. If a match is found:
            - Return the substring from start up to the match.Index, trimmed.
            - This covers inputs like "Swan, The (2023) [junk] 1080p" -> "Swan, The".
         5. If no such parenthetical year is found:
            - Fall back to the previous behavior of removing a trailing "(YYYY)" if it exists at the end.
            - Otherwise return the trimmed input unchanged.
         6. Ensure returned string has no trailing punctuation or stray characters (trim).
        */
        private string ParseName(string givenName)
        {
            if (string.IsNullOrWhiteSpace(givenName))
                return string.Empty;

            var trimmed = givenName.Trim();

            // Regex to find a parenthetical year (e.g. "(2023)", "(2012-2013)", support en-dash or hyphen)
            var yearParenRegex = new System.Text.RegularExpressions.Regex(@"\(\s*\d{4}(?:[–-]\d{4})?\s*\)");
            var match = yearParenRegex.Match(trimmed);

            if (match.Success)
            {
                // Return everything before the first year-parenthesis occurrence
                var titleBeforeYear = trimmed.Substring(0, match.Index).Trim();

                // Additional cleanup: remove trailing separators or stray characters
                titleBeforeYear = System.Text.RegularExpressions.Regex.Replace(titleBeforeYear, @"[\s\-\:\–\—]+$", "").Trim();

                return titleBeforeYear;
            }

            // Fallback: remove a trailing "(YYYY)" or "(YYYY-YYYY)" if present at the end
            var stripped = System.Text.RegularExpressions.Regex.Replace(trimmed, @"\s*\(\s*\d{4}(?:[–-]\d{4})?\s*\)\s*$", "");
            return stripped.Trim();
        }

        private string ParseYear(string givenTitle)
        {
            /*
             1. If givenTitle is null, empty, or whitespace -> return empty string.
             2. Trim the input to remove surrounding whitespace.
             3. Attempt a strict regex match for a trailing year in parentheses,
                capturing the first 4-digit year. Support ranges like "(2012-2013)" or "(2012–2013)".
                Regex: @"\(\s*(\d{4})(?:[–-]\d{4})?\s*\)\s*$"
             4. If that match succeeds, return the captured year (group 1).
             5. If not matched, attempt a looser search for a standalone 4-digit year
                (preferring 19xx or 20xx) anywhere in the string using: @"\b(19|20)\d{2}\b"
             6. If found, return that year; otherwise return empty string.
             */

            if (string.IsNullOrWhiteSpace(givenTitle))
                return string.Empty;

            var trimmed = givenTitle.Trim();

            // Strict trailing parentheses match e.g. "Title (2012)" or "Title (2012-2013)"
            var match = System.Text.RegularExpressions.Regex.Match(trimmed, @"\(\s*(\d{4})(?:[–-]\d{4})?\s*\)\s*$");
            if (match.Success && match.Groups.Count > 1)
            {
                return match.Groups[1].Value;
            }

            // Fallback: find any standalone 4-digit year (prefer 1900-2099)
            var looseMatch = System.Text.RegularExpressions.Regex.Match(trimmed, @"\b(19|20)\d{2}\b");
            if (looseMatch.Success)
            {
                return looseMatch.Value;
            }

            return string.Empty;
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
            IQueryable<Movie> movies = movieDb.Movies.Where(m => m.ReviewBatch == null);
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

            var movieList = await movies.OrderBy(m => m.SimpleTitle).ToListAsync();
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
        public async Task<IActionResult> GetMoviesByRating(int maxRatingId, int page = 1, int pageSize = 60)
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

            var effectiveMax = Math.Min(maxRatingId, ageRestriction);

            // Order at the DB (nulls last, then collation — digit-titles sort before letters) and
            // page there, so the infinite-scroll client's repeated page fetches don't each
            // re-materialize + re-sort the whole rating set.
            var query = movieDb.Movies
                .Where(m => m.ReviewBatch == null)
                .Where(m => movieDb.RatingMaps.Any(rm => rm.MovieRating == m.Rating && rm.MPARatingID == effectiveMax))
                .Select(ToCardDto)
                .OrderBy(c => c.SimpleTitle == null).ThenBy(c => c.SimpleTitle).ThenBy(c => c.id);

            return Ok(await PageCardsAsync(query, page, pageSize));
        }

        // Distinct genre names from the normalized Genre table, for the browse genre filter.
        [HttpGet("/API/GetGenres")]
        public async Task<IActionResult> GetGenres()
        {
            var genres = await movieDb.Genres
                .OrderBy(g => g.Name)
                .Select(g => g.Name)
                .ToListAsync();
            return Ok(genres);
        }

        [HttpGet("/API/GetMPARatings")]
        public async Task<IActionResult> GetMPARatings()
        {
            var ratingIds = await movieDb.RatingMaps
                .Select(rm => rm.MPARatingID)
                .Distinct()
                .OrderBy(id => id)
                .ToListAsync();

            var mpaNames = await movieDb.RatingMpas
                .ToDictionaryAsync(mpa => mpa.RatingID, mpa => mpa.MPAName);

            var result = ratingIds.Select(id => new
            {
                id,
                name = mpaNames.TryGetValue(id, out var n) && !string.IsNullOrEmpty(n) ? n : id.ToString()
            }).ToList();

            return Ok(result);
        }

        public class UserSettingRequest
        {
            public string SettingKey { get; set; }
            public string SettingValue { get; set; }
        }

        [HttpPost("/API/SetUserSetting")]
        public async Task<IActionResult> SetUserSetting([FromBody] UserSettingRequest request)
        {
            var currentUserId = GetCurrentUserId();
            if (!currentUserId.HasValue)
                return Unauthorized(new { Success = false, Message = "Not logged in." });

            if (string.IsNullOrEmpty(request?.SettingKey))
                return BadRequest(new { Success = false, Message = "SettingKey is required." });

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
            IQueryable<Movie> moviesQuery = movieDb.Movies.OrderBy(m => m.SimpleTitle);

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

            var allMovies = await moviesQuery.ToListAsync();

            // Fire all image loads in parallel. Task.WhenAll preserves result order
            // regardless of which file finishes first, so draw order is guaranteed.
            var imageTasks = allMovies.Select(m => imageRepo.GetImage(m.id, PosterImageVariant.Thumbnail));
            var allImageResults = await Task.WhenAll(imageTasks);

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
            outputMs.Position = 0;
            HttpContext.Response.ContentType = "image/png";
            await outputMs.CopyToAsync(HttpContext.Response.Body);
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
            if (string.IsNullOrWhiteSpace(imageUrl))
                return BadRequest(new { Message = "imageUrl is required", Success = false });

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

        [HttpGet("/API/SyncBoardgameFromBgg")]
        [HttpPost("/API/SyncBoardgameFromBgg")]
        public async Task<IActionResult> SyncBoardgameFromBgg(int bggThingId)
        {
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

        [HttpGet("/API/SyncBoardgameFromBggByTitle")]
        [HttpPost("/API/SyncBoardgameFromBggByTitle")]
        public async Task<IActionResult> SyncBoardgameFromBggByTitle(string title)
        {
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

        [HttpGet("/API/InsertBoardgameFromBgg")]
        [HttpPost("/API/InsertBoardgameFromBgg")]
        public async Task<IActionResult> InsertBoardgameFromBgg(int bggThingId)
        {
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

            // Movies only — series-typed rows now live in the Series table (added below).
            var raw = await movieDb.Movies
                .Where(m => m.TitleType != TitleType.TvSeries && m.TitleType != TitleType.TvMiniSeries
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
            var seriesRaw = await movieDb.Series
                .Where(s => s.ReviewBatch != null || gapSeriesIds.Contains(s.Id)
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
            var miscRaw = await movieDb.MiscVideos
                .Where(v => v.ReviewBatch != null)
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
                return Ok(new
                {
                    kind = "series",
                    episodeTotal = seps.Count,
                    episodeHave = seps.Count(e => e.PlayableId != null && sFilesByPlayable.ContainsKey(e.PlayableId.Value)),
                    seasons = sSeasons,
                    seriesExtras,
                    folderListing = ser.FolderListing,   // on-disk folder dump (from scan-series-folders)
                });
            }

            // ── movie ──
            var movie = await movieDb.Movies.FirstOrDefaultAsync(m => m.id == id);
            if (movie == null) return NotFound(new { Message = "Not found" });
            var files = movie.PlayableId == null
                ? new List<object>()
                : await movieDb.MediaFiles.Where(f => f.PlayableId == movie.PlayableId)
                    .OrderBy(f => f.Role).ThenBy(f => f.PartNumber).ThenBy(f => f.Id)
                    .Select(f => (object)new { path = f.Path, role = f.Role.ToString(), label = f.Label,
                        isPlayable = f.JellyfinItemId != null && f.MissingSinceUtc == null, missing = f.MissingSinceUtc != null })
                    .ToListAsync();
            return Ok(new { kind = "movie", files });
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
            // A series target is always an Extra (it has no episode of its own); an episode target honors the role.
            var role = (toSeries || string.Equals(req.Role, "Extra", StringComparison.OrdinalIgnoreCase))
                ? MovieFileRole.Extra : MovieFileRole.Primary;

            int playableId;
            if (toSeries)
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

        public class RemoveFileRequest { public int MediaFileId { get; set; } }

        [HttpPost("/API/Admin/IngestReview/RemoveFile")]
        public async Task<IActionResult> IngestReviewRemoveFile([FromBody] RemoveFileRequest req)
        {
            if (!await IsCurrentUserEditor()) return Forbid();
            var f = await movieDb.MediaFiles.FirstOrDefaultAsync(x => x.Id == req.MediaFileId);
            if (f == null) return NotFound(new { Message = "File not found" });
            movieDb.MediaFiles.Remove(f);
            await movieDb.SaveChangesAsync();
            return Ok(new { Success = true });
        }

        // Movie ids in Ids, series ids in SeriesIds, misc-video ids in MiscIds (separate id sequences — see Kind).
        public class IngestReviewIdsRequest { public List<int> Ids { get; set; } = new(); public List<int> SeriesIds { get; set; } = new(); public List<int> MiscIds { get; set; } = new(); }

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
                m.ReviewBatch = null; m.ReviewProvenance = null; m.ReviewConfidence = null;
            }

            var seriesRows = req.SeriesIds.Count == 0 ? new List<Series>()
                : await movieDb.Series.Where(s => req.SeriesIds.Contains(s.Id) && s.ReviewBatch != null).ToListAsync();
            foreach (var s in seriesRows)
            {
                if (s.ImdbReleaseDate.HasValue && (s.ReleaseDate == null || s.ReleaseDate.Value.Year != s.ImdbReleaseDate.Value.Year))
                { s.ReleaseDate = s.ImdbReleaseDate; s.StartYear = s.ImdbReleaseDate.Value.Year; }
                s.ReviewBatch = null; s.ReviewProvenance = null; s.ReviewConfidence = null;
            }

            var miscRows = req.MiscIds.Count == 0 ? new List<MiscVideo>()
                : await movieDb.MiscVideos.Where(v => req.MiscIds.Contains(v.Id) && v.ReviewBatch != null).ToListAsync();
            foreach (var v in miscRows) { v.ReviewBatch = null; v.ReviewProvenance = null; }

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

            return Ok(new { approved = rows.Count + seriesRows.Count + miscRows.Count });
        }

        // Fetch posters for already-approved movies/series that have none (e.g. the auto-approved series).
        // Runs in the web app so it writes to the live image store — the CLI backfill can't from a dev box.
        // Editor-gated; idempotent (EnsurePosterAsync no-ops where a poster exists).
        [HttpPost("/API/Admin/IngestReview/BackfillPosters")]
        public async Task<IActionResult> IngestReviewBackfillPosters()
        {
            if (!await IsCurrentUserEditor()) return Forbid();
            var series = await movieDb.Series.Where(s => s.ReviewBatch == null && s.imdbID != null && s.PosterDetails == null)
                .Select(s => new { s.Id, s.imdbID }).ToListAsync();
            var movies = await movieDb.Movies.Where(m => m.ReviewBatch == null && m.imdbID != null && m.PosterDetails == null
                    && m.TitleType != TitleType.TvSeries && m.TitleType != TitleType.TvMiniSeries)
                .Select(m => new { m.id, m.imdbID }).ToListAsync();
            var targets = series.Select(s => (id: s.Id, tt: s.imdbID, isSeries: true))
                .Concat(movies.Select(m => (id: m.id, tt: m.imdbID, isSeries: false))).ToList();

            int got = 0;
            if (targets.Count > 0)
                await Parallel.ForEachAsync(targets, new ParallelOptions { MaxDegreeOfParallelism = 6 },
                    async (t, _) => { if (await posterFetchService.EnsurePosterAsync(t.id, t.tt, t.isSeries)) System.Threading.Interlocked.Increment(ref got); });

            return Ok(new { attempted = targets.Count, got });
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
            movieDb.Movies.RemoveRange(rows);

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

                await CopyPosterImagesAsync(m.id, s.Id);
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

                await CopyPosterImagesAsync(s.Id, m.id);
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
        private async Task CopyPosterImagesAsync(int fromId, int toId)
        {
            if (fromId == toId) return;
            foreach (var variant in new[] { PosterImageVariant.Main, PosterImageVariant.Thumbnail })
            {
                try
                {
                    var bytes = await imageRepo.GetImage(fromId, variant);
                    if (bytes != null && bytes.Length > 0) await imageRepo.SaveImage(toId, variant, bytes);
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
            movieDb.Movies.Remove(m);
        }

        // Delete a series subtree: episodes + their Playables/files, then the Series row (which cascades
        // its genre/credit/plot/poster). Mirrors the Reject path. Used when the title moves to movie/misc.
        private async Task DeleteSeriesSubtreeAsync(Series s)
        {
            var eps = await movieDb.Episodes.Where(e => e.SeriesId == s.Id).ToListAsync();
            var epPids = eps.Where(e => e.PlayableId != null).Select(e => e.PlayableId!.Value).ToList();
            movieDb.MediaFiles.RemoveRange(await movieDb.MediaFiles.Where(f => epPids.Contains(f.PlayableId)).ToListAsync());
            movieDb.Episodes.RemoveRange(eps);
            movieDb.Playables.RemoveRange(await movieDb.Playables.Where(p => epPids.Contains(p.Id)).ToListAsync());
            movieDb.Series.Remove(s);
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
