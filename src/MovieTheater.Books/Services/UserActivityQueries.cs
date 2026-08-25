using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using MovieTheater.Books.Access;
using MovieTheater.Books.Db;
using MovieTheater.Books.Identity;
using MovieTheater.Books.Projections;

namespace MovieTheater.Books.Services
{
    /// <summary>Which per-user mark a caller is asking about. The vocabulary the browse filters use.</summary>
    public enum MarkKind
    {
        /// <summary><c>UserItemState.WantToRead</c>.</summary>
        WantToRead,
        /// <summary><c>UserItemState.Favorite</c>.</summary>
        Favorite,
        /// <summary><c>UserItemState.Status == Finished</c> — "read" is a STATUS, not a flag.</summary>
        Read,
    }

    /// <summary>One shelved series' progress: how many issues the library holds and how many the user finished.</summary>
    public sealed record SeriesProgressRow(int SeriesId, int IssueCount, int FinishedCount, int? CoverItemId);

    /// <summary>One row of the reading history: the position plus the item as every list surface sees it.</summary>
    public sealed record HistoryEntry(int ItemId, int LastPage, int? LastSpineItemIndex, double? LastScrollPercent,
        string Status, bool WantToRead, bool Favorite, DateTime? UpdatedAt, ItemSummary? Item);

    /// <summary>A page of history — chunked and observable: the caller drives it with <c>skip</c> until it has them all.</summary>
    public sealed record HistoryPage(int TotalCount, int Skip, int Top, List<HistoryEntry> Entries);

    /// <summary>
    /// The user-activity reads that more than one surface needs — and, deliberately, the ONLY place the browse
    /// layer has to know about <c>UserItemState</c> / <c>GroupMark</c>.
    ///
    /// <para><b>Why static helpers and not a service.</b> Every one of these is a query over the request's own
    /// <see cref="BooksDb"/>; none holds state, none needs configuration. <c>BrowseController</c>'s
    /// <c>wantToReadOnly</c> / <c>readOnly</c> parameters become one <see cref="MarkedItemIds"/> call composed
    /// into its existing item query — no DI, no second round trip.</para>
    ///
    /// <para><b>Two access shapes, deliberately different.</b> A LIST surface uses
    /// <see cref="AccessibleItems"/> (<c>ItemAccess.ExcludeHidden</c> + maturity), so a shadow duplicate never
    /// appears in a history, a shelf or a suggestion — not even in the user's own activity. A BY-ID surface uses
    /// <see cref="AccessibleItemAsync"/>, which keeps a shadow duplicate that the Directory drill still shows
    /// (<c>IsExcluded &amp;&amp; KeepInDirectory</c>) readable: the file is genuinely in that folder, the reader can
    /// open it, so it must be able to save a position for it. The standalone site's by-id gate was maturity-only,
    /// so this is the closer of the two readings. Both gate on maturity, always.</para>
    /// </summary>
    public static class UserActivityQueries
    {
        /// <summary>Hard ceiling on one page of any activity list — the caller pages, the server never dumps.</summary>
        public const int MaxTop = 200;

        // ── the browse layer's three entry points ─────────────────────────────────────────────────────────────

        /// <summary>
        /// The ids of the items a user has marked. Returned as an <see cref="IQueryable{T}"/> so a caller composes
        /// it into its own query (<c>.Where(i =&gt; marked.Contains(i.Id))</c>) instead of materializing 141k ids.
        /// Rides <c>(UserId, WantToRead)</c> for want and <c>(UserId, UpdatedAt DESC)</c> otherwise.
        /// </summary>
        public static IQueryable<int> MarkedItemIds(BooksDb db, int userId, MarkKind kind)
        {
            var rows = db.UserItemStates.AsNoTracking().Where(s => s.UserId == userId);
            rows = kind switch
            {
                MarkKind.WantToRead => rows.Where(s => s.WantToRead),
                MarkKind.Favorite => rows.Where(s => s.Favorite),
                _ => rows.Where(s => s.Status == ReadStatus.Finished),
            };
            return rows.Select(s => s.ItemId);
        }

        /// <summary>
        /// Per-series read progress for a shelf card or a group head: issues held, issues finished, and the
        /// representative cover (the first issue in reading order, falling back to the lowest id).
        ///
        /// <para>Bounded by design: it reads only the issues of the series it was handed, in two queries, and
        /// aggregates in memory. Hand it a page of series, never the whole shelf at once.</para>
        /// </summary>
        public static async Task<Dictionary<int, SeriesProgressRow>> SeriesProgress(
            BooksDb db, int userId, IReadOnlyCollection<int> seriesIds, int ceiling = 3, CancellationToken ct = default)
        {
            var result = new Dictionary<int, SeriesProgressRow>();
            if (seriesIds.Count == 0) return result;

            var ids = seriesIds.Distinct().ToList();
            var items = db.Items.AsNoTracking()
                .Where(i => i.SeriesId != null && ids.Contains(i.SeriesId.Value))
                .ExcludeHidden().ApplyMaturity(db, ceiling);

            var rows = await (from i in items
                              join r in db.ReadingOrderEntries.AsNoTracking() on i.Id equals r.ItemId into ro
                              from r in ro.DefaultIfEmpty()
                              select new { ItemId = i.Id, SeriesId = i.SeriesId!.Value, ReadIndex = r == null ? (int?)null : r.ReadIndex })
                .ToListAsync(ct);
            if (rows.Count == 0) return result;

            var finished = (await (from s in db.UserItemStates.AsNoTracking()
                                       .Where(s => s.UserId == userId && s.Status == ReadStatus.Finished)
                                   join i in items on s.ItemId equals i.Id
                                   select s.ItemId).ToListAsync(ct)).ToHashSet();

            foreach (var group in rows.GroupBy(r => r.SeriesId))
            {
                var cover = group.OrderBy(r => r.ReadIndex ?? int.MaxValue).ThenBy(r => r.ItemId).First().ItemId;
                result[group.Key] = new SeriesProgressRow(
                    group.Key, group.Count(), group.Count(r => finished.Contains(r.ItemId)), cover);
            }
            return result;
        }

        /// <summary>The series the user marked read at the SERIES level (<c>GroupMark(Series)</c>, keys are ids).</summary>
        public static async Task<List<int>> ReadSeriesIds(BooksDb db, int userId, CancellationToken ct = default) =>
            ParseSeriesKeys(await GroupKeys(db, userId, GroupType.Series, m => m.IsRead).ToListAsync(ct));

        /// <summary>The series the user marked want-to-read at the SERIES level. Rides <c>(UserId, GroupType, WantToRead)</c>.</summary>
        public static async Task<List<int>> WantedSeriesIds(BooksDb db, int userId, CancellationToken ct = default) =>
            ParseSeriesKeys(await GroupKeys(db, userId, GroupType.Series, m => m.WantToRead).ToListAsync(ct));

        private static IQueryable<string> GroupKeys(BooksDb db, int userId, GroupType type,
            System.Linq.Expressions.Expression<Func<GroupMark, bool>> flag) =>
            db.GroupMarks.AsNoTracking().Where(m => m.UserId == userId && m.GroupType == type).Where(flag)
                .Select(m => m.GroupKey);

        /// <summary>Series group keys are SeriesId strings in v2; anything else is a v1 artifact and is dropped.</summary>
        public static List<int> ParseSeriesKeys(IEnumerable<string> keys) =>
            keys.Select(k => int.TryParse(k, out var id) ? id : (int?)null)
                .Where(id => id != null).Select(id => id!.Value).Distinct().ToList();

        // ── access ────────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>The caller's visible item set: no shadow duplicates, gated by the ceiling. Every LIST surface.</summary>
        public static IQueryable<Item> AccessibleItems(BooksDb db, ClaimsPrincipal user) =>
            db.Items.AsNoTracking().ExcludeHidden().ApplyMaturity(db, BooksIdentity.CeilingFor(user));

        /// <summary>
        /// One item, if this caller may open it — <see cref="ItemAccess.GetAuthorizedItemAsync"/>, the site's ONE
        /// by-id authorization, with <c>allowExcluded</c> left on. That is deliberate: a shadow duplicate the
        /// Directory drill still lists is readable, so its reading position must be writable too. Null ⇒ the
        /// caller answers 404, never 403 (see that method's remarks for why).
        /// </summary>
        public static Task<Item?> AccessibleItemAsync(BooksDb db, ClaimsPrincipal user, int itemId, CancellationToken ct = default) =>
            ItemAccess.GetAuthorizedItemAsync(db, user, itemId, allowExcluded: true, ct);

        /// <summary>The browse projection for a set of ids, re-gated (defence in depth) and keyed by id.</summary>
        public static async Task<Dictionary<int, ItemSummary>> SummariesAsync(
            BooksDb db, ClaimsPrincipal user, IReadOnlyCollection<int> ids, CancellationToken ct = default)
        {
            if (ids.Count == 0) return new Dictionary<int, ItemSummary>();
            var list = ids.Distinct().ToList();
            var rows = await AccessibleItems(db, user).Where(i => list.Contains(i.Id))
                .Select(ItemSummary.Project).ToListAsync(ct);
            return rows.ToDictionary(r => r.Id);
        }

        // ── history (shared by /positions/history, /shelf/continue and /shelf/last-opened) ─────────────────────

        /// <summary>
        /// The history filters. "opened" is the Last-opened shelf — anything the user actually opened, which is
        /// <c>InProgress</c> OR <c>Finished</c>. Hidden rows are excluded from EVERY filter: hiding is what the
        /// shelf's ✕ writes, and a write to the position clears it again.
        /// </summary>
        public static IQueryable<UserItemState> ApplyStatusFilter(IQueryable<UserItemState> rows, string? status) =>
            (status ?? "").Trim().ToLowerInvariant() switch
            {
                "finished" or "read" => rows.Where(s => s.Status == ReadStatus.Finished),
                "unread" => rows.Where(s => s.Status == ReadStatus.Unread),
                "inprogress" or "in-progress" or "continue" => rows.Where(s => s.Status == ReadStatus.InProgress),
                "all" => rows,
                _ => rows.Where(s => s.Status == ReadStatus.InProgress || s.Status == ReadStatus.Finished),
            };

        /// <summary>
        /// One page of the user's activity, newest first, joined to the browse projection.
        ///
        /// <para>The join to the accessible items happens in SQL, BEFORE paging, so the page is full and the total
        /// is honest even when some of the user's own rows point at items the gate now hides. The sort carries the
        /// item id as a tiebreaker — <c>UpdatedAt</c> alone is not unique, and an unstable sort makes a paged list
        /// drop and repeat rows.</para>
        /// </summary>
        public static async Task<HistoryPage> HistoryAsync(BooksDb db, ClaimsPrincipal user, int userId,
            string? status, int skip, int top, CancellationToken ct = default)
        {
            skip = Math.Max(0, skip);
            top = Math.Clamp(top, 1, MaxTop);

            var accessible = AccessibleItems(db, user);
            var rows = ApplyStatusFilter(
                db.UserItemStates.AsNoTracking().Where(s => s.UserId == userId && !s.HiddenFromHistory), status);
            var joined = from s in rows
                         join i in accessible on s.ItemId equals i.Id
                         select s;

            var total = await joined.CountAsync(ct);
            var page = await joined.OrderByDescending(s => s.UpdatedAt).ThenByDescending(s => s.ItemId)
                .Skip(skip).Take(top).ToListAsync(ct);

            var summaries = await SummariesAsync(db, user, page.Select(p => p.ItemId).ToList(), ct);
            var entries = page.Select(s => new HistoryEntry(
                s.ItemId, s.LastPage, s.LastSpineItemIndex, s.LastScrollPercent, StatusName(s.Status),
                s.WantToRead, s.Favorite, s.UpdatedAt, summaries.GetValueOrDefault(s.ItemId))).ToList();

            return new HistoryPage(total, skip, top, entries);
        }

        /// <summary>The wire spelling of a status: lowercase, as the standalone site's clients already read it.</summary>
        public static string StatusName(ReadStatus status) => status switch
        {
            ReadStatus.Finished => "finished",
            ReadStatus.InProgress => "inprogress",
            _ => "unread",
        };
    }
}
