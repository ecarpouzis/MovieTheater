using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Security.Claims;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.OData.Query;
using Microsoft.EntityFrameworkCore;
using MovieTheater.Db;
using MovieTheater.Models;
using MovieTheater.Services;
using MovieTheater.Services.ImdbApi;
using MovieTheater.Services.Poster;
using MovieTheater.Services.Tmdb;
using MovieTheater.Services.Omdb;
using MovieTheater.Services.Google;

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
        private readonly ImageShrinkService shrinkService;
        private readonly GoogleSearchService googleSearchService;
        private readonly IMDBApiService imdbApiService;

        public APIController(MovieDb movieDb, TmdbApi tmdb, OmdbApi omdb, ImdbApiClient imdb, HttpClient httpClient, IPosterImageRepository imageRepo,
            ImageShrinkService shrinkService, GoogleSearchService googleSearchService, IMDBApiService imdbApiService)
        {
            this.movieDb = movieDb;
            this.tmdb = tmdb;
            this.omdb = omdb;
            this.imdb = imdb;
            this.httpClient = httpClient;
            this.imageRepo = imageRepo;
            this.shrinkService = shrinkService;
            this.googleSearchService = googleSearchService;
            this.imdbApiService = imdbApiService;
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
            
            var movie = await movieDb.Movies.SingleOrDefaultAsync(m => m.id == id);
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

            return movieDb.Movies.Where(m =>
                !movieDb.RatingMaps.Any(rm => rm.MovieRating == m.Rating && rm.MPARatingID > ageRestriction));
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

            IQueryable<Movie> movies = movieDb.Movies.Where(m => !m.RemoveFromRandom);
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

            if (movie.PosterLink.Trim() != "")
            {
                var result = await httpClient.GetAsync(movie.PosterLink);
                var content = await result.Content.ReadAsByteArrayAsync();
                await imageRepo.SaveImage(movie.id, PosterImageVariant.Main, content);
                await shrinkService.EnsurePosterThumnailExists(movie.id);
            }

            return Ok(new { Message = "Movie saved", Success = true });
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

            return Json(new { user.Username, moviesSeen, moviesToWatch, ageRestriction });
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
        public async Task<List<Movie>> GetMoviesFromNames([FromBody]string[] movieNames, bool forceBackupLogic = false)
        {
            List<Movie> movies = new List<Movie>();
            foreach(var givenTitle in movieNames)
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
        public async Task<IActionResult> SetViewingState([FromBody]ViewingState viewingState)
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
           
            if(!String.IsNullOrEmpty(search.Type))
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
                        if(!String.IsNullOrEmpty(search.Text))
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
                    await movieDb.UserSettings.AddAsync(new UserSettings
                    {
                        UserID = currentUserId.Value,
                        SettingKey = request.SettingKey,
                        SettingValue = request.SettingValue,
                    });
                }
                await movieDb.SaveChangesAsync();
            }

            return Ok(new { Success = true });
        }
    }
}
