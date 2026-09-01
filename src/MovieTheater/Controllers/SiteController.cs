using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MovieTheater.Services;

namespace MovieTheater.Controllers
{
    /// <summary>
    /// Facts about the SITE ITSELF that the browser cannot work out on its own — currently one: where
    /// the media plane lives, so a client can find out whether it can actually reach it.
    /// </summary>
    /// <remarks>
    /// <para><b>Why this exists (2026-09-01).</b> The app and the media plane are served from two
    /// different places over two different address families. The app answers on IPv4 from its host;
    /// the StreamGateway (music, movie, photo and book BYTES) lives on Ziggy, which the fiber cutover
    /// put behind carrier-grade NAT — so it is reachable over IPv6 ONLY. A visitor with no IPv6 loads
    /// the whole site perfectly, browses the whole library, presses play, and gets silence: every
    /// media fetch fails at the network layer with nothing to show for it.</para>
    ///
    /// <para>The browser cannot detect that by itself. It never builds a gateway URL — the API hands
    /// it signed absolute URLs at play time — so before this endpoint there was no address for the
    /// client to test until the moment it already failed. This returns the base the site will use, and
    /// the client probes it once (see <c>useMediaReachable</c>) to turn an inexplicable silence into a
    /// sentence that names the cause.</para>
    ///
    /// <para><b>Anonymous on purpose.</b> It returns a hostname that public DNS already publishes and
    /// nothing else — no token, no path, no secret. Gating it behind the streaming policy would mean
    /// the people most likely to be confused (someone who has just signed in and cannot play) are
    /// exactly the ones who could not be told why.</para>
    /// </remarks>
    [AllowAnonymous]
    public class SiteController : Controller
    {
        private readonly MovieTheaterConfiguration config;

        public SiteController(MovieTheaterConfiguration config)
        {
            this.config = config;
        }

        /// <summary>
        /// The media plane's base URL, for a reachability probe. <c>mediaBase</c> is null when the
        /// gateway is not configured at all (dev boxes, and any deployment serving media locally) —
        /// the client then skips the probe rather than warning about a plane that does not exist.
        /// </summary>
        [HttpGet("/API/Site/MediaProbe")]
        public IActionResult MediaProbe() =>
            Json(new { mediaBase = string.IsNullOrWhiteSpace(config.StreamGatewayBaseUrl) ? null : config.StreamGatewayBaseUrl.TrimEnd('/') });
    }
}
