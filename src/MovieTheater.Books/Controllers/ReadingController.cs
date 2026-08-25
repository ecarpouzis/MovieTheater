using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using MovieTheater.Books.Db;
using MovieTheater.Books.Identity;
using MovieTheater.Books.Services;

namespace MovieTheater.Books.Controllers
{
    /// <summary>Where the user is in one book. The same shape whether or not a row exists.</summary>
    public record ReadingPositionResult(int ItemId, int LastPage, int? LastSpineItemIndex, double? LastScrollPercent,
        string Status, bool WantToRead, bool Favorite, bool HiddenFromHistory, DateTime? UpdatedAt);

    /// <summary>
    /// A position write. All three fields optional: a body with none of them is a TOUCH — it stamps
    /// <c>UpdatedAt</c> and re-surfaces an EXISTING row on Last opened, and does nothing at all when there is no
    /// row yet (opening a book is not activity until the reader says where it got to).
    /// </summary>
    public record UpsertPositionRequest(int? LastPage, int? LastSpineItemIndex, double? LastScrollPercent);

    /// <summary>
    /// THE reading-position API. One surface for both readers and every shelf that shows progress, because the
    /// rules that decide when a book is Finished must exist in exactly one place (the standalone site's
    /// 2026-08-16 unification, ported).
    ///
    /// <para><b>The four laws.</b>
    /// (1) <c>lastPage: -1</c> is the ONLY Finished signal — it is the Read button, an explicit act. Reaching the
    /// last page never auto-finishes, or opening a one-page book would file it under "Read".
    /// (2) Any write clears <c>HiddenFromHistory</c>: reading something undoes a prior dismissal from Last opened.
    /// (3) GET returns a start-of-book default, never 404, for an item the caller may open — so the readers have
    /// ONE response shape. A 404 means the gate refused the item, nothing else.
    /// (4) DELETE resets the POSITION only. Want-to-read and Favorite are marks, not progress, and survive it.</para>
    ///
    /// <para>Every row is keyed by <see cref="BooksIdentity.UserId"/>. A request that reached here without one got
    /// past the host's fallback policy in a way it should not have, so it is refused rather than served
    /// anonymously.</para>
    /// </summary>
    [ApiController]
    [Route("positions")]
    public sealed class ReadingController : ControllerBase
    {
        private readonly BooksDb db;
        public ReadingController(BooksDb db) { this.db = db; }

        /// <summary>GET /positions/{itemId} — the row, or the start-of-book default. 404 only if the gate refuses.</summary>
        [HttpGet("{itemId:int}")]
        public async Task<IActionResult> Get(int itemId, CancellationToken ct = default)
        {
            if (BooksIdentity.UserId(User) is not int userId) return Forbid();
            if (await UserActivityQueries.AccessibleItemAsync(db, User, itemId, ct) == null) return NotFound();

            var row = await db.UserItemStates.AsNoTracking()
                .FirstOrDefaultAsync(s => s.UserId == userId && s.ItemId == itemId, ct);
            return Ok(row == null ? Unstarted(itemId) : Map(row));
        }

        /// <summary>
        /// PUT /positions/{itemId} — upsert the position.
        ///
        /// <para>Status is decided here and nowhere else: an EPUB write (a spine index) is progress; page −1 is
        /// Finished; page 0 with nothing else is Unread (the reader opened the cover and closed it — that is not
        /// progress, and calling it progress is what floods "Continue reading" with untouched books); any other
        /// page is InProgress.</para>
        /// </summary>
        [HttpPut("{itemId:int}")]
        public async Task<IActionResult> Upsert(int itemId, [FromBody] UpsertPositionRequest req, CancellationToken ct = default)
        {
            if (BooksIdentity.UserId(User) is not int userId) return Forbid();
            var item = await UserActivityQueries.AccessibleItemAsync(db, User, itemId, ct);
            if (item == null) return NotFound();

            req ??= new UpsertPositionRequest(null, null, null);
            var row = await db.UserItemStates.FirstOrDefaultAsync(s => s.UserId == userId && s.ItemId == itemId, ct);
            var touchOnly = req.LastPage == null && req.LastSpineItemIndex == null;
            if (row == null)
            {
                // A touch has nothing to re-surface and nothing to record, so it creates no row: opening a book
                // is not activity until the reader says where it got to. Only a real position write starts one.
                if (touchOnly) return Ok(Unstarted(itemId));
                row = new UserItemState { UserId = userId, ItemId = itemId };
                db.UserItemStates.Add(row);
            }

            row.UpdatedAt = DateTime.UtcNow;
            row.HiddenFromHistory = false;   // law 2

            if (req.LastSpineItemIndex.HasValue)
            {
                row.LastSpineItemIndex = req.LastSpineItemIndex;
                row.LastScrollPercent = req.LastScrollPercent;
                row.Status = ReadStatus.InProgress;
            }
            else if (req.LastPage.HasValue)
            {
                if (req.LastPage.Value == -1)
                {
                    // Law 1. The stored page is the last page of the book when we know the count, so a reader that
                    // reopens a finished book lands at the end instead of at a sentinel it would have to special-case.
                    row.LastPage = item.PageCount is int pages && pages > 0 ? pages - 1 : -1;
                    row.Status = ReadStatus.Finished;
                }
                else
                {
                    row.LastPage = req.LastPage.Value;
                    var opened = req.LastPage.Value == 0 && req.LastScrollPercent is not > 0;
                    row.Status = opened ? ReadStatus.Unread : ReadStatus.InProgress;
                    if (req.LastScrollPercent.HasValue) row.LastScrollPercent = req.LastScrollPercent;
                }
            }

            await db.SaveChangesAsync(ct);
            return Ok(Map(row));
        }

        /// <summary>
        /// GET /positions/history — the user's activity, newest first, joined to the browse projection.
        /// <c>status</c> selects the shelf: opened (default) / inprogress / finished / unread / all. Hidden rows
        /// never appear in any of them.
        /// </summary>
        [HttpGet("history")]
        public async Task<IActionResult> History(
            [FromQuery] string? status = null,
            [FromQuery] int skip = 0,
            [FromQuery] int top = 48,
            CancellationToken ct = default)
        {
            if (BooksIdentity.UserId(User) is not int userId) return Forbid();
            return Ok(await UserActivityQueries.HistoryAsync(db, User, userId, status, skip, top, ct));
        }

        /// <summary>
        /// POST /positions/{itemId}/hide — drop the item off Last opened WITHOUT unmarking it. Non-destructive:
        /// progress and Finished survive; the next write to the position brings it back (law 2).
        /// </summary>
        [HttpPost("{itemId:int}/hide")]
        public async Task<IActionResult> Hide(int itemId, CancellationToken ct = default)
        {
            if (BooksIdentity.UserId(User) is not int userId) return Forbid();
            var row = await db.UserItemStates.FirstOrDefaultAsync(s => s.UserId == userId && s.ItemId == itemId, ct);
            if (row == null) return NotFound();
            row.HiddenFromHistory = true;
            await db.SaveChangesAsync(ct);
            return NoContent();
        }

        /// <summary>
        /// DELETE /positions/{itemId} — reset the POSITION (law 4). The page fields go back to the start of the
        /// book and the status to Unread; <c>WantToRead</c> / <c>Favorite</c> are marks and are untouched. When
        /// nothing but the position was on the row it is removed outright.
        ///
        /// <para><c>HiddenFromHistory</c> is deliberately NOT cleared here: law 2 is about reading activity, and a
        /// reset is the opposite of that. An Unread row is off every history shelf anyway.</para>
        /// </summary>
        [HttpDelete("{itemId:int}")]
        public async Task<IActionResult> Reset(int itemId, CancellationToken ct = default)
        {
            if (BooksIdentity.UserId(User) is not int userId) return Forbid();
            if (await UserActivityQueries.AccessibleItemAsync(db, User, itemId, ct) == null) return NotFound();

            var row = await db.UserItemStates.FirstOrDefaultAsync(s => s.UserId == userId && s.ItemId == itemId, ct);
            if (row == null) return NoContent();

            if (row.WantToRead || row.Favorite)
            {
                row.LastPage = 0;
                row.LastSpineItemIndex = null;
                row.LastScrollPercent = null;
                row.Status = ReadStatus.Unread;
                row.UpdatedAt = DateTime.UtcNow;
            }
            else db.UserItemStates.Remove(row);

            await db.SaveChangesAsync(ct);
            return NoContent();
        }

        // ── mapping ───────────────────────────────────────────────────────────────────────────────────────────

        internal static ReadingPositionResult Map(UserItemState s) => new(
            s.ItemId, s.LastPage, s.LastSpineItemIndex, s.LastScrollPercent,
            UserActivityQueries.StatusName(s.Status), s.WantToRead, s.Favorite, s.HiddenFromHistory, s.UpdatedAt);

        /// <summary>Law 3: a book with no row answers as "start of book, unread, never updated".</summary>
        internal static ReadingPositionResult Unstarted(int itemId) =>
            new(itemId, 0, null, null, UserActivityQueries.StatusName(ReadStatus.Unread), false, false, false, null);
    }
}
