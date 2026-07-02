using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using MovieTheater.Db;
using MovieTheater.Services;

namespace MovieTheater.Controllers
{
    /// <summary>
    /// Serves arcade box art (arcade-plan.md §5): the file named by <c>ArcadeGame.BoxArtPath</c>, a path
    /// relative to the posters mount (the same persistent volume movie posters live on). Simpler than the
    /// poster pipeline — no thumbnails, no versions, no per-bucket namespace — so it's its own small route
    /// rather than a variant on <c>PosterImageController</c>. A game with no box art (most, until
    /// <c>arcade-boxart</c> fills them) just 404s and the card shows its labeled placeholder.
    /// </summary>
    public class ArcadeImageController : ControllerBase
    {
        private readonly MovieDb movieDb;
        private readonly MovieTheaterConfiguration config;

        public ArcadeImageController(MovieDb movieDb, MovieTheaterConfiguration config)
        {
            this.movieDb = movieDb;
            this.config = config;
        }

        [HttpGet("/ArcadeImage/{id}")]
        public async Task<IActionResult> BoxArt(int id)
        {
            var boxArtPath = await movieDb.ArcadeGames
                .Where(g => g.Id == id)
                .Select(g => g.BoxArtPath)
                .FirstOrDefaultAsync();
            if (string.IsNullOrWhiteSpace(boxArtPath) || string.IsNullOrEmpty(config.MoviePostersDir))
                return NotFound();

            // Resolve under the posters root with a canonical-path traversal guard (BoxArtPath is stored
            // relative; a leaked "../" must not escape the mount).
            var root = Path.GetFullPath(config.MoviePostersDir);
            var full = Path.GetFullPath(Path.Combine(root, boxArtPath));
            if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal) && full != root)
                return NotFound();
            if (!System.IO.File.Exists(full))
                return NotFound();

            var modified = System.IO.File.GetLastWriteTimeUtc(full);
            var etag = $"\"{modified.Ticks}\"";
            if (Request.Headers.TryGetValue("If-None-Match", out var inm) && inm == etag)
                return StatusCode(304);

            Response.Headers["Cache-Control"] = "public, max-age=86400";
            Response.Headers["ETag"] = etag;
            return PhysicalFile(full, ContentTypeFor(full));
        }

        private static string ContentTypeFor(string path) =>
            Path.GetExtension(path).ToLowerInvariant() switch
            {
                ".jpg" or ".jpeg" => "image/jpeg",
                ".webp" => "image/webp",
                _ => "image/png",
            };
    }
}
