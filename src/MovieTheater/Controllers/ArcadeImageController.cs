using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using MovieTheater.Arcade;
using MovieTheater.Db;
using MovieTheater.Services;

namespace MovieTheater.Controllers
{
    /// <summary>
    /// Serves arcade box art (arcade-plan.md §5) from <c>ArcadeGame.BoxArtPath</c> (a path relative to the
    /// posters mount). At ~49k games we don't bulk-store art up front — instead this route <b>lazily</b>
    /// fetches box art from libretro-thumbnails, downscales it to a thumbnail, and caches it on the FIRST
    /// view of a game (via <see cref="ArcadeBoxArt"/>). Games whose art doesn't exist (or whose system has
    /// no repo — e.g. arcade) are negatively cached so they 404 fast and the card shows its placeholder.
    /// The bulk <c>arcade-boxart</c> CLI is the same fetch, run ahead of time; either path works.
    /// </summary>
    public class ArcadeImageController : ControllerBase
    {
        private const int ThumbPx = 220;

        // One long-lived client for the on-demand fetches; a per-game negative cache so a miss is tried once.
        private static readonly HttpClient Http = CreateHttp();
        private static readonly ConcurrentDictionary<int, byte> NoArt = new();

        private static HttpClient CreateHttp()
        {
            var h = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            h.DefaultRequestHeaders.UserAgent.ParseAdd("MovieTheater-arcade-boxart/1.0");
            return h;
        }

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
            if (string.IsNullOrEmpty(config.MoviePostersDir)) return NotFound();
            var root = Path.GetFullPath(config.MoviePostersDir);

            var game = await movieDb.ArcadeGames.FirstOrDefaultAsync(g => g.Id == id);
            if (game == null) return NotFound();

            // 1. Already cached → serve it.
            if (!string.IsNullOrWhiteSpace(game.BoxArtPath))
            {
                var cached = ResolveUnderRoot(root, game.BoxArtPath);
                if (cached != null && System.IO.File.Exists(cached)) return ServeFile(cached);
            }

            // 2. Known to have no art, or a system we don't source (arcade) → placeholder (fast 404).
            if (NoArt.ContainsKey(id) || !ArcadeBoxArt.HasRepo(game.System)) return NotFound();

            // 3. First view → fetch + downscale + cache, then serve.
            var thumb = await ArcadeBoxArt.TryFetchThumbnailAsync(Http, game.System, game.CloudRetroGameKey, ThumbPx);
            if (thumb == null) { NoArt.TryAdd(id, 0); return NotFound(); }

            var rel = $"arcade/{game.System}/{id}.png";
            var dest = ResolveUnderRoot(root, rel);
            if (dest != null)
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                    await System.IO.File.WriteAllBytesAsync(dest, thumb);
                    game.BoxArtPath = rel;
                    await movieDb.SaveChangesAsync();
                }
                catch { /* couldn't cache (mount read-only?) — still serve the bytes we fetched */ }
            }
            Response.Headers["Cache-Control"] = "public, max-age=86400";
            return File(thumb, "image/png");
        }

        // Resolve a stored-relative path under the posters root, rejecting traversal ("../") escapes.
        private static string? ResolveUnderRoot(string root, string rel)
        {
            var full = Path.GetFullPath(Path.Combine(root, rel));
            if (full != root && !full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                return null;
            return full;
        }

        private IActionResult ServeFile(string full)
        {
            var etag = $"\"{System.IO.File.GetLastWriteTimeUtc(full).Ticks}\"";
            if (Request.Headers.TryGetValue("If-None-Match", out var inm) && inm == etag) return StatusCode(304);
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
