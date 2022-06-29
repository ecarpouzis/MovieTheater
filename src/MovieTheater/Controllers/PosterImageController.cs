using Microsoft.AspNetCore.Mvc;
using MovieTheater.Services;
using MovieTheater.Services.Poster;
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
            var poster = await imageRepository.GetImage(id, PosterImageVariant.Main);

            if (poster == null)
            {
                return NotFound();
            }

            return File(poster, "image/png");
        }

        [HttpGet("/ImageThumb/{id}")]
        public async Task<IActionResult> ImageThumbHandler(int id)
        {
            var poster = await imageRepository.GetImage(id, PosterImageVariant.Thumbnail);

            if (poster == null)
            {
                return NotFound();
            }

            return File(poster, "image/png");
        }
    }
}
