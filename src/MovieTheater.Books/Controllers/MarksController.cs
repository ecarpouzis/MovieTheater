using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using MovieTheater.Books.Db;
using MovieTheater.Books.Identity;
using MovieTheater.Books.Projections;
using MovieTheater.Books.Services;

namespace MovieTheater.Books.Controllers
{
    /// <summary>One item's marks, as a list surface returns them (the projection carries the card).</summary>
    public record ItemMarkResult(int ItemId, bool WantToRead, bool Favorite, string Status, int? Rating,
        DateTime? UpdatedAt, ItemSummary? Item);

    /// <summary>A page of item marks. Chunked and observable — the caller pages with <c>skip</c>.</summary>
    public record ItemMarksPage(int TotalCount, int Skip, int Top, List<ItemMarkResult> Entries);

    /// <summary>
    /// An item-mark write. Every field is TRI-STATE, which is why <see cref="Rating"/> is a raw
    /// <see cref="JsonElement"/> and not an <c>int?</c>: absent means "leave it alone", <c>null</c> means "remove
    /// it", a number means "set it". An <c>int?</c> cannot tell the first two apart, and the difference is the
    /// whole delete path for a user rating.
    /// </summary>
    public sealed class UpsertItemMarkRequest
    {
        public bool? WantToRead { get; set; }
        public bool? Favorite { get; set; }
        public JsonElement Rating { get; set; }
    }

    /// <summary>A group-mark write. Same tri-state rule for <see cref="Rating"/> and <see cref="Notes"/>.</summary>
    public sealed class UpsertGroupMarkRequest
    {
        public bool? IsRead { get; set; }
        public bool? WantToRead { get; set; }
        public bool? IsFavorite { get; set; }
        public JsonElement Rating { get; set; }
        public JsonElement Notes { get; set; }
    }

    /// <summary>One group's marks plus what the group IS — the head decoration a banded layout draws.</summary>
    public record GroupMarkResult(string GroupType, string GroupKey, string? Label, bool IsRead, bool WantToRead,
        bool IsFavorite, int? Rating, string? Notes, DateTime? UpdatedAt);

    /// <summary>The batch endpoint's request: many (type, key) pairs, one round trip.</summary>
    public record GroupKeyRef(string GroupType, string GroupKey);
    public record GroupMarkBatchRequest(List<GroupKeyRef> Items);

    /// <summary>
    /// The user's marks — the deliberate, non-positional signals: want-to-read, favourite, a personal rating, and
    /// the same three on a GROUP (a series, a volume, a collection, a publisher, a decade).
    ///
    /// <para><b>Two tables, one rule each.</b> Item marks are flags on the ONE <c>UserItemState</c> row per
    /// user × item that also carries the reading position — v1's three parallel stores (Bookmarks, ComicUserLists,
    /// comic-typed GroupUserMetadata) are gone, and with them the "my mark didn't show up" class of bug. Group
    /// marks are <c>GroupMark(UserId, GroupType, GroupKey)</c>; SERIES keys are <c>SeriesId</c> strings and are
    /// validated against the series table, because a name-keyed mark is a mark that silently detaches the next
    /// time the series resolver runs.</para>
    ///
    /// <para><b>A personal rating is a <c>Rating(Source=User)</c> row</b>, not a column: v2 keeps every rating with
    /// its provenance in one table, and the resolver blends them into <c>Item.ResolvedRating</c>. Group ratings
    /// stay on the mark row — a group is not a rating target.</para>
    ///
    /// <para><b>What a mark write does NOT do</b>: it does not clear <c>HiddenFromHistory</c>. That law belongs to
    /// the reading position (<see cref="ReadingController"/>) — wanting to read something is not reading it, and
    /// a want-to-read toggle must not drag a dismissed book back onto Last opened.</para>
    /// </summary>
    [ApiController]
    [Route("marks")]
    public sealed class MarksController : ControllerBase
    {
        /// <summary>How many issues one "mark the series read" call finishes. Past this the caller re-PUTs.</summary>
        public const int SeriesFanOutBatch = 500;

        /// <summary>How many pairs one batch request may ask about.</summary>
        public const int MaxBatchKeys = 500;

        private readonly BooksDb db;
        public MarksController(BooksDb db) { this.db = db; }

        // ── item marks ────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// GET /marks/items?kind=want|favorite|read — the marked items, newest first, joined to the browse
        /// projection so a card renders straight from the response. Gated: a hidden or above-ceiling item never
        /// appears, not even in the user's own list.
        /// </summary>
        [HttpGet("items")]
        public async Task<IActionResult> GetItems(
            [FromQuery] string kind = "want",
            [FromQuery] int skip = 0,
            [FromQuery] int top = 48,
            CancellationToken ct = default)
        {
            if (BooksIdentity.UserId(User) is not int userId) return Forbid();
            if (ParseMarkKind(kind) is not MarkKind markKind) return BadRequest("kind must be want, favorite or read");

            skip = Math.Max(0, skip);
            top = Math.Clamp(top, 1, UserActivityQueries.MaxTop);

            var rows = db.UserItemStates.AsNoTracking().Where(s => s.UserId == userId);
            rows = markKind switch
            {
                MarkKind.WantToRead => rows.Where(s => s.WantToRead),
                MarkKind.Favorite => rows.Where(s => s.Favorite),
                _ => rows.Where(s => s.Status == ReadStatus.Finished),
            };

            var accessible = UserActivityQueries.AccessibleItems(db, User);
            var joined = from s in rows join i in accessible on s.ItemId equals i.Id select s;

            var total = await joined.CountAsync(ct);
            var page = await joined.OrderByDescending(s => s.UpdatedAt).ThenByDescending(s => s.ItemId)
                .Skip(skip).Take(top).ToListAsync(ct);

            var ids = page.Select(p => p.ItemId).ToList();
            var summaries = await UserActivityQueries.SummariesAsync(db, User, ids, ct);
            var ratings = await UserRatingsAsync(userId, ids, ct);

            var entries = page.Select(s => new ItemMarkResult(
                s.ItemId, s.WantToRead, s.Favorite, UserActivityQueries.StatusName(s.Status),
                Lookup(ratings, s.ItemId), s.UpdatedAt, summaries.GetValueOrDefault(s.ItemId))).ToList();

            return Ok(new ItemMarksPage(total, skip, top, entries));
        }

        /// <summary>
        /// PUT /marks/items/{itemId} — set any of want-to-read, favourite and the personal rating. Omitted fields
        /// keep their value; a <c>null</c> rating REMOVES the <c>Rating(Source=User)</c> row.
        /// </summary>
        /// <summary>
        /// GET /marks/items/{itemId} — one item's marks for the caller (want / favourite / status / rating). The
        /// modal reads this instead of paging the lists; absent rows read as the defaults, never as 404 — only an
        /// item the caller may not see is 404.
        /// </summary>
        [HttpGet("items/{itemId:int}")]
        public async Task<IActionResult> GetItem(int itemId, CancellationToken ct = default)
        {
            if (BooksIdentity.UserId(User) is not int userId) return Forbid();
            if (await UserActivityQueries.AccessibleItemAsync(db, User, itemId, ct) == null) return NotFound();

            var row = await db.UserItemStates.AsNoTracking().FirstOrDefaultAsync(s => s.UserId == userId && s.ItemId == itemId, ct);
            var rating = await UserRatingAsync(userId, itemId, ct);
            return Ok(new ItemMarkResult(itemId, row?.WantToRead ?? false, row?.Favorite ?? false,
                UserActivityQueries.StatusName(row?.Status ?? ReadStatus.Unread), rating, row?.UpdatedAt, null));
        }

        [HttpPut("items/{itemId:int}")]
        public async Task<IActionResult> UpsertItem(int itemId, [FromBody] UpsertItemMarkRequest req, CancellationToken ct = default)
        {
            if (BooksIdentity.UserId(User) is not int userId) return Forbid();
            if (await UserActivityQueries.AccessibleItemAsync(db, User, itemId, ct) == null) return NotFound();
            if (!TryReadInt(req.Rating, out var ratingAction, out var rating)) return BadRequest("rating must be a number 0-100 or null");

            var row = await db.UserItemStates.FirstOrDefaultAsync(s => s.UserId == userId && s.ItemId == itemId, ct);
            if (row == null)
            {
                row = new UserItemState { UserId = userId, ItemId = itemId };
                db.UserItemStates.Add(row);
            }
            if (req.WantToRead.HasValue) row.WantToRead = req.WantToRead.Value;
            if (req.Favorite.HasValue) row.Favorite = req.Favorite.Value;
            row.UpdatedAt = DateTime.UtcNow;

            if (ratingAction != FieldAction.Untouched)
                await WriteUserRatingAsync(userId, itemId, ratingAction == FieldAction.Set ? rating : null, ct);

            await db.SaveChangesAsync(ct);

            var current = await UserRatingAsync(userId, itemId, ct);
            return Ok(new ItemMarkResult(itemId, row.WantToRead, row.Favorite,
                UserActivityQueries.StatusName(row.Status), current, row.UpdatedAt, null));
        }

        /// <summary>
        /// DELETE /marks/items/{itemId}/{kind} — clear ONE mark (want | favorite | rating). Clearing "read" is a
        /// position reset, so it lives on <c>DELETE /positions/{itemId}</c> and is refused here rather than
        /// duplicated. A row left holding nothing at all is removed.
        /// </summary>
        [HttpDelete("items/{itemId:int}/{kind}")]
        public async Task<IActionResult> DeleteItemMark(int itemId, string kind, CancellationToken ct = default)
        {
            if (BooksIdentity.UserId(User) is not int userId) return Forbid();
            var which = (kind ?? "").Trim().ToLowerInvariant();
            if (which is not ("want" or "wanttoread" or "want-to-read" or "favorite" or "favourite" or "rating"))
                return BadRequest("kind must be want, favorite or rating (clearing 'read' is DELETE /positions/{itemId})");
            if (await UserActivityQueries.AccessibleItemAsync(db, User, itemId, ct) == null) return NotFound();

            if (which == "rating") await WriteUserRatingAsync(userId, itemId, null, ct);

            var row = await db.UserItemStates.FirstOrDefaultAsync(s => s.UserId == userId && s.ItemId == itemId, ct);
            if (row != null)
            {
                if (which is "want" or "wanttoread" or "want-to-read") row.WantToRead = false;
                if (which is "favorite" or "favourite") row.Favorite = false;
                row.UpdatedAt = DateTime.UtcNow;

                var empty = !row.WantToRead && !row.Favorite && row.Status == ReadStatus.Unread
                            && row.LastPage <= 0 && row.LastSpineItemIndex == null;
                if (empty) db.UserItemStates.Remove(row);
            }

            await db.SaveChangesAsync(ct);
            return NoContent();
        }

        // ── group marks ───────────────────────────────────────────────────────────────────────────────────────

        /// <summary>GET /marks/groups?groupType=series|volume|collection|publisher|decade — the user's marks of one type.</summary>
        [HttpGet("groups")]
        public async Task<IActionResult> GetGroups([FromQuery] string groupType = "series", CancellationToken ct = default)
        {
            if (BooksIdentity.UserId(User) is not int userId) return Forbid();
            if (ParseGroupType(groupType) is not GroupType type) return BadRequest(GroupTypeError);

            var rows = await db.GroupMarks.AsNoTracking()
                .Where(m => m.UserId == userId && m.GroupType == type)
                .OrderByDescending(m => m.UpdatedAt).ThenBy(m => m.GroupKey)
                .ToListAsync(ct);

            var labels = await SeriesLabelsAsync(type, rows.Select(r => r.GroupKey), ct);
            return Ok(rows.Select(m => Map(m, labels.GetValueOrDefault(m.GroupKey))).ToList());
        }

        /// <summary>
        /// PUT /marks/groups/{groupType}/{key} — upsert one group mark. Omitted fields keep their value; a
        /// <c>null</c> (or empty) rating/notes clears it.
        ///
        /// <para><b>Marking a SERIES read fans out to its issues</b> — that is what makes the shelf's "3/12 read"
        /// arithmetic mean anything, and it is the standalone behaviour. The fan-out is bounded and resumable: at
        /// most <see cref="SeriesFanOutBatch"/> issues per call, already-finished issues skipped, and the response
        /// reports <c>issuesMarked</c> / <c>issuesRemaining</c> so the caller re-PUTs until remaining is 0.
        /// Re-running is idempotent.</para>
        /// </summary>
        [HttpPut("groups/{groupType}/{*key}")]
        public async Task<IActionResult> UpsertGroup(string groupType, string key,
            [FromBody] UpsertGroupMarkRequest req, CancellationToken ct = default)
        {
            if (BooksIdentity.UserId(User) is not int userId) return Forbid();
            if (ParseGroupType(groupType) is not GroupType type) return BadRequest(GroupTypeError);
            key = (key ?? "").Trim();
            if (key.Length == 0) return BadRequest("a group key is required");
            if (!TryReadInt(req.Rating, out var ratingAction, out var rating)) return BadRequest("rating must be a number 0-100 or null");
            if (!TryReadString(req.Notes, out var notesAction, out var notes)) return BadRequest("notes must be a string or null");

            // Series marks key on SeriesId: a name-keyed mark detaches the next time the resolver runs.
            if (type == GroupType.Series)
            {
                if (!int.TryParse(key, out var seriesId)) return BadRequest("a series group key is a SeriesId");
                if (!await db.Series.AsNoTracking().AnyAsync(s => s.Id == seriesId, ct)) return NotFound();
            }

            var row = await db.GroupMarks.FirstOrDefaultAsync(
                m => m.UserId == userId && m.GroupType == type && m.GroupKey == key, ct);
            if (row == null)
            {
                row = new GroupMark { UserId = userId, GroupType = type, GroupKey = key };
                db.GroupMarks.Add(row);
            }
            if (req.IsRead.HasValue) row.IsRead = req.IsRead.Value;
            if (req.WantToRead.HasValue) row.WantToRead = req.WantToRead.Value;
            if (req.IsFavorite.HasValue) row.IsFavorite = req.IsFavorite.Value;
            if (ratingAction != FieldAction.Untouched) row.Rating = ratingAction == FieldAction.Set ? rating : null;
            if (notesAction != FieldAction.Untouched) row.Notes = notesAction == FieldAction.Set ? notes : null;
            row.UpdatedAt = DateTime.UtcNow;

            var (marked, remaining) = type == GroupType.Series && req.IsRead == true
                ? await FanOutSeriesReadAsync(userId, int.Parse(key), ct)
                : (0, 0);

            await db.SaveChangesAsync(ct);

            var labels = await SeriesLabelsAsync(type, new[] { key }, ct);
            return Ok(new
            {
                mark = Map(row, labels.GetValueOrDefault(key)),
                issuesMarked = marked,
                issuesRemaining = remaining,
            });
        }

        /// <summary>DELETE /marks/groups/{groupType}/{key} — remove the mark. The issues it fanned out to are untouched.</summary>
        [HttpDelete("groups/{groupType}/{*key}")]
        public async Task<IActionResult> DeleteGroup(string groupType, string key, CancellationToken ct = default)
        {
            if (BooksIdentity.UserId(User) is not int userId) return Forbid();
            if (ParseGroupType(groupType) is not GroupType type) return BadRequest(GroupTypeError);

            var row = await db.GroupMarks.FirstOrDefaultAsync(
                m => m.UserId == userId && m.GroupType == type && m.GroupKey == (key ?? "").Trim(), ct);
            if (row == null) return NotFound();
            db.GroupMarks.Remove(row);
            await db.SaveChangesAsync(ct);
            return NoContent();
        }

        /// <summary>
        /// POST /marks/groups/batch — many (type, key) pairs → their marks, keyed <c>"{groupType}::{groupKey}"</c>.
        /// This is what decorates a whole band of group heads in one round trip. The user's marks are few, so the
        /// rows are read once and matched in memory (a composite tuple IN does not translate).
        /// </summary>
        [HttpPost("groups/batch")]
        public async Task<IActionResult> Batch([FromBody] GroupMarkBatchRequest req, CancellationToken ct = default)
        {
            if (BooksIdentity.UserId(User) is not int userId) return Forbid();
            var asked = (req?.Items ?? []).Take(MaxBatchKeys).ToList();
            var result = new Dictionary<string, GroupMarkResult>(StringComparer.Ordinal);
            if (asked.Count == 0) return Ok(result);

            var wanted = new HashSet<(GroupType, string)>();
            foreach (var pair in asked)
                if (ParseGroupType(pair.GroupType) is GroupType t) wanted.Add((t, (pair.GroupKey ?? "").Trim()));
            if (wanted.Count == 0) return Ok(result);

            var types = wanted.Select(w => w.Item1).Distinct().ToList();
            var rows = await db.GroupMarks.AsNoTracking()
                .Where(m => m.UserId == userId && types.Contains(m.GroupType)).ToListAsync(ct);

            var hits = rows.Where(m => wanted.Contains((m.GroupType, m.GroupKey))).ToList();
            var labels = await SeriesLabelsAsync(GroupType.Series,
                hits.Where(h => h.GroupType == GroupType.Series).Select(h => h.GroupKey), ct);

            foreach (var m in hits)
                result[$"{Name(m.GroupType)}::{m.GroupKey}"] = Map(m, labels.GetValueOrDefault(m.GroupKey));
            return Ok(result);
        }

        // ── the series fan-out ────────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Mark up to <see cref="SeriesFanOutBatch"/> not-yet-finished issues of a series Finished, in reading
        /// order. Returns what it did and what is left, so the caller can drive it to completion; running it again
        /// picks up exactly where it stopped because "already finished" is the skip condition.
        /// </summary>
        private async Task<(int Marked, int Remaining)> FanOutSeriesReadAsync(int userId, int seriesId, CancellationToken ct)
        {
            var items = db.Items.AsNoTracking().Where(i => i.SeriesId == seriesId);
            var finished = db.UserItemStates.AsNoTracking()
                .Where(s => s.UserId == userId && s.Status == ReadStatus.Finished).Select(s => s.ItemId);

            var pending = await (from i in items
                                 where !finished.Contains(i.Id)
                                 join r in db.ReadingOrderEntries.AsNoTracking() on i.Id equals r.ItemId into ro
                                 from r in ro.DefaultIfEmpty()
                                 orderby r == null ? int.MaxValue : (r.ReadIndex ?? int.MaxValue), i.Id
                                 select new { i.Id, i.PageCount })
                .Take(SeriesFanOutBatch + 1).ToListAsync(ct);

            var remaining = Math.Max(0, pending.Count - SeriesFanOutBatch);
            var batch = pending.Take(SeriesFanOutBatch).ToList();
            if (batch.Count == 0) return (0, 0);

            var ids = batch.Select(b => b.Id).ToList();
            var existing = await db.UserItemStates.Where(s => s.UserId == userId && ids.Contains(s.ItemId))
                .ToDictionaryAsync(s => s.ItemId, ct);

            var now = DateTime.UtcNow;
            foreach (var item in batch)
            {
                var lastPage = item.PageCount is int pages && pages > 0 ? pages - 1 : 0;
                if (existing.TryGetValue(item.Id, out var row))
                {
                    row.LastPage = lastPage;
                    row.Status = ReadStatus.Finished;
                    row.UpdatedAt = now;
                }
                else db.UserItemStates.Add(new UserItemState
                {
                    UserId = userId,
                    ItemId = item.Id,
                    LastPage = lastPage,
                    Status = ReadStatus.Finished,
                    UpdatedAt = now,
                });
            }
            return (batch.Count, remaining);
        }

        // ── user ratings (Rating rows, Source = User) ─────────────────────────────────────────────────────────

        private async Task WriteUserRatingAsync(int userId, int itemId, int? value, CancellationToken ct)
        {
            var row = await db.Ratings.FirstOrDefaultAsync(
                r => r.TargetKind == SubjectKind.Item && r.TargetId == itemId && r.Source == RatingSource.User, ct);
            if (value == null)
            {
                if (row != null) db.Ratings.Remove(row);
                return;
            }
            var clamped = Math.Clamp(value.Value, 0, 100);
            if (row == null)
            {
                row = new Rating { TargetKind = SubjectKind.Item, TargetId = itemId, Source = RatingSource.User };
                db.Ratings.Add(row);
            }
            row.Value = clamped;
            row.RawValue = clamped;
            row.RawScale = "0-100";
            row.IsOverride = false;
            row.ModelId = "user:" + userId;
            row.GeneratedAt = DateTime.UtcNow;
        }

        private async Task<int?> UserRatingAsync(int userId, int itemId, CancellationToken ct) =>
            Lookup(await UserRatingsAsync(userId, new[] { itemId }, ct), itemId);

        /// <summary>
        /// "No rating" is <c>null</c>, never 0 — 0 is a legitimate rating. <c>GetValueOrDefault</c> on an
        /// <c>int</c> dictionary cannot say that, so every read of the rating map goes through here.
        /// </summary>
        private static int? Lookup(Dictionary<int, int> ratings, int itemId) =>
            ratings.TryGetValue(itemId, out var value) ? value : null;

        /// <summary>
        /// The per-item user ratings for a page of items. <c>Rating</c> is keyed (TargetKind, TargetId, Source) —
        /// there is no UserId on it, because only the one site user writes <c>Source=User</c> rows; the ModelId
        /// carries whose they are and is what a future multi-user split would key on.
        /// </summary>
        private async Task<Dictionary<int, int>> UserRatingsAsync(int userId, IReadOnlyCollection<int> itemIds, CancellationToken ct)
        {
            if (itemIds.Count == 0) return new Dictionary<int, int>();
            var ids = itemIds.Distinct().ToList();
            var owner = "user:" + userId;
            var rows = await db.Ratings.AsNoTracking()
                .Where(r => r.TargetKind == SubjectKind.Item && r.Source == RatingSource.User
                            && r.ModelId == owner && r.Value != null && ids.Contains(r.TargetId))
                .Select(r => new { r.TargetId, Value = r.Value!.Value }).ToListAsync(ct);
            return rows.ToDictionary(r => r.TargetId, r => r.Value);
        }

        // ── small helpers ─────────────────────────────────────────────────────────────────────────────────────

        private const string GroupTypeError = "groupType must be series, volume, collection, publisher or decade";

        internal static MarkKind? ParseMarkKind(string? kind) => (kind ?? "").Trim().ToLowerInvariant() switch
        {
            "want" or "wanttoread" or "want-to-read" => MarkKind.WantToRead,
            "favorite" or "favourite" or "fav" => MarkKind.Favorite,
            "read" or "finished" => MarkKind.Read,
            _ => null,
        };

        internal static GroupType? ParseGroupType(string? groupType) => (groupType ?? "").Trim().ToLowerInvariant() switch
        {
            "series" => GroupType.Series,
            "volume" => GroupType.Volume,
            "collection" => GroupType.Collection,
            "publisher" => GroupType.Publisher,
            "decade" => GroupType.Decade,
            _ => null,
        };

        internal static string Name(GroupType type) => type.ToString().ToLowerInvariant();

        private async Task<Dictionary<string, string>> SeriesLabelsAsync(GroupType type, IEnumerable<string> keys, CancellationToken ct)
        {
            var empty = new Dictionary<string, string>(StringComparer.Ordinal);
            if (type != GroupType.Series) return empty;
            var ids = UserActivityQueries.ParseSeriesKeys(keys);
            if (ids.Count == 0) return empty;
            var rows = await db.Series.AsNoTracking().Where(s => ids.Contains(s.Id))
                .Select(s => new { s.Id, s.Name, s.DisplayNameOverride }).ToListAsync(ct);
            foreach (var s in rows) empty[s.Id.ToString()] = s.DisplayNameOverride ?? s.Name ?? "";
            return empty;
        }

        private static GroupMarkResult Map(GroupMark m, string? label) =>
            new(Name(m.GroupType), m.GroupKey, label, m.IsRead, m.WantToRead, m.IsFavorite, m.Rating, m.Notes, m.UpdatedAt);

        /// <summary>What a tri-state JSON field asked for.</summary>
        private enum FieldAction { Untouched, Clear, Set }

        private static bool TryReadInt(JsonElement element, out FieldAction action, out int value)
        {
            action = FieldAction.Untouched; value = 0;
            switch (element.ValueKind)
            {
                case JsonValueKind.Undefined: return true;
                case JsonValueKind.Null: action = FieldAction.Clear; return true;
                case JsonValueKind.Number when element.TryGetInt32(out var v):
                    action = FieldAction.Set; value = v; return true;
                default: return false;
            }
        }

        private static bool TryReadString(JsonElement element, out FieldAction action, out string? value)
        {
            action = FieldAction.Untouched; value = null;
            switch (element.ValueKind)
            {
                case JsonValueKind.Undefined: return true;
                case JsonValueKind.Null: action = FieldAction.Clear; return true;
                case JsonValueKind.String:
                    var text = element.GetString();
                    // "" is how a cleared textarea arrives; treat it as a clear, never as an empty note.
                    action = string.IsNullOrWhiteSpace(text) ? FieldAction.Clear : FieldAction.Set;
                    value = action == FieldAction.Set ? text : null;
                    return true;
                default: return false;
            }
        }
    }
}
