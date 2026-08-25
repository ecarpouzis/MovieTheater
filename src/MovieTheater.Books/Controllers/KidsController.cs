using System.Globalization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using MovieTheater.Books.Access;
using MovieTheater.Books.Db;
using MovieTheater.Books.Media;
using MovieTheater.Books.Projections;

namespace MovieTheater.Books.Controllers
{
    /// <summary>
    /// The kids browse: one spine-out shelf per kid-clear series, each holding that series' issues.
    ///
    /// <para><b>What makes this view different from every other browse</b> is that its gate does not come from
    /// the caller. <see cref="KidsPolicy"/> forces ceiling 0 and requires an admin-allow-listed audience tag on
    /// top of it, so an adult, an admin and a child all see the same shelves — which is the only way the view can
    /// be checked before a child is handed it. Everything else is the house browse shape: the groups are
    /// <see cref="BrowseGroupItem"/>s, paged BY GROUP, so the kids shelf and the main shelf are the same
    /// component with a different source.</para>
    ///
    /// <para><b>Books ride as one trailing group.</b> A book carries its own kid clearance rather than inheriting
    /// a series', and Calibre gives roughly one folder per book, so there are no book "shelves" to build — the
    /// standalone site listed kid books flat after the comic shelves and this keeps that, folded into the group
    /// shape as a single <c>books</c> group at the end of the ordered list.</para>
    ///
    /// <para>The response carries a <c>covers</c> map (item id → media URL) beside the groups rather than a URL
    /// per row: <see cref="ItemSummary"/> is the shared flat projection and must not grow a per-surface field.</para>
    /// </summary>
    [ApiController]
    [Route("kids")]
    public sealed class KidsController : ControllerBase
    {
        /// <summary>Issues shown on one series shelf. The standalone's number — it keeps a shelf one easy scan.</summary>
        public const int PerSeries = 40;

        /// <summary>Shelf cap. Past this it stops being a browse a child can hold in their head.</summary>
        public const int MaxSeries = 160;

        /// <summary>The trailing group's key — books have no series shelf of their own.</summary>
        public const string BooksGroupKey = "books";

        private readonly BooksDb db;
        private readonly BooksOptions options;

        public KidsController(BooksDb db, BooksOptions options)
        {
            this.db = db;
            this.options = options;
        }

        /// <summary>
        /// GET /kids/browse?groupBy=series&amp;groupsSkip=&amp;groupsTop=&amp;perGroupTop= — the kid-safe shelves.
        ///
        /// <para>Shelves are ordered by series rating (best first), then by how many issues the library holds,
        /// then by id — the id being the tiebreaker that makes a <c>groupsSkip</c> page boundary reproducible.</para>
        /// </summary>
        [HttpGet("browse")]
        public async Task<IActionResult> Browse(
            [FromQuery] string groupBy = "series",
            [FromQuery] int groupsSkip = 0,
            [FromQuery] int groupsTop = 20,
            [FromQuery] int perGroupTop = PerSeries,
            [FromQuery] string? mediaToken = null,
            CancellationToken ct = default)
        {
            if (!string.Equals(groupBy, "series", StringComparison.OrdinalIgnoreCase))
                return BadRequest("the kids browse groups by series");

            groupsSkip = Math.Max(0, groupsSkip);
            groupsTop = Math.Clamp(groupsTop, 1, MaxSeries);
            perGroupTop = Math.Clamp(perGroupTop, 1, PerSeries);

            var heads = await ShelvesAsync(ct);
            var paged = heads.Skip(groupsSkip).Take(groupsTop).ToList();

            var chosen = paged.ToDictionary(
                h => h.Key,
                h => h.ItemIds.Take(perGroupTop).ToList(),
                StringComparer.Ordinal);

            var ids = chosen.Values.SelectMany(x => x).Distinct().ToList();
            var byId = ids.Count == 0
                ? new Dictionary<int, ItemSummary>()
                : (await db.Items.AsNoTracking().Where(i => ids.Contains(i.Id)).Select(ItemSummary.Project).ToListAsync(ct))
                    .ToDictionary(s => s.Id);

            var media = MediaUrls.For(options, User, mediaToken);
            var groups = paged.Select(h => new BrowseGroupItem(
                h.Key, h.Label, h.Total,
                chosen[h.Key].Where(byId.ContainsKey).Select(id => byId[id]).ToList())).ToList();

            return Ok(new
            {
                totalGroups = heads.Count,
                groups,
                covers = Covers(byId.Keys, media),
            });
        }

        /// <summary>
        /// GET /kids/series/{id}/items?skip=&amp;top= — one shelf's issues, paged. 404 when the series is not
        /// kid-clear: absent and forbidden must look the same from outside, here as everywhere.
        /// </summary>
        [HttpGet("series/{seriesId:int}/items")]
        public async Task<IActionResult> SeriesItems(int seriesId, [FromQuery] int skip = 0,
            [FromQuery] int top = PerSeries, [FromQuery] string? mediaToken = null, CancellationToken ct = default)
        {
            skip = Math.Max(0, skip);
            top = Math.Clamp(top, 1, PerSeries);

            var kidSeries = await KidsPolicy.KidSeriesAsync(db, ItemKind.Comic, ct);
            if (!kidSeries.TryGetValue(seriesId, out var series)) return NotFound();

            var query = KidsPolicy.KidItems(db, ItemKind.Comic, new[] { seriesId });
            var total = await query.CountAsync(ct);
            var items = await query.OrderBy(i => i.Id).Skip(skip).Take(top).Select(ItemSummary.Project).ToListAsync(ct);

            var media = MediaUrls.For(options, User, mediaToken);
            return Ok(new
            {
                series = new { id = series.Id, name = series.Name, rating = series.Rating },
                total,
                skip,
                top,
                items,
                covers = Covers(items.Select(i => i.Id), media),
            });
        }

        // ── shelves ──────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>One shelf before its items are projected: the head plus the ids that belong to it.</summary>
        private sealed record Shelf(string Key, string Label, int Total, List<int> ItemIds);

        /// <summary>
        /// The whole ordered shelf list. Bounded by construction — it reads only kid-clear content — and the
        /// caller pages it, so the response never carries more than one band of projections.
        /// </summary>
        private async Task<List<Shelf>> ShelvesAsync(CancellationToken ct)
        {
            var kidSeries = await KidsPolicy.KidSeriesAsync(db, ItemKind.Comic, ct);
            var shelves = new List<Shelf>();

            if (kidSeries.Count > 0)
            {
                var rows = await KidsPolicy.KidItems(db, ItemKind.Comic, kidSeries.Keys.ToList())
                    .Select(i => new { i.Id, SeriesId = i.SeriesId!.Value })
                    .ToListAsync(ct);

                shelves = rows.GroupBy(r => r.SeriesId)
                    .Where(g => kidSeries.ContainsKey(g.Key))
                    .Select(g => new
                    {
                        Series = kidSeries[g.Key],
                        Ids = g.Select(x => x.Id).OrderBy(id => id).Take(PerSeries).ToList(),
                        Total = g.Count(),
                    })
                    .OrderByDescending(x => x.Series.Rating ?? 0)
                    .ThenByDescending(x => x.Total)
                    .ThenBy(x => x.Series.Id)
                    .Take(MaxSeries)
                    .Select(x => new Shelf(x.Series.Id.ToString(CultureInfo.InvariantCulture), x.Series.Name, x.Total, x.Ids))
                    .ToList();
            }

            var bookIds = await KidsPolicy.KidBookIdsAsync(db, ct);
            if (bookIds.Count > 0)
                shelves.Add(new Shelf(BooksGroupKey, "Books", bookIds.Count, bookIds.Take(PerSeries).ToList()));

            return shelves;
        }

        private static Dictionary<string, string?> Covers(IEnumerable<int> ids, MediaUrls media) =>
            ids.Distinct().ToDictionary(id => id.ToString(CultureInfo.InvariantCulture), id => media.Thumb(id));
    }
}
