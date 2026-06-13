using System.Linq;
using MovieTheater.Db;

namespace MovieTheater.Web
{
    /// <summary>
    /// The single source of the age-gate rule. Both the browse path (GetMovie) and the
    /// streaming path (StreamController) resolve a movie's MPA rating id through here so
    /// the two can never drift (streaming-plan.md §6).
    /// </summary>
    public static class RatingGate
    {
        /// <summary>
        /// Maps a movie's free-text rating (e.g. "PG-13") to its MPA rating id via
        /// <see cref="RatingMap"/>. Unknown/blank ratings map to 0 (most permissive).
        /// </summary>
        public static int MpaRatingIdFor(MovieDb movieDb, string? movieRating)
        {
            if (string.IsNullOrWhiteSpace(movieRating))
                return 0;

            var trimmed = movieRating.Trim();
            var map = movieDb.RatingMaps.FirstOrDefault(rm => rm.MovieRating == trimmed);
            return map?.MPARatingID ?? 0;
        }
    }
}
