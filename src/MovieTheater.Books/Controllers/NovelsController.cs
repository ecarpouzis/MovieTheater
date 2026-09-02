using System.Globalization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using MovieTheater.Books.Access;
using MovieTheater.Books.Db;
using MovieTheater.Books.Identity;
using MovieTheater.Books.Media;
using MovieTheater.Books.Projections;
using MovieTheater.Books.Services;

namespace MovieTheater.Books.Controllers
{
    public record NovelFacetOption(string Value, int Count);

    public sealed class NovelFacets
    {
        public List<NovelFacetOption> Authors { get; init; } = [];
        public List<NovelFacetOption> Series { get; init; } = [];
        public List<NovelFacetOption> Publishers { get; init; } = [];
        public List<NovelFacetOption> Decades { get; init; } = [];
        public List<NovelFacetOption> Tags { get; init; } = [];

        /// <summary>
        /// The group axes a BOOK shelf can be banded by (<see cref="BrowseController.BookGroupAxes"/>) — the
        /// Group pill's vocabulary, advertised rather than assumed.
        /// </summary>
        public List<string> GroupAxes { get; init; } = [];

        /// <summary>
        /// True when this binary applies the novels filter (<c>?book.author=</c> &amp;c.) on the GROUPED
        /// endpoints. It rides here because the Novels section already fetches this payload, and it exists
        /// because of a silent failure: an older host does not reject <c>book.author=</c>, it IGNORES it, so
        /// a grouped Novels view would show the whole library under a rail full of active chips. An old host
        /// omits the field, it deserializes to false, and the section stays flat — which is what it is now.
        /// </summary>
        public bool BookFilters { get; init; }
    }

    /// <summary>
    /// <b>Novels</b> — the prose half of the library (<c>Item.Kind == Book</c>): the Calibre-native EPUB shelf,
    /// with its own filters and facets. The standalone site called this <c>BooksController</c>; here "book" is
    /// already the entity kind, so the SURFACE is named for what a reader calls the thing.
    ///
    /// <para><b>Books have their own gate</b>, and it is the strictest one in the vertical: a book's maturity
    /// lives on its own current <c>Insight</c> row, and a book with no rating at all is HIDDEN below ceiling 3.
    /// That is the standalone's fail-safe, kept exactly — an unclassified book is not assumed safe. It is
    /// enforced in <see cref="MaturityFilter"/>, so this controller does not restate it; it just starts from
    /// <see cref="ItemAccess.VisibleItems"/> like every other list surface.</para>
    ///
    /// <para><b>The filters are exact-equality, multi-valued, OR within a facet and AND across</b> — the
    /// standalone's semantics, comma-separated per parameter. In v2 they land on rows rather than on columns
    /// scraped at request time: an author is an <c>ItemCredit(Source=Calibre, Role=Author)</c>, a series and a
    /// publisher are <c>BookDetail</c> fields, a decade is <c>Item.ResolvedYear</c> (not a string prefix of a
    /// publication date, which is what made the old query need <c>SUBSTR</c> and let a malformed date invent a
    /// "0100s" facet), and a tag is an <c>ItemTag(Category, Value)</c> EXISTS.</para>
    ///
    /// <para><c>GET /novels/{id}</c> is the SAME payload <c>/items/{id}</c> returns — it calls the same
    /// <see cref="ItemDetailBuilder"/>. A book detail that drifted from an item detail would be two truths about
    /// one row.</para>
    /// </summary>
    [ApiController]
    [Route("novels")]
    public sealed class NovelsController : ControllerBase
    {
        public const int MaxTop = 200;
        public const int AuthorFacetLimit = 400;
        public const int SeriesFacetLimit = 400;
        public const int PublisherFacetLimit = 300;
        public const int TagFacetLimit = 200;

        /// <summary>Calibre's own author role — the one credit source a book actually has.</summary>
        public const string AuthorRole = NovelFilters.AuthorRole;

        /// <summary>Backstop TTL; the facets only move when the library does. Same policy as the browse facets.</summary>
        private static readonly TimeSpan FacetsTtl = TimeSpan.FromHours(48);

        private readonly BooksDb db;
        private readonly IMemoryCache cache;
        private readonly BooksOptions options;
        private readonly ThumbnailService thumbnails;
        private readonly CatalogCacheVersion? version;

        public NovelsController(BooksDb db, IMemoryCache cache, BooksOptions options, ThumbnailService thumbnails, CatalogCacheVersion? version = null)
        {
            this.db = db;
            this.cache = cache;
            this.options = options;
            this.thumbnails = thumbnails;
            this.version = version;
        }

        /// <summary>
        /// GET /novels?author=&amp;series=&amp;publisher=&amp;decade=&amp;tag=&amp;q=&amp;skip=&amp;top=&amp;orderby=
        /// &amp;excludeTag=&amp;minRating=&amp;unknown=
        ///
        /// <para>The three the standalone's Books view had beyond the facets: <c>excludeTag</c> (the same
        /// composite spelling as <c>tag</c>, NOT EXISTS — its default chip was "not adult-romance"),
        /// <c>minRating</c> (a floor on the resolved 0–100 rating) and <c>unknown=true</c> (ONLY the books with no
        /// current insight row — the "no metadata yet" pile, the inverse of what the rest of the rail can reach).
        /// The response also carries <c>maturity</c> per row (the current insight's 0–3, null when unrated) beside
        /// <c>covers</c>, because the row projection is the shared one and may not grow a book-only column.</para>
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> List(
            [FromQuery] string? author = null,
            [FromQuery] string? series = null,
            [FromQuery] string? publisher = null,
            [FromQuery] string? decade = null,
            [FromQuery] string? tag = null,
            [FromQuery] string? q = null,
            [FromQuery] int skip = 0,
            [FromQuery] int top = 60,
            [FromQuery] string? orderby = null,
            [FromQuery] string? excludeTag = null,
            [FromQuery] int? minRating = null,
            [FromQuery] bool unknown = false,
            CancellationToken ct = default)
        {
            skip = Math.Max(0, skip);
            top = Math.Clamp(top, 1, MaxTop);

            var query = Filtered(author, series, publisher, decade, tag, q, excludeTag, minRating, unknown);
            var total = await query.CountAsync(ct);
            var items = await Sorted(query.Select(ItemSummary.Project), orderby)
                .Skip(skip).Take(top).ToListAsync(ct);

            var ids = items.Select(i => i.Id).ToList();
            var maturity = ids.Count == 0
                ? new Dictionary<int, int?>()
                : await db.Insights.AsNoTracking()
                    .Where(n => n.SubjectKind == SubjectKind.Item && n.IsCurrent && n.SubjectId != null && ids.Contains(n.SubjectId.Value))
                    .GroupBy(n => n.SubjectId!.Value)
                    .Select(g => new { Id = g.Key, Maturity = g.Max(n => n.Maturity) })
                    .ToDictionaryAsync(x => x.Id, x => x.Maturity, ct);

            var media = MediaUrls.For(options, User);
            return Ok(new
            {
                total,
                skip,
                top,
                items,
                covers = items.ToDictionary(i => i.Id.ToString(CultureInfo.InvariantCulture), i => media.Thumb(i.Id)),
                maturity = items.ToDictionary(i => i.Id.ToString(CultureInfo.InvariantCulture), i => maturity.GetValueOrDefault(i.Id)),
            });
        }

        /// <summary>
        /// GET /novels/letters — the flat A–Z buckets over the same filtered set, in the list's own order (R9 S0:
        /// the site's strip shows page numbers unless the source can bucket the flat order). <c>orderby=title</c>
        /// buckets on the title; anything else buckets on the author line, the default sort's key.
        /// </summary>
        [HttpGet("letters")]
        public async Task<IActionResult> Letters(
            [FromQuery] string? author = null,
            [FromQuery] string? series = null,
            [FromQuery] string? publisher = null,
            [FromQuery] string? decade = null,
            [FromQuery] string? tag = null,
            [FromQuery] string? q = null,
            [FromQuery] string? orderby = null,
            [FromQuery] string? excludeTag = null,
            [FromQuery] int? minRating = null,
            [FromQuery] bool unknown = false,
            CancellationToken ct = default)
        {
            var summary = Filtered(author, series, publisher, decade, tag, q, excludeTag, minRating, unknown).Select(ItemSummary.Project);
            var byTitle = string.Equals((orderby ?? "").Trim(), "title", StringComparison.OrdinalIgnoreCase);
            IQueryable<string?> keys = byTitle
                ? summary.OrderBy(s => s.Title).ThenBy(s => s.Id).Select(s => s.Title)
                : summary.OrderBy(s => s.CreatorsCsv).ThenBy(s => s.Series).ThenBy(s => s.Title).ThenBy(s => s.Id).Select(s => s.CreatorsCsv);
            var buckets = new List<BrowseController.LetterBucket>();
            var indexOf = new Dictionary<string, int>(StringComparer.Ordinal);
            var i = 0;
            await foreach (var k in keys.AsAsyncEnumerable().WithCancellation(ct))
            {
                var ch = k is { Length: > 0 } ? char.ToUpperInvariant(k[0]) : '#';
                var letter = ch is >= 'A' and <= 'Z' ? ch.ToString() : "#";
                if (indexOf.TryGetValue(letter, out var at)) buckets[at] = buckets[at] with { Count = buckets[at].Count + 1 };
                else { indexOf[letter] = buckets.Count; buckets.Add(new BrowseController.LetterBucket(letter, 1, i)); }
                i++;
            }
            return Ok(new { total = i, letters = buckets });
        }

        /// <summary>
        /// GET /novels/facets — the option lists with counts, computed over the books this caller may see, so a
        /// restricted account never learns that an author or a tag it is gated out of exists.
        ///
        /// <para>Like the standalone's, the counts are over the GATED set and not over the currently-selected
        /// filters: the rail is what you could choose, not what you have chosen.</para>
        /// </summary>
        [HttpGet("facets")]
        public async Task<IActionResult> Facets(CancellationToken ct = default)
        {
            var key = $"books:novels:facets:{UserSig()}";
            if (cache.TryGetValue(key, out NovelFacets? hit) && hit != null) return Ok(hit);

            var books = Visible();
            var ids = books.Select(i => i.Id);

            var authors = await db.ItemCredits.AsNoTracking()
                .Where(c => c.Source == TagSource.Calibre && c.Role == AuthorRole && c.Name != null && c.Name != "")
                .Join(books, c => c.ItemId, i => i.Id, (c, i) => new { Value = c.Name!, c.ItemId })
                .Distinct()
                .GroupBy(x => x.Value).Select(g => new { g.Key, Count = g.Count() })
                .OrderByDescending(x => x.Count).ThenBy(x => x.Key).Take(AuthorFacetLimit).ToListAsync(ct);

            var seriesRows = await db.BookDetails.AsNoTracking()
                .Where(b => b.SeriesName != null && b.SeriesName != "")
                .Join(books, b => b.ItemId, i => i.Id, (b, i) => new { Value = b.SeriesName! })
                .GroupBy(x => x.Value).Select(g => new { g.Key, Count = g.Count() })
                .OrderByDescending(x => x.Count).ThenBy(x => x.Key).Take(SeriesFacetLimit).ToListAsync(ct);

            var publisherRows = await db.BookDetails.AsNoTracking()
                .Where(b => b.Publisher != null && b.Publisher != "")
                .Join(books, b => b.ItemId, i => i.Id, (b, i) => new { Value = b.Publisher! })
                .GroupBy(x => x.Value).Select(g => new { g.Key, Count = g.Count() })
                .OrderByDescending(x => x.Count).ThenBy(x => x.Key).Take(PublisherFacetLimit).ToListAsync(ct);

            var decadeRows = await books.Where(i => i.ResolvedYear != null)
                .GroupBy(i => i.ResolvedYear!.Value / 10).Select(g => new { g.Key, Count = g.Count() })
                .ToListAsync(ct);

            var tagRows = await db.ItemTags.AsNoTracking()
                .Where(t => t.Value != "")
                .Join(books, t => t.ItemId, i => i.Id, (t, i) => new { t.Category, t.Value, t.ItemId })
                .Distinct()
                .GroupBy(x => new { x.Category, x.Value }).Select(g => new { g.Key.Category, g.Key.Value, Count = g.Count() })
                .OrderByDescending(x => x.Count).ThenBy(x => x.Category).ThenBy(x => x.Value)
                .Take(TagFacetLimit).ToListAsync(ct);

            var facets = new NovelFacets
            {
                Authors = authors.Select(x => new NovelFacetOption(x.Key, x.Count)).ToList(),
                Series = seriesRows.Select(x => new NovelFacetOption(x.Key, x.Count)).ToList(),
                Publishers = publisherRows.Select(x => new NovelFacetOption(x.Key, x.Count)).ToList(),
                // Decades stay CHRONOLOGICAL (newest first), never count-sorted: a decade rail that jumps around
                // by popularity is unreadable.
                Decades = decadeRows.OrderByDescending(d => d.Key)
                    .Select(d => new NovelFacetOption($"{d.Key * 10}s", d.Count)).ToList(),
                // The tag facet's value is the COMPOSITE "category:value" — the same string ?tag= takes, so a
                // client can echo a chip straight back as a filter.
                Tags = tagRows.Select(x => new NovelFacetOption($"{x.Category}:{x.Value}", x.Count)).ToList(),
                // Compile-time, so a cached payload always agrees with the binary that cached it.
                GroupAxes = [.. BrowseController.BookGroupAxes],
                BookFilters = true,
            };

            var entry = new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = FacetsTtl, Size = 1 };
            if (version != null) entry.AddExpirationToken(version.Token);   // expires with the catalog, not the clock
            cache.Set(key, facets, entry);
            return Ok(facets);
        }

        /// <summary>
        /// GET /novels/{id} — the full item detail, identical to <c>/items/{id}</c>. 404 for anything that is not
        /// a visible BOOK, including a comic at that id: absent and forbidden look the same.
        /// </summary>
        [HttpGet("{id:int}")]
        public async Task<IActionResult> Get(int id, [FromQuery] string? mediaToken = null, CancellationToken ct = default)
        {
            var item = await ItemAccess.GetAuthorizedItemAsync(db, User, id, allowExcluded: true, ct);
            if (item == null || item.Kind != ItemKind.Book) return NotFound();

            var media = MediaUrls.For(options, User, mediaToken);
            var detail = await ItemDetailBuilder.BuildAsync(db, item,
                media.Configured ? media.Thumb : null,
                media.Configured ? media.Download : null,
                media.Configured ? media.PagesTemplate : null,
                thumbnails.Exists(item.Id), ct);
            return Ok(detail);
        }

        // ── filtering ────────────────────────────────────────────────────────────────────────────────────────

        private IQueryable<Item> Visible() => ItemAccess.VisibleItems(db, User, ItemKind.Book);

        /// <summary>
        /// The rail's eight facets live in <see cref="NovelFilters"/>, which the GROUPED browse
        /// (<c>/browse/groups?kind=book&amp;book.*</c>) applies too — one definition, so a reader switching
        /// the View pill cannot find a book present in Grid and absent in Shelves. Only the FTS text stays
        /// here, because the browse prologue already does the identical thing with it.
        /// </summary>
        private IQueryable<Item> Filtered(string? author, string? series, string? publisher, string? decade,
            string? tag, string? q, string? excludeTag = null, int? minRating = null, bool unknown = false)
        {
            var query = NovelFilters.From(author, series, publisher, decade, tag, excludeTag, minRating, unknown)
                .Apply(db, Visible());

            if (!string.IsNullOrWhiteSpace(q))
            {
                var match = CatalogController.BuildFtsQuery(q.Trim());
                if (match.Length == 0) return query.Where(_ => false);
                var ids = ItemFts.Search(db, match, CatalogController.FtsLimit);
                query = query.Where(i => ids.Contains(i.Id));
            }

            return query;
        }

        /// <summary>
        /// The five sorts. The default is the shelf order a reader expects from a prose library — by author,
        /// then within an author by series and title — and every one of them ends with the item id, so a page
        /// boundary can neither drop a book nor show it twice.
        /// </summary>
        private static IQueryable<ItemSummary> Sorted(IQueryable<ItemSummary> q, string? orderby) =>
            (orderby ?? "").Trim().ToLowerInvariant() switch
            {
                "title" => q.OrderBy(s => s.Title).ThenBy(s => s.Id),
                "rating" => q.OrderByDescending(s => s.Rating).ThenBy(s => s.Title).ThenBy(s => s.Id),
                "newest" => q.OrderByDescending(s => s.Year).ThenBy(s => s.Title).ThenBy(s => s.Id),
                "oldest" => q.OrderBy(s => s.Year).ThenBy(s => s.Title).ThenBy(s => s.Id),
                _ => q.OrderBy(s => s.CreatorsCsv).ThenBy(s => s.Series).ThenBy(s => s.Title).ThenBy(s => s.Id),
            };

        // ── small helpers ────────────────────────────────────────────────────────────────────────────────────

        private string UserSig() =>
            $"{BooksIdentity.UserId(User)}:{BooksIdentity.CeilingFor(User)}:{(BooksIdentity.IsAdmin(User) ? 1 : 0)}";
    }
}
