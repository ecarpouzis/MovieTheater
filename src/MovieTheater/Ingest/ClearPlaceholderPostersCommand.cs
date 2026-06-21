using System.Linq;
using System.Threading.Tasks;
using CliFx;
using CliFx.Attributes;
using CliFx.Infrastructure;
using Microsoft.EntityFrameworkCore;
using MovieTheater.Console;
using MovieTheater.Db;
using MovieTheater.Services;
using MovieTheater.Services.Poster;

namespace MovieTheater.Ingest
{
    /// <summary>
    /// Removes "posters" that are actually IMDb's placeholder logo (…/imdb_logo.png) — the image OMDB/IMDb
    /// hand back when a title has no real poster (see <see cref="PosterFetchService"/>). Deletes the cached
    /// image files (main + thumbnail) so the card falls back to its placeholder, and drops the PosterDetails
    /// row. Must run where the poster files live (prod = the posters mount). Dry-run by default.
    /// </summary>
    [Command("clear-placeholder-posters", Description = "Delete IMDb-logo placeholder posters (files + PosterDetails) so those titles show the card placeholder.")]
    public class ClearPlaceholderPostersCommand : BasicDICommand, ICommand
    {
        [CommandOption("apply", Description = "Write changes. Omit for a dry run (default).")]
        public bool Apply { get; set; }

        private readonly IDbContextFactory<MovieDb> dbFactory;
        private readonly IPosterImageRepository imageRepo;

        public ClearPlaceholderPostersCommand(MovieTheaterConfiguration config) : base(config)
        {
            dbFactory = GetRequiredService<IDbContextFactory<MovieDb>>();
            imageRepo = GetRequiredService<IPosterImageRepository>();
        }

        public async ValueTask ExecuteAsync(IConsole console)
        {
            var w = console.Output;
            await using var db = await dbFactory.CreateDbContextAsync();

            var movies = await db.MoviePosterDetails.Where(p => p.PosterLink != null && p.PosterLink.Contains("imdb_logo")).ToListAsync();
            var series = await db.SeriesPosterDetails.Where(p => p.PosterLink != null && p.PosterLink.Contains("imdb_logo")).ToListAsync();

            w.WriteLine($"Placeholder (imdb_logo) posters: {movies.Count} movie(s), {series.Count} series{(Apply ? "" : "  (dry run)")}");
            foreach (var m in movies) w.WriteLine($"  M{m.MovieId}");
            foreach (var s in series) w.WriteLine($"  S{s.SeriesId}");
            if (!Apply) { w.WriteLine("\nDRY RUN — re-run with --apply."); return; }

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
            await db.SaveChangesAsync();
            w.WriteLine($"Cleared {movies.Count + series.Count} placeholder poster(s).");
        }
    }
}
