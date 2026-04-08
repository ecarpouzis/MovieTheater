using Microsoft.AspNetCore.Mvc;
using MovieTheater.Services;
using MovieTheater.Services.Poster;
using System;
using System.Threading.Tasks;

namespace MovieTheater.Controllers
{
    public class PosterImageController : ControllerBase
    {
        private readonly IPosterImageRepository imageRepository;

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

        private async Task<IActionResult> PosterResponse(int movieId, PosterImageVariant variant)
        {
            // Try to get the modified date for caching. In dev mode the file may not
            // exist yet but the repository can fetch it on demand (DevPosterImageRepository).
            var modifiedDate = await imageRepository.GetImageModifiedDate(movieId, variant);
            byte[]? posterBytes = null;

            if (modifiedDate == null)
            {
                // Attempt to fetch the image (this will download & save in DevPosterImageRepository).
                posterBytes = await imageRepository.GetImage(movieId, variant);
                if (posterBytes == null)
                    return NotFound();

                // Re-check modified date; if repository doesn't supply it, use now.
                modifiedDate = await imageRepository.GetImageModifiedDate(movieId, variant) ?? DateTimeOffset.UtcNow;
            }

            var etag = $"\"{modifiedDate.Value.Ticks}\"";

            var hasVersion = Request.Query.ContainsKey("v");
            Response.Headers["Cache-Control"] = hasVersion ? "public, max-age=31536000, immutable" : "public, max-age=3600";
            Response.Headers["ETag"] = etag;

            if (Request.Headers.TryGetValue("If-None-Match", out var ifNoneMatch) && ifNoneMatch == etag)
                return StatusCode(304);

            // If we already fetched the bytes above, use them. Otherwise load from repository.
            if (posterBytes == null)
            {
                posterBytes = await imageRepository.GetImage(movieId, variant);
                if (posterBytes == null)
                    return NotFound();
            }

            return File(posterBytes, "image/png");
        }
    }
}

