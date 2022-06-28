using Microsoft.AspNetCore.Mvc;
using MovieTheater.Services;
using System.Threading.Tasks;

namespace MovieTheater.Controllers
{
    public class PosterImageController : ControllerBase
    {
        private readonly IImageHandler imageHandler;

        public PosterImageController(IImageHandler imageHandler)
        {
            this.imageHandler = imageHandler;
        }

        [HttpGet("/Image/{id}")]
        public async Task<IActionResult> ImageHandler(int id)
        {
            var poster = await imageHandler.GetPosterImageFromID(id);

            if (poster == null)
            {
                return NotFound();
            }

            return File(poster, "image/png");
        }

        [HttpGet("/ImageThumb/{id}")]
        public async Task<IActionResult> ImageThumbHandler(int id)
        {
            var poster = await imageHandler.GetPosterImageFromID(id, true);

            if (poster == null)
            {
                return NotFound();
            }

            return File(poster, "image/png");
        }
    }
}
