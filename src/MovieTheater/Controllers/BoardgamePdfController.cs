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

        [HttpGet("/BoardgamePdf/{id:int}/{slot:int}")]
        public async Task<IActionResult> GetPdf(int id, int slot)
        {
            var bytes = await pdfRepository.GetPdfAsync(id, slot);
            if (bytes == null)
                return NotFound();

            return File(bytes, "application/pdf", enableRangeProcessing: true);
        }
    }
}
