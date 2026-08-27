using System.Linq;
using Microsoft.EntityFrameworkCore;
using MovieTheater.Db;

namespace MovieTheater.Web
{
    /// <summary>
    /// The browse path's BASE queries — quarantine, the series de-duplication and the age gate — as
    /// statics, so the controller and the out-of-request <see cref="CatalogWarmupService"/> read the
    /// SAME set. They depend on exactly one viewer fact, the age restriction, which is why a warmed
    /// index built off-request is usable by every viewer at that age (see <see cref="BrowseCacheKeys"/>).
    /// </summary>
    public static class CatalogQueries
    {
        public static IQueryable<Movie> BaseMovies(MovieDb db, int ageRestriction) =>
            db.Movies
                .Include(m => m.PosterDetails)
                // Quarantine: rows still pending library-ingest review (ReviewBatch != null) are
                // invisible to every browse/odata path until they're approved.
                .Where(m => m.ReviewBatch == null)
                // Series live in their own table; exclude series-typed Movie rows so a series shows
                // once (from Series), never doubled during the dual-existence window.
                .Where(m => m.TitleType != TitleType.TvSeries && m.TitleType != TitleType.TvMiniSeries)
                // Age gate on the EFFECTIVE rating (real cert → legacy → inferred).
                .Where(RatingGate.MovieVisibleAtAge(db, ageRestriction));

        public static IQueryable<Series> BaseSeries(MovieDb db, int ageRestriction) =>
            db.Series
                .Include(s => s.PosterDetails)
                .Where(s => s.ReviewBatch == null)
                .Where(RatingGate.SeriesVisibleAtAge(db, ageRestriction));
    }
}
