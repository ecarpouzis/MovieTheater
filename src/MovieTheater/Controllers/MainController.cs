using Microsoft.AspNetCore.Mvc;

namespace MovieTheater.Controllers
{
    public class MainController : ControllerBase
    {
        [HttpGet("/api/status")]
        public ActionResult Status()
        {
            return Ok();
        }
    }
}
