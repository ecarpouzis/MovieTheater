using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using MovieTheater.Books.Access;
using MovieTheater.Books.Db;
using MovieTheater.Books.Identity;
using MovieTheater.Books.Projections;
using MovieTheater.Books.Services;

namespace MovieTheater.Books.Controllers
{
    // ── response shapes (the standalone site's, on v2 vocabulary) ─────────────────────────────────────────────

    public record FacetOption(string Value, int Count);
    public record SeriesFacetOption(int Id, string Value, int Count);
    public record PublisherFacetOption(int? Id, string Name, string? Full, int Count);
    public record CollectionFacetOption(int Id, string Name, int Count);

    public sealed class BrowseFacetsResult
    {
        public List<SeriesFacetOption> Series { get; init; } = [];
        public List<FacetOption> Tags { get; init; } = [];
        public List<FacetOption> Authors { get; init; } = [];
        public List<FacetOption> Artists { get; init; } = [];
        public List<FacetOption> Events { get; init; } = [];
        public List<FacetOption> Franchises { get; init; } = [];
        public List<PublisherFacetOption> Publishers { get; init; } = [];
        public List<CollectionFacetOption> Collections { get; init; } = [];
        public List<FacetOption> Decades { get; init; } = [];
    }

    /// <summary>Per-user marks on a group (read / want / favourite), read from <c>GroupMark</c>. Null when the
    /// caller has not marked that group — or when the grouping has no group type of its own (franchise).</summary>
    public record GroupUserMarkResult(bool IsRead, bool WantToRead, bool IsFavorite, int? Rating, string? Notes);

    /// <summary>The series group's AI card: the current insight's prose, score and tags.</summary>
    public record GroupDetailResult(string? AiSynopsis, int? AiRating, bool AiKnownSeries, List<string> AiTags);

    /// <summary>
    /// One group of a band. <c>TotalItems</c> is the group's ITEM count (the header figure); <c>RenderTotal</c> is
    /// how many cards a layout actually renders for it — in the series sub-view that is the distinct-series count,
    /// and a layout that reserves space up front must size against RenderTotal or it reserves room for cards that
    /// never appear. Null ⇒ same as TotalItems.
    /// </summary>
    public record BrowseGroupItem(string Key, string Label, int TotalItems, List<ItemSummary> Items,
        GroupUserMarkResult? UserMeta = null, GroupDetailResult? GroupDetail = null, int? RenderTotal = null);

    public record BrowseGroupsResponse(int TotalGroups, List<BrowseGroupItem> Groups);

    /// <summary>One ordered entry of a grouping's full group list — the band-invariant "heads" phase.</summary>
    public sealed record GroupHead(string Key, string Label, int Count);

    /// <summary>One issue of a series run: the flat row plus the reading-order and containment rows the flat projection omits.</summary>
    public sealed record SeriesRunRow(ItemSummary Item, ReadingOrderBlock? ReadingOrder, CollectionBlock? Collection);

    /// <summary>
    /// The grouped/faceted browse surface: what the facet rail, the letter rail and every banded layout read.
    ///
    /// <para><b>Facets are GROUP BY over rows.</b> In v2 tags, credits and ratings are tables
    /// (<c>ItemTag</c> / <c>SeriesTag</c> / <c>ItemCredit</c> / <c>Rating</c>), so a facet is a real aggregate on a
    /// real index — never a CSV string split at request time the way the standalone site had to.
    /// <c>Item.ResolvedTagsCsv</c> exists only so an OData <c>$filter</c> can <c>contains()</c> it.</para>
    ///
    /// <para><b>Groups are two-phase.</b> The expensive part of a band fetch is the HEADS phase — every group key,
    /// label and count for the whole filtered library, in the order the bands page through. It is identical for
    /// every band of the same query, so it is memory-cached and shared by <c>/groups</c> and
    /// <c>/group-letters</c> (a letter fetch warms the cache for band fetches and vice versa). The ITEMS phase then
    /// ranks only the band's groups in SQL order, materializes a LIGHT row (id + group keys), windows each group in
    /// memory, and fetches the full projection for just the chosen ids — bounded by groups × perGroupTop instead of
    /// "every item in the band's groups", which for a decade band is the entire library.</para>
    ///
    /// <para><b>Cache keys carry the user's facts</b> — user id, maturity ceiling and the admin flag — because the
    /// gate changes what a query returns. Two TTLs: 48 h for the default (no search, no filter) signatures, which
    /// <see cref="Services.CacheWarmupService"/> keeps permanently hot, and 20 min for ad-hoc filtered/search
    /// signatures, which are session working sets.</para>
    /// </summary>
    [ApiController]
    [Route("browse")]
    public sealed class BrowseController : ControllerBase
    {
        // Facet display limits (the standalone site's; the paginated facet-options endpoint serves the long tail).
        public const int SeriesLimit = 500;
        public const int PublisherLimit = 200;
        public const int EventLimit = 200;
        public const int FranchiseLimit = 200;
        public const int CollectionLimit = 200;
        public const int CreditFacetLimit = 50;
        public const int TagFacetLimit = 50;
        private const int MaxGroupsTop = 200;
        private const int CreditScanLimit = 300;   // how deep the on-index credit aggregate goes before display names

        private static readonly TimeSpan HeadsTtlDefault = TimeSpan.FromHours(48);
        private static readonly TimeSpan HeadsTtlFiltered = TimeSpan.FromMinutes(20);

        private readonly BooksDb db;
        private readonly IMemoryCache cache;
        public BrowseController(BooksDb db, IMemoryCache cache) { this.db = db; this.cache = cache; }

        // ── facets ───────────────────────────────────────────────────────────────────────────────────────────

        [HttpGet("facets")]
        public async Task<IActionResult> GetFacets([FromQuery] string? kind = null, CancellationToken ct = default)
        {
            var itemKind = CatalogController.ParseKind(kind);
            var key = $"books:facets:{UserSig()}:{itemKind}";
            if (cache.TryGetValue(key, out BrowseFacetsResult? hit) && hit != null) return Ok(hit);

            var live = ItemAccess.VisibleItems(db, User, itemKind);

            var seriesCounts = await live.Where(i => i.SeriesId != null)
                .GroupBy(i => i.SeriesId!.Value).Select(g => new { g.Key, Count = g.Count() })
                .OrderByDescending(x => x.Count).ThenBy(x => x.Key).Take(SeriesLimit).ToListAsync(ct);
            var seriesNames = await SeriesNamesAsync(seriesCounts.Select(x => x.Key).ToList(), ct);
            var series = seriesCounts
                .Select(x => new SeriesFacetOption(x.Key, seriesNames.GetValueOrDefault(x.Key) ?? "", x.Count))
                .Where(o => o.Value.Length > 0).ToList();

            var pubCounts = await live.Where(i => i.ResolvedPublisher != null)
                .GroupBy(i => i.ResolvedPublisher!).Select(g => new { g.Key, Count = g.Count() })
                .OrderByDescending(x => x.Count).ThenBy(x => x.Key).Take(PublisherLimit).ToListAsync(ct);
            var pubNames = pubCounts.Select(x => x.Key).ToList();
            var pubRows = (await db.Publishers.AsNoTracking().Where(p => pubNames.Contains(p.Name))
                    .Select(p => new { p.Id, p.Name, p.FullName }).ToListAsync(ct))
                .GroupBy(p => p.Name).ToDictionary(g => g.Key, g => g.First());
            var publishers = pubCounts.Select(x =>
            {
                var row = pubRows.GetValueOrDefault(x.Key);
                return new PublisherFacetOption(row?.Id, x.Key, row?.FullName, x.Count);
            }).ToList();

            var decadeRows = await live.Where(i => i.ResolvedYear != null)
                .GroupBy(i => i.ResolvedYear!.Value / 10).Select(g => new { g.Key, Count = g.Count() })
                .OrderBy(x => x.Key).ToListAsync(ct);
            var decades = decadeRows.Select(d => new FacetOption($"{d.Key * 10}s", d.Count)).ToList();

            var eventRows = await live.Where(i => i.Comic != null && i.Comic.EventName != null && i.Comic.EventName != "")
                .GroupBy(i => i.Comic!.EventName!).Select(g => new { g.Key, Count = g.Count() })
                .OrderByDescending(x => x.Count).ThenBy(x => x.Key).Take(EventLimit).ToListAsync(ct);
            var events = eventRows.Select(x => new FacetOption(x.Key, x.Count)).ToList();

            var franchiseRows = await live.Where(i => i.Series != null && i.Series.Franchise != null && i.Series.Franchise != "")
                .GroupBy(i => i.Series!.Franchise!).Select(g => new { g.Key, Count = g.Count() })
                .OrderByDescending(x => x.Count).ThenBy(x => x.Key).Take(FranchiseLimit).ToListAsync(ct);
            var franchises = franchiseRows.Select(x => new FacetOption(x.Key, x.Count)).ToList();

            var folderCounts = await live.Where(i => i.TopFolderId != null)
                .GroupBy(i => i.TopFolderId!.Value).Select(g => new { g.Key, Count = g.Count() })
                .OrderByDescending(x => x.Count).ThenBy(x => x.Key).Take(CollectionLimit).ToListAsync(ct);
            var folderNames = await FolderNamesAsync(folderCounts.Select(x => x.Key).ToList(), ct);
            var collections = folderCounts
                .Select(x => new CollectionFacetOption(x.Key, folderNames.GetValueOrDefault(x.Key) ?? "Unknown", x.Count))
                .ToList();

            var authors = await CreditFacetAsync(live, CreditRoles.Authors, CreditFacetLimit, ct);
            var artists = await CreditFacetAsync(live, CreditRoles.Artists, CreditFacetLimit, ct);

            var tagCounts = await TagCountsAsync(live, ct);
            var tags = tagCounts.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
                .Take(TagFacetLimit).Select(kv => new FacetOption(kv.Key, kv.Value)).ToList();

            var result = new BrowseFacetsResult
            {
                Series = series,
                Publishers = publishers,
                Decades = decades,
                Events = events,
                Franchises = franchises,
                Collections = collections,
                Authors = authors,
                Artists = artists,
                Tags = tags,
            };

            // The multi-day TTL is a backstop only: CacheWarmupService re-warms this entry whenever the catalog
            // actually changes, so real staleness is bounded by the warmer, not by this number.
            Cache(key, result, HeadsTtlDefault);
            return Ok(result);
        }

        /// <summary>
        /// Paginated, searchable option list for ONE dynamic facet — the long tail the rail's "more…" opens.
        /// GET /browse/facet-options?field=authors|artists|tags&amp;q=&amp;skip=0&amp;top=50
        /// </summary>
        [HttpGet("facet-options")]
        public async Task<IActionResult> GetFacetOptions(
            [FromQuery] string field,
            [FromQuery] string? q = null,
            [FromQuery] int skip = 0,
            [FromQuery] int top = 50,
            [FromQuery] string? kind = null,
            CancellationToken ct = default)
        {
            if (field is not ("authors" or "artists" or "tags")) return BadRequest($"Unknown field: {field}");
            skip = Math.Max(0, skip);
            top = Math.Clamp(top, 1, 500);

            var itemKind = CatalogController.ParseKind(kind);
            var key = $"books:facet-opts:{UserSig()}:{itemKind}:{field}";
            if (!cache.TryGetValue(key, out Dictionary<string, int>? all) || all == null)
            {
                var live = ItemAccess.VisibleItems(db, User, itemKind);
                all = field switch
                {
                    "authors" => await CreditKeyCountsAsync(live, CreditRoles.Authors, null, ct),
                    "artists" => await CreditKeyCountsAsync(live, CreditRoles.Artists, null, ct),
                    _ => await TagCountsAsync(live, ct),
                };
                Cache(key, all, HeadsTtlFiltered);
            }

            IEnumerable<KeyValuePair<string, int>> source = string.IsNullOrWhiteSpace(q)
                ? all
                : all.Where(kv => kv.Key.Contains(q, StringComparison.OrdinalIgnoreCase));
            var ordered = source.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase).ToList();
            var page = ordered.Skip(skip).Take(top).ToList();

            // Credit keys are normalized (lower-cased); resolve display names for the PAGE only, so the long-tail
            // query stays on the (Role, NormalizedName, ItemId) index and the row lookups are bounded by `top`.
            var items = field == "tags"
                ? page.Select(kv => new FacetOption(kv.Key, kv.Value)).ToList()
                : await WithDisplayNamesAsync(page, ct);

            return Ok(new { items, total = ordered.Count });
        }

        // ── groups (two-phase heads / bands) ──────────────────────────────────────────────────────────────────

        /// <summary>
        /// GET /browse/groups — items grouped server-side, paginated BY GROUP.
        /// groupBy = series | publisher | decade | collection | franchise; orderby sorts items INSIDE a group
        /// (groups themselves are always label-ordered, which is what makes the letter rail's offsets valid).
        /// </summary>
        [HttpGet("groups")]
        public async Task<IActionResult> GetGroups(
            [FromQuery] string groupBy = "collection",
            [FromQuery] string? q = null,
            [FromQuery] string? orderby = null,
            [FromQuery] int groupsTop = 20,
            [FromQuery] int groupsSkip = 0,
            [FromQuery] int perGroupTop = 48,
            [FromQuery] int perGroupSkip = 0,
            [FromQuery(Name = "$filter")] string? filter = null,
            [FromQuery] string? subGroupBy = null,
            [FromQuery] string? singleGroupKey = null,
            [FromQuery] string? kind = null,
            // The per-user mark filters (see ApplyMarkFilters): they restrict the ITEM set, so the heads, the
            // bands and the letter rail all agree, and they make the heads signature uncacheable.
            [FromQuery] bool wantToReadOnly = false,
            [FromQuery] bool readOnly = false,
            [FromQuery] string[]? author = null,
            [FromQuery] string[]? artist = null,
            [FromQuery] string[]? tag = null,
            [FromQuery(Name = "event")] string[]? eventName = null,
            [FromQuery] string[]? exAuthor = null,
            [FromQuery] string[]? exArtist = null,
            [FromQuery] string[]? exTag = null,
            [FromQuery] string[]? exEvent = null,
            CancellationToken ct = default)
        {
            var exact = ExactFilters.From(author, artist, tag, eventName, exAuthor, exArtist, exTag, exEvent);
            groupsTop = Math.Clamp(groupsTop, 1, MaxGroupsTop);
            perGroupTop = Math.Clamp(perGroupTop, 1, 5000);
            perGroupSkip = Math.Max(0, perGroupSkip);
            groupsSkip = Math.Max(0, groupsSkip);
            var by = NormalizeGroupBy(groupBy);
            var itemKind = CatalogController.ParseKind(kind);

            var (countQuery, summaryQuery) = await BuildFilteredContextAsync(itemKind, q, filter, exact, wantToReadOnly, readOnly, ct);
            var heads = await CachedHeadsAsync(HeadsSig(itemKind, by, q, filter, exact, wantToReadOnly, readOnly), Ttl(q, filter, exact),
                () => GroupHeadsAsync(countQuery, by, ct));

            var paged = singleGroupKey != null
                ? heads.Where(h => h.Key == singleGroupKey).ToList()
                : heads.Skip(groupsSkip).Take(groupsTop).ToList();

            var seriesSubView = subGroupBy == "series";
            var (byKey, renderTotals) = await BandItemsAsync(
                BandQuery(summaryQuery, by, paged), orderby, by, seriesSubView, perGroupTop, perGroupSkip, ct);

            var details = by == "series" ? await SeriesDetailsAsync(paged, ct) : null;
            var marks = await GroupMarksAsync(by, paged, ct);

            var groups = paged.Select(h => new BrowseGroupItem(
                h.Key, h.Label, h.Count,
                byKey.GetValueOrDefault(h.Key, []),
                UserMeta: marks.GetValueOrDefault(h.Key),
                GroupDetail: details?.GetValueOrDefault(h.Key),
                RenderTotal: renderTotals?.GetValueOrDefault(h.Key))).ToList();

            return Ok(new BrowseGroupsResponse(heads.Count, groups));
        }

        /// <summary>
        /// GET /browse/group-letters — the first group index per leading letter across the WHOLE ordered group set,
        /// so a banded view can render an A–Z rail and jump anywhere without holding every band. Groups are always
        /// label-ordered, so these offsets are valid for every item sort. Non-alphabetic leading chars ⇒ "#".
        /// Shares the heads cache with <see cref="GetGroups"/>, which is why the warmer only has to call this one.
        /// </summary>
        [HttpGet("group-letters")]
        public async Task<IActionResult> GetGroupLetters(
            [FromQuery] string groupBy = "collection",
            [FromQuery] string? q = null,
            [FromQuery(Name = "$filter")] string? filter = null,
            [FromQuery] string? kind = null,
            [FromQuery] bool wantToReadOnly = false,   // see GetGroups
            [FromQuery] bool readOnly = false,
            [FromQuery] string[]? author = null,
            [FromQuery] string[]? artist = null,
            [FromQuery] string[]? tag = null,
            [FromQuery(Name = "event")] string[]? eventName = null,
            [FromQuery] string[]? exAuthor = null,
            [FromQuery] string[]? exArtist = null,
            [FromQuery] string[]? exTag = null,
            [FromQuery] string[]? exEvent = null,
            CancellationToken ct = default)
        {
            var exact = ExactFilters.From(author, artist, tag, eventName, exAuthor, exArtist, exTag, exEvent);
            var by = NormalizeGroupBy(groupBy);
            var itemKind = CatalogController.ParseKind(kind);
            var (countQuery, _) = await BuildFilteredContextAsync(itemKind, q, filter, exact, wantToReadOnly, readOnly, ct);
            var heads = await CachedHeadsAsync(HeadsSig(itemKind, by, q, filter, exact, wantToReadOnly, readOnly), Ttl(q, filter, exact),
                () => GroupHeadsAsync(countQuery, by, ct));

            var letters = new List<object>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (var i = 0; i < heads.Count; i++)
            {
                var letter = LeadingLetter(heads[i].Label);
                if (seen.Add(letter)) letters.Add(new { letter, firstIndex = i });
            }
            return Ok(new { totalGroups = heads.Count, letters });
        }

        /// <summary>
        /// GET /browse/groups/{groupBy}/{key}/items — the band continuation: more items inside ONE group, in the
        /// same sort order the band used. This is what a shelf's horizontal load-more calls.
        /// </summary>
        [HttpGet("groups/{groupBy}/{key}/items")]
        public async Task<IActionResult> GetGroupItems(
            string groupBy, string key,
            [FromQuery] int skip = 0,
            [FromQuery] int top = 48,
            [FromQuery] string? orderby = null,
            [FromQuery] string? q = null,
            [FromQuery(Name = "$filter")] string? filter = null,
            [FromQuery] string? kind = null,
            [FromQuery] bool wantToReadOnly = false,   // see GetGroups
            [FromQuery] bool readOnly = false,
            [FromQuery] string[]? author = null,
            [FromQuery] string[]? artist = null,
            [FromQuery] string[]? tag = null,
            [FromQuery(Name = "event")] string[]? eventName = null,
            [FromQuery] string[]? exAuthor = null,
            [FromQuery] string[]? exArtist = null,
            [FromQuery] string[]? exTag = null,
            [FromQuery] string[]? exEvent = null,
            CancellationToken ct = default)
        {
            var exact = ExactFilters.From(author, artist, tag, eventName, exAuthor, exArtist, exTag, exEvent);
            skip = Math.Max(0, skip);
            top = Math.Clamp(top, 1, 500);
            var by = NormalizeGroupBy(groupBy);
            var itemKind = CatalogController.ParseKind(kind);
            var (_, summaryQuery) = await BuildFilteredContextAsync(itemKind, q, filter, exact, wantToReadOnly, readOnly, ct);

            var head = new GroupHead(key, key, 0);
            var band = BandQuery(summaryQuery, by, new List<GroupHead> { head });
            var total = await band.CountAsync(ct);
            var items = await ApplySort(band, orderby).Skip(skip).Take(top).ToListAsync(ct);
            return Ok(new { items, total });
        }

        /// <summary>
        /// GET /browse/series/{id}/library-rating — the blended 0–100 series score and its rationale, for the
        /// series modal's rating chip (the note is the hover tooltip). 200 with nulls when unrated: the client
        /// reads that as "no chip", and a 404 would only be console noise.
        /// </summary>
        [HttpGet("series/{seriesId:int}/library-rating")]
        public async Task<IActionResult> GetSeriesLibraryRating(int seriesId, CancellationToken ct = default)
        {
            var rating = await db.Series.AsNoTracking().Where(s => s.Id == seriesId)
                .Select(s => s.ResolvedRating).FirstOrDefaultAsync(ct);
            // Series.ResolvedRating is the materialized truth; the note lives on the row that produced it —
            // a hand-set override outranks the computed blend.
            var note = await db.Ratings.AsNoTracking()
                .Where(r => r.TargetKind == SubjectKind.Series && r.TargetId == seriesId
                            && (r.Source == RatingSource.Override || r.Source == RatingSource.Library))
                .OrderByDescending(r => r.IsOverride).ThenByDescending(r => r.Source)
                .Select(r => r.Note).FirstOrDefaultAsync(ct);
            return Ok(new { rating, note });
        }

        /// <summary>
        /// GET /browse/series/{seriesId}/run — every visible issue of one series with its reading-order and
        /// containment rows, in reading order. This is what the series modal's smart reading list and the shelf
        /// drawer need and the flat projection deliberately does not carry: the containment tree
        /// (<c>trackRole</c> / <c>level</c> / <c>spanStart..spanEnd</c> / <c>parentItemId</c>) and the
        /// <c>readIndex</c>. Joined, never id-listed, so a 2,000-issue weekly stays one query per table.
        /// </summary>
        [HttpGet("series/{seriesId:int}/run")]
        public async Task<IActionResult> GetSeriesRun(int seriesId, [FromQuery] string? kind = null, CancellationToken ct = default)
        {
            var itemKind = CatalogController.ParseKind(kind);
            var visible = ItemAccess.VisibleItems(db, User, itemKind).Where(i => i.SeriesId == seriesId);
            var summaries = await visible.Select(ItemSummary.Project).ToListAsync(ct);

            var orders = await (from r in db.ReadingOrderEntries.AsNoTracking()
                                join i in visible on r.ItemId equals i.Id
                                select new { r.ItemId, Block = new ReadingOrderBlock(r.SeriesId, r.ReadTier, r.ReadNumber, r.ReadDate,
                                    r.ReadDatePrecision, r.ReadIndex, r.ReadCount, r.Source, r.Confidence) })
                .ToListAsync(ct);
            var orderById = orders.GroupBy(o => o.ItemId).ToDictionary(g => g.Key, g => g.First().Block);

            var nodes = await (from n in db.CollectionNodes.AsNoTracking()
                               join i in visible on n.ItemId equals i.Id
                               select new { n.ItemId, Block = new CollectionBlock(n.Level, n.TrackRole, n.SpanStart, n.SpanEnd,
                                   n.ContainsCount, n.ParentItemId, n.SpanSource, n.SpanLabel) })
                .ToListAsync(ct);
            var nodeById = nodes.GroupBy(n => n.ItemId).ToDictionary(g => g.Key, g => g.First().Block);

            // Reading order: the standalone's readDate, readTier, readIndex ascending, then the issue's own year
            // and id so an issue the order job never reached still lands somewhere deterministic.
            var rows = summaries
                .Select(s => new SeriesRunRow(s, orderById.GetValueOrDefault(s.Id), nodeById.GetValueOrDefault(s.Id)))
                .OrderBy(r => r.ReadingOrder?.ReadDate ?? "9999")
                .ThenBy(r => r.ReadingOrder?.ReadTier ?? int.MaxValue)
                .ThenBy(r => r.ReadingOrder?.ReadIndex ?? int.MaxValue)
                .ThenBy(r => r.Item.Year ?? int.MaxValue)
                .ThenBy(r => r.Item.Id)
                .ToList();
            return Ok(new { seriesId, total = rows.Count, items = rows });
        }

        // ── the shared request prologue ───────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Auth + maturity gate + the per-user mark filters + FTS + the OData <c>$filter</c>, shared by every
        /// groups endpoint.
        /// The COUNT query stays at the ENTITY level (EF cannot GROUP BY a projected type). With no <c>$filter</c>
        /// the summary set IS the entity set, so the ids subquery is skipped entirely — embedding the projection as
        /// a correlated subquery made every unfiltered GROUP BY pay the projection's join for nothing.
        /// </summary>
        private async Task<(IQueryable<Item> CountQuery, IQueryable<ItemSummary> SummaryQuery)> BuildFilteredContextAsync(
            ItemKind kind, string? q, string? filter, ExactFilters exact, bool wantToReadOnly, bool readOnly, CancellationToken ct)
        {
            var entityQuery = await ApplyMarkFiltersAsync(ItemAccess.VisibleItems(db, User, kind), wantToReadOnly, readOnly, ct);
            // The exact facet filters (credits / tags / events) narrow the ENTITY set, before the projection and the
            // OData filter, so heads, bands, letters and counts all agree — see ExactFilters.
            entityQuery = exact.Apply(db, entityQuery);

            if (!string.IsNullOrWhiteSpace(q))
            {
                var match = CatalogController.BuildFtsQuery(q.Trim());
                if (match.Length == 0) entityQuery = entityQuery.Where(_ => false);
                else
                {
                    var ids = ItemFts.Search(db, match, CatalogController.FtsLimit);
                    entityQuery = entityQuery.Where(i => ids.Contains(i.Id));
                }
            }

            var summaryQuery = entityQuery.Select(ItemSummary.Project);
            if (!string.IsNullOrEmpty(filter))
            {
                // The SAME parser and the same camelCase vocabulary the OData catalog uses, so one filter string
                // means one thing across both surfaces.
                summaryQuery = CatalogEdm.ApplyFilter(summaryQuery, filter);
                var filteredIds = summaryQuery.Select(s => s.Id);
                entityQuery = entityQuery.Where(i => filteredIds.Contains(i.Id));
            }
            return (entityQuery, summaryQuery);
        }

        // ── the per-user mark filters ─────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Restrict the browse to what the caller has MARKED — <c>?wantToReadOnly=true</c> for their queue,
        /// <c>?readOnly=true</c> for what they have finished. Both together AND (the standalone's semantics):
        /// "wanted AND read" is a legitimate, if small, question.
        ///
        /// <para><b>It restricts ITEMS, not group keys.</b> The standalone filtered the group KEYS against the
        /// user's <c>GroupUserMetadata</c>, which only ever worked for the series grouping — a reader marks
        /// series, not decades, so "read only" grouped by decade returned nothing unless they had literally
        /// marked a decade. In v2 a mark is an item mark (<c>UserItemState</c>) or a series mark
        /// (<c>GroupMark(Series)</c>, which fans out to the issues anyway), so the honest reading is "the items
        /// you marked, plus the items of the series you marked" — and then the heads, the bands and the letter
        /// rail all fall out of one filtered set and cannot disagree.</para>
        ///
        /// <para><b>These signatures are never cached</b> (see <see cref="HeadsSig"/>): they are per-user and
        /// change on every click, so a cached head list would be wrong the moment a reader marked something.</para>
        /// </summary>
        private async Task<IQueryable<Item>> ApplyMarkFiltersAsync(
            IQueryable<Item> items, bool wantToReadOnly, bool readOnly, CancellationToken ct)
        {
            if (!wantToReadOnly && !readOnly) return items;
            // No principal and a per-user filter is not "everything" — it is nothing.
            if (BooksIdentity.UserId(User) is not int userId) return items.Where(_ => false);

            if (wantToReadOnly)
                items = Restrict(items, UserActivityQueries.MarkedItemIds(db, userId, MarkKind.WantToRead),
                    await UserActivityQueries.WantedSeriesIds(db, userId, ct));
            if (readOnly)
                items = Restrict(items, UserActivityQueries.MarkedItemIds(db, userId, MarkKind.Read),
                    await UserActivityQueries.ReadSeriesIds(db, userId, ct));
            return items;
        }

        /// <summary>
        /// The item ids stay an <see cref="IQueryable{T}"/> (a subquery, so a reader with thousands of marks does
        /// not become a thousand-parameter IN list); the series ids are a short materialized list.
        /// </summary>
        private static IQueryable<Item> Restrict(IQueryable<Item> items, IQueryable<int> markedItemIds, List<int> markedSeriesIds) =>
            markedSeriesIds.Count == 0
                ? items.Where(i => markedItemIds.Contains(i.Id))
                : items.Where(i => markedItemIds.Contains(i.Id)
                                   || (i.SeriesId != null && markedSeriesIds.Contains(i.SeriesId.Value)));

        /// <summary>
        /// The caller's marks on the groups of THIS band, as the standalone decorated its heads: one read of the
        /// user's own (few) rows for the grouping's type, matched in memory — the same shape
        /// <c>POST /marks/groups/batch</c> uses, and for the same reason (a composite tuple IN does not translate).
        /// Franchise has no group type of its own, so its heads carry no marks.
        /// </summary>
        private async Task<Dictionary<string, GroupUserMarkResult>> GroupMarksAsync(
            string by, List<GroupHead> paged, CancellationToken ct)
        {
            var empty = new Dictionary<string, GroupUserMarkResult>(StringComparer.Ordinal);
            if (paged.Count == 0) return empty;
            if (BooksIdentity.UserId(User) is not int userId) return empty;
            if (MarksController.ParseGroupType(by) is not GroupType type) return empty;

            var keys = paged.Select(h => h.Key).ToHashSet(StringComparer.Ordinal);
            var rows = await db.GroupMarks.AsNoTracking()
                .Where(m => m.UserId == userId && m.GroupType == type).ToListAsync(ct);
            foreach (var m in rows.Where(m => keys.Contains(m.GroupKey)))
                empty[m.GroupKey] = new GroupUserMarkResult(m.IsRead, m.WantToRead, m.IsFavorite, m.Rating, m.Notes);
            return empty;
        }

        // ── heads ─────────────────────────────────────────────────────────────────────────────────────────────

        private Task<List<GroupHead>> GroupHeadsAsync(IQueryable<Item> live, string by, CancellationToken ct) => by switch
        {
            "series" => SeriesHeadsAsync(live, ct),
            "publisher" => PublisherHeadsAsync(live, ct),
            "decade" => DecadeHeadsAsync(live, ct),
            "franchise" => FranchiseHeadsAsync(live, ct),
            _ => CollectionHeadsAsync(live, ct),
        };

        private async Task<List<GroupHead>> CollectionHeadsAsync(IQueryable<Item> live, CancellationToken ct)
        {
            var counts = await live.Where(i => i.TopFolderId != null)
                .GroupBy(i => i.TopFolderId!.Value).Select(g => new { g.Key, Count = g.Count() }).ToListAsync(ct);
            var names = await FolderNamesAsync(counts.Select(x => x.Key).ToList(), ct);
            return Ordered(counts.Select(x => new GroupHead(Key(x.Key), names.GetValueOrDefault(x.Key) ?? "Unknown", x.Count)));
        }

        private async Task<List<GroupHead>> PublisherHeadsAsync(IQueryable<Item> live, CancellationToken ct)
        {
            var counts = await live.Where(i => i.ResolvedPublisher != null && i.ResolvedPublisher != "")
                .GroupBy(i => i.ResolvedPublisher!).Select(g => new { g.Key, Count = g.Count() }).ToListAsync(ct);
            // The publisher group key is the RESOLVED NAME, not the Publisher row id: Item.ResolvedPublisher is the
            // v2 truth (and its (Kind, ResolvedPublisher, Id) index is what keeps the aggregate off a table scan).
            return Ordered(counts.Select(x => new GroupHead(x.Key, x.Key, x.Count)));
        }

        private async Task<List<GroupHead>> DecadeHeadsAsync(IQueryable<Item> live, CancellationToken ct)
        {
            var counts = await live.Where(i => i.ResolvedYear != null)
                .GroupBy(i => i.ResolvedYear!.Value / 10 * 10).Select(g => new { g.Key, Count = g.Count() }).ToListAsync(ct);
            // Decades are chronological, not alphabetical — the one grouping whose order is not its label's.
            return counts.OrderBy(x => x.Key)
                .Select(x => new GroupHead(Key(x.Key), $"{x.Key}s", x.Count)).ToList();
        }

        private async Task<List<GroupHead>> FranchiseHeadsAsync(IQueryable<Item> live, CancellationToken ct)
        {
            var counts = await live.Where(i => i.Series != null && i.Series.Franchise != null && i.Series.Franchise != "")
                .GroupBy(i => i.Series!.Franchise!).Select(g => new { g.Key, Count = g.Count() }).ToListAsync(ct);
            return Ordered(counts.Select(x => new GroupHead(x.Key, x.Key, x.Count)));
        }

        private async Task<List<GroupHead>> SeriesHeadsAsync(IQueryable<Item> live, CancellationToken ct)
        {
            var counts = await live.Where(i => i.SeriesId != null)
                .GroupBy(i => i.SeriesId!.Value).Select(g => new { g.Key, Count = g.Count() }).ToListAsync(ct);
            var names = await SeriesNamesAsync(counts.Select(x => x.Key).ToList(), ct);
            return Ordered(counts.Select(x => new GroupHead(Key(x.Key), names.GetValueOrDefault(x.Key) ?? "", x.Count)));
        }

        // Label A–Z, then the key — the stable tiebreaker that makes a letter offset reproducible across calls.
        private static List<GroupHead> Ordered(IEnumerable<GroupHead> heads) => heads
            .OrderBy(h => h.Label, StringComparer.OrdinalIgnoreCase)
            .ThenBy(h => h.Key, StringComparer.Ordinal).ToList();

        private static string Key(int value) => value.ToString(CultureInfo.InvariantCulture);

        private static string LeadingLetter(string label)
        {
            var ch = label.Length > 0 ? char.ToUpperInvariant(label[0]) : '#';
            return ch >= 'A' && ch <= 'Z' ? ch.ToString() : "#";
        }

        // ── bands ─────────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>The band's slice of the filtered set: only the items belonging to the paged groups.</summary>
        private static IQueryable<ItemSummary> BandQuery(IQueryable<ItemSummary> summaryQuery, string by, List<GroupHead> paged)
        {
            switch (by)
            {
                case "series":
                    {
                        var ids = paged.Select(h => ParseInt(h.Key)).Where(v => v != null).Select(v => v!.Value).ToList();
                        return summaryQuery.Where(s => s.SeriesId != null && ids.Contains(s.SeriesId.Value));
                    }
                case "publisher":
                    {
                        var keys = paged.Select(h => h.Key).ToList();
                        return summaryQuery.Where(s => s.Publisher != null && keys.Contains(s.Publisher));
                    }
                case "decade":
                    {
                        var decades = paged.Select(h => ParseInt(h.Key)).Where(v => v != null).Select(v => v!.Value).ToList();
                        return summaryQuery.Where(s => s.Year != null && decades.Contains(s.Year.Value / 10 * 10));
                    }
                case "franchise":
                    {
                        var keys = paged.Select(h => h.Key).ToList();
                        return summaryQuery.Where(s => s.Franchise != null && keys.Contains(s.Franchise));
                    }
                default:
                    {
                        var ids = paged.Select(h => ParseInt(h.Key)).Where(v => v != null).Select(v => v!.Value).ToList();
                        return summaryQuery.Where(s => s.TopFolderId != null && ids.Contains(s.TopFolderId.Value));
                    }
            }
        }

        /// <summary>The light ranking row: id plus every group-key candidate plus the series name the sub-view's
        /// distinct-first logic needs. Selecting ONLY these lets EF prune the full projection from phase 1.</summary>
        private sealed class LightRow
        {
            public int Id { get; init; }
            public int? TopFolderId { get; init; }
            public string? Publisher { get; init; }
            public int? Year { get; init; }
            public int? SeriesId { get; init; }
            public string? Franchise { get; init; }
            public string? Series { get; init; }
        }

        private static string? KeyOf(LightRow r, string by) => by switch
        {
            "series" => r.SeriesId?.ToString(CultureInfo.InvariantCulture),
            "publisher" => r.Publisher,
            "decade" => r.Year == null ? null : (r.Year.Value / 10 * 10).ToString(CultureInfo.InvariantCulture),
            "franchise" => r.Franchise,
            _ => r.TopFolderId?.ToString(CultureInfo.InvariantCulture),
        };

        private async Task<(Dictionary<string, List<ItemSummary>> ByKey, Dictionary<string, int>? RenderTotals)>
            BandItemsAsync(IQueryable<ItemSummary> bandQuery, string? orderby, string by, bool seriesSubView,
                int perGroupTop, int perGroupSkip, CancellationToken ct)
        {
            var light = await ApplySort(bandQuery, orderby)
                .Select(s => new LightRow
                {
                    Id = s.Id, TopFolderId = s.TopFolderId, Publisher = s.Publisher,
                    Year = s.Year, SeriesId = s.SeriesId, Franchise = s.Franchise, Series = s.Series,
                }).ToListAsync(ct);

            var buckets = new Dictionary<string, List<LightRow>>(StringComparer.Ordinal);
            foreach (var row in light)
            {
                var k = KeyOf(row, by);
                if (k == null) continue;
                if (!buckets.TryGetValue(k, out var list)) buckets[k] = list = [];
                list.Add(row);
            }

            Dictionary<string, int>? renderTotals = null;
            var chosen = new Dictionary<string, List<int>>(StringComparer.Ordinal);
            if (seriesSubView)
            {
                renderTotals = new Dictionary<string, int>(StringComparer.Ordinal);
                foreach (var (k, rows) in buckets)
                {
                    // One card per distinct series, keeping each series' first (best-sorted) item.
                    var firstPerSeries = rows.GroupBy(r => r.Series ?? "").Select(g => g.First()).ToList();
                    renderTotals[k] = firstPerSeries.Count;
                    chosen[k] = firstPerSeries.Skip(perGroupSkip).Take(perGroupTop).Select(r => r.Id).ToList();
                }
            }
            else
            {
                foreach (var (k, rows) in buckets)
                    chosen[k] = rows.Skip(perGroupSkip).Take(perGroupTop).Select(r => r.Id).ToList();
            }

            var allIds = chosen.Values.SelectMany(x => x).ToList();
            var summaries = allIds.Count == 0
                ? []
                : await bandQuery.Where(s => allIds.Contains(s.Id)).ToListAsync(ct);
            var byId = summaries.ToDictionary(s => s.Id);
            var byKey = chosen.ToDictionary(
                kv => kv.Key,
                kv => kv.Value.Where(byId.ContainsKey).Select(id => byId[id]).ToList(),
                StringComparer.Ordinal);
            return (byKey, renderTotals);
        }

        /// <summary>
        /// The item sorts. Every one ends with the item id, so a page boundary never duplicates or drops a row.
        /// <c>reading</c> orders by the reading-order job's <c>readIndex</c> (unordered issues last) — it only means
        /// something inside a series group, which is the only place the client offers it.
        /// </summary>
        private IQueryable<ItemSummary> ApplySort(IQueryable<ItemSummary> q, string? orderby) => orderby switch
        {
            "reading" => q.OrderBy(s => db.ReadingOrderEntries.Where(r => r.ItemId == s.Id).Select(r => r.ReadIndex).FirstOrDefault() ?? int.MaxValue)
                .ThenBy(s => s.Year).ThenBy(s => s.Id),
            "newest" => q.OrderByDescending(s => s.Year).ThenByDescending(s => s.IndexedAt).ThenBy(s => s.Id),
            "oldest" => q.OrderBy(s => s.Year).ThenBy(s => s.IndexedAt).ThenBy(s => s.Id),
            "rating" => q.OrderByDescending(s => s.Rating).ThenBy(s => s.Id),
            "title" => q.OrderBy(s => s.Title).ThenBy(s => s.Id),
            "publisher" => q.OrderBy(s => s.Publisher).ThenBy(s => s.Year).ThenBy(s => s.Id),
            "pages" or "issues" => q.OrderByDescending(s => s.PageCount).ThenBy(s => s.Id),
            _ => q.OrderBy(s => s.Series).ThenBy(s => s.Year).ThenBy(s => s.Id),
        };

        /// <summary>The AI card for the paged series groups: the CURRENT insight only (append-only history stays hidden).</summary>
        private async Task<Dictionary<string, GroupDetailResult>> SeriesDetailsAsync(List<GroupHead> paged, CancellationToken ct)
        {
            var ids = paged.Select(h => ParseInt(h.Key)).Where(v => v != null).Select(v => v!.Value).ToList();
            if (ids.Count == 0) return new Dictionary<string, GroupDetailResult>(StringComparer.Ordinal);
            var insights = await db.Insights.AsNoTracking()
                .Where(n => n.SubjectKind == SubjectKind.Series && n.IsCurrent && n.SubjectId != null && ids.Contains(n.SubjectId.Value))
                .Select(n => new { n.Id, n.SubjectId, n.Synopsis, n.Rating, n.Recognized }).ToListAsync(ct);
            var insightIds = insights.Select(n => n.Id).ToList();
            var tags = (await db.InsightTags.AsNoTracking().Where(t => insightIds.Contains(t.InsightId))
                    .Select(t => new { t.InsightId, t.Category, t.Value }).ToListAsync(ct))
                .GroupBy(t => t.InsightId)
                .ToDictionary(g => g.Key, g => g.Select(t => $"{t.Category}:{t.Value}").ToList());
            return insights.ToDictionary(
                n => n.SubjectId!.Value.ToString(CultureInfo.InvariantCulture),
                n => new GroupDetailResult(n.Synopsis, n.Rating, n.Recognized, tags.GetValueOrDefault(n.Id) ?? []),
                StringComparer.Ordinal);
        }

        // ── facet helpers ─────────────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Credit counts grouped by the NORMALIZED name — the (Role, NormalizedName, ItemId) index covers both the
        /// grouping and the join back to the visible items, so nothing touches the credit rows themselves.
        ///
        /// <para>The count is DISTINCT ITEMS, not credit rows: the same person legitimately arrives on one item
        /// from two sources (its embedded ComicInfo and its LOCG credits), and a facet chip that said "3" for two
        /// books would be a lie.</para>
        /// </summary>
        private async Task<Dictionary<string, int>> CreditKeyCountsAsync(
            IQueryable<Item> live, string[] roles, int? take, CancellationToken ct)
        {
            var grouped = db.ItemCredits.AsNoTracking()
                .Where(c => c.Role != null && roles.Contains(c.Role) && c.NormalizedName != null && c.NormalizedName != "")
                .Join(live, c => c.ItemId, i => i.Id, (c, i) => new { Name = c.NormalizedName!, c.ItemId })
                .Distinct()
                .GroupBy(x => x.Name).Select(g => new { g.Key, Count = g.Count() })
                .OrderByDescending(x => x.Count).ThenBy(x => x.Key);
            var rows = take is int t ? await grouped.Take(t).ToListAsync(ct) : await grouped.ToListAsync(ct);
            return rows.ToDictionary(x => x.Key, x => x.Count, StringComparer.OrdinalIgnoreCase);
        }

        private async Task<List<FacetOption>> CreditFacetAsync(IQueryable<Item> live, string[] roles, int limit, CancellationToken ct)
        {
            var counts = await CreditKeyCountsAsync(live, roles, CreditScanLimit, ct);
            var top = counts.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
                .Take(limit).ToList();
            return await WithDisplayNamesAsync(top, ct);
        }

        /// <summary>Turn normalized credit keys back into the name a person reads, for a BOUNDED set of keys.</summary>
        private async Task<List<FacetOption>> WithDisplayNamesAsync(List<KeyValuePair<string, int>> counts, CancellationToken ct)
        {
            if (counts.Count == 0) return [];
            var keys = counts.Select(kv => kv.Key).ToList();
            var display = (await db.ItemCredits.AsNoTracking()
                    .Where(c => c.NormalizedName != null && keys.Contains(c.NormalizedName))
                    .GroupBy(c => c.NormalizedName!)
                    .Select(g => new { g.Key, Name = g.Max(c => c.Name) }).ToListAsync(ct))
                .ToDictionary(x => x.Key, x => x.Name, StringComparer.OrdinalIgnoreCase);
            return counts.Select(kv => new FacetOption(display.GetValueOrDefault(kv.Key) ?? kv.Key, kv.Value)).ToList();
        }

        /// <summary>
        /// Tag counts: the item's own tags plus the tags its SERIES carries, both counted per ITEM (a tag arriving
        /// from two sources is one book, not two) so a chip's number means "this many books". The two halves are
        /// summed the way the standalone site summed its per-source contributions.
        /// </summary>
        private async Task<Dictionary<string, int>> TagCountsAsync(IQueryable<Item> live, CancellationToken ct)
        {
            var itemTags = await db.ItemTags.AsNoTracking()
                .Where(t => (t.Category == "tag" || t.Category == "genre") && t.Value != "")
                .Join(live, t => t.ItemId, i => i.Id, (t, i) => new { t.Value, t.ItemId })
                .Distinct()
                .GroupBy(x => x.Value).Select(g => new { g.Key, Count = g.Count() }).ToListAsync(ct);

            var seriesTags = await live.Where(i => i.SeriesId != null)
                .Join(db.SeriesTags.AsNoTracking().Where(t => t.Category == "tag" && t.Value != ""),
                    i => i.SeriesId!.Value, t => t.SeriesId, (i, t) => new { t.Value, ItemId = i.Id })
                .Distinct()
                .GroupBy(x => x.Value).Select(g => new { g.Key, Count = g.Count() }).ToListAsync(ct);

            var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var row in itemTags.Concat(seriesTags))
                counts[row.Key] = counts.GetValueOrDefault(row.Key) + row.Count;
            return counts;
        }

        private async Task<Dictionary<int, string>> FolderNamesAsync(List<int> ids, CancellationToken ct)
        {
            if (ids.Count == 0) return new Dictionary<int, string>();
            var rows = await db.Folders.AsNoTracking().Where(f => ids.Contains(f.Id))
                .Select(f => new { f.Id, f.Name }).ToListAsync(ct);
            return rows.ToDictionary(f => f.Id, f => f.Name ?? "Unknown");
        }

        /// <summary>
        /// Series display names for a set of ids. The full id → name map is ~19k two-column rows and streams faster
        /// than SQLite parses a 19k-value IN list, so past a threshold the whole (small) table is read instead.
        /// </summary>
        private async Task<Dictionary<int, string>> SeriesNamesAsync(List<int> ids, CancellationToken ct)
        {
            if (ids.Count == 0) return new Dictionary<int, string>();
            var query = db.Series.AsNoTracking().Select(s => new { s.Id, s.Name, s.DisplayNameOverride });
            var rows = ids.Count > 2000
                ? await query.ToListAsync(ct)
                : await query.Where(s => ids.Contains(s.Id)).ToListAsync(ct);
            return rows.ToDictionary(s => s.Id, s => s.DisplayNameOverride ?? s.Name ?? "");
        }

        // ── caching ───────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>Every cache key carries the caller's facts: the gate changes what a query returns.</summary>
        private string UserSig() =>
            $"{BooksIdentity.UserId(User)}:{BooksIdentity.CeilingFor(User)}:{(BooksIdentity.IsAdmin(User) ? 1 : 0)}";

        /// <summary>
        /// The heads cache key — or NULL, which means "do not cache this one". A mark-filtered signature is
        /// per-user AND changes on every click, so caching it would serve a stale shelf the moment the reader
        /// marked something; recomputing it is the cheaper of the two mistakes.
        /// </summary>
        private string? HeadsSig(ItemKind kind, string by, string? q, string? filter, ExactFilters exact, bool wantToReadOnly, bool readOnly) =>
            wantToReadOnly || readOnly ? null : $"books:heads:{UserSig()}:{kind}:{by}:{q}:{filter}:{exact.Sig}";

        private static TimeSpan Ttl(string? q, string? filter, ExactFilters exact) =>
            string.IsNullOrEmpty(q) && string.IsNullOrEmpty(filter) && exact.IsEmpty ? HeadsTtlDefault : HeadsTtlFiltered;

        private async Task<List<GroupHead>> CachedHeadsAsync(string? sig, TimeSpan ttl, Func<Task<List<GroupHead>>> factory)
        {
            if (sig == null) return await factory();
            if (cache.TryGetValue(sig, out List<GroupHead>? hit) && hit != null) return hit;
            var value = await factory();
            Cache(sig, value, ttl);
            return value;
        }

        // The cache runs with a size limit, so every entry declares a size. One entry = one unit: the limit is a
        // count of payloads, not bytes — enough to bound the working set without pretending to measure heap.
        private void Cache<T>(string key, T value, TimeSpan ttl) =>
            cache.Set(key, value, new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = ttl, Size = 1 });

        // ── small helpers ─────────────────────────────────────────────────────────────────────────────────────

        private static readonly Regex GroupByPattern = new("^(series|publisher|decade|collection|franchise)$", RegexOptions.Compiled);

        internal static string NormalizeGroupBy(string? groupBy)
        {
            var value = (groupBy ?? "").Trim().ToLowerInvariant();
            return GroupByPattern.IsMatch(value) ? value : "collection";
        }

        private static int? ParseInt(string s) =>
            int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;
    }
}
