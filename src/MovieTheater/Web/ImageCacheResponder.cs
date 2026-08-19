using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using System;
using System.Threading.Tasks;

namespace MovieTheater.Web
{
    /// <summary>
    /// The versioned-image serving convention, shared by the poster, boardgame and music image
    /// controllers (each used to carry its own copy; the boardgame copy was missing the byte
    /// cache entirely). The arcade image controller deliberately does NOT use this — its cache
    /// policy is a bounded week, because enrichment can rewrite bytes without moving the artV
    /// token (see its CachePolicy comment).
    ///
    /// The contract:
    /// <list type="bullet">
    /// <item>A versioned request (?v=&lt;version&gt;) is immutable for that version — the UI always
    /// passes one and bumps it when the image changes — so bytes are served from a bounded RAM
    /// cache across all viewers (grids fire bursts of image requests; repeats become memory hits),
    /// with <c>max-age=1y, immutable</c>.</item>
    /// <item>An unversioned request gets <c>max-age=1h</c> plus an mtime-ticks ETag and 304s on
    /// If-None-Match.</item>
    /// <item><paramref name="getModified"/> returning null means "not on disk yet" — the responder
    /// calls <paramref name="getBytes"/> (which may fetch-on-demand: the dev read-through repos,
    /// music's lazy remote art) and re-reads the modified date after.</item>
    /// </list>
    /// </summary>
    public sealed class ImageCacheResponder
    {
        private readonly MemoryCache byteCache;

        public ImageCacheResponder(long sizeLimitBytes)
        {
            byteCache = new MemoryCache(new MemoryCacheOptions { SizeLimit = sizeLimitBytes });
        }

        public async Task<IActionResult> ServeAsync(
            ControllerBase controller,
            string cacheKeyBase,
            Func<Task<DateTimeOffset?>> getModified,
            Func<Task<byte[]?>> getBytes,
            string contentType = "image/png")
        {
            var request = controller.Request;
            var response = controller.Response;

            bool versioned = request.Query.TryGetValue("v", out var ver) && !string.IsNullOrEmpty(ver);
            string? cacheKey = versioned ? $"{cacheKeyBase}|{ver}" : null;
            if (cacheKey != null && byteCache.TryGetValue(cacheKey, out byte[]? cachedBytes) && cachedBytes != null)
            {
                response.Headers["Cache-Control"] = "public, max-age=31536000, immutable";
                return controller.File(cachedBytes, contentType);
            }

            var modifiedDate = await getModified();
            byte[]? bytes = null;

            if (modifiedDate == null)
            {
                bytes = await getBytes();
                if (bytes == null)
                    return controller.NotFound();
                modifiedDate = await getModified() ?? DateTimeOffset.UtcNow;
            }

            var etag = $"\"{modifiedDate.Value.Ticks}\"";
            response.Headers["Cache-Control"] = versioned ? "public, max-age=31536000, immutable" : "public, max-age=3600";
            response.Headers["ETag"] = etag;

            if (request.Headers.TryGetValue("If-None-Match", out var ifNoneMatch) && ifNoneMatch == etag)
                return controller.StatusCode(304);

            if (bytes == null)
            {
                bytes = await getBytes();
                if (bytes == null)
                    return controller.NotFound();
            }

            if (cacheKey != null)
                byteCache.Set(cacheKey, bytes, new MemoryCacheEntryOptions
                {
                    Size = bytes.Length,
                    SlidingExpiration = TimeSpan.FromHours(12),
                });

            return controller.File(bytes, contentType);
        }
    }
}
