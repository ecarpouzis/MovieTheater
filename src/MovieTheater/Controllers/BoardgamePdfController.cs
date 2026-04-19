using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using MovieTheater.Services.BoardgameImage;

namespace MovieTheater.Controllers
{
    public class BoardgamePdfController : Controller
    {
        private readonly BoardgamePdfRepository pdfRepository;

        public BoardgamePdfController(BoardgamePdfRepository pdfRepository)
        {
            this.pdfRepository = pdfRepository;
        }

        [HttpGet("/BoardgamePdf/{id:int}")]
        public async Task<IActionResult> GetPdf(int id)
        {
            var bytes = await pdfRepository.GetPdfAsync(id);
            if (bytes == null)
                return NotFound();

            return File(bytes, "application/pdf", enableRangeProcessing: true);
        }
    }
}
