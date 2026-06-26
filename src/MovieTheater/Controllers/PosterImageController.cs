using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using MovieTheater.Services;
using MovieTheater.Services.Poster;
using System;
using System.Threading.Tasks;

namespace MovieTheater.Controllers
{
    public class PosterImageController : ControllerBase
    {
        private readonly IPosterImageRepository imageRepository;

        // A small in-memory cache of poster bytes keyed by id+variant+bucket+version. A versioned
        // request (?v=) is immutable for that version, so once a poster is read from disk it's served
        // from RAM — no stat, no file read — across all viewers. The guide/browse fire bursts of poster
        // requests; this turns the repeat ones into memory hits. Bounded so it can't grow unbounded.
        private static readonly MemoryCache PosterByteCache = new(new MemoryCacheOptions
        {
            SizeLimit = 128L * 1024 * 1024, // 128 MB of poster bytes (thumbs are small → thousands of them)
        });

        public PosterImageController(IPosterImageRepository imageProvider)
        {
            this.imageRepository = imageProvider;
        }

        [HttpGet("/Image/{id}")]
        public async Task<IActionResult> ImageHandler(int id)
        {
            return await PosterResponse(id, PosterImageVariant.Main);
        }

        [HttpGet("/ImageThumb/{id}")]
        public async Task<IActionResult> ImageThumbHandler(int id)
        {
            return await PosterResponse(id, PosterImageVariant.Thumbnail);
        }

        // Series posters live in their own namespace because Movie and Series ids are NOT disjoint — a
        // given id can be both a Movie and a Series, so /Image/{id} (the Movie namespace) would serve the
        // movie's poster for a same-id series. /SeriesImage keeps them apart.
        [HttpGet("/SeriesImage/{id}")]
        public async Task<IActionResult> SeriesImageHandler(int id)
        {
            return await PosterResponse(id, PosterImageVariant.Main, PosterBucket.Series);
        }

        [HttpGet("/SeriesImageThumb/{id}")]
        public async Task<IActionResult> SeriesImageThumbHandler(int id)
        {
            return await PosterResponse(id, PosterImageVariant.Thumbnail, PosterBucket.Series);
        }

        // MiscVideo has a disjoint id space from Movie/Series, so its posters are served from a
        // separate "misc" namespace to avoid colliding with /Image/{id}. Most misc videos have no
        // poster (no IMDb id / poster source), so a 404 here is expected and the card shows a
        // placeholder.
        [HttpGet("/MiscImage/{id}")]
        public async Task<IActionResult> MiscImageHandler(int id)
        {
            return await PosterResponse(id, PosterImageVariant.Main, PosterBucket.Misc);
        }

        [HttpGet("/MiscImageThumb/{id}")]
        public async Task<IActionResult> MiscImageThumbHandler(int id)
        {
            return await PosterResponse(id, PosterImageVariant.Thumbnail, PosterBucket.Misc);
        }

        private async Task<IActionResult> PosterResponse(int movieId, PosterImageVariant variant, string? bucket = null)
        {
            // Fast path: a versioned request (?v=<posterVersion>) is immutable for that version — the UI
            // always passes it — so serve the bytes straight from RAM, skipping the disk stat + read. A
            // new poster gets a new version, hence a new key, so this never goes stale.
            bool versioned = Request.Query.TryGetValue("v", out var ver) && !string.IsNullOrEmpty(ver);
            string? cacheKey = versioned ? $"{bucket ?? "movie"}|{variant}|{movieId}|{ver}" : null;
            if (cacheKey != null && PosterByteCache.TryGetValue(cacheKey, out byte[]? cachedBytes) && cachedBytes != null)
            {
                Response.Headers["Cache-Control"] = "public, max-age=31536000, immutable";
                return File(cachedBytes, "image/png");
            }

            // Try to get the modified date for caching. In dev mode the file may not
            // exist yet but the repository can fetch it on demand (DevPosterImageRepository).
            var modifiedDate = await imageRepository.GetImageModifiedDate(movieId, variant, bucket);
            byte[]? posterBytes = null;

            if (modifiedDate == null)
            {
                // Attempt to fetch the image (this will download & save in DevPosterImageRepository).
                posterBytes = await imageRepository.GetImage(movieId, variant, bucket);
                if (posterBytes == null)
                    return NotFound();

                // Re-check modified date; if repository doesn't supply it, use now.
                modifiedDate = await imageRepository.GetImageModifiedDate(movieId, variant, bucket) ?? DateTimeOffset.UtcNow;
            }

            var etag = $"\"{modifiedDate.Value.Ticks}\"";

            Response.Headers["Cache-Control"] = versioned ? "public, max-age=31536000, immutable" : "public, max-age=3600";
            Response.Headers["ETag"] = etag;

            if (Request.Headers.TryGetValue("If-None-Match", out var ifNoneMatch) && ifNoneMatch == etag)
                return StatusCode(304);

            // If we already fetched the bytes above, use them. Otherwise load from repository.
            if (posterBytes == null)
            {
                posterBytes = await imageRepository.GetImage(movieId, variant, bucket);
                if (posterBytes == null)
                    return NotFound();
            }

            // Cache versioned bytes so the next request (any viewer) is a memory hit, not a disk read.
            if (cacheKey != null)
                PosterByteCache.Set(cacheKey, posterBytes, new MemoryCacheEntryOptions
                {
                    Size = posterBytes.Length,
                    SlidingExpiration = TimeSpan.FromHours(12),
                });

            return File(posterBytes, "image/png");
        }
    }
}

