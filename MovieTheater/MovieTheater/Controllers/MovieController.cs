using HtmlAgilityPack;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using MovieTheater.Db;
using MovieTheater.Services;
using MovieTheater.ViewModels;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Claims;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Web;

namespace MovieTheater.Controllers
{
    public class MovieController : Controller
    {
        /* Viewing information:
         * 'w' = watched
         * 's' = suggested
         * 'r' = rating
         *
         * To Do:
         * 
         * Suggested comments text styling
         * Insert Movie button
         * Suggest Aquire button
         * Revised styles for watched and suggested movies
         * Insert error movies
         * 
         * */

        private readonly MovieDb movieDb;
        private readonly IImageHandler imageHandler;

        private IActionResult JsonSuccess => Json(new { success = true });

        public MovieController(MovieDb movieDb, IImageHandler imageHandler)
        {
            this.movieDb = movieDb;
            this.imageHandler = imageHandler;
        }

        [HttpGet("/")]
        public IActionResult Home()
        {
            Random rnd = new Random();

            /*
             * If I can figure out how to AJAX this,  
             * Math.floor($("#centerContent").outerWidth()/288)
             * gives the number of movies that can fit on any one row.
             * 
             */
            int movieCount = movieDb.Movies.Count();
            var list = Enumerable.Range(1, movieCount).OrderBy(i => rnd.Next()).ToList<int>();
            list = list.Take(8).ToList<int>();

            var items = movieDb.Movies.Where(d => list.Contains(d.id));

            return View(items);
        }


        [HttpGet("/Movie/{id}")]
        public IActionResult Movie(int id)
        {
            var movie = movieDb.Movies.SingleOrDefault(d => d.id == id);

            if (movie == null)
            {
                return NotFound();
            }

            return View(movie);
        }


        [HttpGet("/Movie/ShrinkAllPosters")]
        public IActionResult ShrinkOldPosters()
        {
            int[] movieIDs = movieDb.Movies.Select(m => m.id).ToArray();
            foreach (int id in movieIDs)
            {
                imageHandler.CreateShrunkImageFile(id);
            }
            
            return Ok();
        }

        [HttpGet("/Browse")]
        public async Task<IActionResult> Browse()
        {
            int? userId;

            if (User.Identity.IsAuthenticated)
            {
                userId = Int32.Parse(User.Claims.Single(d => d.Type == "UserID").Value);
            }
            else
            {
                userId = null;
            }

            IQueryable<Movie> movies = movieDb.Movies;

            string sort = Request.Query["sort"];

            if (sort == "Letter")
            {
                string givenLetter = Request.Query["Letter"];
                if (givenLetter == "1")
                {
                    string allLetters = "abcdefghijklmnopqrstuvwxyz";
                    movies = movieDb.Movies.Where(m => !allLetters.Contains(m.SimpleTitle.Substring(0, 1)));
                }
                else
                {
                    movies = movieDb.Movies.Where(m => m.SimpleTitle.Substring(0, 1) == givenLetter);
                }
            }

            if (sort == "Title")
            {
                String givenTitle = Request.Query["Title"];
                movies = movieDb.Movies.Where(m => m.SimpleTitle.Contains(givenTitle) || m.Title.Contains(givenTitle));
            }

            if (sort == "Actor")
            {
                string givenActor = Request.Query["Actor"];
                movies = movieDb.Movies.Where(m => m.Actors.Contains(givenActor));
            }

            if (sort == "Watched")
            {
                if (User.Identity.IsAuthenticated)
                {
                    movies = movieDb.Viewings
                        .Include(d => d.Movie)
                        .Where(d => d.UserID == userId && d.ViewingType == "w")
                        .Select(d => d.Movie)
                        .Distinct();
                }
                else
                {
                    movies = movieDb.Movies.Take(0);
                }
            }

            if (sort == "Watchlist")
            {
                if (User.Identity.IsAuthenticated)
                {
                    movies = movieDb.Viewings
                        .Include(d => d.Movie)
                        .Where(d => d.UserID == userId && d.ViewingType == "s")
                        .Select(d => d.Movie)
                        .Distinct();
                }
                else
                {
                    movies = movieDb.Movies.Take(0);
                }
            }

            //At this point, all sorts have been alphabetical
            if (movies != null)
            {
                movies = movies.OrderBy(m => m.SimpleTitle);
            }

            //All future sorts are non-alphabetical

            if (sort == "Uploaded")
            {
                DateTime minDate = new DateTime(2000, 1, 1);

                DateTime dateFrom = Convert.ToDateTime(Request.Query["dateFrom"]);

                if (dateFrom < minDate)
                {
                    dateFrom = minDate;
                }

                movies = movieDb.Movies.Where(d => d.UploadedDate > dateFrom);
            }


            if (sort == "Rank")
            {
                int top = Convert.ToInt32(Request.Query["Top"]);
                decimal toplimit = Convert.ToDecimal(Request.Query["Topscore"]);
                decimal bottomlimit = Convert.ToDecimal(Request.Query["Bottomscore"]);

                movies = movieDb.Movies.OrderByDescending(d => d.imdbRating)
                    .Where(m => toplimit > m.imdbRating && m.imdbRating > bottomlimit)
                    .Take(top);
            }

            var viewModel = new BrowseViewModel();

            viewModel.seenMovieIDs = movieDb.Viewings.Where(v => v.UserID == userId
                                                       && v.ViewingType == "w").Select(m => m.MovieID).ToList();


            viewModel.watchlistMovieIDs = movieDb.Viewings.Where(v => v.UserID == userId
                                                       && v.ViewingType == "s").Select(m => m.MovieID).ToList();

            viewModel.ratedMovieData = movieDb.Viewings.Where(v => v.UserID == userId
                                                 && v.ViewingType == "r").ToDictionary(m => m.MovieID, m => Int32.Parse(m.ViewingData));

            viewModel.Movies = await movies.Select(d => new BrowseViewModel.MovieItem
            {
                Title = d.Title,
                Actors = d.Actors,
                Director = d.Director,
                Genre = d.Genre,
                MovieID = d.id,
                imdbID = d.imdbID,
                imdbRating = d.imdbRating,
                Plot = d.Plot,
                PosterLink = d.PosterLink,
                Rating = d.Rating,
                ReleaseDate = d.ReleaseDate,
                Runtime = d.Runtime,
                SimpleTitle = d.SimpleTitle,
                tomatoRating = d.tomatoRating,
                Writer = d.Writer
            })
                .ToListAsync();

            return View("Browse", viewModel);
        }

        [HttpGet("/Update/{id}")]
        public ActionResult Update(int id)
        {
            var movie = movieDb.Movies.SingleOrDefault(d => d.id == id);

            if (movie == null)
            {
                return NotFound();
            }
            else
            {
                return View(movie);
            }
        }


        [HttpPost("/Movie/UpdateMovie")]
        public IActionResult UpdateMovie(int givenID, string title, string simpletitle, string rated, string released, string runtime, string genre, string director,
       string writer, string actors, string plot, string givenPosterLink, string imdbrating, string imdbid, string tomatorating)
        {
            Movie updateMovie = movieDb.Movies.SingleOrDefault(d => d.id == givenID);

            if (title != null)
            {
                updateMovie.Title = title.Trim();
            }

            if (simpletitle.Trim() != "")
            {
                updateMovie.SimpleTitle = simpletitle.Trim();
            }

            if (rated.Trim() != "")
            {
                updateMovie.Rating = rated;
            }

            if (released.Trim() != "")
            {
                updateMovie.ReleaseDate = Convert.ToDateTime(released);
            }

            if (runtime.Trim() != "")
            {
                updateMovie.Runtime = runtime;
            }

            if (genre.Trim() != "")
            {
                updateMovie.Genre = genre;
            }

            if (director.Trim() != "")
            {
                updateMovie.Director = director;
            }

            if (writer.Trim() != "")
            {
                updateMovie.Writer = writer;
            }

            if (actors.Trim() != "")
            {
                updateMovie.Actors = actors;
            }

            if (plot.Trim() != "")
            {
                updateMovie.Plot = plot;
            }

            if (givenPosterLink.Trim() != "")
            {
                //If there's a poster link, then set the Movie Posterlink
                updateMovie.PosterLink = givenPosterLink;
                //Download the image at the Posterlink
                imageHandler.SavePosterImageFromLink(givenID, updateMovie.PosterLink);
            }

            if (imdbrating.Trim() != "")
            {
                updateMovie.imdbRating = Decimal.Parse(imdbrating);
            }

            if (imdbid.Trim() != "")
            {
                updateMovie.imdbID = imdbid;
            }

            if (tomatorating != null)
            {
                if (tomatorating.Trim() != "")
                {
                    updateMovie.tomatoRating = Convert.ToInt32(tomatorating);
                }
            }

            movieDb.SaveChanges();

            return JsonSuccess;
        }

        [HttpGet("/Insert")]
        public IActionResult Insert()
        {
            return View();
        }

        [HttpGet("/BatchInsert")]
        public IActionResult BatchInsert()
        {
            return View();
        }

        [HttpGet("/Movie/BatchGetMovie")]
        public IActionResult BatchGetMovie(string foundMovie)
        {
            if (foundMovie != null)
            {
                //First remove any quality (720p) designation from the final portion of the folder name
                string qual = foundMovie.Split(' ').Last<string>().ToLower();
                if (qual.Last<char>() == 'p')
                {
                    foundMovie = foundMovie.Replace(qual, "");
                }

                //Next remove any tags between square brackets in movie name
                foundMovie = Regex.Replace(foundMovie, @"\[.*?\]", "");

                foundMovie = foundMovie.Trim();
                ActionResult scrapedMovie = null;
                try
                {
                    ////Try IMDB page lookup.
                    //Search using Google:         
                    string GoogleRT = "http://www.google.com/search?num=1&q=" + HttpUtility.UrlEncode(foundMovie) + " IMDB";
                    var result = new HtmlWeb().Load(GoogleRT);
                    HtmlNode googleNode = result.DocumentNode.SelectNodes("//html//body//a[contains(@href,'imdb')]")[0];

                    HtmlAgilityPack.HtmlDocument doc3 = new HtmlAgilityPack.HtmlDocument();
                    HtmlAgilityPack.HtmlWeb docHFile3 = new HtmlWeb();
                    //I notice URLs returned this way have additional text. Split on = and remove the extra "&amp" from the href
                    string imdbLink = googleNode.Attributes["href"].Value;
                    string imdbID = imdbLink.Split("/")[4];
                    scrapedMovie = ImdbScrape(imdbID);
                }
                catch
                {
                    //Google may have temporarily blocked us, try DuckDuckGo
                    try
                    {
                        ////Try IMDB page lookup.
                        //Search using Google:         

                        var baseAddress = new Uri("http://www.google.com/search?num=1&q=" + HttpUtility.UrlEncode(foundMovie) + " IMDB");
                        var cookieContainer = new CookieContainer();
                        using (var handler = new HttpClientHandler() { CookieContainer = cookieContainer })
                        using (var client = new HttpClient(handler) { BaseAddress = baseAddress })
                        {
                            var result = client.GetAsync("/").Result;
                        }
                    }
                    catch
                    {

                    }

                }
                return Json(scrapedMovie);
            }
            return null;
        }

        public class MovieInfo
        {
            public string movieName;
            public string movieReleased;
            public string movieRating;
            public string movieGenres;
            public string movieRuntime;
            public string movieDirector;
            public string movieWriter;
            public string movieActor;
            public string moviePoster;
            public string movieIMDBRating;
            public string movieIMDBID;
            public string moviePlot;
            public string movieRottenRating;
        }

        [HttpGet("/Movie/ImdbScrape")]
        public ActionResult ImdbScrape(string givenID)
        {
            var urlId = givenID;
            var constructedUrl1 = "http://www.imdb.com/title/" + urlId + "/";
            //URL for Release Dates:
            var constructedUrl2 = "http://www.imdb.com/title/" + urlId + "/releaseinfo";
            //URL for Actors:
            var constructedUrl3 = "https://www.imdb.com/title/" + urlId + "/fullcredits/";

            MovieInfo thisMovie = new MovieInfo();
            thisMovie.movieIMDBID = urlId;

            HtmlAgilityPack.HtmlDocument doc1 = new HtmlAgilityPack.HtmlDocument();
            HtmlAgilityPack.HtmlWeb docHFile1 = new HtmlWeb();
            doc1 = docHFile1.Load(constructedUrl1);

            HtmlAgilityPack.HtmlDocument doc2 = new HtmlAgilityPack.HtmlDocument();
            HtmlAgilityPack.HtmlWeb docHFile2 = new HtmlWeb();
            doc2 = docHFile2.Load(constructedUrl2);

            HtmlAgilityPack.HtmlDocument doc3 = new HtmlAgilityPack.HtmlDocument();
            HtmlAgilityPack.HtmlWeb docHFile3 = new HtmlWeb();
            doc3 = docHFile3.Load(constructedUrl3);

            var movieName = "";
            var movieReleased = "";
            var movieRating = "";
            var movieGenres = "";
            var movieRuntime = "";
            var movieDirector = "";
            var movieWriter = "";
            var movieActor = "";
            var moviePoster = "";
            var movieIMDBRating = "";
            var moviePlot = "";

            try
            {
                foreach (HtmlNode node in doc1.DocumentNode.SelectNodes("//div[@class='title_wrapper']//h1/text()[1]"))
                {
                    movieName = node.InnerText;
                }
                thisMovie.movieName = HttpUtility.HtmlDecode(movieName.Trim());
                //Movie Name is setup
            }
            catch
            {

            }


            try
            {
                foreach (HtmlNode node in doc2.DocumentNode.SelectNodes("(//td[@class='release-date-item__date'])[1]/text()"))
                {
                    movieReleased += node.InnerText;
                }

                thisMovie.movieReleased = movieReleased.Replace("Ratings", "").Trim();
                //Movie Releasedate is setup
            }
            catch
            {

            }


            try
            {
                foreach (HtmlNode node in doc1.DocumentNode.SelectNodes("//div[@class='subtext'][1]/text()"))
                {
                    //movieRating += node.Attributes["content"].Value;
                    movieRating += node.InnerText;
                }
                thisMovie.movieRating = movieRating.Replace(",", "").Trim().Replace("_", " ");
            }
            catch
            {
                //There was no movie rating found
            }
            //If there was no movie rating found, set the movie to Unrated
            if (string.IsNullOrEmpty(thisMovie.movieRating))
            {
                thisMovie.movieRating = "Unrated";
            }

            try
            {
                foreach (HtmlNode node in doc1.DocumentNode.SelectNodes("//div[@class='subtext']//a[contains(@href,'genre')]/text()"))
                {
                    movieGenres += node.InnerText + ", ";
                }
                movieGenres = movieGenres.Trim();
                if (movieGenres[movieGenres.Length - 1] == ',')
                {
                    movieGenres = movieGenres.Remove(movieGenres.Length - 1);
                }
                thisMovie.movieGenres = movieGenres;
                //Movie Genres is setup
            }
            catch
            {

            }

            try
            {
                string thisLookup = "//div[@class='subtext']//time/text()";

                foreach (HtmlNode node in doc1.DocumentNode.SelectNodes(thisLookup))
                {
                    movieRuntime += node.InnerText;
                }
                movieRuntime = movieRuntime.Replace("-", "").Replace("min", " min").Replace("&nbsp;", "").Replace("h", " h").Trim();
                //int movieHours = 0;
                //int movieMinutes = 0;
                //int movieTimeHolder = Convert.ToInt32(movieRuntime);
                //while (movieTimeHolder >= 60)
                //{
                //    movieHours++;
                //    movieTimeHolder -= 60;
                //}
                //movieMinutes = movieTimeHolder;

                thisMovie.movieRuntime = movieRuntime;
                //movie Runtime is setup
            }
            catch
            {

            }

            try
            {
                foreach (HtmlNode node in doc1.DocumentNode.SelectNodes("//div[@class='plot_summary_wrapper']//div[@class='credit_summary_item'][1]//a/text()"))
                {
                    if (node.InnerText != "" && !node.InnerText.Contains(" more credit"))
                    {
                        movieDirector += node.InnerText + ", ";
                    }
                }
                movieDirector = movieDirector.Trim();
                if (movieDirector[movieDirector.Length - 1] == ',')
                {
                    movieDirector = movieDirector.Remove(movieDirector.Length - 1);
                }
                thisMovie.movieDirector = HttpUtility.HtmlDecode(movieDirector);
                //Movie Directors are setup
            }
            catch
            {

            }

            try
            {
                foreach (HtmlNode node in doc1.DocumentNode.SelectNodes("//div[@class='plot_summary_wrapper']//div[@class='credit_summary_item'][2]//a/text()"))
                {
                    if (node.InnerText != "" && !node.InnerText.Contains(" more credit"))
                    {
                        movieWriter += node.InnerText + ", ";
                    }
                }
                movieWriter = movieWriter.Trim();
                if (movieWriter[movieWriter.Length - 1] == ',')
                {
                    movieWriter = movieWriter.Remove(movieWriter.Length - 1);
                }
                thisMovie.movieWriter = HttpUtility.HtmlDecode(movieWriter);
                //Movie Writers are setup
            }
            catch
            {

            }

            try
            {
                int actorsToInclude = 4;
                int actorsIncluded = 0;
                foreach (HtmlNode node in doc3.DocumentNode.SelectNodes("//table[@class='cast_list']//tr//td[2]/a[contains(@href,'name')]/text()"))
                {
                    if (actorsIncluded >= actorsToInclude)
                    {
                        break;
                    }
                    if (node.InnerText != "" && actorsIncluded < actorsToInclude)
                    {
                        movieActor += node.InnerText.Trim() + ", ";
                        actorsIncluded++;
                    }
                }
                movieActor = movieActor.Trim();
                if (movieActor[movieActor.Length - 1] == ',')
                {
                    movieActor = movieActor.Remove(movieActor.Length - 1);
                }
                thisMovie.movieActor = HttpUtility.HtmlDecode(movieActor);
                //Movie Actors are setup
            }
            catch
            {

            }

            try
            {
                foreach (HtmlNode node in doc1.DocumentNode.SelectNodes("//div[@class='poster']//img"))
                {
                    moviePoster = node.Attributes["src"].Value;
                }
                moviePoster = moviePoster.Trim();
                var moviePosterStart = "https://m.media-amazon.com/images/M/";
                var moviePosterEnd = "._V1._SX1000_SY1000_.jpg";
                moviePoster = moviePoster.Split('/')[5].Split('.')[0];
                thisMovie.moviePoster = moviePosterStart + moviePoster + moviePosterEnd;
                //Movie Poster is setup
            }
            catch
            {

            }

            try
            {
                foreach (HtmlNode node in doc1.DocumentNode.SelectNodes("//span[@itemprop='ratingValue']/text()"))
                {
                    movieIMDBRating = node.InnerText;
                }
                thisMovie.movieIMDBRating = movieIMDBRating.Trim();
                //Movie IMDB Rating is setup
            }
            catch
            {

            }
            try
            {
                foreach (HtmlNode node in doc1.DocumentNode.SelectNodes("//div[@class='summary_text']/text()"))
                {
                    moviePlot += node.InnerText;
                }
                thisMovie.moviePlot = HttpUtility.HtmlDecode(moviePlot.Trim());
                //Movie Plot is setup
            }
            catch
            {

            }

            thisMovie.movieRottenRating = GetRottenTomatoesRating(thisMovie);

            return Json(thisMovie);
        }

        [HttpGet("/Movie/RTLookup")]
        public IActionResult RTLookup(MovieInfo givenMovie)
        {
            var rating = GetRottenTomatoesRating(givenMovie);

            return new JsonResult(rating.Trim());
        }

        private string GetRottenTomatoesRating(MovieInfo givenMovie)
        {
            var foundRating = "";
            try
            {
                ////Try Rotten Tomatoes lookup.
                //No clear way to turn IMDB ID into Rotten Tomatoes ID.
                //Tried searching film name on Rotten Tomatoes, search page is dynamic. Instead, I search through Google:         
                string GoogleRT = "http://www.google.com/search?num=10&q=" + HttpUtility.UrlEncode(givenMovie.movieName) + " " + givenMovie.movieReleased + " Rotten Tomatoes Score";
                var result = new HtmlWeb().Load(GoogleRT);
                HtmlNode googleNode = result.DocumentNode.SelectNodes("//html//body//a[contains(@href,'rottentomatoes')]")[0];

                HtmlAgilityPack.HtmlDocument doc4 = new HtmlAgilityPack.HtmlDocument();
                HtmlAgilityPack.HtmlWeb docHFile4 = new HtmlWeb();
                //I notice URLs returned this way have additional text. Split on = and remove the extra "&amp" from the href
                string rtLink = googleNode.Attributes["href"].Value;

                doc4 = docHFile4.Load(HttpUtility.HtmlDecode(HttpUtility.UrlDecode(rtLink)).Replace("&sa", ""));

                //This seems to be failing:
                //doc4 = docHFile4.Load(HttpUtility.HtmlDecode(HttpUtility.UrlDecode(googleNode.Attributes["href"].Value.Split('=')[1])).Replace("&sa", ""));

                //Oddly enough, when I hit the direct movie page from Google, I don't get a properly formatted Rotten Tomatoes page.
                //Instead, there's a JSON object in a script tag with the jsonLdSchema id. I can serialize that object for the information I need.
                var node = doc4.DocumentNode.SelectNodes("//a[@id='tomato_meter_link']//span[contains(@class,percentage)]/text()").First();
                {
                    //JToken item = JToken.Parse(node.InnerHtml);
                    //string rating = item["aggregateRating"]["ratingValue"].Value<string>();
                    string rating = node.InnerHtml.Trim().Replace("%", "");
                    foundRating = rating;
                }
            }
            catch
            {

            }

            return foundRating;
        }

        [HttpPost("/Movie/InsertMovie")]
        public IActionResult InsertMovie(string title, string rated, string released, string runtime, string genre, string director,
            string writer, string actors, string plot, string poster, string imdbrating, string imdbid, string tomatorating)
        {
            var checkMovie = movieDb.Movies.SingleOrDefault(d => d.imdbID == imdbid);

            if (checkMovie == null)
            {
                Movie newMovie = new Movie();
                WebClient client = new WebClient();
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
                    var loc = imageHandler.SavePosterImageFromLink(newMovie.id, newMovie.PosterLink);
                }
            }

            return JsonSuccess;
        }

        [HttpGet("/Movie/ImdbYearScrape")]
        public IActionResult ImdbYearScrape(string givenID)
        {
            var urlId = givenID;
            var constructedUrl = "http://www.imdb.com/title/" + urlId + "/releaseinfo";
            MovieInfo thisMovie = new MovieInfo();
            thisMovie.movieIMDBID = urlId;

            HtmlAgilityPack.HtmlDocument doc = new HtmlAgilityPack.HtmlDocument();
            HtmlAgilityPack.HtmlWeb docHFile = new HtmlWeb();
            doc = docHFile.Load(constructedUrl);

            var movieReleased = "";

            try
            {
                var releaseDateElement = doc.DocumentNode.SelectNodes("//td[@class='release_date']/text()").First();
                string releaseMonthDate = releaseDateElement.InnerText;
                string releaseYear = releaseDateElement.NextSibling.InnerText;
                movieReleased = releaseMonthDate.Trim() + " " + releaseYear.Trim();
                //Movie Releasedate is setup
            }
            catch
            {

            }

            return new JsonResult(movieReleased.Trim());
        }

        [HttpPost("/Movie/Login")]
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

            var claims = new List<System.Security.Claims.Claim>()
            {
                new Claim(ClaimTypes.Name, username),
                new Claim("UserID", user.UserID.ToString())
            };

            var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);

            var authProperties = new AuthenticationProperties { };

            await HttpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity), authProperties);

            return Ok();
        }


        [HttpGet("/Movie/Logout")]
        public async Task<IActionResult> Logout()
        {
            await HttpContext.SignOutAsync();
            return Ok();
        }

        [HttpGet("/Movie/UserList/{descript}")]
        public IActionResult UserList(string descript)
        {
            var userList = movieDb.Users.Where(d => d.Username.Contains(descript)).Select(d => d.Username).ToList();
            return Json(userList);
        }

        [HttpGet("/Movie/CountWatched")]
        public async Task<IActionResult> CountWatched()
        {
            if (User.Identity.IsAuthenticated)
            {
                var userID = Int32.Parse(User.Claims.Single(d => d.Type == "UserID").Value);

                var count = await movieDb.Viewings.CountAsync(d => d.UserID == userID && d.ViewingType == "w");
                return new JsonResult(new { count = count });
            }
            else
            {
                return new JsonResult(new { count = 0 });
            }
        }

        [HttpGet("/Movie/CountWatchlist")]
        public async Task<IActionResult> CountWatchlist()
        {
            if (User.Identity.IsAuthenticated)
            {
                var userID = Int32.Parse(User.Claims.Single(d => d.Type == "UserID").Value);

                var count = await movieDb.Viewings.CountAsync(d => d.UserID == userID && d.ViewingType == "s");
                return new JsonResult(new { count = count });
            }
            else
            {
                return new JsonResult(new { count = 0 });
            }
        }

        [HttpGet("/Movie/Display/{movieID}")]
        public async Task<IActionResult> Display(int movieID)
        {
            var movie = await movieDb.Movies.SingleOrDefaultAsync(d => d.id == movieID);

            if (movie == null)
            {
                return NotFound();
            }

            var vm = new MovieDisplayViewModel
            {
                Movie = movie
            };

            if (User.Identity.IsAuthenticated)
            {
                var userId = Int32.Parse(User.Claims.Single(d => d.Type == "UserID").Value);
            }

            return PartialView("Display", vm);
        }


        //public ActionResult SaveRate(string rating, int movieID)
        //{
        //    movieDB db = new movieDB();
        //    var userID = Convert.ToInt32(Session["UserID"]);
        //    Viewing previousRating = (from v in db.Viewings
        //                              where v.MovieID == movieID &&
        //                              v.UserID == userID &&
        //                              v.ViewingType == "r"
        //                              select v).FirstOrDefault();

        //    if (previousRating != null)
        //    {
        //        db.Viewings.Remove(previousRating);
        //        //db.Viewings.DeleteObject(previousRating);
        //    }

        //    if (rating != null)
        //    {
        //        Viewing ratedView = new Viewing();
        //        ratedView.MovieID = movieID;
        //        ratedView.UserID = userID;
        //        ratedView.ViewingType = "r";
        //        ratedView.ViewingData = rating;
        //        db.Viewings.Add(ratedView);
        //        //db.Viewings.AddObject(ratedView);
        //    }
        //    db.SaveChanges();
        //    return null;
        //}

        int GetID()
        {

            if (User.Identity.IsAuthenticated)
            {
                return Int32.Parse(User.Claims.Single(d => d.Type == "UserID").Value);
            }
            else
            {
                return 0;
            }
        }

        [HttpPost("/Movie/SaveStatus")]
        public ActionResult SaveStatus(bool newStatus, int movieID, string statusType)
        {
            int userId = GetID();
            if (userId > 0)
            {
                Viewing previousStatus = movieDb.Viewings.Where(o => o.ViewingType == statusType && o.UserID == userId && o.MovieID == movieID).FirstOrDefault();

                if (previousStatus != null)
                {

                    movieDb.Viewings.Remove(previousStatus);
                }

                if (newStatus)
                {
                    Viewing newView = new Viewing();
                    newView.MovieID = movieID;
                    newView.UserID = userId;
                    newView.ViewingType = statusType;
                    movieDb.Viewings.Add(newView);
                }

                movieDb.SaveChanges();
            }
            return null;
        }

        //public ActionResult SaveSuggestion(string suggested, string comments, int movieID)
        //{
        //    movieDB db = new movieDB();
        //    var userName = Session["User"];
        //    var userID = Convert.ToInt32(Session["UserID"]);

        //    Viewing previousSuggest = (from v in db.Viewings
        //                               where v.MovieID == movieID &&
        //                               v.UserID == userID &&
        //                               v.ViewingType == "s"
        //                               select v).FirstOrDefault();

        //    if (suggested != null && suggested != "")
        //    {
        //        Viewing suggestedView = new Viewing();
        //        suggestedView.MovieID = movieID;

        //        var suggestID = Convert.ToInt32((from u in db.Users where u.Username.Contains(suggested) select u.UserID).FirstOrDefault());
        //        suggestedView.UserID = suggestID;

        //        Viewing oldViewing = (from v in db.Viewings where v.UserID == suggestID && v.MovieID == movieID select v).FirstOrDefault();
        //        string oldComments = "";
        //        if (oldViewing != null)
        //        {
        //            oldComments += oldViewing.ViewingData;
        //            db.Viewings.Remove(oldViewing);
        //        }

        //        suggestedView.ViewingType = "s";
        //        suggestedView.ViewingData = userName + "|" + comments + "•" + oldComments;
        //        db.Viewings.Add(suggestedView);
        //    }
        //    db.SaveChanges();
        //    return null;
        //}


        //public ActionResult FetchSuggestData()
        //{
        //    var userID = Convert.ToInt32(Session["UserID"]);
        //    movieDB db = new movieDB();
        //    var items = (from v in db.Viewings
        //                 where v.UserID == userID
        //                 && v.ViewingType == "s"
        //                 select v);
        //    String[][] UserData = new String[items.Count()][];
        //    int i = 0;
        //    foreach (var v in items)
        //    {
        //        String[] currentData = new String[2];
        //        currentData[0] = v.MovieID.ToString();
        //        currentData[1] = v.ViewingData;
        //        UserData[i] = currentData;
        //        i++;
        //    }

        //    return Json(UserData, JsonRequestBehavior.AllowGet);
        //}

        [HttpGet("/Image/{id}")]
        public async Task<IActionResult> ImageHandler(int id)
        {
            var poster = await imageHandler.GetPosterImageFromID(id);

            if (poster == null)
            {
                return NotFound();
            }

            return File(poster, "image/png");
        }

        [HttpGet("/ImageThumb/{id}")]
        public async Task<IActionResult> ImageThumbHandler(int id)
        {
            var poster = await imageHandler.GetPosterImageFromID(id, true);

            if (poster == null)
            {
                return NotFound();
            }

            return File(poster, "image/png");
        }


        [HttpGet("/PosterCollage")]
        public IActionResult PosterCollage()
        {
            //This is my attempt at a webpage to return one massive image of all my posters

            //Get the max number of movies for my batching routines
            int movieCount = movieDb.Movies.Count();
            //allMovies will hold the results of each batch
            List<Movie> allMovies = new List<Movie>();

            //We're starting on the 0th query, getting 100 movies each time. Increasing this number
            //introduces instability from closed connections.

            //int queryNumber = 0;
            //int queryCount = 100;
            //int queryMin = 0;

            ////queryMin is the movie ID we're starting at, collecting queryCount movies from there.
            //while (queryMin < movieCount)
            //{
            //    queryMin = queryNumber * queryCount;
            //    List<Movie> movies = (from v in movieDb.Movies
            //                          where v.id > queryMin &&
            //                          v.id <= queryMin + queryCount
            //                          select v).ToList();
            //    allMovies.AddRange(movies);
            //    queryNumber++;
            //}
            //Take all my movies and order them by their simple title
            allMovies = movieDb.Movies.OrderBy(x => x.SimpleTitle).ToList();

            //Posters in the collage should be 75 x 100. Increasing these numbers makes the poster too large.
            int posterWidth = 75;
            int posterHeight = 100;

            //How many posters per row, and how does that effect the height/width of the final image:
            int rowLength = 25;
            int totalWidth = rowLength * posterWidth;
            int totalHeight = (int)(Math.Ceiling((double)movieCount / (double)rowLength) * posterHeight);

            //Create a new Bitmap of the calculated final size
            Bitmap combinedBitmap = new Bitmap(totalWidth, totalHeight);
            //Create a Graphics object from the bitmap so we can draw
            Graphics combinedGraphics = Graphics.FromImage(combinedBitmap);
            int drawingPosition = 0;
            int drawingHeight = 0;
            int rowCounter = 0;
            List<Bitmap> allPosters = new List<Bitmap>();
            //COMMENTED UNTIL I CAN FIX:
            foreach (Movie movie in allMovies)
            {
                var posterFile = imageHandler.GetShrunkPosterFile(movie.id);

                if (!posterFile.Exists)
                {
                    continue;
                }

                Image originalBitmap = Image.FromFile(posterFile.FullName);
                Bitmap resizeBitmap = new Bitmap(posterWidth, posterHeight);
                Graphics resizeGraphic = Graphics.FromImage(resizeBitmap);
                resizeGraphic.InterpolationMode = InterpolationMode.High;
                resizeGraphic.CompositingQuality = CompositingQuality.HighQuality;
                resizeGraphic.SmoothingMode = SmoothingMode.AntiAlias;
                resizeGraphic.DrawImage(originalBitmap, new Rectangle(0, 0, posterWidth, posterHeight));
                if (rowCounter == rowLength)
                {
                    rowCounter = 0;
                    drawingPosition = 0;
                    drawingHeight += posterHeight;
                }
                combinedGraphics.DrawImage(resizeBitmap, new Point(drawingPosition, drawingHeight));
                drawingPosition += posterWidth;
                rowCounter++;
            }

            MemoryStream memoryStream = new MemoryStream();
            HttpContext.Response.ContentType = "image/png";
            combinedBitmap.Save(HttpContext.Response.Body, System.Drawing.Imaging.ImageFormat.Png);
            return Ok();
        }

    }
}