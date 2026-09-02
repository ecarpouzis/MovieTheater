using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using MovieTheater.Books.Db;

namespace MovieTheater.Books.Services
{
    /// <summary>What one signatures batch did.</summary>
    public sealed record SignatureBatchResult(int Processed, long Remaining, long? NextCursor, int Computed, int Skipped, int Failed)
    {
        public bool Done => Processed == 0;
        public override string ToString() =>
            $"{{ processed: {Processed}, remaining: {Remaining}, nextCursor: \"{NextCursor}\" }}  [signatures, computed: {Computed}, skipped: {Skipped}, failed: {Failed}]";
    }

    /// <summary>
    /// <c>books-signatures</c> — the v2 WRITER of <c>ItemSignature</c>, which until 2026-09-01 only the migration
    /// filled: every item scanned on v2 had no row, so <c>books-dedup</c> could never group it with anything.
    ///
    /// <para><b>What it computes, and what it costs.</b> A ZIP-family archive (cbz / zip / epub) gets its content
    /// fingerprint and page signature from one central-directory read — a tail seek on the share, nothing
    /// decompressed. The cover hash is a dHash of the LOCAL thumbnail (<c>books-thumbs</c> must have run; an
    /// item with no thumb yet is stamped without one and picked up again once the thumb exists). A whole-file
    /// byte hash — the only signal a CBR / PDF / MOBI can carry — walks every byte over the share and is
    /// therefore opt-in (<paramref name="hashBytes"/>); without it those formats keep whatever the migration
    /// carried and gain nothing, which is stated rather than silently slow.</para>
    ///
    /// <para><b>Idempotent by stamp.</b> <c>SignaturesComputedFor</c> is <c>{size}:{mtime}|{flags}</c> — the
    /// file's signature plus WHICH signals were computed — so a settled library is a walk of skips, a replaced
    /// file recomputes, and a later thumb or a later <c>--hash-bytes</c> run re-drives exactly the rows it adds
    /// a signal to. A field the pass did not attempt is preserved, never nulled: a migrated CBR byte hash
    /// survives a run that did not ask for byte hashing.</para>
    ///
    /// <para>Chunked by <c>Item.Id</c> — the cursor IS the batch query's ordering — committed with the batch's
    /// rows, so a kill costs one batch and a rerun continues.</para>
    /// </summary>
    public sealed class SignatureJob
    {
        public const string CursorKey = "books:signatures:cursor";
        public const int DefaultBatchSize = 500;

        private readonly ThumbnailService thumbnails;
        private readonly ILogger<SignatureJob> logger;

        public SignatureJob(ThumbnailService thumbnails, ILogger<SignatureJob> logger)
        {
            this.thumbnails = thumbnails;
            this.logger = logger;
        }

        public async Task ResetAsync(BooksDb db, CancellationToken ct = default)
        {
            var row = await db.SystemStates.FirstOrDefaultAsync(s => s.Key == CursorKey, ct);
            if (row != null) db.SystemStates.Remove(row);
            await db.SaveChangesAsync(ct);
        }

        public async Task<(long Cursor, long Remaining, long Signed)> StatusAsync(BooksDb db, CancellationToken ct = default)
        {
            var cursor = await ReadCursorAsync(db, ct);
            return (cursor,
                await db.Items.AsNoTracking().CountAsync(i => !i.IsExcluded && i.Id > cursor, ct),
                await db.ItemSignatures.AsNoTracking().CountAsync(s => s.SignaturesComputedFor != null, ct));
        }

        /// <summary>One bounded batch. The caller loops.</summary>
        public async Task<SignatureBatchResult> RunBatchAsync(BooksDb db, int batchSize, bool hashBytes = false, CancellationToken ct = default)
        {
            batchSize = Math.Clamp(batchSize, 1, 5_000);
            var cursor = await ReadCursorAsync(db, ct);

            var batch = await db.Items.AsNoTracking()
                .Where(i => !i.IsExcluded && i.Id > cursor)
                .OrderBy(i => i.Id)
                .Take(batchSize)
                .Select(i => new { i.Id, i.Path, i.Extension, i.FileSize, i.FileModifiedAt })
                .ToListAsync(ct);
            if (batch.Count == 0) return new SignatureBatchResult(0, 0, null, 0, 0, 0);

            // The batch is a contiguous id range (ORDER BY Id, TAKE n), so its existing rows are one indexed
            // range read rather than an id IN-list that would trip SQLite's variable cap.
            var lastId = batch[^1].Id;
            var existing = await db.ItemSignatures
                .Where(s => s.ItemId > cursor && s.ItemId <= lastId)
                .ToDictionaryAsync(s => s.ItemId, ct);

            int computed = 0, skipped = 0, failed = 0;
            foreach (var item in batch)
            {
                ct.ThrowIfCancellationRequested();
                existing.TryGetValue(item.Id, out var row);

                var archive = Signatures.SupportsArchiveFingerprint(item.Extension);
                var thumbPath = thumbnails.Configured ? thumbnails.GetCachePath(item.Id) : null;
                var haveThumb = thumbPath != null && File.Exists(thumbPath);
                var flags = (archive ? "c" : hashBytes ? "b" : "") + (haveThumb ? "p" : "");
                var stamp = ThumbnailJob.FileSignature(item.FileSize, item.FileModifiedAt) + "|" + flags;
                if (row?.SignaturesComputedFor == stamp) { skipped++; continue; }

                var ok = true;
                string? content = row?.ContentFingerprint, pages = row?.PageSignature;
                long? cover = row?.CoverPHash;

                if (archive)
                {
                    var sig = Signatures.ArchiveSignatures(item.Path);
                    if (sig == null) ok = false;
                    else (content, pages) = (sig.Value.Content, sig.Value.Pages);
                }
                else if (hashBytes)
                {
                    var hash = Signatures.HashFileBytes(item.Path);
                    if (hash == null) ok = false;
                    else content = hash;
                }

                if (haveThumb)
                {
                    var h = Signatures.CoverHash(thumbPath!);
                    if (h == null) ok = false;
                    else cover = h;
                }

                if (!ok)
                {
                    // Recorded, never thrown — one unreadable file must not stop a 245k-item walk. The row is
                    // left exactly as it was (no stamp), so the next run tries it again.
                    failed++;
                    continue;
                }

                if (row == null)
                {
                    row = new ItemSignature { ItemId = item.Id };
                    db.ItemSignatures.Add(row);
                }
                row.ContentFingerprint = content;
                row.PageSignature = pages;
                row.CoverPHash = cover;
                row.SignaturesComputedFor = stamp;
                computed++;
            }

            await WriteCursorAsync(db, lastId, ct);
            await db.SaveChangesAsync(ct);   // the cursor and the rows commit TOGETHER

            var remaining = await db.Items.AsNoTracking().CountAsync(i => !i.IsExcluded && i.Id > lastId, ct);
            logger.LogInformation("signatures batch: processed {N}, computed {Computed}, skipped {Skipped}, failed {Failed}, remaining {Remaining}",
                batch.Count, computed, skipped, failed, remaining);
            return new SignatureBatchResult(batch.Count, remaining, lastId, computed, skipped, failed);
        }

        private static async Task<long> ReadCursorAsync(BooksDb db, CancellationToken ct)
        {
            var row = await db.SystemStates.AsNoTracking().FirstOrDefaultAsync(s => s.Key == CursorKey, ct);
            return long.TryParse(row?.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 0;
        }

        private static async Task WriteCursorAsync(BooksDb db, long value, CancellationToken ct)
        {
            var row = await db.SystemStates.FirstOrDefaultAsync(s => s.Key == CursorKey, ct);
            var text = value.ToString(CultureInfo.InvariantCulture);
            if (row == null) db.SystemStates.Add(new SystemState { Key = CursorKey, Value = text });
            else row.Value = text;
        }
    }
}
