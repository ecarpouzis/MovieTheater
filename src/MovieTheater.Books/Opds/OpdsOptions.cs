using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using System.Security.Claims;
using MovieTheater.Books.Identity;

namespace MovieTheater.Books.Opds
{
    /// <summary>
    /// The OPDS surface's own settings. They are read straight from <see cref="IConfiguration"/> rather than
    /// added to <c>BooksOptions</c> so this slice needs no change to <c>BooksServiceExtensions</c> and no service
    /// registration of its own — the controller builds them per request from the host's configuration, which is
    /// three dictionary lookups.
    ///
    /// <para><b>Why a SITE base at all.</b> Every other Books URL the host hands out points at the host
    /// (<c>Books:PublicBaseUrl</c>), because the media plane authenticates itself with a token in the path. OPDS
    /// is the opposite: an e-reader authenticates with HTTP <b>Basic</b>, that is verified at the SITE, and the
    /// site is what forwards <c>/opds/**</c> (prefix kept) to this host with the signed identity header. A feed
    /// link that pointed at the host directly would therefore reach an endpoint no e-reader can authenticate to.
    /// So <b>feed</b> links are built from <see cref="SiteBaseUrl"/> and <b>byte</b> links from the media plane's
    /// public base.</para>
    /// </summary>
    public sealed class OpdsOptions
    {
        /// <summary>Configuration section this reads first (<c>Opds:SiteBaseUrl</c>, <c>Opds:PageSize</c>, …).</summary>
        public const string Section = "Opds";

        /// <summary>What the docs print for an unconfigured site origin. Never emitted: see <see cref="OpdsUrls.FeedBase"/>.</summary>
        public const string PlaceholderSiteBaseUrl = "https://<site>";

        public const int DefaultPageSize = 50;
        public const int MaxPageSize = 200;

        /// <summary>
        /// The origin an e-reader talks to — the SITE, not this host. Null means "not configured", and the feed
        /// base then falls back to the forwarded/request origin so a local run still emits usable links.
        /// </summary>
        public string? SiteBaseUrl { get; init; }

        /// <summary>Entries per feed page. 50 is the standalone site's number and what the paging tests pin.</summary>
        public int PageSize { get; init; } = DefaultPageSize;

        /// <summary>The catalog's display name, shown as the root feed's title in every reader's library list.</summary>
        public string CatalogTitle { get; init; } = "Books";

        /// <summary>
        /// Kill switch. Default ON: the site-wide policy is that a lever ships enabled and is opted OUT of, and
        /// an OPDS catalog that has to be switched on is an OPDS catalog nobody discovers.
        /// </summary>
        public bool Enabled { get; init; } = true;

        /// <summary>
        /// Bind from configuration. <c>Opds:</c> wins; <c>Books:</c> is accepted for the two keys that plausibly
        /// belong beside the rest of the vertical's settings, so an operator who puts them there is not silently
        /// ignored.
        /// </summary>
        public static OpdsOptions From(IConfiguration? config)
        {
            if (config == null) return new OpdsOptions();
            var opds = config.GetSection(Section);
            var books = config.GetSection("Books");

            var site = Text(opds["SiteBaseUrl"]) ?? Text(books["SiteBaseUrl"]);
            if (site != null && site.Contains('<')) site = null;   // the documented placeholder is not a URL

            var enabled = Bool(opds["Enabled"]) ?? Bool(books["EnableOpds"]) ?? true;
            var size = int.TryParse(opds["PageSize"], out var n) ? Math.Clamp(n, 1, MaxPageSize) : DefaultPageSize;
            var title = Text(opds["Title"]) ?? Text(books["CatalogTitle"]) ?? "Books";

            return new OpdsOptions { SiteBaseUrl = site?.TrimEnd('/'), PageSize = size, CatalogTitle = title, Enabled = enabled };
        }

        private static string? Text(string? v) => string.IsNullOrWhiteSpace(v) ? null : v.Trim();
        private static bool? Bool(string? v) => bool.TryParse(v, out var b) ? b : null;
    }

    /// <summary>
    /// Everything one feed build needs to know about its caller and its links. A record so a test can hand the
    /// service a fixed base URL and a fabricated principal and compare the document byte for byte.
    /// </summary>
    /// <param name="User">The caller, as the identity header established it.</param>
    /// <param name="FeedBase">Origin for FEED links — the site (see <see cref="OpdsOptions.SiteBaseUrl"/>).</param>
    /// <param name="MediaBase">Origin for BYTE links — this host's public base. Null ⇒ no byte links are emitted.</param>
    /// <param name="MediaToken">A media capability minted for this caller. Null ⇒ no byte links are emitted.</param>
    public sealed record OpdsContext(
        ClaimsPrincipal User,
        string FeedBase,
        string? MediaBase = null,
        string? MediaToken = null,
        int PageSize = OpdsOptions.DefaultPageSize,
        string CatalogTitle = "Books")
    {
        /// <summary>The caller's maturity ceiling — the ONE gate, identical to every other Books surface.</summary>
        public int Ceiling => BooksIdentity.CeilingFor(User);

        /// <summary>The caller's site user id. Null means the personal shelves cannot be built.</summary>
        public int? UserId => BooksIdentity.UserId(User);

        /// <summary>True when byte links (thumbnail, download, page stream) can be built at all.</summary>
        public bool HasMedia => !string.IsNullOrEmpty(MediaBase) && !string.IsNullOrEmpty(MediaToken);
    }

    /// <summary>Every URL the feeds emit, built in one place so a route and its link cannot drift.</summary>
    public static class OpdsUrls
    {
        /// <summary>The route prefix. The site forwards <c>/opds/{**}</c> with the prefix KEPT, so it is the same on both sides.</summary>
        public const string Prefix = "/opds";

        /// <summary>
        /// The origin FEED links are built from, in order: the configured site base; the forwarded origin a
        /// reverse proxy stamped; the request's own origin. The placeholder is never emitted — an unconfigured
        /// host that answered <c>https://&lt;site&gt;/opds/recent</c> would hand every e-reader a dead link,
        /// whereas the request origin is at minimum the one that just worked.
        /// </summary>
        public static string FeedBase(OpdsOptions options, HttpRequest? request)
        {
            if (!string.IsNullOrEmpty(options.SiteBaseUrl)) return options.SiteBaseUrl!.TrimEnd('/');
            if (request != null)
            {
                var proto = First(request.Headers["X-Forwarded-Proto"].ToString()) ?? request.Scheme;
                var host = First(request.Headers["X-Forwarded-Host"].ToString());
                if (!string.IsNullOrEmpty(host)) return $"{proto}://{host}".TrimEnd('/');
                if (request.Host.HasValue) return $"{proto}://{request.Host.Value}".TrimEnd('/');
            }
            return OpdsOptions.PlaceholderSiteBaseUrl;
        }

        private static string? First(string headerValue)
        {
            if (string.IsNullOrWhiteSpace(headerValue)) return null;
            var comma = headerValue.IndexOf(',');
            var one = (comma < 0 ? headerValue : headerValue[..comma]).Trim();
            return one.Length == 0 ? null : one;
        }

        public static string Root(string feedBase) => $"{feedBase}{Prefix}";
        public static string Category(string feedBase, string category, int page = 1, string? key = null)
        {
            var url = $"{feedBase}{Prefix}/{Uri.EscapeDataString(category)}?page={page}";
            return key == null ? url : url + "&key=" + Uri.EscapeDataString(key);
        }
        public static string Series(string feedBase, int seriesId, int page = 1) => $"{feedBase}{Prefix}/series/{seriesId}?page={page}";
        public static string Search(string feedBase, string q, int page = 1) => $"{feedBase}{Prefix}/search?q={Uri.EscapeDataString(q)}&page={page}";
        public static string OpenSearch(string feedBase) => $"{feedBase}{Prefix}/opensearch.xml";

        /// <summary>
        /// The OPDS-PSE page template. It points at THIS controller, not straight at the media plane, for two
        /// reasons that are both load-bearing:
        /// <list type="number">
        /// <item>PSE's <c>{pageNumber}</c> is <b>1-based</b> and the media plane's page index is <b>0-based</b>.
        /// Handing a client the media URL directly would shift every page by one and make the last page a 404.</item>
        /// <item>A media token lives 12 hours and an e-reader caches a feed for months. A token baked into the
        /// feed would stream pages today and fail silently next week; this route mints a fresh one per request.</item>
        /// </list>
        /// The controller redirects to the media plane, so the BYTES still never travel through the catalog path.
        /// </summary>
        public static string PageTemplate(string feedBase, int itemId) =>
            $"{feedBase}{Prefix}/pages/{itemId}/{{pageNumber}}?maxWidth={{maxWidth}}";

        /// <summary>A stable, location-independent entry id. A feed's id must survive the catalog moving origins.</summary>
        public static string ItemUrn(int itemId) => $"urn:mt-books:item:{itemId}";
        public static string SeriesUrn(int seriesId) => $"urn:mt-books:series:{seriesId}";
        public static string CategoryUrn(string category, string? key = null) =>
            key == null ? $"urn:mt-books:category:{category}" : $"urn:mt-books:category:{category}:{key}";
    }
}
