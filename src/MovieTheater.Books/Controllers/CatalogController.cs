using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.OData.Extensions;
using Microsoft.AspNetCore.OData.Query;
using MovieTheater.Books.Access;
using MovieTheater.Books.Db;
using MovieTheater.Books.Identity;
using MovieTheater.Books.Projections;

namespace MovieTheater.Books.Controllers
{
    /// <summary>
    /// The flat catalog: <c>GET /odata/catalog</c>. Query-options-only OData, exactly the way the site's own
    /// <c>/odata/Movies</c> works — <see cref="EnableQueryAttribute"/> on a plain attribute-routed action, no EDM
    /// route components, no <c>ODataController</c>. The response is a JSON array of <see cref="ItemSummary"/>;
    /// <c>$filter</c>, <c>$orderby</c>, <c>$select</c>, <c>$top</c>, <c>$skip</c> and <c>$count</c> all ride
    /// <c>[EnableQuery]</c>, which is why the projection has to stay flat (no collections, no subqueries).
    ///
    /// <para>Ordering: the action returns the query UNORDERED and lets <c>EnsureStableOrdering</c> append the key,
    /// so a page is always deterministic even when the caller sends no <c>$orderby</c>; every caller-supplied
    /// <c>$orderby</c> gets the same id tiebreaker appended. That is the "every ORDER BY ends with a stable id"
    /// law, enforced by the framework rather than restated per sort.</para>
    ///
    /// <para>Two custom parameters carry what OData cannot express:
    /// <c>q=</c> is the FTS5 search (title / series / creators / tags, indexed by <c>ItemFts</c>) and
    /// <c>directory=</c> is the Directory drill — the items physically inside one folder, shadow duplicates
    /// INCLUDED, because that view mirrors the folder tree. Everything else uses the normal exclusion filter.
    /// The maturity gate applies to both, always.</para>
    /// </summary>
    [ApiController]
    [Route("odata/catalog")]
    public sealed class CatalogController : ControllerBase
    {
        /// <summary>How many FTS hits a search may contribute. Deep enough for any browse page; bounded so a
        /// one-letter prefix search cannot drag the whole index into an IN-subquery.</summary>
        public const int FtsLimit = 10_000;

        private readonly BooksDb db;
        public CatalogController(BooksDb db) => this.db = db;

        /// <summary>
        /// The exact facet filters ride beside the OData options (see <see cref="ExactFilters"/>): repeatable
        /// <c>author= artist= tag= event=</c> and their <c>ex*</c> excludes. They narrow the ITEM set before the
        /// projection, so <c>$filter</c>, <c>$count</c> and paging all see the same rows.
        /// </summary>
        [HttpGet]
        [EnableQuery(PageSize = 120, MaxTop = 500)]
        public IQueryable<ItemSummary> Get(
            [FromQuery] string? q = null,
            [FromQuery] int? directory = null,
            [FromQuery] string? kind = null,
            [FromQuery] string[]? author = null,
            [FromQuery] string[]? artist = null,
            [FromQuery] string[]? tag = null,
            [FromQuery(Name = "event")] string[]? eventName = null,
            [FromQuery] string[]? exAuthor = null,
            [FromQuery] string[]? exArtist = null,
            [FromQuery] string[]? exTag = null,
            [FromQuery] string[]? exEvent = null)
        {
            // [EnableQuery] runs AFTER the action and builds its own EDM from the CLR type unless the request
            // already carries one — and the one it builds is PascalCase, so a client filtering on the camelCase
            // JSON it just received would be told the property does not exist. Handing it the shared model is what
            // makes /odata/catalog and /browse/groups accept the identical $filter string.
            if (HttpContext != null) HttpContext.ODataFeature().Model = CatalogEdm.Model;

            var itemKind = ParseKind(kind);

            // directory= is the file-explorer drill: it shows shadow duplicates alongside normal items (dimmed by
            // the client). Every other caller gets ExcludeHidden, which removes them. The ceiling applies either way.
            var query = directory is int folderId
                ? ItemAccess.DirectoryItems(db, User, itemKind, folderId)
                : ItemAccess.VisibleItems(db, User, itemKind);

            if (!string.IsNullOrWhiteSpace(q))
            {
                var match = BuildFtsQuery(q.Trim());
                if (match.Length == 0) return Enumerable.Empty<ItemSummary>().AsQueryable();
                // Kept as IQueryable so EF renders a subquery instead of an id list — SQLite's 999-variable limit
                // would otherwise cap a search at 999 hits.
                var ids = ItemFts.Search(db, match, FtsLimit);
                query = query.Where(i => ids.Contains(i.Id));
            }

            query = ExactFilters.From(author, artist, tag, eventName, exAuthor, exArtist, exTag, exEvent).Apply(db, query);

            var summary = query.Select(ItemSummary.Project);
            EmitTotalCount(summary);
            return summary;
        }

        /// <summary>
        /// <c>$count=true</c> → the filtered total in an <c>X-Total-Count</c> header.
        ///
        /// <para>Stated plainly because it is a deviation: OData's own <c>@odata.count</c> envelope is written by
        /// the OData output formatter, and that formatter only engages for an EDM-ROUTED endpoint. This endpoint is
        /// query-options-only (the site's mode — see <c>/odata/Movies</c>), so the response is a plain JSON array
        /// and there is nowhere in it for a count to live. <c>[EnableQuery]</c> still parses and applies
        /// <c>$count</c>; the number is simply dropped. Rather than let a caller believe it got a total it did not,
        /// the total is computed here — through the SAME parser, so it honours <c>$filter</c> — and returned in a
        /// header. It costs one extra COUNT query, so callers ask for it on the FIRST page only (the band engine's
        /// rule: band 0 establishes the total, later bands send <c>$count=false</c>).</para>
        /// </summary>
        private void EmitTotalCount(IQueryable<ItemSummary> summary)
        {
            if (Request == null) return;
            if (!Request.Query.TryGetValue("$count", out var raw) || !bool.TryParse(raw, out var wanted) || !wanted) return;
            var filter = Request.Query["$filter"].ToString();
            var counted = string.IsNullOrWhiteSpace(filter) ? summary : CatalogEdm.ApplyFilter(summary, filter);
            Response.Headers[TotalCountHeader] = counted.Count().ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        public const string TotalCountHeader = "X-Total-Count";

        internal static ItemKind ParseKind(string? kind) =>
            string.Equals(kind, "book", StringComparison.OrdinalIgnoreCase) ? ItemKind.Book : ItemKind.Comic;

        // Strip ALL non-word characters: a denylist misses tokens like '.', which is FTS5 syntax (so "B.P.R.D."
        // becomes a syntax error rather than a search). Anything that is not a word character is a separator, and
        // every term gets a prefix star so typing keeps narrowing.
        private static readonly Regex FtsNonWord = new(@"[^\w\s]", RegexOptions.Compiled);

        internal static string BuildFtsQuery(string q)
        {
            var words = FtsNonWord.Replace(q, " ")
                .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            return words.Length == 0 ? "" : string.Join(" ", words.Select(w => w + "*"));
        }
    }
}
