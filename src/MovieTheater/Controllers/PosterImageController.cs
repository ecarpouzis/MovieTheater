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
            var modifiedDate = await imageRepository.GetImageModifiedDate(movieId, variant);
            if (modifiedDate == null)
                return NotFound();

            var etag = $"\"{modifiedDate.Value.Ticks}\"";

            Response.Headers["Cache-Control"] = "public, max-age=3600";
            Response.Headers["ETag"] = etag;

            if (Request.Headers.TryGetValue("If-None-Match", out var ifNoneMatch) && ifNoneMatch == etag)
                return StatusCode(304);

            var poster = await imageRepository.GetImage(movieId, variant);
            if (poster == null)
                return NotFound();

            return File(poster, "image/png");
        }
    }
}

