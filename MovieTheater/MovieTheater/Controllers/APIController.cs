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
using MovieTheater.Services;

namespace MovieTheater.Controllers
{
    public class APIController : Controller
    {

        private readonly MovieDb movieDb;
        private readonly IImageHandler imageHandler;

        public APIController(MovieDb movieDb, IImageHandler imageHandler)
        {
            this.movieDb = movieDb;
            this.imageHandler = imageHandler;
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


        //[HttpGet("/API/Logout")]
        //public async Task<IActionResult> API_Logout()
        //{
        //    await HttpContext.SignOutAsync();
        //    return Ok();
        //}

        [HttpGet("/API/API_UserList")]
        public IActionResult API_UserList()
        {
            var userList = movieDb.Users.Select(d => d.Username).ToList();
            return Json(userList);
        }

        //[HttpGet("/API/CountWatched")]
        //public async Task<IActionResult> API_CountWatched()
        //{
        //    if (User.Identity.IsAuthenticated)
        //    {
        //        var userID = Int32.Parse(User.Claims.Single(d => d.Type == "UserID").Value);

        //        var count = await movieDb.Viewings.CountAsync(d => d.UserID == userID && d.ViewingType == "w");
        //        return new JsonResult(new { count = count });
        //    }
        //    else
        //    {
        //        return new JsonResult(new { count = 0 });
        //    }
        //}

        //[HttpGet("/API/CountWatchlist")]
        //public async Task<IActionResult> API_CountWatchlist()
        //{
        //    if (User.Identity.IsAuthenticated)
        //    {
        //        var userID = Int32.Parse(User.Claims.Single(d => d.Type == "UserID").Value);

        //        var count = await movieDb.Viewings.CountAsync(d => d.UserID == userID && d.ViewingType == "s");
        //        return new JsonResult(new { count = count });
        //    }
        //    else
        //    {
        //        return new JsonResult(new { count = 0 });
        //    }
        //}

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
