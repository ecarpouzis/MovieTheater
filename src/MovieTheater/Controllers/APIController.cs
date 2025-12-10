using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Security.Claims;
using System.Threading.Tasks;
using IMDbApiLib.Models;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using MovieTheater.Db;
using MovieTheater.Models;
using MovieTheater.Services;
using MovieTheater.Services.ImdbApi;
using MovieTheater.Services.Poster;
using MovieTheater.Services.Tmdb;
using MovieTheater.Services.Omdb;
using HotChocolate;

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

        [HttpGet("/API/GetMovie")]
        public async Task<IActionResult> GetMovie(int id)
        {
            var movie = await movieDb.Movies.SingleOrDefaultAsync(m => m.id == id);
            if (movie != null)
            {
                return Ok(new { Success = true, data = movie });
            }
            return BadRequest(new { Success = false, Message = "Movie ID not found" });
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
                return Ok(new { Message = "Save failed", Success = false });
            }

            if (movie.PosterLink.Trim() != "")
            {
                var result = await httpClient.GetAsync(movie.PosterLink);
                var content = await result.Content.ReadAsByteArrayAsync();
                await imageRepo.SaveImage(movie.id, PosterImageVariant.Main, content);
                await shrinkService.EnsurePosterThumnailExists(movie.id);
            }


            return Ok();
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


            //watched
            var moviesSeen = await movieDb.Viewings.Where(d => d.UserID == user.UserID && d.ViewingType == "Seen").Select(d => d.MovieID).ToListAsync();

            //want to watch
            var moviesToWatch = await movieDb.Viewings.Where(d => d.UserID == user.UserID && d.ViewingType == "WantToWatch").Select(d => d.MovieID).ToListAsync();

            return Json(new { user.Username, moviesSeen, moviesToWatch });
        }

        [HttpGet("/API/ImdbApiLookupImdbID")]
        public async Task<Movie> ImdbApiLookupImdbID(string imdbID)
        {
            return await imdb.ImdbApiLookupImdbID(imdbID);
        }

        [HttpPost("/API/GetMoviesFromNames")]
        public async Task<List<Movie>> GetMoviesFromNames([FromBody]string[] movieNames)
        {
            List<Movie> movies = new List<Movie>();
            foreach(var movieName in movieNames)
            {

                //First check if the input is already an IMDBID
                var imdbID = movieName;
                if (!IsValidImdbId(movieName))
                {
                    //If not, start attempting to find the ImdbID
                    //  OMDB lookup-by-title is very inconsistent
                    //  Google search is best, but Google has been unreliable to search using HttpClient
                    //  ImdbApi seems reliable, but has been down at times
                    imdbID = await imdbApiService.FindImdbIdFromMovieName(movieName);
                }
                if(string.IsNullOrEmpty(imdbID))
                {
                    imdbID = await googleSearchService.FindImdbIdFromMovieName(movieName);
                }
                var movie = await omdb.GetMovie(imdbID);

                var checkMovie = await movieDb.Movies.AnyAsync(d => d.imdbID == movie.imdbID);

                if (checkMovie)
                {
                    movie.Title = "!DUPLICATE DETECTED! - "+movie.Title;
                }

                movies.Add(movie);
            }
            return movies;
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
            return await omdb.GetMovie(imdbID);
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
    }
}
