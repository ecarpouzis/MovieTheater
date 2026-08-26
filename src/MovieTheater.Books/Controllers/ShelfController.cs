using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using MovieTheater.Books.Db;
using MovieTheater.Books.Identity;
using MovieTheater.Books.Services;

namespace MovieTheater.Books.Controllers
{
    /// <summary>
    /// One series card on the shelf: what it is, how far through it the user is, and which cover represents it.
    /// <c>IssueCount</c> is what the LIBRARY holds and can be gated away; <c>SeriesIssueCount</c> is the series'
    /// own published total — a card that says "3 / 12 read" must not divide by the wrong one.
    /// </summary>
    public record ShelfSeriesCard(int SeriesId, string SeriesName, int IssueCount, int FinishedCount,
        int? SeriesIssueCount, int? CoverItemId, string? Publisher, int? Year, int? YearEnd, bool IsOngoing,
        bool IsRead, bool WantToRead, bool IsFavorite, int? Rating);

    /// <summary>
    /// The Bookshelf's backend: the user's own library, at the level a person thinks about it.
    ///
    /// <para><b>Series are cards, issues are not.</b> A shelved run of 60 issues is ONE card with a progress
    /// figure, never 60 tiles — that is the whole point of the shelf. Single-issue series are excluded on purpose:
    /// the catalog collapses those into one issue+series entity, so they belong on the item shelves
    /// (<c>/marks/items</c>) and would otherwise appear twice.</para>
    ///
    /// <para><b>Progress is computed, never stored.</b> <c>finishedCount / issueCount</c> comes from the user's
    /// <c>UserItemState</c> rows joined to <c>Item.SeriesId</c> at read time. There is no counter to drift, and
    /// marking one issue read anywhere in the site moves every shelf that shows the series.</para>
    /// </summary>
    [ApiController]
    [Route("shelf")]
    public sealed class ShelfController : ControllerBase
    {
        /// <summary>How many series cards one page carries. The shelf is a page like everything else.</summary>
        public const int MaxSeriesTop = 200;

        private readonly BooksDb db;
        public ShelfController(BooksDb db) { this.db = db; }

        /// <summary>
        /// GET /shelf/series?kind=read|want — the series the user shelved, with progress. Ordered by name so the
        /// shelf reads like a shelf; the id breaks ties so paging is stable.
        /// </summary>
        [HttpGet("series")]
        public async Task<IActionResult> GetSeries(
            [FromQuery] string kind = "read",
            [FromQuery] int skip = 0,
            [FromQuery] int top = 100,
            CancellationToken ct = default)
        {
            if (BooksIdentity.UserId(User) is not int userId) return Forbid();
            var want = MarksController.ParseMarkKind(kind) switch
            {
                MarkKind.WantToRead => true,
                MarkKind.Read => false,
                _ => (bool?)null,
            };
            if (want == null) return BadRequest("kind must be read or want");

            skip = Math.Max(0, skip);
            top = Math.Clamp(top, 1, MaxSeriesTop);

            var marks = await db.GroupMarks.AsNoTracking()
                .Where(m => m.UserId == userId && m.GroupType == GroupType.Series)
                .Where(m => want.Value ? m.WantToRead : m.IsRead)
                .ToListAsync(ct);
            var markByKey = marks.GroupBy(m => m.GroupKey).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

            var ids = UserActivityQueries.ParseSeriesKeys(markByKey.Keys);
            if (ids.Count == 0) return Ok(Page(0, skip, top, []));

            // Single-issue series track as items, not shelves (they are collapsed entities in the catalog).
            var series = await db.Series.AsNoTracking().Where(s => ids.Contains(s.Id) && s.IssueCount > 1)
                .Select(s => new { s.Id, s.Name, s.DisplayNameOverride, s.IssueCount, s.YearStart, s.YearEnd, s.IsOngoing, s.PublisherId })
                .ToListAsync(ct);
            if (series.Count == 0) return Ok(Page(0, skip, top, []));

            var ordered = series
                .OrderBy(s => s.DisplayNameOverride ?? s.Name ?? "", StringComparer.OrdinalIgnoreCase).ThenBy(s => s.Id)
                .ToList();
            var total = ordered.Count;
            var page = ordered.Skip(skip).Take(top).ToList();

            var progress = await UserActivityQueries.SeriesProgress(
                db, userId, page.Select(s => s.Id).ToList(), BooksIdentity.CeilingFor(User), ct);
            var publishers = await PublisherNamesAsync(page.Select(s => s.PublisherId).ToList(), ct);

            var cards = page.Select(s =>
            {
                var p = progress.GetValueOrDefault(s.Id);
                var mark = markByKey.GetValueOrDefault(s.Id.ToString());
                return new ShelfSeriesCard(
                    s.Id, s.DisplayNameOverride ?? s.Name ?? "",
                    p?.IssueCount ?? 0, p?.FinishedCount ?? 0, s.IssueCount, p?.CoverItemId,
                    s.PublisherId == null ? null : publishers.GetValueOrDefault(s.PublisherId.Value),
                    s.YearStart, s.YearEnd, s.IsOngoing,
                    mark?.IsRead ?? false, mark?.WantToRead ?? false, mark?.IsFavorite ?? false, mark?.Rating);
            }).ToList();

            return Ok(Page(total, skip, top, cards));
        }

        /// <summary>
        /// GET /shelf/series/{seriesId}/progress — the user's per-issue state inside ONE series: which visible
        /// issues are finished and which are under way. The shelf drawer's done-ticks read this instead of paging
        /// the whole read list; the counts are the same arithmetic <see cref="GetSeries"/> uses for its cards.
        /// </summary>
        [HttpGet("series/{seriesId:int}/progress")]
        public async Task<IActionResult> SeriesProgress(int seriesId, CancellationToken ct = default)
        {
            if (BooksIdentity.UserId(User) is not int userId) return Forbid();

            var items = UserActivityQueries.AccessibleItems(db, User).Where(i => i.SeriesId == seriesId);
            var total = await items.CountAsync(ct);
            var states = await (from s in db.UserItemStates.AsNoTracking().Where(s => s.UserId == userId)
                                join i in items on s.ItemId equals i.Id
                                select new { s.ItemId, s.Status }).ToListAsync(ct);

            var finishedIds = states.Where(s => s.Status == ReadStatus.Finished).Select(s => s.ItemId).OrderBy(id => id).ToList();
            var inProgressIds = states.Where(s => s.Status == ReadStatus.InProgress).Select(s => s.ItemId).OrderBy(id => id).ToList();
            return Ok(new { seriesId, total, finishedCount = finishedIds.Count, finishedIds, inProgressIds });
        }

        /// <summary>GET /shelf/continue — what the user is part-way through, most recent first.</summary>
        [HttpGet("continue")]
        public async Task<IActionResult> Continue([FromQuery] int skip = 0, [FromQuery] int top = 24, CancellationToken ct = default)
        {
            if (BooksIdentity.UserId(User) is not int userId) return Forbid();
            return Ok(await UserActivityQueries.HistoryAsync(db, User, userId, "inprogress", skip, top, ct));
        }

        /// <summary>
        /// GET /shelf/last-opened — everything the user actually opened (in progress OR finished), most recent
        /// first, minus what they dismissed with the shelf's ✕ (<c>POST /positions/{id}/hide</c>).
        /// </summary>
        [HttpGet("last-opened")]
        public async Task<IActionResult> LastOpened([FromQuery] int skip = 0, [FromQuery] int top = 24, CancellationToken ct = default)
        {
            if (BooksIdentity.UserId(User) is not int userId) return Forbid();
            return Ok(await UserActivityQueries.HistoryAsync(db, User, userId, "opened", skip, top, ct));
        }

        private async Task<Dictionary<int, string>> PublisherNamesAsync(List<int?> publisherIds, CancellationToken ct)
        {
            var ids = publisherIds.Where(id => id != null).Select(id => id!.Value).Distinct().ToList();
            if (ids.Count == 0) return new Dictionary<int, string>();
            var rows = await db.Publishers.AsNoTracking().Where(p => ids.Contains(p.Id))
                .Select(p => new { p.Id, p.Name }).ToListAsync(ct);
            return rows.ToDictionary(p => p.Id, p => p.Name ?? "");
        }

        private static object Page(int total, int skip, int top, List<ShelfSeriesCard> series) =>
            new { totalCount = total, skip, top, series };
    }
}
