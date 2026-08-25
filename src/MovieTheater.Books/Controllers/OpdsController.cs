using System.Globalization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using MovieTheater.Books.Access;
using MovieTheater.Books.Db;
using MovieTheater.Books.Identity;
using MovieTheater.Books.Media;
using MovieTheater.Books.Opds;

namespace MovieTheater.Books.Controllers
{
    /// <summary>
    /// The OPDS catalog — the vertical's e-reader surface. Chunky, Panels, KyBook, Moon+ Reader and Calibre all
    /// speak it, so one endpoint set turns the library into a shelf inside every reading app.
    ///
    /// <para><b>Authentication happens at the SITE, not here.</b> An e-reader sends HTTP Basic; the pod verifies
    /// it and forwards <c>/opds/{**}</c> (prefix kept) to this host with the same signed identity header every
    /// other Books route already rides. So this controller parses no credentials of its own — it is an ordinary
    /// identity-gated controller under the host's fallback policy, and <c>User</c> is the site's user exactly as
    /// it is in <c>ItemsController</c>. Anything else would put a second password path in front of the
    /// library.</para>
    ///
    /// <para><b>Two origins, deliberately.</b> FEED links point at the site (an e-reader can only authenticate
    /// there); BYTE links — cover, download — point at this host's media plane with a minted capability token.
    /// The one exception is the OPDS-PSE page link, which points back here and redirects: see
    /// <see cref="Page"/>.</para>
    ///
    /// <para><c>GET /opds/ping</c> is mapped by the host as a minimal API (the R5 seam proof) and is NOT
    /// redefined here; a literal route segment outranks this controller's <c>{category}</c> parameter, so the
    /// two coexist.</para>
    /// </summary>
    [ApiController]
    [Route("opds")]
    public sealed class OpdsController : ControllerBase
    {
        private readonly BooksDb db;
        private readonly BooksOptions books;
        private readonly IConfiguration? configuration;

        public OpdsController(BooksDb db, BooksOptions books, IConfiguration? configuration = null)
        {
            this.db = db;
            this.books = books;
            this.configuration = configuration;
        }

        // ── feeds ─────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>GET /opds — the navigation feed every reader fetches first.</summary>
        [HttpGet("")]
        public async Task<IActionResult> Root(CancellationToken ct = default)
        {
            if (!Options().Enabled) return NotFound();
            return Feed(await Service().BuildRootAsync(Context(), ct), OpdsXml.NavigationContentType);
        }

        /// <summary>
        /// GET /opds/{category} — one shelf. <c>?page=</c> is 1-BASED (what every OPDS client's next link
        /// carries), <c>?key=</c> names the publisher for the publisher drill. An unknown category is a 404, and
        /// so is a personal shelf asked for without an identity.
        /// </summary>
        [HttpGet("{category}")]
        public async Task<IActionResult> Category(
            string category, [FromQuery] int page = 1, [FromQuery] string? key = null, CancellationToken ct = default)
        {
            if (!Options().Enabled) return NotFound();
            var definition = OpdsCategories.Find(category);
            var xml = await Service().BuildCategoryAsync(category, Context(), page, key, ct);
            if (xml == null) return NotFound();
            return Feed(xml, definition is { IsNavigation: true } ? OpdsXml.NavigationContentType : OpdsXml.AcquisitionContentType);
        }

        /// <summary>GET /opds/series/{id} — one series' issues in reading order. 404 when the caller can see none.</summary>
        [HttpGet("series/{id:int}")]
        public async Task<IActionResult> Series(int id, [FromQuery] int page = 1, CancellationToken ct = default)
        {
            if (!Options().Enabled) return NotFound();
            var xml = await Service().BuildSeriesFeedAsync(id, Context(), page, ct);
            if (xml == null) return NotFound();
            return Feed(xml, OpdsXml.AcquisitionContentType);
        }

        /// <summary>GET /opds/search?q= — the same FTS5 index the web catalog searches.</summary>
        [HttpGet("search")]
        public async Task<IActionResult> Search([FromQuery] string? q = null, [FromQuery] int page = 1, CancellationToken ct = default)
        {
            if (!Options().Enabled) return NotFound();
            return Feed(await Service().BuildSearchAsync(q, Context(), page, ct), OpdsXml.AcquisitionContentType);
        }

        /// <summary>GET /opds/opensearch.xml — the description document that puts a search box in the reader.</summary>
        [HttpGet("opensearch.xml")]
        public IActionResult OpenSearch()
        {
            if (!Options().Enabled) return NotFound();
            return Feed(OpdsFeedService.BuildOpenSearchDescription(Context()), OpdsXml.OpenSearchContentType);
        }

        // ── OPDS-PSE page streaming ───────────────────────────────────────────────────────────────────────

        /// <summary>
        /// GET /opds/pages/{id}/{pageNumber} — the target of the PSE stream template, which redirects to the
        /// media plane.
        ///
        /// <para><b>Why the hop exists at all.</b> Two things it fixes, both silent failures if the feed pointed
        /// straight at the media plane: PSE's <paramref name="pageNumber"/> is 1-BASED while the media plane's
        /// page index is 0-based (so every page would be off by one and the last page a 404), and a media token
        /// lives 12 hours while an e-reader keeps a cached feed for months (so a token baked into the feed would
        /// stream today and fail next week). This route converts the index and mints a fresh token per request;
        /// the BYTES still come off the media plane, never through the catalog path.</para>
        ///
        /// <para><paramref name="maxWidth"/> is read as a STRING because PSE clients that do not implement the
        /// <c>{maxWidth}</c> substitution send the placeholder literally — binding it as an int would answer 400
        /// to a request that is merely unspecific.</para>
        /// </summary>
        [HttpGet("pages/{id:int}/{pageNumber:int}")]
        public async Task<IActionResult> Page(int id, int pageNumber, [FromQuery] string? maxWidth = null, CancellationToken ct = default)
        {
            if (!Options().Enabled) return NotFound();
            if (pageNumber < 1) return NotFound();

            // The one authorization, exactly as every other by-id route runs it: 404 for absent AND for
            // forbidden, so a gated account cannot map the library it is gated out of.
            var item = await ItemAccess.GetAuthorizedItemAsync(db, User, id, allowExcluded: true, ct);
            if (item == null) return NotFound();

            var token = MintMediaToken();
            if (token == null || string.IsNullOrEmpty(books.PublicBaseUrl))
                return StatusCode(StatusCodes.Status503ServiceUnavailable, new { configured = false });

            var url = BooksMediaRoutes.PageUrl(books.PublicBaseUrl!, token, id, pageNumber - 1);
            if (int.TryParse(maxWidth, NumberStyles.Integer, CultureInfo.InvariantCulture, out var width) && width > 0)
                url += "?maxWidth=" + width.ToString(CultureInfo.InvariantCulture);
            return Redirect(url);
        }

        // ── wiring ────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// The OPDS settings. Read from <see cref="IConfiguration"/> per request rather than from
        /// <c>BooksOptions</c> so this slice adds NO service registration and needs no edit to
        /// <c>BooksServiceExtensions</c> — it is three dictionary lookups against an already-built configuration.
        /// </summary>
        private OpdsOptions Options() => OpdsOptions.From(configuration);

        private OpdsFeedService Service() => new(db);

        private OpdsContext Context()
        {
            var options = Options();
            return new OpdsContext(
                User,
                OpdsUrls.FeedBase(options, Request),
                books.PublicBaseUrl?.TrimEnd('/'),
                MintMediaToken(),
                options.PageSize,
                options.CatalogTitle);
        }

        /// <summary>
        /// A media capability for THIS caller, carrying the same ceiling and admin flag the identity header
        /// established — a token can never widen what its holder may fetch. Null when the host has no media
        /// configured, in which case the feeds simply carry no byte links.
        /// </summary>
        private string? MintMediaToken()
        {
            if (string.IsNullOrEmpty(books.MediaTokenSecret) || string.IsNullOrEmpty(books.PublicBaseUrl)) return null;
            if (BooksIdentity.UserId(User) is not int userId) return null;
            return BooksMediaToken.MintNow(books.MediaTokenSecret!, userId,
                BooksIdentity.CeilingFor(User), BooksIdentity.IsAdmin(User), out _);
        }

        /// <summary>
        /// The response. <see cref="ContentResult"/> with an explicit UTF-8 content type and no BOM: the document
        /// was written by <see cref="OpdsXml.Utf8StringWriter"/> so its prolog says utf-8, and a BOM in front of
        /// an XML declaration is what makes some readers reject a feed outright.
        /// </summary>
        private ContentResult Feed(string xml, string contentType) => new()
        {
            Content = xml,
            ContentType = contentType,
            StatusCode = StatusCodes.Status200OK,
        };
    }
}
