using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using MovieTheater.Books.Access;
using MovieTheater.Books.Archives;
using MovieTheater.Books.Db;
using MovieTheater.Books.Identity;
using MovieTheater.Books.Media;
using MovieTheater.Books.Projections;
using MovieTheater.Books.Services;

namespace MovieTheater.Books.Controllers
{
    /// <summary>One item's media URLs, as the batch manifest hands them out.</summary>
    public sealed record ThumbManifestEntry(string Url, string? Etag);

    /// <summary>
    /// Everything addressed by ITEM ID: the modal's detail payload, the reading-order hops, the Bubble Zoom
    /// overlay, the small "give me something to look at" library rails, and the thumbnail manifest a grid asks
    /// for in one round trip.
    ///
    /// <para><b>Every route here starts with <see cref="ItemAccess.GetAuthorizedItemAsync"/>.</b> That is the
    /// vertical's one authorization — exclusion plus the maturity ceiling, in a single indexed read — and it
    /// answers 404 rather than 403 so a gated account cannot map what it is gated out of. The BYTES live on the
    /// media plane (<c>/m/{token}/…</c>) and run the same helper there, so there is exactly one rule and two
    /// places that call it.</para>
    /// </summary>
    [ApiController]
    [Route("")]
    public sealed class ItemsController : ControllerBase
    {
        /// <summary>How many ids one manifest call may carry. A grid page is ~120; the cap keeps a hostile or
        /// buggy client from asking for the whole library in one request.</summary>
        public const int MaxManifestIds = 500;

        private readonly BooksDb db;
        private readonly BooksOptions options;
        private readonly ThumbnailService thumbnails;
        private readonly TextRegionService textRegions;
        private readonly PageByteCache pageCache;
        private readonly LocalArchiveCache archiveCache;
        private readonly IEnumerable<IArchiveReader> readers;

        public ItemsController(
            BooksDb db, BooksOptions options, ThumbnailService thumbnails, TextRegionService textRegions,
            PageByteCache pageCache, LocalArchiveCache archiveCache, IEnumerable<IArchiveReader> readers)
        {
            this.db = db;
            this.options = options;
            this.thumbnails = thumbnails;
            this.textRegions = textRegions;
            this.pageCache = pageCache;
            this.archiveCache = archiveCache;
            this.readers = readers;
        }

        // ── detail ────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// GET /items/{id} — the modal's payload: the browse summary plus every provenance block (the embedded
        /// ComicInfo, the series' ComicVine / external / MangaUpdates facts, the current insight prose and score,
        /// the LOCG record when the link is High or Medium, reading order, containment, collected-edition spans,
        /// and the credit and tag rows with their sources).
        /// </summary>
        [HttpGet("items/{id:int}")]
        public async Task<IActionResult> GetItem(int id, [FromQuery] string? mediaToken = null, CancellationToken ct = default)
        {
            var item = await ItemAccess.GetAuthorizedItemAsync(db, User, id, allowExcluded: true, ct);
            if (item == null) return NotFound();
            return Ok(await DetailAsync(item, mediaToken, ct));
        }

        /// <summary>
        /// GET /items/{id}/next — the next item to read.
        ///
        /// <para>Order of authority: the DERIVED reading order within the series (<c>ReadIndex</c> is dense
        /// 1..N within a series run), and when this item is not orderable, the next ITEM ID in the same series.
        /// The id fallback exists because a series with no computed order still has to let a reader move forward;
        /// it is a weaker claim, and the response says which one answered.</para>
        ///
        /// <para>204 when there is nothing after this one — not 404, which would read as "the item is gone".
        /// The target is authorized independently before it is exposed.</para>
        /// </summary>
        [HttpGet("items/{id:int}/next")]
        public Task<IActionResult> GetNext(int id, [FromQuery] string? mediaToken = null, CancellationToken ct = default) =>
            NeighbourAsync(id, forward: true, mediaToken, ct);

        /// <summary>GET /items/{id}/prev — the mirror of <see cref="GetNext"/>.</summary>
        [HttpGet("items/{id:int}/prev")]
        public Task<IActionResult> GetPrev(int id, [FromQuery] string? mediaToken = null, CancellationToken ct = default) =>
            NeighbourAsync(id, forward: false, mediaToken, ct);

        private async Task<IActionResult> NeighbourAsync(int id, bool forward, string? mediaToken, CancellationToken ct)
        {
            var item = await ItemAccess.GetAuthorizedItemAsync(db, User, id, allowExcluded: true, ct);
            if (item == null) return NotFound();

            var order = await db.ReadingOrderEntries.AsNoTracking()
                .Where(r => r.ItemId == id)
                .Select(r => new { r.SeriesId, r.ReadIndex })
                .FirstOrDefaultAsync(ct);

            int? targetId = null;
            string via = "id";

            if (order?.SeriesId is int runSeries && order.ReadIndex is int curIdx)
            {
                var run = db.ReadingOrderEntries.AsNoTracking().Where(r => r.SeriesId == runSeries && r.ReadIndex != null);
                targetId = forward
                    ? await run.Where(r => r.ReadIndex > curIdx).OrderBy(r => r.ReadIndex).ThenBy(r => r.ItemId)
                        .Select(r => (int?)r.ItemId).FirstOrDefaultAsync(ct)
                    : await run.Where(r => r.ReadIndex < curIdx).OrderByDescending(r => r.ReadIndex).ThenByDescending(r => r.ItemId)
                        .Select(r => (int?)r.ItemId).FirstOrDefaultAsync(ct);
                if (targetId is > 0) via = "readingOrder";
                else targetId = null;
            }

            if (targetId == null && item.SeriesId is int seriesId)
            {
                var siblings = db.Items.AsNoTracking().Where(i => i.SeriesId == seriesId).ExcludeHidden();
                targetId = forward
                    ? await siblings.Where(i => i.Id > id).OrderBy(i => i.Id).Select(i => (int?)i.Id).FirstOrDefaultAsync(ct)
                    : await siblings.Where(i => i.Id < id).OrderByDescending(i => i.Id).Select(i => (int?)i.Id).FirstOrDefaultAsync(ct);
                if (targetId is not > 0) targetId = null;
            }

            if (targetId == null) return NoContent();

            // Authorize the TARGET on its own: being allowed to see this item says nothing about the next one.
            var target = await ItemAccess.GetAuthorizedItemAsync(db, User, targetId.Value, allowExcluded: false, ct);
            if (target == null) return NoContent();

            var detail = await DetailAsync(target, mediaToken, ct);
            return Ok(new { via, item = detail });
        }

        // ── Bubble Zoom ───────────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// GET /items/{id}/pages/{n}/text-regions — where the lettering is on a page, normalized to 0–1, so the
        /// reader can magnify a balloon under a tap.
        ///
        /// <para>This is a BEST-EFFORT overlay: the reader prefetches regions for the current page and the next
        /// two, so a request routinely lands past the end of the archive or on a format with no reader. Those
        /// answer an empty list, not an error — an overlay that 500s would put a red line in the console on every
        /// last page of every book.</para>
        /// </summary>
        [HttpGet("items/{id:int}/pages/{pageIndex:int}/text-regions")]
        public async Task<IActionResult> GetTextRegions(int id, int pageIndex, CancellationToken ct = default)
        {
            var item = await ItemAccess.GetAuthorizedItemAsync(db, User, id, allowExcluded: true, ct);
            if (item == null) return NotFound();
            if (pageIndex < 0) return Ok(new { regions = Array.Empty<TextRegion>() });

            // Fully determined by (item, page, file mtime): a conditional revalidation is answered before the
            // detector — which is the expensive part — ever runs.
            var etag = $"\"{id}_tr_{pageIndex}_{Ticks(item)}\"";
            Response.Headers.CacheControl = "private, max-age=3600";
            Response.Headers.ETag = etag;
            if (Request.Headers.IfNoneMatch.ToString() == etag) return StatusCode(StatusCodes.Status304NotModified);

            TextRegion[] regions;
            try
            {
                var cacheKey = PageByteCache.Key(item.Path, Ticks(item), pageIndex);
                regions = await textRegions.GetRegionsAsync(id, pageIndex, async () =>
                {
                    // Through the SAME byte cache the image request uses, so the detector does not re-extract a
                    // page the reader already fetched. warm:false — the image request for this page already
                    // started the local archive copy, and a second warm would be a duplicate.
                    var bytes = await pageCache.GetOrExtractAsync(cacheKey, () =>
                    {
                        var physical = archiveCache.Resolve(item.Path, Ticks(item), warm: false);
                        var reader = readers.ForFile(physical, item.Extension)
                            ?? throw new NotSupportedException("no reader");
                        return reader.GetPageAsync(physical, pageIndex);
                    });
                    return new MemoryStream(bytes, writable: false);
                });
            }
            catch (NotSupportedException) { regions = []; }
            catch (ArgumentOutOfRangeException) { regions = []; }
            catch (FileNotFoundException) { regions = []; }
            catch (DirectoryNotFoundException) { regions = []; }
            catch (IOException) { regions = []; }

            return Ok(new { regions });
        }

        // ── thumbnail manifest ────────────────────────────────────────────────────────────────────────────

        public sealed record ThumbBatchRequest(List<int>? Ids, string? MediaToken);

        /// <summary>
        /// POST /thumbs/batch — ids in, media URLs out, in ONE round trip.
        ///
        /// <para>A grid needs a URL and a cache validator per card; asking per card would be 120 requests before
        /// the first picture. An id the caller may not see comes back <c>null</c> — the same answer as an id that
        /// does not exist, because the gate must not be a directory of what is hidden.</para>
        ///
        /// <para>The manifest does NOT generate anything. It reports what is on disk, so a cold library returns
        /// nulls and the thumbnail job fills them in — a request must never turn into 120 archive opens.</para>
        /// </summary>
        [HttpPost("thumbs/batch")]
        public async Task<IActionResult> ThumbsBatch([FromBody] ThumbBatchRequest? request, CancellationToken ct = default)
        {
            var ids = (request?.Ids ?? []).Distinct().Take(MaxManifestIds).ToList();
            var results = new Dictionary<int, ThumbManifestEntry?>();
            if (ids.Count == 0) return Ok(results);

            var token = ResolveMediaToken(request?.MediaToken);
            if (token == null) return StatusCode(StatusCodes.Status503ServiceUnavailable, new { configured = false });

            // One indexed read for the whole page instead of one per id — the same gate, applied in bulk.
            var visible = await db.Items.AsNoTracking().Where(i => ids.Contains(i.Id))
                .ExcludeHiddenForDirectory().ApplyMaturity(db, BooksIdentity.CeilingFor(User))
                .Select(i => i.Id).ToListAsync(ct);
            var visibleSet = visible.ToHashSet();

            foreach (var id in ids)
            {
                if (!visibleSet.Contains(id) || !thumbnails.Exists(id)) { results[id] = null; continue; }
                var path = thumbnails.GetCachePath(id);
                string? etag = null;
                try { etag = "\"" + System.IO.File.GetLastWriteTimeUtc(path).Ticks.ToString("x") + "\""; } catch { /* stat raced a delete */ }
                results[id] = new ThumbManifestEntry(BooksMediaRoutes.ThumbUrl(options.PublicBaseUrl!, token, id), etag);
            }
            return Ok(results);
        }

        // ── library rails ─────────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// GET /items/random?kind= — one item, uniformly at random from what this caller may see.
        ///
        /// <para>It picks by OFFSET, not by ordering on a random key: <c>ORDER BY random()</c> would sort the
        /// whole gated set on every call, and the set is the library.</para>
        /// </summary>
        [HttpGet("items/random")]
        public async Task<IActionResult> GetRandom([FromQuery] string? kind = null, [FromQuery] string? mediaToken = null,
            CancellationToken ct = default)
        {
            var query = ItemAccess.VisibleItems(db, User, CatalogController.ParseKind(kind));
            var count = await query.CountAsync(ct);
            if (count == 0) return NotFound();
            var item = await query.OrderBy(i => i.Id).Skip(Random.Shared.Next(count)).FirstAsync(ct);
            return Ok(await DetailAsync(item, mediaToken, ct));
        }

        /// <summary>GET /items/latest?kind=&amp;skip=&amp;top= — most recently indexed first, id as the tiebreaker.</summary>
        [HttpGet("items/latest")]
        public async Task<IActionResult> GetLatest([FromQuery] string? kind = null, [FromQuery] int skip = 0,
            [FromQuery] int top = 24, CancellationToken ct = default)
        {
            skip = Math.Max(0, skip);
            top = Math.Clamp(top, 1, 200);
            var query = ItemAccess.VisibleItems(db, User, CatalogController.ParseKind(kind));
            var total = await query.CountAsync(ct);
            var items = await query.OrderByDescending(i => i.IndexedAt).ThenByDescending(i => i.Id)
                .Skip(skip).Take(top).Select(ItemSummary.Project).ToListAsync(ct);
            return Ok(new { total, skip, top, items });
        }

        /// <summary>
        /// GET /items/featured?kind=&amp;count=&amp;seed= — a shuffled handful of the best-rated items, with the
        /// most recent ones filling in when there are not enough rated ones.
        ///
        /// <para><paramref name="seed"/> makes the shuffle REPRODUCIBLE, which is what lets a page that re-renders
        /// (or a second component on the same page) show the same row instead of a different one each time.</para>
        /// </summary>
        [HttpGet("items/featured")]
        public async Task<IActionResult> GetFeatured([FromQuery] string? kind = null, [FromQuery] int count = 6,
            [FromQuery] int? seed = null, CancellationToken ct = default)
        {
            count = Math.Clamp(count, 1, 60);
            var query = ItemAccess.VisibleItems(db, User, CatalogController.ParseKind(kind));

            // A pool several times the ask, so the shuffle has something to choose from without ordering the
            // whole library.
            var pool = await query.Where(i => i.ResolvedRating != null)
                .OrderByDescending(i => i.ResolvedRating).ThenByDescending(i => i.IndexedAt).ThenBy(i => i.Id)
                .Take(count * 8).Select(ItemSummary.Project).ToListAsync(ct);

            if (pool.Count < count)
            {
                var filler = await query.Where(i => i.ResolvedRating == null)
                    .OrderByDescending(i => i.IndexedAt).ThenBy(i => i.Id)
                    .Take((count - pool.Count) * 2).Select(ItemSummary.Project).ToListAsync(ct);
                pool.AddRange(filler);
            }

            var random = seed.HasValue ? new Random(seed.Value) : Random.Shared;
            var shuffled = pool.OrderBy(_ => random.Next()).Take(count).ToList();
            return Ok(new { seed, items = shuffled });
        }

        /// <summary>
        /// GET /library/{kind}/publishers — publisher names with their item counts, plus the letter each falls
        /// under so a client can build an A–Z rail without a second pass.
        /// </summary>
        [HttpGet("library/{kind}/publishers")]
        public async Task<IActionResult> GetPublishers(string kind, CancellationToken ct = default)
        {
            var live = ItemAccess.VisibleItems(db, User, CatalogController.ParseKind(kind));
            var counts = await live.Where(i => i.ResolvedPublisher != null && i.ResolvedPublisher != "")
                .GroupBy(i => i.ResolvedPublisher!)
                .Select(g => new { Name = g.Key, Count = g.Count() })
                .ToListAsync(ct);

            var names = counts.Select(c => c.Name).ToList();
            var rows = (await db.Publishers.AsNoTracking().Where(p => names.Contains(p.Name))
                    .Select(p => new { p.Id, p.Name, p.FullName }).ToListAsync(ct))
                .GroupBy(p => p.Name).ToDictionary(g => g.Key, g => g.First());

            var publishers = counts
                .OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
                .Select(c =>
                {
                    var row = rows.GetValueOrDefault(c.Name);
                    return new { id = row?.Id, name = c.Name, fullName = row?.FullName, itemCount = c.Count, firstLetter = FirstLetter(c.Name) };
                })
                .ToList();
            return Ok(publishers);
        }

        /// <summary>GET /library/{kind}/events — the distinct crossover/event names with their counts.</summary>
        [HttpGet("library/{kind}/events")]
        public async Task<IActionResult> GetEvents(string kind, CancellationToken ct = default)
        {
            var live = ItemAccess.VisibleItems(db, User, CatalogController.ParseKind(kind));
            var events = await live.Where(i => i.Comic != null && i.Comic.EventName != null && i.Comic.EventName != "")
                .GroupBy(i => i.Comic!.EventName!)
                .Select(g => new { name = g.Key, count = g.Count() })
                .OrderBy(x => x.name)
                .ToListAsync(ct);
            return Ok(events);
        }

        // ── shared ────────────────────────────────────────────────────────────────────────────────────────

        private static string FirstLetter(string? name) =>
            !string.IsNullOrEmpty(name) && char.IsLetter(name[0]) ? char.ToUpperInvariant(name[0]).ToString() : "#";

        private static long Ticks(Item item) => item.FileModifiedAt?.Ticks ?? 0;

        private Task<ItemDetail> DetailAsync(Item item, string? mediaToken, CancellationToken ct)
        {
            var token = ResolveMediaToken(mediaToken);
            var baseUrl = options.PublicBaseUrl;
            Func<long, string?>? thumb = null, download = null, pages = null;
            if (token != null && baseUrl != null)
            {
                thumb = id => BooksMediaRoutes.ThumbUrl(baseUrl, token, id);
                download = id => BooksMediaRoutes.DownloadUrl(baseUrl, token, id);
                // A TEMPLATE, not a URL: the client substitutes the page number, so one detail response covers
                // every page of the book instead of carrying hundreds of links.
                pages = id => BooksMediaRoutes.PageUrlTemplate(baseUrl, token, id);
            }
            return ItemDetailBuilder.BuildAsync(db, item, thumb, download, pages, thumbnails.Exists(item.Id), ct);
        }

        /// <summary>
        /// The token to build media URLs with: the caller's own if it sent one, otherwise a fresh one minted for
        /// the caller's identity. Minting here rather than making the client fetch <c>/media-token</c> first is
        /// what keeps a card's URLs valid in one round trip; the token carries the SAME ceiling and admin flag
        /// the identity header established, so it can never widen what its holder may fetch.
        /// </summary>
        private string? ResolveMediaToken(string? supplied)
        {
            if (!string.IsNullOrWhiteSpace(supplied)) return supplied;
            if (string.IsNullOrEmpty(options.MediaTokenSecret) || string.IsNullOrEmpty(options.PublicBaseUrl)) return null;
            if (BooksIdentity.UserId(User) is not int userId) return null;
            return BooksMediaToken.MintNow(options.MediaTokenSecret, userId,
                BooksIdentity.CeilingFor(User), BooksIdentity.IsAdmin(User), out _);
        }
    }
}
