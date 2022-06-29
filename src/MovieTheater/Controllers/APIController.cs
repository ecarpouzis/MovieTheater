using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using MovieTheater.Db;
using MovieTheater.Models;
using MovieTheater.Services;

namespace MovieTheater.Controllers
{
    public class APIController : Controller
    {

        private readonly MovieDb movieDb;

        public APIController(MovieDb movieDb)
        {
            this.movieDb = movieDb;
        }

        [HttpGet("/API/GetMovie")]
        public async Task<IActionResult> GetMovie(int id)
        {
            var movie = await movieDb.Movies.SingleOrDefaultAsync(m => m.id == id);
            if(movie != null)
            {
                return Ok( new { Success=true, data=movie });
            }
            return BadRequest(new { Success=false, Message="Movie ID not found" });
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

        [HttpPost("/API/SetViewingState")]
        public async Task<IActionResult> SetViewingState([FromBody]ViewingState viewingState)
        {
            if (viewingState == null)
            {
                return BadRequest(new { Success = false, Message = "No User Movie Data Provided."});
            }
            
            var user = await movieDb.Users.FirstOrDefaultAsync(u => u.Username == viewingState.Username);
            if(user == null)
            {
                return BadRequest(new { Success = false, Message = "No User Found." });
            }

            var movie = await movieDb.Movies.FirstOrDefaultAsync(m=>m.id == viewingState.MovieID);
            if(movie == null)
            {
                return BadRequest(new { Success = false, Message = "Invalid Movie ID." });
            }

            var action = viewingState.Action == ViewingState.ViewingType.SetWatched ? "s" : "w";
            var existingViewing = await movieDb.Viewings.FirstOrDefaultAsync(e => e.UserID == user.UserID && e.MovieID == movie.id && e.ViewingType == action);
            bool shouldCreateNew = existingViewing == null && viewingState.SetActive;
            bool shouldDeleteExisting = existingViewing != null && !viewingState.SetActive;

            if(shouldCreateNew)
            {
                    var newViewing = new Viewing
                    {
                        MovieID = movie.id,
                        UserID = user.UserID,
                        ViewingType = action,
                    };
                await movieDb.Viewings.AddAsync(newViewing);
            }
            if(shouldDeleteExisting)
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

        public class search{
            public string type;
            public int? count;
            public string startsWith;
            public string actor;
            public string releaseYear;
            public string uploadDate;
        }

        [HttpPost("/API/API_Movies")]
        public IActionResult API_Movies([FromBody]search search = null)
        {
            IQueryable<Movie> movies = movieDb.Movies;
            if(search == null)
            {
                return BadRequest(new { message="No Search Data Provided" });
            }
            if (!String.IsNullOrEmpty(search.type))
            {
                switch (search.type)
                {
                    case "startsWith":
                        if (!String.IsNullOrEmpty(search.startsWith))
                        {
                            if (search.startsWith == "#")
                            {
                                List<char> digits = new List<char>() { '0', '1', '2', '3', '4', '5', '6', '7', '8', '9' };
                                movies = movies.Where(m => digits.Contains(m.Title[0]));
                            }
                            else
                            {
                                movies = movies.Where(m => m.Title.StartsWith(search.startsWith));
                            }
                        }
                        break;
                    default:
                        break;
                }

            }

            if (search.count.HasValue)
            {
                movies = movies.OrderBy(elem => Guid.NewGuid()).Take(search.count.Value);
            }

            return Json(movies);
        }
    }
}
