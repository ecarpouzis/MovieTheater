using Microsoft.AspNetCore.Mvc;
using MovieTheater.Services.BoardgameImage;
using MovieTheater.Web;
using System.Threading.Tasks;

namespace MovieTheater.Controllers
{
    public class BoardgameImageController : ControllerBase
    {
        // The shared versioned-image convention (ImageCacheResponder). This controller previously
        // carried its own copy of the ETag/304/immutable logic and NO byte cache — the shared
        // responder brings the RAM fast path the poster/music controllers already had. 32 MB is
        // generous for ~315 games' art. (Note: "versioned" now means a NON-EMPTY ?v=, matching the
        // other image routes; a bare ?v= used to count here.)
        private static readonly ImageCacheResponder Responder = new(32L * 1024 * 1024);

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
            // GetImage fetch-on-miss covers dev's read-through repository (DevBoardgameImageRepository).
            return await Responder.ServeAsync(
                this,
                $"bg|{variant}|{boardgameId}",
                () => imageRepository.GetImageModifiedDate(boardgameId, variant),
                () => imageRepository.GetImage(boardgameId, variant));
        }
    }
}
