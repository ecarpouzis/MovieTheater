using System.Globalization;
using Microsoft.EntityFrameworkCore;
using MovieTheater.Books.Access;
using MovieTheater.Books.Controllers;
using MovieTheater.Books.Db;
using MovieTheater.Books.Projections;

namespace MovieTheater.Books.Opds
{
    /// <summary>
    /// Every OPDS document the vertical serves: the root navigation feed, the category feeds (with paging), the
    /// per-series acquisition feed in reading order, search, and the OpenSearch description.
    ///
    /// <para><b>The gate is the same one everything else uses.</b> Each query starts at
    /// <c>ExcludeHidden().ApplyMaturity(ceiling)</c> — the shadow duplicates the dedup pass hid never appear, and
    /// a restricted account cannot enumerate through OPDS what the web catalog refuses it. That hole was real on
    /// the standalone site (folder authorisation alone, no ceiling) and closing it is why the maturity tests
    /// below exist.</para>
    ///
    /// <para><b>Materialize before shaping strings.</b> Every string built for a document is built AFTER the rows
    /// are in memory. The standalone site's root feed was a hard 500 in production because it projected
    /// <c>Enum.ToString().ToLowerInvariant()</c> inside the LINQ tree; the tests here execute against real SQLite
    /// precisely so that class of bug cannot come back invisible to the compiler.</para>
    /// </summary>
    public sealed class OpdsFeedService
    {
        /// <summary>Tags copied onto an entry as Atom categories. Enough to be useful, bounded so one over-tagged
        /// item cannot double the size of a page of 50.</summary>
        public const int MaxEntryCategories = 12;

        /// <summary>Creators promoted to Atom authors. The rest stay in the content line.</summary>
        public const int MaxEntryAuthors = 3;

        private readonly BooksDb db;
        public OpdsFeedService(BooksDb db) => this.db = db;

        /// <summary>The base visible set for a caller: no shadow duplicates, gated at the caller's ceiling.</summary>
        private IQueryable<Item> Visible(int ceiling) =>
            db.Items.AsNoTracking().ExcludeHidden().ApplyMaturity(db, ceiling);

        // ── root ──────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// The navigation feed every reader fetches first. An entry is written only when it would lead somewhere:
        /// a caller with no books sees no Books shelf, and a caller with no user id sees no personal shelves.
        /// </summary>
        public async Task<string> BuildRootAsync(OpdsContext ctx, CancellationToken ct = default)
        {
            var visible = Visible(ctx.Ceiling);
            var anyComics = await visible.AnyAsync(i => i.Kind == ItemKind.Comic, ct);
            var anyBooks = await visible.AnyAsync(i => i.Kind == ItemKind.Book, ct);
            var anyKids = ctx.Ceiling == 0 ? anyComics || anyBooks : await Visible(0).AnyAsync(ct);
            var anything = anyComics || anyBooks;

            using var w = new OpdsFeedWriter(OpdsUrls.Root(ctx.FeedBase), ctx.CatalogTitle, "OPDS catalog");
            w.WriteLink("self", OpdsXml.NavigationType, OpdsUrls.Root(ctx.FeedBase));
            w.WriteLink("start", OpdsXml.NavigationType, OpdsUrls.Root(ctx.FeedBase));
            w.WriteLink("search", OpdsXml.OpenSearchLinkType, OpdsUrls.OpenSearch(ctx.FeedBase), "Search the catalog");

            foreach (var category in OpdsCategories.All)
            {
                if (category.NeedsKey) continue;                                  // reached from the publishers feed
                if (category.NeedsUser && ctx.UserId == null) continue;           // no identity ⇒ no personal shelf
                if (!anything) continue;
                if (category.Key == OpdsCategories.Comics && !anyComics) continue;
                if (category.Key == OpdsCategories.Books && !anyBooks) continue;
                if (category.Key == OpdsCategories.Kids && !anyKids) continue;

                w.StartEntry(OpdsUrls.CategoryUrn(category.Key), category.Title, DateTime.UtcNow);
                w.WriteContent(category.Subtitle);
                w.WriteLink("subsection", category.IsNavigation ? OpdsXml.NavigationType : OpdsXml.AcquisitionType,
                    OpdsUrls.Category(ctx.FeedBase, category.Key));
                w.EndEntry();
            }

            return w.Finish();
        }

        // ── categories ────────────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// One category feed. Null for an unknown category, a personal shelf without an identity, or the
        /// publisher drill without a key — all of which the route answers as 404.
        /// </summary>
        public async Task<string?> BuildCategoryAsync(
            string? categoryKey, OpdsContext ctx, int page = 1, string? key = null, CancellationToken ct = default)
        {
            var category = OpdsCategories.Find(categoryKey);
            if (category == null) return null;
            if (category.NeedsUser && ctx.UserId == null) return null;
            if (category.NeedsKey && string.IsNullOrWhiteSpace(key)) return null;

            return category.Key switch
            {
                OpdsCategories.SeriesList => await BuildSeriesListAsync(ctx, page, ct),
                OpdsCategories.PublisherList => await BuildPublisherListAsync(ctx, page, ct),
                _ => await BuildItemCategoryAsync(category, ctx, page, key, ct),
            };
        }

        private async Task<string> BuildItemCategoryAsync(
            OpdsCategory category, OpdsContext ctx, int page, string? key, CancellationToken ct)
        {
            var visible = Visible(ctx.Ceiling);
            IQueryable<Item> query = category.Key switch
            {
                OpdsCategories.Recent => visible.OrderByDescending(i => i.IndexedAt).ThenByDescending(i => i.Id),
                OpdsCategories.Comics => ByTitle(visible.Where(i => i.Kind == ItemKind.Comic)),
                OpdsCategories.Books => ByTitle(visible.Where(i => i.Kind == ItemKind.Book)),
                // The kids shelf is the ceiling-0 view of the library for ANY caller — it is a shelf, not a
                // permission, so an adult browsing it gets exactly what a kid account would get.
                OpdsCategories.Kids => ByTitle(Visible(0)),
                OpdsCategories.Publisher => ByTitle(visible.Where(i => i.ResolvedPublisher == key)),
                OpdsCategories.WantToRead => MarkedItems(ctx, visible, wanted: true),
                OpdsCategories.InProgress => MarkedItems(ctx, visible, wanted: false),
                _ => ByTitle(visible),
            };

            var title = category.Key == OpdsCategories.Publisher ? key! : category.Title;
            var self = OpdsUrls.Category(ctx.FeedBase, category.Key, page, category.NeedsKey ? key : null);
            var pager = (int p) => OpdsUrls.Category(ctx.FeedBase, category.Key, p, category.NeedsKey ? key : null);
            return await WriteAcquisitionFeedAsync(
                query, OpdsUrls.CategoryUrn(category.Key, category.NeedsKey ? key : null),
                $"{ctx.CatalogTitle} / {title}", category.Subtitle, self, pager, ctx, page, ct);
        }

        /// <summary>Alphabetical by the normalized title, id as the stable tiebreaker — every ORDER BY ends with the key.</summary>
        private static IOrderedQueryable<Item> ByTitle(IQueryable<Item> items) =>
            items.OrderBy(i => i.NormalizedTitle ?? i.ResolvedTitle ?? i.FileName).ThenBy(i => i.Id);

        /// <summary>
        /// The caller's own shelves, newest activity first. Written as a join rather than an id set so the ORDER
        /// BY can use the state row's <c>UpdatedAt</c> — "where you left off" is worthless in title order.
        /// </summary>
        private IQueryable<Item> MarkedItems(OpdsContext ctx, IQueryable<Item> visible, bool wanted)
        {
            var states = db.UserItemStates.AsNoTracking().Where(s => s.UserId == ctx.UserId!.Value);
            states = wanted ? states.Where(s => s.WantToRead) : states.Where(s => s.Status == ReadStatus.InProgress);
            return from s in states
                   join i in visible on s.ItemId equals i.Id
                   orderby s.UpdatedAt descending, i.Id descending
                   select i;
        }

        // ── series ────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>The A–Z navigation feed of every series the caller can see something from.</summary>
        private async Task<string> BuildSeriesListAsync(OpdsContext ctx, int page, CancellationToken ct)
        {
            var seriesIds = Visible(ctx.Ceiling).Where(i => i.SeriesId != null).Select(i => i.SeriesId!.Value).Distinct();
            var query = db.Series.AsNoTracking().Where(s => seriesIds.Contains(s.Id))
                .OrderBy(s => s.DisplayNameOverride ?? s.Name).ThenBy(s => s.Id);

            var total = await query.CountAsync(ct);
            var rows = await query.Skip(Offset(page, ctx.PageSize)).Take(ctx.PageSize)
                .Select(s => new { s.Id, s.Name, s.DisplayNameOverride, s.YearStart, s.YearEnd, s.IsOngoing })
                .ToListAsync(ct);

            // How many issues of each the caller actually HOLDS — bounded to this page's ids, never the shelf.
            var pageIds = rows.Select(r => r.Id).ToList();
            var held = (await Visible(ctx.Ceiling)
                    .Where(i => i.SeriesId != null && pageIds.Contains(i.SeriesId.Value))
                    .GroupBy(i => i.SeriesId!.Value)
                    .Select(g => new { SeriesId = g.Key, Count = g.Count() })
                    .ToListAsync(ct))
                .ToDictionary(x => x.SeriesId, x => x.Count);

            var self = OpdsUrls.Category(ctx.FeedBase, OpdsCategories.SeriesList, page);
            using var w = new OpdsFeedWriter(OpdsUrls.CategoryUrn(OpdsCategories.SeriesList), $"{ctx.CatalogTitle} / Series");
            WriteFeedLinks(w, ctx, self, p => OpdsUrls.Category(ctx.FeedBase, OpdsCategories.SeriesList, p),
                OpdsXml.NavigationType, page, ctx.PageSize, total, rows.Count);

            foreach (var s in rows)
            {
                var name = Coalesce(s.DisplayNameOverride, s.Name) ?? $"Series {s.Id}";
                w.StartEntry(OpdsUrls.SeriesUrn(s.Id), name, null);
                w.WriteContent(SeriesLine(held.GetValueOrDefault(s.Id), s.YearStart, s.YearEnd, s.IsOngoing));
                w.WriteLink("subsection", OpdsXml.AcquisitionType, OpdsUrls.Series(ctx.FeedBase, s.Id));
                w.EndEntry();
            }
            return w.Finish();
        }

        /// <summary>
        /// One series' issues IN READING ORDER — the derived <c>ReadIndex</c> when the resolver produced one,
        /// the item id after that. Null when the caller can see nothing in it (which is also the answer for a
        /// series that does not exist: absent and forbidden must look the same).
        /// </summary>
        public async Task<string?> BuildSeriesFeedAsync(int seriesId, OpdsContext ctx, int page = 1, CancellationToken ct = default)
        {
            var items = Visible(ctx.Ceiling).Where(i => i.SeriesId == seriesId);
            if (!await items.AnyAsync(ct)) return null;

            var name = await db.Series.AsNoTracking().Where(s => s.Id == seriesId)
                .Select(s => s.DisplayNameOverride ?? s.Name).FirstOrDefaultAsync(ct) ?? $"Series {seriesId}";

            var ordered = from i in items
                          join r in db.ReadingOrderEntries.AsNoTracking() on i.Id equals r.ItemId into ro
                          from r in ro.DefaultIfEmpty()
                          orderby (r == null ? int.MaxValue : r.ReadIndex ?? int.MaxValue), i.Id
                          select i;

            var self = OpdsUrls.Series(ctx.FeedBase, seriesId, page);
            return await WriteAcquisitionFeedAsync(ordered, OpdsUrls.SeriesUrn(seriesId),
                $"{ctx.CatalogTitle} / {name}", "In reading order", self,
                p => OpdsUrls.Series(ctx.FeedBase, seriesId, p), ctx, page, ct);
        }

        // ── publishers ────────────────────────────────────────────────────────────────────────────────────

        private async Task<string> BuildPublisherListAsync(OpdsContext ctx, int page, CancellationToken ct)
        {
            var named = Visible(ctx.Ceiling).Where(i => i.ResolvedPublisher != null && i.ResolvedPublisher != "");
            var total = await named.Select(i => i.ResolvedPublisher).Distinct().CountAsync(ct);

            // Anonymous type, NOT a positional record: a record constructor inside GroupBy+Select does not
            // translate on EF 10 (the data-layer lesson the port carries over).
            var rows = await named.GroupBy(i => i.ResolvedPublisher!)
                .Select(g => new { Name = g.Key, Count = g.Count() })
                .OrderBy(x => x.Name)
                .Skip(Offset(page, ctx.PageSize)).Take(ctx.PageSize)
                .ToListAsync(ct);

            var self = OpdsUrls.Category(ctx.FeedBase, OpdsCategories.PublisherList, page);
            using var w = new OpdsFeedWriter(OpdsUrls.CategoryUrn(OpdsCategories.PublisherList), $"{ctx.CatalogTitle} / Publishers");
            WriteFeedLinks(w, ctx, self, p => OpdsUrls.Category(ctx.FeedBase, OpdsCategories.PublisherList, p),
                OpdsXml.NavigationType, page, ctx.PageSize, total, rows.Count);

            foreach (var p in rows)
            {
                w.StartEntry(OpdsUrls.CategoryUrn(OpdsCategories.Publisher, p.Name), p.Name, null);
                w.WriteContent(Plural(p.Count, "title", "titles"));
                w.WriteLink("subsection", OpdsXml.AcquisitionType,
                    OpdsUrls.Category(ctx.FeedBase, OpdsCategories.Publisher, 1, p.Name));
                w.EndEntry();
            }
            return w.Finish();
        }

        // ── search ────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// <c>GET /opds/search?q=</c> — the same FTS5 index and the same escaping the web catalog's <c>q=</c>
        /// uses, so a term that finds a book in the browser finds it on the e-reader. An empty or
        /// punctuation-only query is an empty feed, never an error: readers send whatever the user typed.
        /// </summary>
        public async Task<string> BuildSearchAsync(string? q, OpdsContext ctx, int page = 1, CancellationToken ct = default)
        {
            var term = (q ?? "").Trim();
            var match = term.Length == 0 ? "" : CatalogController.BuildFtsQuery(term);
            IQueryable<Item> query;
            if (match.Length == 0)
            {
                query = Visible(ctx.Ceiling).Where(i => i.Id < 0);
            }
            else
            {
                // An IQueryable of ids, so EF renders a subquery instead of an id list — SQLite's variable
                // limit would otherwise cap a search at 999 hits.
                var ids = ItemFts.Search(db, match, CatalogController.FtsLimit);
                query = ByTitle(Visible(ctx.Ceiling).Where(i => ids.Contains(i.Id)));
            }

            var self = OpdsUrls.Search(ctx.FeedBase, term, page);
            return await WriteAcquisitionFeedAsync(query, $"urn:mt-books:search:{term}",
                $"{ctx.CatalogTitle} / Search", $"Results for \"{term}\"", self,
                p => OpdsUrls.Search(ctx.FeedBase, term, p), ctx, page, ct);
        }

        /// <summary>
        /// The OpenSearch description an e-reader fetches once and then owns: it is what puts a search box on the
        /// catalog. The template's <c>{searchTerms}</c> is substituted by the client.
        /// </summary>
        public static string BuildOpenSearchDescription(OpdsContext ctx)
        {
            using var sw = new OpdsXml.Utf8StringWriter();
            using (var xw = System.Xml.XmlWriter.Create(sw, new System.Xml.XmlWriterSettings { Indent = true, Encoding = System.Text.Encoding.UTF8 }))
            {
                xw.WriteStartDocument();
                xw.WriteStartElement("OpenSearchDescription", OpdsXml.OpenSearchNs);
                xw.WriteElementString("ShortName", OpdsXml.OpenSearchNs, ctx.CatalogTitle);
                xw.WriteElementString("Description", OpdsXml.OpenSearchNs, $"Search the {ctx.CatalogTitle} catalog");
                xw.WriteElementString("InputEncoding", OpdsXml.OpenSearchNs, "UTF-8");
                xw.WriteElementString("OutputEncoding", OpdsXml.OpenSearchNs, "UTF-8");
                xw.WriteStartElement("Url", OpdsXml.OpenSearchNs);
                xw.WriteAttributeString("type", OpdsXml.AcquisitionType);
                xw.WriteAttributeString("template", $"{ctx.FeedBase}{OpdsUrls.Prefix}/search?q={{searchTerms}}");
                xw.WriteEndElement();
                xw.WriteEndElement();
                xw.WriteEndDocument();
            }
            return sw.ToString();
        }

        // ── the acquisition feed ──────────────────────────────────────────────────────────────────────────

        private async Task<string> WriteAcquisitionFeedAsync(
            IQueryable<Item> query, string feedId, string title, string? subtitle,
            string self, Func<int, string> pageUrl, OpdsContext ctx, int page, CancellationToken ct)
        {
            var total = await query.CountAsync(ct);
            var rows = await query.Skip(Offset(page, ctx.PageSize)).Take(ctx.PageSize)
                .Select(ItemSummary.Project).ToListAsync(ct);

            // The caller's own positions for THIS page only — that is what fills pse:lastRead, and it is one
            // indexed read for 50 ids rather than one per entry.
            var ids = rows.Select(r => r.Id).ToList();
            var positions = ctx.UserId is int uid && ids.Count > 0
                ? await db.UserItemStates.AsNoTracking()
                    .Where(s => s.UserId == uid && ids.Contains(s.ItemId))
                    .ToDictionaryAsync(s => s.ItemId, ct)
                : new Dictionary<int, UserItemState>();

            using var w = new OpdsFeedWriter(feedId, title, subtitle);
            WriteFeedLinks(w, ctx, self, pageUrl, OpdsXml.AcquisitionType, page, ctx.PageSize, total, rows.Count);
            foreach (var row in rows) WriteItemEntry(w, row, positions.GetValueOrDefault(row.Id), ctx);
            return w.Finish();
        }

        private static void WriteFeedLinks(OpdsFeedWriter w, OpdsContext ctx, string self, Func<int, string> pageUrl,
            string selfType, int page, int pageSize, int total, int returned)
        {
            w.WritePaging(total, pageSize, Offset(page, pageSize));
            w.WriteLink("self", selfType, self);
            w.WriteLink("start", OpdsXml.NavigationType, OpdsUrls.Root(ctx.FeedBase));
            w.WriteLink("up", OpdsXml.NavigationType, OpdsUrls.Root(ctx.FeedBase));
            w.WriteLink("search", OpdsXml.OpenSearchLinkType, OpdsUrls.OpenSearch(ctx.FeedBase), "Search the catalog");
            if (Offset(page, pageSize) + returned < total) w.WriteLink("next", selfType, pageUrl(page + 1));
            if (page > 1) w.WriteLink("previous", selfType, pageUrl(page - 1));
        }

        private static void WriteItemEntry(OpdsFeedWriter w, ItemSummary row, UserItemState? position, OpdsContext ctx)
        {
            w.StartEntry(OpdsUrls.ItemUrn(row.Id), row.Title ?? row.FileName, row.IndexedAt);

            foreach (var author in Split(row.CreatorsCsv).Take(MaxEntryAuthors)) w.WriteAuthor(author);
            w.WriteContent(ContentLine(row));
            if (row.Year is int year) w.WriteDc("issued", year.ToString(CultureInfo.InvariantCulture));
            if (!string.IsNullOrWhiteSpace(row.Series)) w.WriteDc("isPartOf", row.Series!);
            foreach (var tag in Split(row.TagsCsv).Take(MaxEntryCategories)) w.WriteCategory(tag);

            if (ctx.HasMedia)
            {
                var thumb = Media.BooksMediaRoutes.ThumbUrl(ctx.MediaBase!, ctx.MediaToken!, row.Id);
                w.WriteLink(OpdsXml.ThumbnailRel, Services.ThumbnailService.ContentType, thumb);
                w.WriteLink(OpdsXml.ImageRel, Services.ThumbnailService.ContentType, thumb);
                w.WriteLink(OpdsXml.AcquisitionRel, OpdsXml.MediaTypeFor(row.Extension),
                    Media.BooksMediaRoutes.DownloadUrl(ctx.MediaBase!, ctx.MediaToken!, row.Id), row.FileName);
            }

            // PSE. A row with no indexed page count cannot advertise a count, and a stream link without one is
            // ignored by every client — so it gets no stream link at all rather than a broken one.
            if (row.PageCount is int count && count > 0)
            {
                var (lastRead, lastReadDate) = LastRead(position, count);
                w.WriteLink(OpdsXml.PseStreamRel, "image/jpeg", OpdsUrls.PageTemplate(ctx.FeedBase, row.Id),
                    pseCount: count, pseLastRead: lastRead, pseLastReadDate: lastReadDate);
            }

            w.EndEntry();
        }

        /// <summary>
        /// PSE's <c>lastRead</c> is a 1-BASED page number; the stored position is a 0-based index. A finished
        /// book reports the last page (that is what "read to the end" means to a reader app), an in-progress one
        /// reports where it stopped, and an untouched one reports nothing — sending <c>lastRead=1</c> for a book
        /// nobody opened makes every cover look half-read.
        /// </summary>
        internal static (int?, DateTime?) LastRead(UserItemState? position, int pageCount)
        {
            if (position == null) return (null, null);
            if (position.Status == ReadStatus.Finished) return (pageCount, position.UpdatedAt);
            if (position.Status == ReadStatus.InProgress && position.LastPage >= 0)
                return (Math.Clamp(position.LastPage + 1, 1, pageCount), position.UpdatedAt);
            return (null, null);
        }

        // ── small shaping helpers (all run on materialized rows, never inside a query) ─────────────────────

        internal static int Offset(int page, int pageSize) => (Math.Max(page, 1) - 1) * pageSize;

        private static string? Coalesce(string? a, string? b) =>
            !string.IsNullOrWhiteSpace(a) ? a : (!string.IsNullOrWhiteSpace(b) ? b : null);

        private static IEnumerable<string> Split(string? csv) =>
            (csv ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        private static string Plural(int n, string one, string many) =>
            $"{n.ToString(CultureInfo.InvariantCulture)} {(n == 1 ? one : many)}";

        private static string SeriesLine(int held, int? yearStart, int? yearEnd, bool ongoing)
        {
            var parts = new List<string> { Plural(held, "issue held", "issues held") };
            if (yearStart is int start)
                parts.Add(ongoing ? $"{start}–present" : yearEnd is int end && end != start ? $"{start}–{end}" : start.ToString(CultureInfo.InvariantCulture));
            return string.Join(" · ", parts);
        }

        private static string ContentLine(ItemSummary row)
        {
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(row.Series)) parts.Add(row.Series!);
            if (!string.IsNullOrWhiteSpace(row.Publisher)) parts.Add(row.Publisher!);
            if (row.Year is int y) parts.Add(y.ToString(CultureInfo.InvariantCulture));
            if (row.PageCount is int p && p > 0) parts.Add(Plural(p, "page", "pages"));
            if (row.FileSize > 0) parts.Add(Size(row.FileSize));
            return string.Join(" · ", parts);
        }

        private static string Size(long bytes) => bytes switch
        {
            >= 1024L * 1024 * 1024 => (bytes / (1024d * 1024 * 1024)).ToString("0.0", CultureInfo.InvariantCulture) + " GB",
            >= 1024 * 1024 => (bytes / (1024d * 1024)).ToString("0.0", CultureInfo.InvariantCulture) + " MB",
            >= 1024 => (bytes / 1024d).ToString("0", CultureInfo.InvariantCulture) + " KB",
            _ => bytes.ToString(CultureInfo.InvariantCulture) + " B",
        };
    }
}
