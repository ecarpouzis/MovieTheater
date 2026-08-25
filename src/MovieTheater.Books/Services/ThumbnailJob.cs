using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using MovieTheater.Books.Db;

namespace MovieTheater.Books.Services
{
    /// <summary>What one batch did. This is the whole observability contract: a caller prints it, accumulates it,
    /// and stops when <see cref="Remaining"/> hits zero or <see cref="Processed"/> stops moving.</summary>
    public sealed record ThumbnailBatchResult(
        int Processed, long Remaining, long? NextCursor, int Generated, int Skipped, int Failed)
    {
        /// <summary>A batch that moved no cursor is the end of the run (or a defect) — either way, stop.</summary>
        public bool Done => Processed == 0 || NextCursor == null;
    }

    /// <summary>
    /// Generate the thumbnails that are MISSING, a bounded batch at a time.
    ///
    /// <para><b>There is exactly one mode.</b> "Regenerate all" is not a mode and is not ported: it was a single
    /// unbounded pass over 141k files with no cursor, which is the failure this codebase refuses by rule. A
    /// rebuild is delete-then-generate-missing, so it stays countable and resumable at every point.</para>
    ///
    /// <para><b>Chunked, resumable, observable.</b> The cursor is <c>Item.Id</c> — the same ordering the batch
    /// query uses, which is what makes resumption exact rather than approximate. It is persisted in
    /// <c>SystemState</c> together with the running counters and committed WITH the batch's writes, so a process
    /// killed anywhere restarts from the last committed batch and re-does at most one batch. Re-doing a batch is
    /// free: an item whose file already exists is skipped, so the work is idempotent, never doubled.</para>
    ///
    /// <para><b>The only table it writes is <see cref="ItemState"/></b> (cover dimensions, the thumbnail error,
    /// the checked-at stamp) plus its own <c>SystemState</c> progress rows. It never touches the catalog row, and
    /// it never touches the library file — the source is opened read-only.</para>
    ///
    /// <para>The loop that repeats batches lives in the CALLER (the CLI verb, slice 5's admin endpoint), not
    /// here: a job that must survive to the end inside one call is the thing this design exists to prevent.</para>
    /// </summary>
    public sealed class ThumbnailJob
    {
        public const string CursorKey = "books:thumbs:cursor";
        public const string ProcessedKey = "books:thumbs:processed";
        public const string GeneratedKey = "books:thumbs:generated";
        public const string SkippedKey = "books:thumbs:skipped";
        public const string FailedKey = "books:thumbs:failed";
        public const string StartedAtKey = "books:thumbs:startedAt";

        public const int DefaultBatchSize = 200;

        private readonly ThumbnailService thumbnails;
        private readonly ILogger<ThumbnailJob> logger;

        public ThumbnailJob(ThumbnailService thumbnails, ILogger<ThumbnailJob> logger)
        {
            this.thumbnails = thumbnails;
            this.logger = logger;
        }

        /// <summary>Drop the persisted cursor and counters so the next batch starts from the beginning.</summary>
        public async Task ResetAsync(BooksDb db, CancellationToken ct = default)
        {
            foreach (var key in new[] { CursorKey, ProcessedKey, GeneratedKey, SkippedKey, FailedKey, StartedAtKey })
            {
                var row = await db.SystemStates.FirstOrDefaultAsync(s => s.Key == key, ct);
                if (row != null) db.SystemStates.Remove(row);
            }
            await db.SaveChangesAsync(ct);
        }

        /// <summary>The run's persisted totals so far — what a status endpoint reads without doing any work.</summary>
        public async Task<(long Cursor, long Processed, long Generated, long Skipped, long Failed, long Remaining)>
            StatusAsync(BooksDb db, CancellationToken ct = default)
        {
            var cursor = await ReadLongAsync(db, CursorKey, ct);
            var remaining = await db.Items.AsNoTracking().CountAsync(i => !i.IsExcluded && i.Id > cursor, ct);
            return (cursor,
                await ReadLongAsync(db, ProcessedKey, ct),
                await ReadLongAsync(db, GeneratedKey, ct),
                await ReadLongAsync(db, SkippedKey, ct),
                await ReadLongAsync(db, FailedKey, ct),
                remaining);
        }

        /// <summary>
        /// One bounded batch. Returns what it did and where it stopped; the caller decides whether to call again.
        /// </summary>
        public async Task<ThumbnailBatchResult> RunBatchAsync(BooksDb db, int batchSize, CancellationToken ct = default)
        {
            batchSize = Math.Clamp(batchSize, 1, 5_000);
            if (!thumbnails.Configured) throw new InvalidOperationException("Books:CacheDir is not configured.");

            var cursor = await ReadLongAsync(db, CursorKey, ct);

            // The batch query and the cursor share ONE ordering (Item.Id ascending). If they ever diverge, a
            // resumed run silently skips or repeats rows — which is why the ordering is spelled right here.
            var batch = await db.Items.AsNoTracking()
                .Where(i => !i.IsExcluded && i.Id > cursor)
                .OrderBy(i => i.Id)
                .Take(batchSize)
                .Select(i => new { i.Id, i.Path, i.Extension, i.FileSize, i.FileModifiedAt })
                .ToListAsync(ct);

            if (batch.Count == 0)
            {
                await WriteLongAsync(db, CursorKey, cursor, ct);
                await db.SaveChangesAsync(ct);
                return new ThumbnailBatchResult(0, 0, null, 0, 0, 0);
            }

            int generated = 0, skipped = 0, failed = 0;
            var states = new Dictionary<int, ItemState>();

            foreach (var item in batch)
            {
                ct.ThrowIfCancellationRequested();

                if (thumbnails.Exists(item.Id)) { skipped++; continue; }

                var result = await thumbnails.TryGetOrGenerateAsync(item.Id, item.Path, item.Extension);
                var state = await LoadOrCreateStateAsync(db, states, item.Id, ct);
                state.ThumbnailCheckedAt = DateTime.UtcNow;

                if (result.Success)
                {
                    generated++;
                    state.ThumbnailError = null;
                    if (result.Width is int w && result.Height is int h)
                    {
                        state.CoverWidth = w;
                        state.CoverHeight = h;
                        // The signature of the SOURCE the dimensions describe: a re-scanned or replaced file
                        // changes it, so a later pass knows the stored dimensions are stale without re-decoding.
                        state.CoverDimsComputedFor = FileSignature(item.FileSize, item.FileModifiedAt);
                    }
                }
                else
                {
                    // A missing or unreadable file is RECORDED, never thrown: one bad file must not stop a job
                    // that has 141k of them to walk. The broken flag is set only when the ARCHIVE was the
                    // problem, not when one cover image merely would not decode.
                    failed++;
                    state.ThumbnailError = Truncate(result.Error ?? "unknown error", 500);
                    if (result.ArchiveUnreadable)
                    {
                        state.IsBroken = true;
                        state.BrokenReason = state.ThumbnailError;
                        state.BrokenCheckedAt = state.ThumbnailCheckedAt;
                    }
                }
            }

            var nextCursor = batch[^1].Id;
            await WriteLongAsync(db, CursorKey, nextCursor, ct);
            await AddLongAsync(db, ProcessedKey, batch.Count, ct);
            await AddLongAsync(db, GeneratedKey, generated, ct);
            await AddLongAsync(db, SkippedKey, skipped, ct);
            await AddLongAsync(db, FailedKey, failed, ct);
            if (await ReadRowAsync(db, StartedAtKey, ct) == null)
                await WriteAsync(db, StartedAtKey, DateTime.UtcNow.ToString("O"), ct);

            // The cursor and the batch's ItemState writes commit TOGETHER: a crash between them is what would
            // make a resume skip work.
            await db.SaveChangesAsync(ct);

            var remaining = await db.Items.AsNoTracking().CountAsync(i => !i.IsExcluded && i.Id > nextCursor, ct);
            logger.LogInformation(
                "thumbs batch: processed {Processed}, generated {Generated}, skipped {Skipped}, failed {Failed}, remaining {Remaining}, nextCursor {Cursor}",
                batch.Count, generated, skipped, failed, remaining, nextCursor);

            return new ThumbnailBatchResult(batch.Count, remaining, nextCursor, generated, skipped, failed);
        }

        public static string FileSignature(long fileSize, DateTime? modifiedAt) =>
            $"{fileSize}:{modifiedAt?.Ticks ?? 0}";

        private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];

        private static async Task<ItemState> LoadOrCreateStateAsync(
            BooksDb db, Dictionary<int, ItemState> seen, int itemId, CancellationToken ct)
        {
            if (seen.TryGetValue(itemId, out var cached)) return cached;
            var state = await db.ItemStates.FirstOrDefaultAsync(s => s.ItemId == itemId, ct);
            if (state == null)
            {
                state = new ItemState { ItemId = itemId };
                db.ItemStates.Add(state);
            }
            seen[itemId] = state;
            return state;
        }

        private static Task<SystemState?> ReadRowAsync(BooksDb db, string key, CancellationToken ct) =>
            db.SystemStates.FirstOrDefaultAsync(s => s.Key == key, ct);

        private static async Task<long> ReadLongAsync(BooksDb db, string key, CancellationToken ct)
        {
            var row = await db.SystemStates.AsNoTracking().FirstOrDefaultAsync(s => s.Key == key, ct);
            return long.TryParse(row?.Value, out var v) ? v : 0;
        }

        private static async Task WriteAsync(BooksDb db, string key, string value, CancellationToken ct)
        {
            var row = await ReadRowAsync(db, key, ct);
            if (row == null) db.SystemStates.Add(new SystemState { Key = key, Value = value });
            else row.Value = value;
        }

        private static Task WriteLongAsync(BooksDb db, string key, long value, CancellationToken ct) =>
            WriteAsync(db, key, value.ToString(System.Globalization.CultureInfo.InvariantCulture), ct);

        private static async Task AddLongAsync(BooksDb db, string key, long delta, CancellationToken ct)
        {
            var row = await ReadRowAsync(db, key, ct);
            var current = long.TryParse(row?.Value, out var v) ? v : 0;
            var next = (current + delta).ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (row == null) db.SystemStates.Add(new SystemState { Key = key, Value = next });
            else row.Value = next;
        }
    }
}
