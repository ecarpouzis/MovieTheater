using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.OData.Query;
using MovieTheater.Db;
using System.Linq;

namespace MovieTheater.Controllers
{
    [ApiController]
    public class ODataMoviesController : ControllerBase
    {
        private readonly MovieDb movieDb;

        public ODataMoviesController(MovieDb movieDb)
        {
            this.movieDb = movieDb;
        }

        [EnableQuery]
        [HttpGet("/odata/Movies")]
        public IQueryable<Movie> GetMovies([FromQuery] int? maxMpaRatingId = null)
        {
            IQueryable<Movie> movies = movieDb.Movies;
            if (maxMpaRatingId.HasValue)
            {
                movies = movies.Where(m => !movieDb.RatingMaps.Any(rm => rm.MovieRating == m.Rating && rm.MPARatingID > maxMpaRatingId.Value));
            }
            return movies;
        }
    }
}
