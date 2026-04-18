using Microsoft.AspNetCore.Mvc;
using MovieTheater.Services.BoardgameImage;
using System;
using System.Threading.Tasks;

namespace MovieTheater.Controllers
{
    public class BoardgameImageController : ControllerBase
    {
        private readonly IBoardgameImageRepository imageRepository;

        public BoardgameImageController(IBoardgameImageRepository imageProvider)
        {
            this.imageRepository = imageProvider;
        }

        [HttpGet("/BoardgameImage/{id}")]
        public async Task<IActionResult> ImageHandler(int id)
        {
            return await BoardgameImageResponse(id, BoardgameImageVariant.Main);
        }

        [HttpGet("/BoardgameImageThumb/{id}")]
        public async Task<IActionResult> ImageThumbHandler(int id)
        {
            return await BoardgameImageResponse(id, BoardgameImageVariant.Thumbnail);
        }

        private async Task<IActionResult> BoardgameImageResponse(int boardgameId, BoardgameImageVariant variant)
        {
            // Try to get the modified date for caching. In dev mode the file may not
            // exist yet but the repository can fetch it on demand (DevBoardgameImageRepository).
            var modifiedDate = await imageRepository.GetImageModifiedDate(boardgameId, variant);
            byte[]? imageBytes = null;

            if (modifiedDate == null)
            {
                // Attempt to fetch the image (this will download & save in DevBoardgameImageRepository).
                imageBytes = await imageRepository.GetImage(boardgameId, variant);
                if (imageBytes == null)
                    return NotFound();

                // Re-check modified date; if repository doesn't supply it, use now.
                modifiedDate = await imageRepository.GetImageModifiedDate(boardgameId, variant) ?? DateTimeOffset.UtcNow;
            }

            var etag = $"\"{modifiedDate.Value.Ticks}\"";

            var hasVersion = Request.Query.ContainsKey("v");
            Response.Headers["Cache-Control"] = hasVersion ? "public, max-age=31536000, immutable" : "public, max-age=3600";
            Response.Headers["ETag"] = etag;

            if (Request.Headers.TryGetValue("If-None-Match", out var ifNoneMatch) && ifNoneMatch == etag)
                return StatusCode(304);

            // If we already fetched the bytes above, use them. Otherwise load from repository.
            if (imageBytes == null)
            {
                imageBytes = await imageRepository.GetImage(boardgameId, variant);
                if (imageBytes == null)
                    return NotFound();
            }

            return File(imageBytes, "image/png");
        }
    }
}
