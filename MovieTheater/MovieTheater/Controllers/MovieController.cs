using System;
using System.Linq;
using System.Web;
using System.Net;
using System.Text.RegularExpressions;
using HtmlAgilityPack;
using System.Net.Http;
using Microsoft.AspNetCore.Mvc;
using MovieTheater.Db;
using Newtonsoft.Json.Linq;

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

        private IActionResult JsonSuccess => Json(new { success = true });

        public MovieController(MovieDb movieDb)
        {
            this.movieDb = movieDb;
        }


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

        //[HttpGet("/Browse")]
        //public ActionResult Browse(string sort)
        //{
        //    IQueryable<Movie> movies = movieDb.Movies;

        //    /*
        //    if (sort == "Letter")
        //    {
        //        String givenLetter = Request.QueryString["Letter"];
        //        if (givenLetter == "1")
        //        {
        //            string allLetters = "abcdefghijklmnopqrstuvwxyz";
        //            movies = from m in db.Movies
        //                    where !allLetters.Contains(m.SimpleTitle.Substring(0, 1))
        //                    orderby m.SimpleTitle
        //                    select m;
        //        }
        //        else
        //        {
        //            movies = from m in db.Movies
        //                    where m.SimpleTitle.Substring(0, 1) == givenLetter
        //                    orderby m.SimpleTitle
        //                    select m;
        //        }
        //    }

        //    if (givenSort == "Title")
        //    {
        //        String givenTitle = Request.QueryString["Title"];
        //        movies = from m in db.Movies
        //                where m.SimpleTitle.Contains(givenTitle) ||
        //                m.Title.Contains(givenTitle)
        //                orderby m.SimpleTitle
        //                select m;
        //    }

        //    if (givenSort == "Actor")
        //    {
        //        String givenActor = Request.QueryString["Actor"];
        //        movies = from m in db.Movies
        //                where m.Actors.Contains(givenActor)
        //                orderby m.SimpleTitle
        //                select m;
        //    }

        //    if (givenSort == "Watched")
        //    {
        //        var userID = Convert.ToInt32(Session["UserID"]);
        //        if (userID > 0)
        //        {
        //            var viewings = (from v in db.Viewings
        //                            where v.UserID == userID
        //                            && v.ViewingType == "w"
        //                            select v.MovieID);

        //            movies = (from m in db.Movies
        //                     where viewings.Contains(m.id)
        //                     select m);
        //        }

        //    }

        //    if (givenSort == "Watchlist")
        //    {

        //        var userID = Convert.ToInt32(Session["UserID"]);
        //        if (userID != 0)
        //        {
        //            var viewings = (from v in db.Viewings
        //                            where v.UserID == userID
        //                            && v.ViewingType == "s"
        //                            select v.MovieID);

        //            movies = (from m in db.Movies
        //                     where viewings.Contains(m.id)
        //                     orderby m.SimpleTitle
        //                     select m);
        //        }

        //    }

        //    if (givenSort == "Overwatchlist")
        //    {
        //        var viewings = (from v in db.Viewings
        //                        where v.ViewingType == "s"
        //                        select v.MovieID);

        //        movies = (from m in db.Movies
        //                 where viewings.Contains(m.id)
        //                 select m);
        //    }

        //    if (givenSort == "Uploaded")
        //    {
        //        DateTime minDate = new DateTime(2000, 1, 1);
        //        DateTime dateFrom = Convert.ToDateTime(Request.QueryString["dateFrom"]);
        //        if (dateFrom < minDate)
        //        {
        //            dateFrom = minDate;
        //        }
        //        movies = (from m in db.Movies
        //                 where m.UploadedDate > dateFrom
        //                 orderby m.UploadedDate
        //                 select m);
        //    }

        //    if (givenSort == "Rank")
        //    {
        //        int top = Convert.ToInt32(Request.QueryString["Top"]);
        //        decimal toplimit = Convert.ToDecimal(Request.QueryString["Topscore"]);
        //        decimal bottomlimit = Convert.ToDecimal(Request.QueryString["Bottomscore"]);
        //        movies = (from m in db.Movies
        //                 orderby m.imdbRating descending
        //                 where toplimit > m.imdbRating && m.imdbRating > bottomlimit
        //                 select m).Take(top);
        //    }

        //    if (givenSort == "Watchability")
        //    {
        //        //It prefers newer movies over older movies
        //        //30% older than 1990 starts a drop off          

        //        List<String> actors = db.Movies.Select(x => x.Actors).ToList();
        //        List<String> allActors = new List<String>();
        //        foreach (String actor in actors)
        //        {
        //            foreach (String actorName in actor.Split(','))
        //            {
        //                allActors.Add(actorName.Trim());
        //            }
        //        }


        //        var actorProto =
        //        from actor in allActors
        //        group actor by actor into g
        //        select new actorCount { actorName = g.Key, count = g.Count() };

        //        List<String> directors = db.Movies.Select(x => x.Director).ToList();
        //        List<String> allDirectors = new List<String>();
        //        foreach (String director in directors.Where(o => o != null))
        //        {
        //            if (director.Contains(','))
        //            {
        //                foreach (String directorName in director.Split(','))
        //                {
        //                    allDirectors.Add(directorName.Trim());
        //                }
        //            }
        //            else
        //            {
        //                allDirectors.Add(director.Trim());
        //            }
        //        }

        //        var directorProto =
        //        from director in allDirectors
        //        group director by director into g
        //        select new actorCount { actorName = g.Key, count = g.Count() };

        //        var actorCount = actorProto.ToList();
        //        var directorCount = directorProto.ToList();
        //        List<rankedMovie> movieList = new List<rankedMovie>();

        //        List<minorMovie> allMovies = db.Movies.Select(x => new minorMovie() { id = x.id, movieName = x.Title, releaseDate = x.ReleaseDate, imdbRating = x.imdbRating, rtRating = x.tomatoRating, Actors = x.Actors, Directors = x.Director }).ToList();


        //        foreach (minorMovie movie in allMovies)
        //        {
        //            movieList.Add(new rankedMovie(movie));
        //        }
        //        var givenYear = 2000;
        //        var yearModifier = .2;
        //        int FavoredYear = givenYear;
        //        foreach (rankedMovie m in movieList)
        //        {
        //            var yearDifference = m.movie.releaseDate.Value.Year - FavoredYear;
        //            var givenRank = yearDifference * yearModifier;
        //            m.rating += 30 - Math.Abs(givenRank);

        //            double pointValues = 0.2857;
        //            decimal imdbPoints = 0;
        //            if (m.movie.imdbRating.HasValue)
        //            {
        //                imdbPoints = (m.movie.imdbRating.Value * 10);
        //                if (imdbPoints > 69)
        //                {
        //                    imdbPoints += 10;
        //                }
        //                imdbPoints = imdbPoints * (decimal)pointValues;
        //            }
        //            double rtPoints = 0;
        //            if (m.movie.rtRating.HasValue)
        //            {
        //                rtPoints = m.movie.rtRating.Value;
        //                if (rtPoints > 69)
        //                {
        //                    rtPoints += 10;
        //                }
        //                rtPoints = rtPoints * pointValues;

        //                var pointAverage = ((decimal)rtPoints + imdbPoints) / 2;

        //                m.rating += (double)pointAverage;
        //            }
        //            else
        //            {
        //                m.rating += (double)imdbPoints;
        //            }


        //            var actorPoints = 0;
        //            foreach (String actorName in m.movie.Actors.Split(','))
        //            {
        //                var actorAdd = actorCount.Where(o => o.actorName == actorName.Trim()).FirstOrDefault();
        //                actorPoints += actorAdd.count;
        //            }

        //            m.rating += (actorPoints / 3);

        //            var directorPoints = 0;
        //            if (m.movie.Directors != null)
        //            {
        //                foreach (String directorName in m.movie.Directors.Split(','))
        //                {
        //                    var directorAdd = directorCount.Where(o => o.actorName == directorName.Trim()).FirstOrDefault();
        //                    directorPoints += directorAdd.count;
        //                }
        //            }
        //            m.rating += (directorPoints / 5);

        //        }



        //        movieList = movieList.OrderByDescending(m => m.rating).ToList();

        //        List<int> rankedMovieIDList = new List<int>();
        //        foreach (rankedMovie movie in movieList)
        //        {
        //            rankedMovieIDList.Add(movie.movie.id);
        //        }

        //        movies = db.Movies.ToList().OrderBy(i => rankedMovieIDList.IndexOf(i.id)).Take(100).AsQueryable();

        //    }

        //    return View(MovieCollectionToMovie(movies));
        //    */

        //    return View();
        //}
        //public class minorMovie
        //{
        //    public int id;
        //    public string movieName;
        //    public DateTime? releaseDate;
        //    public decimal? imdbRating;
        //    public int? rtRating;
        //    public string Actors;
        //    public string Directors;
        //    public minorMovie()
        //    {
        //    }
        //}

        //public class actorCount
        //{
        //    public string actorName;
        //    public int count;
        //    public actorCount()
        //    {
        //    }
        //}

        //public class rankedMovie
        //{
        //    public double rating = 0;
        //    public minorMovie movie;
        //    public rankedMovie(minorMovie givenMovie)
        //    {
        //        this.movie = givenMovie;
        //    }
        //}

        //public IQueryable<UserMovies> MovieCollectionToMovie(IQueryable<Movie> givenMovies)
        //{
        //    movieDB db = new movieDB();
        //    var userID = Convert.ToInt32(Session["UserID"]);
        //    var movieCollectionList = givenMovies.ToList();
        //    var movieList = new List<UserMovies>();
        //    foreach (Movie movie in movieCollectionList)
        //    {
        //        UserMovies newMovie = UserMovieSetup(movie);
        //        newMovie.isWatched = (from m in db.Viewings
        //                              where m.UserID == userID
        //                              && m.ViewingType == "w"
        //                              && m.MovieID == newMovie.id
        //                              select "1").FirstOrDefault();
        //        newMovie.isWatchlist = (from m in db.Viewings
        //                                where m.UserID == userID
        //                                && m.ViewingType == "s"
        //                                && m.MovieID == newMovie.id
        //                                select "1").FirstOrDefault();
        //        movieList.Add(newMovie);
        //    }
        //    return movieList.AsQueryable();
        //}

        //UserMovies UserMovieSetup(Movie givenMovie)
        //{
        //    UserMovies newMovie = new UserMovies();

        //    newMovie.Title = givenMovie.Title;
        //    newMovie.Actors = givenMovie.Actors;
        //    newMovie.Director = givenMovie.Director;
        //    newMovie.Genre = givenMovie.Genre;
        //    newMovie.id = givenMovie.id;
        //    newMovie.imdbID = givenMovie.imdbID;
        //    newMovie.imdbRating = givenMovie.imdbRating;
        //    newMovie.Plot = givenMovie.Plot;
        //    newMovie.PosterLink = givenMovie.PosterLink;
        //    newMovie.Rating = givenMovie.Rating;
        //    newMovie.ReleaseDate = givenMovie.ReleaseDate;
        //    newMovie.Runtime = givenMovie.Runtime;
        //    newMovie.SimpleTitle = givenMovie.SimpleTitle;
        //    newMovie.tomatoRating = givenMovie.tomatoRating;
        //    newMovie.Writer = givenMovie.Writer;

        //    return newMovie;
        //}

            [HttpGet("/Movie/Update/{id}")]
        public ActionResult Update(int id)
        {
            var movie = movieDb.Movies.SingleOrDefault(d => d.id == id);            
            return View(movie);
        }

        public IActionResult UpdateMovie(int givenID, string title, string simpletitle, string rated, string released, string runtime, string genre, string director,
       string writer, string actors, string plot, string givenPosterLink, string imdbrating, string imdbid, string tomatorating)
        {
            Movie updateMovie = movieDb.Movies.SingleOrDefault(d => d.id == givenID);

            WebClient client = new WebClient();
            byte[] potentialPoster = new byte[0];

            if (title != null)
            {
                updateMovie.Title = title;
            }

            if (simpletitle.Trim() != "")
            {
                updateMovie.SimpleTitle = simpletitle;
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
                potentialPoster = client.DownloadData(givenPosterLink);
                //Create a MoviePoster object for MoviePoster table insertion
                MoviePoster posterInsert = movieDb.MoviePosters.FirstOrDefault(m => m.MovieID == givenID);
                if (posterInsert == null)
                {
                    posterInsert = new MoviePoster();
                    posterInsert.MovieID = givenID;
                    movieDb.MoviePosters.Add(posterInsert);
                    movieDb.SaveChanges();
                }
                
                //posterInsert.Poster = potentialPoster;
                posterInsert.MovieID = givenID;
                movieDb.SaveChanges();
            }

            if (imdbrating.Trim() != "")
            {
                updateMovie.imdbRating = Decimal.Parse(imdbrating);
            }

            if (imdbid.Trim() != "")
            {
                updateMovie.imdbID = imdbid;
            }

            if (tomatorating.Trim() != "")
            {
                updateMovie.tomatoRating = Convert.ToInt32(tomatorating);
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

        public IActionResult BatchGetMovie(string foundMovie)
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
                HtmlNode googleNode = result.DocumentNode.SelectNodes("//html//body//div[@class='g']//a/@href").First();

                HtmlAgilityPack.HtmlDocument doc3 = new HtmlAgilityPack.HtmlDocument();
                HtmlAgilityPack.HtmlWeb docHFile3 = new HtmlWeb();
                //I notice URLs returned this way have additional text. Split on = and remove the extra "&amp" from the href
                string[] imdbLink = googleNode.Attributes["href"].Value.Split('=')[1].Split(new string[] { "&amp;" }, StringSplitOptions.None)[0].Split('/');
                string imdbID = imdbLink[imdbLink.Length - 2];
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

        public ActionResult ImdbScrape(string givenID)
        {
            var urlId = givenID;
            var constructedUrl1 = "http://www.imdb.com/title/" + urlId + "/";
            var constructedUrl2 = "http://www.imdb.com/title/" + urlId + "/releaseinfo";

            MovieInfo thisMovie = new MovieInfo();
            thisMovie.movieIMDBID = urlId;

            HtmlAgilityPack.HtmlDocument doc1 = new HtmlAgilityPack.HtmlDocument();
            HtmlAgilityPack.HtmlWeb docHFile1 = new HtmlWeb();
            doc1 = docHFile1.Load(constructedUrl1);

            HtmlAgilityPack.HtmlDocument doc2 = new HtmlAgilityPack.HtmlDocument();
            HtmlAgilityPack.HtmlWeb docHFile2 = new HtmlWeb();
            doc2 = docHFile2.Load(constructedUrl2);


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
                foreach (HtmlNode node in doc1.DocumentNode.SelectNodes("//h1[@itemprop='name']/text()[1]"))
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
                foreach (HtmlNode node in doc2.DocumentNode.SelectNodes("(//td[@class='release_date'])[1]/text()"))
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
                foreach (HtmlNode node in doc1.DocumentNode.SelectNodes("//meta[@itemprop='contentRating']"))
                {
                    movieRating += node.Attributes["content"].Value;
                    //movieRating += node.InnerText;
                }
                thisMovie.movieRating = movieRating.Trim().Replace("_", " ");
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
                foreach (HtmlNode node in doc1.DocumentNode.SelectNodes("//span[@itemprop='genre']/text()"))
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
                string thisLookup = "//div[@class='title_wrapper']//time[@itemprop='duration']/text()";

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
                foreach (HtmlNode node in doc1.DocumentNode.SelectNodes("//div[@class='main'][1]//span[@itemprop='director']//span[@itemprop='name']/text()"))
                {
                    if (node.InnerText != "")
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
                foreach (HtmlNode node in doc1.DocumentNode.SelectNodes("//div[@class='main'][1]//span[@itemprop='creator']//span[@itemprop='name']/text()"))
                {
                    if (node.InnerText != "")
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
                foreach (HtmlNode node in doc1.DocumentNode.SelectNodes("//td[@itemprop='actor']//span/text()"))
                {
                    if (node.InnerText != "" && actorsIncluded < actorsToInclude)
                    {
                        movieActor += node.InnerText + ", ";
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
                foreach (HtmlNode node in doc1.DocumentNode.SelectNodes("//div[@class='poster']//img[@itemprop='image']"))
                {
                    moviePoster = node.Attributes["src"].Value;
                }
                moviePoster = moviePoster.Trim();
                var moviePosterStart = "http://ia.media-imdb.com/images/M/";
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

            thisMovie.movieRottenRating = RTLookup(thisMovie);


            return Json(thisMovie);
        }

        public string RTLookup(MovieInfo givenMovie)
        {
            var foundRating = "";
            try
            {
                ////Try Rotten Tomatoes lookup.
                //No clear way to turn IMDB ID into Rotten Tomatoes ID.
                //Tried searching film name on Rotten Tomatoes, search page is dynamic. Instead, I search through Google:         
                string GoogleRT = "http://www.google.com/search?num=1&q=" + HttpUtility.UrlEncode(givenMovie.movieName) + " " + givenMovie.movieReleased + " Rotten Tomatoes Score";
                var result = new HtmlWeb().Load(GoogleRT);
                HtmlNode googleNode = result.DocumentNode.SelectNodes("//html//body//div[@class='g']//a/@href").First();

                HtmlAgilityPack.HtmlDocument doc3 = new HtmlAgilityPack.HtmlDocument();
                HtmlAgilityPack.HtmlWeb docHFile3 = new HtmlWeb();
                //I notice URLs returned this way have additional text. Split on = and remove the extra "&amp" from the href
                doc3 = docHFile3.Load(HttpUtility.HtmlDecode(HttpUtility.UrlDecode(googleNode.Attributes["href"].Value.Split('=')[1])).Replace("&sa", ""));

                //Oddly enough, when I hit the direct movie page from Google, I don't get a properly formatted Rotten Tomatoes page.
                //Instead, there's a JSON object in a script tag with the jsonLdSchema id. I can serialize that object for the information I need.
                foreach (HtmlNode node in doc3.DocumentNode.SelectNodes("//script[@id='jsonLdSchema']/text()"))
                {
                    JToken item = JToken.Parse(node.InnerHtml);
                    string rating = item["aggregateRating"]["ratingValue"].Value<string>();

                    foundRating = rating;
                }
            }
            catch
            {

            }

            if (String.IsNullOrEmpty(foundRating))
            {

            }
            return foundRating.Trim();
            //Movie IMDB Rating is setup
        }

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
                    newMovie.Title = title;
                    newMovie.SimpleTitle = title;
                }

                if (rated.Trim() != "")
                {
                    newMovie.Rating = rated;
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

                if (tomatorating.Trim() != "" && tomatorating.Trim() != "N/A")
                {
                    newMovie.tomatoRating = Convert.ToInt32(tomatorating);
                }
                newMovie.UploadedDate = DateTime.Now;

                movieDb.Movies.Add(newMovie);
                movieDb.SaveChanges();

                if (poster.Trim() != "")
                {
                    var posterInsertion = new MoviePoster();
                    try
                    {
                        //posterInsertion.Poster = client.DownloadData(poster);
                    }
                    catch
                    {
                        //posterInsertion.Poster = client.DownloadData("http://" + poster);
                    }
                    posterInsertion.MovieID = newMovie.id;

                    movieDb.MoviePosters.Add(posterInsertion);
                    movieDb.SaveChanges();
                }
            }

            return JsonSuccess;
        }

        public string ImdbYearScrape(string givenID)
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

            return movieReleased.Trim();
        }

        //public void AutoLogin()
        //{
        //    var givenUsername = Request.Cookies["Username"];
        //    var givenID = Request.Cookies["UserID"];
        //    if (givenUsername != null)
        //    {
        //        if (givenUsername.Value.Trim() != "")
        //        {
        //            Session["User"] = givenUsername.Value;
        //            Session["UserID"] = givenID.Value;
        //        }
        //    }
        //}

        //public void Login(string Username)
        //{
        //    String givenUser = Username.Trim();
        //    movieDB db = new movieDB();
        //    if (givenUser != "")
        //    {
        //        Session["User"] = givenUser;
        //        var user = (from u in db.Users
        //                    where u.Username == givenUser
        //                    select u).FirstOrDefault();
        //        HttpCookie cookie = new HttpCookie("Username", givenUser);
        //        cookie.Expires = DateTime.Now.AddYears(1);
        //        Response.Cookies.Add(cookie);
        //        if (user == null)
        //        {
        //            User newUser = new User();
        //            newUser.Username = givenUser;
        //            db.Users.Add(newUser);
        //            //db.Users.AddObject(newUser);
        //            db.SaveChanges();
        //        }

        //        if (user != null)
        //        {
        //            Session["UserID"] = user.UserID;
        //            HttpCookie cookie2 = new HttpCookie("UserID", user.UserID.ToString());
        //            cookie2.Expires = DateTime.Now.AddYears(1);
        //            Response.Cookies.Add(cookie2);
        //        }
        //    }
        //}

        //public ActionResult Navigation()
        //{
        //    AutoLogin();
        //    return View();
        //}

        public ActionResult UserList(string descript)
        {
            var userList = movieDb.Users.Where(d => d.Username.Contains(descript)).Select(d => d.Username).ToList();
            return Json(userList);
        }

        //public ActionResult CountWatched()
        //{
        //    var userID = Convert.ToInt32(Session["UserID"]);
        //    int watchedCount = 0;
        //    if (userID > 0)
        //    {
        //        watchedCount = (from v in db.Viewings
        //                        where v.UserID == userID
        //                        && v.ViewingType == "w"
        //                        select v.MovieID).Count();
        //    }
        //    return Json(watchedCount, JsonRequestBehavior.AllowGet);
        //}

        //public string ScrapeMultiple(string urls)
        //{
        //    var allLinks = urls.Split('-');
        //    var allHtml = "";
        //    foreach (string link in allLinks)
        //    {
        //        allHtml += ScrapeGiven(link);
        //    }
        //    return allHtml;
        //}

        //public string ScrapeGiven(string url)
        //{
        //    string data = "";
        //    HttpWebRequest request = (HttpWebRequest)WebRequest.Create(url);
        //    HttpWebResponse response = (HttpWebResponse)request.GetResponse();
        //    if (response.StatusCode == HttpStatusCode.OK)
        //    {
        //        Stream receiveStream = response.GetResponseStream();
        //        StreamReader readStream = null;
        //        if (response.CharacterSet == null)
        //            readStream = new StreamReader(receiveStream);
        //        else
        //            readStream = new StreamReader(receiveStream, Encoding.GetEncoding(response.CharacterSet));
        //        data = readStream.ReadToEnd();
        //        response.Close();
        //        readStream.Close();
        //    }

        //    return data;
        //}

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

        //public ActionResult SaveWatchlist(string watchList, int movieID)
        //{
        //    movieDB db = new movieDB();
        //    var userID = Convert.ToInt32(Session["UserID"]);

        //    var isSuggested = false;
        //    Viewing previousSuggest = (from v in db.Viewings
        //                               where v.MovieID == movieID &&
        //                               v.UserID == userID &&
        //                               v.ViewingType == "s"
        //                               select v).FirstOrDefault();

        //    if (previousSuggest != null)
        //    {
        //        isSuggested = true;
        //    }

        //    if (watchList != "on" && isSuggested)
        //    {
        //        db.Viewings.Remove(previousSuggest);
        //        //db.Viewings.DeleteObject(previousSuggest);
        //    }

        //    if (watchList == "on" && !isSuggested)
        //    {
        //        Viewing suggestedView = new Viewing();
        //        suggestedView.MovieID = movieID;
        //        suggestedView.UserID = userID;
        //        suggestedView.ViewingType = "s";
        //        //db.Viewings.AddObject(suggestedView);
        //    }
        //    db.SaveChanges();
        //    return null;
        //}

        //public ActionResult SaveWatched(string watched, int movieID)
        //{
        //    int givenID = Convert.ToInt32(Request.QueryString["ID"]);
        //    movieDB db = new movieDB();
        //    var userID = Convert.ToInt32(Session["UserID"]);

        //    Viewing previousWatch = (from v in db.Viewings
        //                             where v.MovieID == movieID &&
        //                             v.UserID == userID &&
        //                             v.ViewingType == "w"
        //                             select v).FirstOrDefault();

        //    if (previousWatch != null)
        //    {

        //        db.Viewings.Remove(previousWatch);
        //        //db.Viewings.DeleteObject(previousWatch);
        //    }

        //    if (watched == "on")
        //    {
        //        Viewing watchedView = new Viewing();
        //        watchedView.MovieID = movieID;
        //        watchedView.UserID = userID;
        //        watchedView.ViewingType = "w";
        //        db.Viewings.Add(watchedView);
        //    }

        //    db.SaveChanges();
        //    return null;
        //}

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

        //public ActionResult CountWatchlist()
        //{
        //    movieDB db = new movieDB();
        //    var userID = Convert.ToInt32(Session["UserID"]);
        //    int watchedCount = 0;
        //    if (userID > 0)
        //    {
        //        watchedCount = (from v in db.Viewings
        //                        where v.UserID == userID
        //                        && v.ViewingType == "s"
        //                        select v.MovieID).Count();
        //    }
        //    return Json(watchedCount, JsonRequestBehavior.AllowGet);
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
        public IActionResult ImageHandler(int id)
        {
            var poster = movieDb.MoviePosters.FirstOrDefault(d => d.MovieID == id);

            if (poster == null)
            {
                return NotFound();
            }

            //var imageData = poster.Poster;

            return File(new byte[] { }, "image/jpeg");
        }

        //public static byte[] ImageToByte(Image img)
        //{
        //    byte[] byteArray = new byte[0];
        //    using (MemoryStream stream = new MemoryStream())
        //    {
        //        img.Save(stream, System.Drawing.Imaging.ImageFormat.Png);
        //        stream.Close();

        //        byteArray = stream.ToArray();
        //    }
        //    return byteArray;
        //}

        //public ActionResult PosterCollage()
        //{
        //    //This is my attempt at a webpage to return one massive image of all my posters

        //    movieDB db = new movieDB();
        //    //Get the max number of movies for my batching routines
        //    int movieCount = db.Movies.OrderByDescending(m => m.id).FirstOrDefault().id;
        //    //allMovies will hold the results of each batch
        //    List<Movie> allMovies = new List<Movie>();

        //    //We're starting on the 0th query, getting 100 movies each time. Increasing this number
        //    //introduces instability from closed connections.

        //    int queryNumber = 0;
        //    int queryCount = 100;
        //    int queryMin = 0;

        //    //queryMin is the movie ID we're starting at, collecting queryCount movies from there.
        //    while (queryMin < movieCount)
        //    {
        //        queryMin = queryNumber * queryCount;
        //        List<Movie> movies = (from v in db.Movies
        //                              where v.id > queryMin &&
        //                              v.id <= queryMin + queryCount
        //                              select v).ToList();
        //        allMovies.AddRange(movies);
        //        queryNumber++;
        //    }
        //    //Take all my movies and order them by their simple title
        //    allMovies = allMovies.OrderBy(x => x.SimpleTitle).ToList();

        //    //Posters in the collage should be 75 x 100. Increasing these numbers makes the poster too large.
        //    int posterWidth = 75;
        //    int posterHeight = 100;

        //    //How many posters per row, and how does that effect the height/width of the final image:
        //    int rowLength = 25;
        //    int totalWidth = rowLength * posterWidth;
        //    int totalHeight = (int)(Math.Ceiling((double)movieCount / (double)rowLength) * posterHeight);

        //    //Create a new Bitmap of the calculated final size
        //    Bitmap combinedBitmap = new Bitmap(totalWidth, totalHeight);
        //    //Create a Graphics object from the bitmap so we can draw
        //    Graphics combinedGraphics = Graphics.FromImage(combinedBitmap);
        //    int drawingPosition = 0;
        //    int drawingHeight = 0;
        //    int rowCounter = 0;
        //    List<Bitmap> allPosters = new List<Bitmap>();
        //    //COMMENTED UNTIL I CAN FIX:
        //    //foreach (Movie movie in allMovies)
        //    //{
        //    //    TypeConverter tc = TypeDescriptor.GetConverter(typeof(Bitmap));
        //    //    Bitmap originalBitmap = (Bitmap)tc.ConvertFrom(movie.Poster);
        //    //    Bitmap resizeBitmap = new Bitmap(posterWidth, posterHeight);
        //    //    Graphics resizeGraphic = Graphics.FromImage(resizeBitmap);
        //    //    resizeGraphic.InterpolationMode = InterpolationMode.High;
        //    //    resizeGraphic.CompositingQuality = CompositingQuality.HighQuality;
        //    //    resizeGraphic.SmoothingMode = SmoothingMode.AntiAlias;
        //    //    resizeGraphic.DrawImage(originalBitmap, new Rectangle(0, 0, posterWidth, posterHeight));
        //    //    if (rowCounter == rowLength)
        //    //    {
        //    //        rowCounter = 0;
        //    //        drawingPosition = 0;
        //    //        drawingHeight += posterHeight;
        //    //    }
        //    //    combinedGraphics.DrawImage(resizeBitmap, new Point(drawingPosition, drawingHeight));
        //    //    drawingPosition += posterWidth;
        //    //    rowCounter++;
        //    //}

        //    byte[] posterBits = ImageToByte(combinedBitmap);
        //    return File(posterBits, "image/jpeg");
        //}

    }
}