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

namespace MovieTheater.Controllers
{
    public class APIController : Controller
    {

        private readonly MovieDb movieDb;
        private readonly TmdbApi tmdb;
        private readonly ImdbApiClient imdb;
        private readonly HttpClient httpClient;
        private readonly IPosterImageRepository imageRepo;
        private readonly ImageShrinkService shrinkService;

        public APIController(MovieDb movieDb, TmdbApi tmdb, ImdbApiClient imdb, HttpClient httpClient, IPosterImageRepository imageRepo, ImageShrinkService shrinkService)
        {
            this.movieDb = movieDb;
            this.tmdb = tmdb;
            this.imdb = imdb;
            this.httpClient = httpClient;
            this.imageRepo = imageRepo;
            this.shrinkService = shrinkService;
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
        public async Task<IActionResult> InsertMovie(string title, string rated, string released, string runtime, string genre, string director,
            string writer, string actors, string plot, string poster, string imdbrating, string imdbid, string tomatorating)
        {
            var checkMovie = movieDb.Movies.SingleOrDefault(d => d.imdbID == imdbid);

            if (checkMovie == null)
            {
                Movie newMovie = new Movie();
                byte[] potentialPoster = new byte[0];

                if (title.Trim() != "")
                {
                    newMovie.Title = title.Trim();
                    newMovie.SimpleTitle = title.Trim();
                }

                if (rated.Trim() != "")
                {
                    newMovie.Rating = rated.Trim();
                }

                if (released.Trim() != "")
                {
                    newMovie.ReleaseDate = Convert.ToDateTime(released);
                }

                if (runtime.Trim() != "")
                {
                    newMovie.Runtime = runtime;
                }

                if (genre.Trim() != "")
                {
                    newMovie.Genre = genre;
                }

                if (director.Trim() != "")
                {
                    newMovie.Director = director;
                }
                else
                {
                    newMovie.Director = "";
                }

                if (writer.Trim() != "")
                {
                    newMovie.Writer = writer;
                }
                else
                {
                    newMovie.Writer = "";
                }

                if (poster.Trim() != "")
                {
                    newMovie.PosterLink = poster;
                }

                if (actors.Trim() != "")
                {
                    newMovie.Actors = actors;
                }
                else
                {
                    newMovie.Actors = "";
                }

                if (plot.Trim() != "")
                {
                    newMovie.Plot = plot;
                }
                if (imdbrating.Trim() != "")
                {
                    newMovie.imdbRating = Decimal.Parse(imdbrating);
                }

                if (imdbid.Trim() != "")
                {
                    newMovie.imdbID = imdbid;
                }

                if (tomatorating != null)
                {
                    if (tomatorating.Trim() != "" && tomatorating.Trim() != "N/A")
                    {
                        newMovie.tomatoRating = Convert.ToInt32(tomatorating);
                    }
                }
                newMovie.UploadedDate = DateTime.Now;

                movieDb.Movies.Add(newMovie);
                movieDb.SaveChanges();

                if (poster.Trim() != "")
                {
                    var result = await httpClient.GetAsync(poster);
                    var content = await result.Content.ReadAsByteArrayAsync();
                    await imageRepo.SaveImage(newMovie.id, PosterImageVariant.Main, content);
                    await shrinkService.EnsurePosterThumnailExists(newMovie.id);
                }
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
            var moviesSeen = await movieDb.Viewings.Where(d => d.UserID == user.UserID && d.ViewingType == "w").Select(d => d.MovieID).ToListAsync();

            //want to watch
            var moviesToWatch = await movieDb.Viewings.Where(d => d.UserID == user.UserID && d.ViewingType == "s").Select(d => d.MovieID).ToListAsync();

            return Json(new { user.Username, moviesSeen, moviesToWatch });
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

            var action = viewingState.Action == ViewingState.ViewingType.SetWatched ? "s" : "w";
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
            public string Actor { get; set; }
            public string ReleaseYear { get; set; }
            public string UploadDate { get; set; }
        }

        [HttpPost("/API/API_Movies")]
        public IActionResult API_Movies([FromBody] search search = null)
        {
            IQueryable<Movie> movies = movieDb.Movies;
            if (search == null)
            {
                return BadRequest(new { message = "No Search Data Provided" });
            }
            if (!String.IsNullOrEmpty(search.Type))
            {
                switch (search.Type)
                {
                    case "startsWith":
                        if (!String.IsNullOrEmpty(search.StartsWith))
                        {
                            if (search.StartsWith == "#")
                            {
                                List<char> digits = new List<char>() { '0', '1', '2', '3', '4', '5', '6', '7', '8', '9' };
                                movies = movies.Where(m => digits.Contains(m.Title[0]));
                            }
                            else
                            {
                                movies = movies.Where(m => m.Title.StartsWith(search.StartsWith));
                            }
                        }
                        break;
                    default:
                        break;
                }

            }

            if (search.Count.HasValue)
            {
                movies = movies.OrderBy(elem => Guid.NewGuid()).Take(search.Count.Value);
            }

            return Json(movies);
        }
    }
}
