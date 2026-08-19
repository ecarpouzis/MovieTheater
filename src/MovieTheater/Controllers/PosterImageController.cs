using Microsoft.AspNetCore.Mvc;
using MovieTheater.Services;
using MovieTheater.Services.Poster;
using MovieTheater.Web;
using System.Threading.Tasks;

namespace MovieTheater.Controllers
{
    public class PosterImageController : ControllerBase
    {
        private readonly IPosterImageRepository imageRepository;

        // The shared versioned-image convention (ImageCacheResponder): RAM byte-cache on versioned
        // requests, mtime ETag + 304, immutable-vs-1h Cache-Control. 128 MB of poster bytes (thumbs
        // are small → thousands of them).
        private static readonly ImageCacheResponder Responder = new(128L * 1024 * 1024);

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
            // GetImage fetch-on-miss covers dev's read-through repository (DevPosterImageRepository).
            return await Responder.ServeAsync(
                this,
                $"{bucket ?? "movie"}|{variant}|{movieId}",
                () => imageRepository.GetImageModifiedDate(movieId, variant, bucket),
                () => imageRepository.GetImage(movieId, variant, bucket));
        }
    }
}

