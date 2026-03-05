using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.OData.Query;
using Microsoft.EntityFrameworkCore;
using MovieTheater.Db;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;

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
        public async Task<IQueryable<Movie>> GetMovies()
        {
            int ageRestriction = 100;
            var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier);
            if (userIdClaim != null && int.TryParse(userIdClaim.Value, out int userId))
            {
                var setting = await movieDb.UserSettings
                    .FirstOrDefaultAsync(s => s.SettingKey == "AgeRestriction" && s.UserID == userId);
                if (setting != null && int.TryParse(setting.SettingValue, out int restriction))
                    ageRestriction = restriction;
            }

            return movieDb.Movies.Where(m =>
                !movieDb.RatingMaps.Any(rm => rm.MovieRating == m.Rating && rm.MPARatingID > ageRestriction));
        }
    }
}

