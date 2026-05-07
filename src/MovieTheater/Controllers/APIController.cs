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
using System.Security.Claims;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.OData.Query;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using MovieTheater.Db;
using MovieTheater.Models;
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

        public APIController(MovieDb movieDb, TmdbApi tmdb, OmdbApi omdb, ImdbApiClient imdb, HttpClient httpClient, IPosterImageRepository imageRepo,
            IBoardgameImageRepository boardgameImageRepo, ImageShrinkService shrinkService, GoogleSearchService googleSearchService, IMDBApiService imdbApiService,
            BoardGameGeekApi boardGameGeekApi, PosterMosaicService posterMosaicService,
            BoardgameRulesService boardgameRulesService, BoardgamePdfRepository boardgamePdfRepository,
            IConfiguration configuration, YouTubeService youTubeService, IMemoryCache memoryCache,
            BoardgameSimilarityService boardgameSimilarityService)
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

        private int GetMPARatingFromMovieRating(string movieRating)
        {
            if (string.IsNullOrWhiteSpace(movieRating))
            {
                return 0;
            }

            var trimmedRating = movieRating.Trim();

            var ratingMap = movieDb.RatingMaps
                                  .FirstOrDefault(rm => rm.MovieRating == trimmedRating);

            return ratingMap.MPARatingID;
        }

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
            var rating = GetMPARatingFromMovieRating(movie.Rating);
            if (movie != null && (rating <= ageRestriction))
            {
                return Ok(new { Success = true, data = movie });
            }
            return BadRequest(new { Success = false, Message = "Movie ID not found" });
        }

        [HttpGet("/API/GetTotalMovieCount")]
        public async Task<IActionResult> GetTotalMovieCount()
        {
            try
            {
                var count = await movieDb.Movies.CountAsync();
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

        [HttpPost("/API/GetMoviesByIds")]
        public async Task<IActionResult> GetMoviesByIds([FromBody] List<int> ids)
        {
            if (ids == null || ids.Count == 0)
                return Ok(new List<Movie>());

            var baseQuery = await GetBaseMovieQuery();
            var movies = await baseQuery
                .Where(m => ids.Contains(m.id))
                .OrderBy(m => m.SimpleTitle)
                .ToListAsync();

            return Ok(movies);
        }

        private async Task<IQueryable<Movie>> GetBaseMovieQuery()
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

            return movieDb.Movies
                .Include(m => m.PosterDetails)
                .Where(m => !movieDb.RatingMaps.Any(rm => rm.MovieRating == m.Rating && rm.MPARatingID > ageRestriction));
        }

        [HttpGet("/API/GetRandomMovies")]
        public async Task<IActionResult> GetRandomMovies(int take = 50)
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

            IQueryable<Movie> movies = movieDb.Movies.Include(m => m.PosterDetails).Where(m => !m.RemoveFromRandom);
            movies = movies.Where(m => !movieDb.RatingMaps.Any(rm => rm.MovieRating == m.Rating && rm.MPARatingID > ageRestriction));
            var result = await movies.OrderBy(m => Guid.NewGuid()).Take(take).ToListAsync();
            return Ok(result);
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

            movieDb.Movies.Add(movie);
            try
            {
                movieDb.SaveChanges();
            }
            catch
            {
                return Conflict(new { Message = "Save failed", Success = false });
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
            existing.RemoveFromRandom = dto.RemoveFromRandom;

            try
            {
                await movieDb.SaveChangesAsync();
            }
            catch (Exception ex)
            {
                return Conflict(new { Message = $"Save failed: {ex.InnerException?.Message ?? ex.Message}", Success = false });
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

        [HttpPost("/API/Login")]
        public async Task<IActionResult> Login(string username)
        {
            String givenUser = username.Trim();

            if (string.IsNullOrEmpty(givenUser))
            {
                return NotFound();
            }

            var user = await movieDb.Users.SingleOrDefaultAsync(d => d.Username == username);

            if (user == null)
            {
                user = new User()
                {
                    Username = username
                };

                await movieDb.Users.AddAsync(user);
                await movieDb.SaveChangesAsync();
            }

            var claims = new List<Claim>
            {
                new Claim(ClaimTypes.NameIdentifier, user.UserID.ToString()),
                new Claim(ClaimTypes.Name, user.Username)
            };

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

            //watched
            var moviesSeen = await movieDb.Viewings.Where(d => d.UserID == user.UserID && d.ViewingType == "Seen").Select(d => d.MovieID).ToListAsync();

            //want to watch
            var moviesToWatch = await movieDb.Viewings.Where(d => d.UserID == user.UserID && d.ViewingType == "WantToWatch").Select(d => d.MovieID).ToListAsync();

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

            return Json(new { user.Username, moviesSeen, moviesToWatch, ageRestriction, cardStyle, canEditMovies, enablePagination, showBoardgameExpansions });
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

            var user = await movieDb.Users.FirstOrDefaultAsync(u => u.Username == viewingState.Username);
            if (user == null)
            {
                return BadRequest(new { Success = false, Message = "No User Found." });
            }

            var movie = await movieDb.Movies.FirstOrDefaultAsync(m => m.id == viewingState.MovieID);
            if (movie == null)
            {
                return BadRequest(new { Success = false, Message = "Invalid Movie ID." });
            }

            var action = viewingState.Action == ViewingType.SetWatched ? "Seen" : "WantToWatch";
            var existingViewing = await movieDb.Viewings.FirstOrDefaultAsync(e => e.UserID == user.UserID && e.MovieID == movie.id && e.ViewingType == action);
            bool shouldCreateNew = existingViewing == null && viewingState.SetActive;
            bool shouldDeleteExisting = existingViewing != null && !viewingState.SetActive;

            if (shouldCreateNew)
            {
                var newViewing = new Viewing
                {
                    MovieID = movie.id,
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
            var userList = movieDb.Users.Select(d => d.Username).ToList();
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
            IQueryable<Movie> movies = movieDb.Movies;
            if (search == null)
                return BadRequest(new { message = "No Search Data Provided" });

            if (!String.IsNullOrEmpty(search.Type))
                switch (search.Type)
                {
                    case "startsWith":
                        if (search.StartsWith == "#")
                        {
                            List<char> digits = new List<char>() { '0', '1', '2', '3', '4', '5', '6', '7', '8', '9' };
                            movies = movies.Where(m => digits.Contains(m.SimpleTitle[0]));
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
                            movies = movies.Where(m => m.Actors.Contains(search.Actor));
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
        public async Task<IActionResult> GetMoviesByRating(int maxRatingId, int page = 1, int pageSize = 50)
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

            var query = movieDb.Movies
                .Where(m => movieDb.RatingMaps.Any(rm => rm.MovieRating == m.Rating && rm.MPARatingID == effectiveMax));

            var moviesList = await query.ToListAsync();
            var sorted = moviesList
                .OrderBy(m => string.IsNullOrEmpty(m.SimpleTitle) || !char.IsDigit(m.SimpleTitle[0]))
                .ThenBy(m => m.SimpleTitle)
                .ToList();
            var totalCount = sorted.Count;

            if (pageSize <= 0)
            {
                return Ok(new { movies = sorted, totalCount, page = 1, pageSize = totalCount });
            }

            if (page < 1) page = 1;
            var skip = (page - 1) * pageSize;
            var paged = sorted.Skip(skip).Take(pageSize).ToList();
            return Ok(new { movies = paged, totalCount, page, pageSize });
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
                moviesQuery = moviesQuery.Where(m => m.Actors.Contains(actor));

            if (!string.IsNullOrEmpty(text))
                moviesQuery = moviesQuery.Where(m => m.SimpleTitle.Contains(text) || m.Title.Contains(text));

            if (!string.IsNullOrEmpty(startsWith))
            {
                if (startsWith == "#")
                {
                    var digits = new List<char> { '0', '1', '2', '3', '4', '5', '6', '7', '8', '9' };
                    moviesQuery = moviesQuery.Where(m => digits.Contains(m.SimpleTitle[0]));
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

            byte[]? mainBytes = null;
            bool hasMain = await boardgameImageRepo.HasImage(boardgame.id, BoardgameImageVariant.Main);
            bool hasThumb = await boardgameImageRepo.HasImage(boardgame.id, BoardgameImageVariant.Thumbnail);
            bool savedAny = false;

            if (force || !hasMain)
            {
                if (!string.IsNullOrWhiteSpace(imageUrl))
                {
                    var imageResponse = await httpClient.GetAsync(imageUrl);
                    imageResponse.EnsureSuccessStatusCode();
                    mainBytes = await imageResponse.Content.ReadAsByteArrayAsync();
                    await boardgameImageRepo.SaveImage(boardgame.id, BoardgameImageVariant.Main, mainBytes);
                    savedAny = true;
                }
            }

            if (force || !hasThumb)
            {
                byte[]? thumbBytes = null;

                if (!string.IsNullOrWhiteSpace(thumbnailUrl))
                {
                    var thumbResponse = await httpClient.GetAsync(thumbnailUrl);
                    if (thumbResponse.IsSuccessStatusCode)
                    {
                        thumbBytes = await thumbResponse.Content.ReadAsByteArrayAsync();
                      }
                }

                if (thumbBytes == null)
                {
                    mainBytes ??= await boardgameImageRepo.GetImage(boardgame.id, BoardgameImageVariant.Main);
                    if (mainBytes != null)
                    {
                        thumbBytes = BuildBoardgameThumbnail(mainBytes);
                    }
                }

                if (thumbBytes != null)
                {
                    await boardgameImageRepo.SaveImage(boardgame.id, BoardgameImageVariant.Thumbnail, thumbBytes);
                    savedAny = true;
                }
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

                image.Mutate(x => x.Resize(finalWidth, finalHeight, KnownResamplers.Lanczos2));
                image.Mutate(x => x.GaussianSharpen(.5f));
                image.Mutate(x => x.GaussianSharpen(.5f));
                image.Mutate(x => x.GaussianSharpen(.4f));
                image.Mutate(x => x.GaussianSharpen(.3f));
                image.Mutate(x => x.GaussianSharpen(.2f));

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
