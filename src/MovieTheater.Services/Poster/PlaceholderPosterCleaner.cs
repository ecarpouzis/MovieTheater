using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using MovieTheater.Db;

namespace MovieTheater.Services.Poster
{
    /// <summary>
    /// Removes "posters" that are actually IMDb's placeholder logo (…/imdb_logo.png) — what OMDB/IMDb hand
    /// back when a title has no real poster. Deletes the cached image files (main + thumbnail) and the
    /// PosterDetails row, so the title falls back to the card placeholder (/Image then 404s). Shared by the
    /// CLI command and the startup self-heal task. Idempotent: a no-op once none remain.
    /// </summary>
    public static class PlaceholderPosterCleaner
    {
        public const string Marker = "imdb_logo";

        public static async Task<int> RunAsync(MovieDb db, IPosterImageRepository imageRepo, CancellationToken ct = default)
        {
            var movies = await db.MoviePosterDetails.Where(p => p.PosterLink != null && p.PosterLink.Contains(Marker)).ToListAsync(ct);
            var series = await db.SeriesPosterDetails.Where(p => p.PosterLink != null && p.PosterLink.Contains(Marker)).ToListAsync(ct);
            if (movies.Count == 0 && series.Count == 0) return 0;

            foreach (var m in movies)
            {
                await imageRepo.DeleteImage(m.MovieId, PosterImageVariant.Main);
                await imageRepo.DeleteImage(m.MovieId, PosterImageVariant.Thumbnail);
                db.MoviePosterDetails.Remove(m);
            }
            foreach (var s in series)
            {
                await imageRepo.DeleteImage(s.SeriesId, PosterImageVariant.Main);
                await imageRepo.DeleteImage(s.SeriesId, PosterImageVariant.Thumbnail);
                db.SeriesPosterDetails.Remove(s);
            }
            await db.SaveChangesAsync(ct);
            return movies.Count + series.Count;
        }
    }
}
