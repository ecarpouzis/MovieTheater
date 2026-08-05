using System;
using System.IO;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using MovieTheater.Music;
using MovieTheater.Services;

namespace MovieTheater.Controllers
{
    /// <summary>
    /// Serves album art (music-plan.md §2.5) written by the <c>music-art</c> CLI. Same shape as
    /// <see cref="PosterImageController"/>: a bounded static byte cache keyed by id+variant+version,
    /// an ETag from the file's mtime, and <c>immutable</c> caching for a versioned (<c>?v=</c>)
    /// request. Unauthenticated like the other image routes — the art is not the media.
    ///
    /// <para>Album art files are written once and never regenerated (project rule), so a versioned
    /// URL really is immutable and the memory cache can never go stale.</para>
    /// </summary>
    public class MusicImageController : ControllerBase
    {
        // Album art is small (a 300px thumb is a few KB); 64 MB holds the whole catalog's thumbs.
        private static readonly MemoryCache ArtByteCache = new(new MemoryCacheOptions
        {
            SizeLimit = 64L * 1024 * 1024,
        });

        private readonly MovieTheaterConfiguration config;

        public MusicImageController(MovieTheaterConfiguration config)
        {
            this.config = config;
        }

        [HttpGet("/MusicImage/{albumId}")]
        public IActionResult Main(int albumId) => ArtResponse(albumId, thumbnail: false);

        [HttpGet("/MusicImageThumb/{albumId}")]
        public IActionResult Thumb(int albumId) => ArtResponse(albumId, thumbnail: true);

        private IActionResult ArtResponse(int albumId, bool thumbnail)
        {
            var path = MusicArtStore.PathFor(config, albumId, thumbnail);
            if (path == null) return NotFound();

            bool versioned = Request.Query.TryGetValue("v", out var ver) && !string.IsNullOrEmpty(ver);
            string cacheKey = versioned ? $"music|{(thumbnail ? "s" : "m")}|{albumId}|{ver}" : null;
            if (cacheKey != null && ArtByteCache.TryGetValue(cacheKey, out byte[] cached) && cached != null)
            {
                Response.Headers["Cache-Control"] = "public, max-age=31536000, immutable";
                return File(cached, "image/png");
            }

            if (!System.IO.File.Exists(path)) return NotFound();

            var etag = $"\"{System.IO.File.GetLastWriteTimeUtc(path).Ticks}\"";
            Response.Headers["Cache-Control"] = versioned ? "public, max-age=31536000, immutable" : "public, max-age=3600";
            Response.Headers["ETag"] = etag;
            if (Request.Headers.TryGetValue("If-None-Match", out var ifNoneMatch) && ifNoneMatch == etag)
                return StatusCode(304);

            byte[] bytes;
            try { bytes = System.IO.File.ReadAllBytes(path); }
            catch (IOException) { return NotFound(); }

            if (cacheKey != null)
                ArtByteCache.Set(cacheKey, bytes, new MemoryCacheEntryOptions
                {
                    Size = bytes.Length,
                    SlidingExpiration = TimeSpan.FromHours(12),
                });

            return File(bytes, "image/png");
        }
    }
}
