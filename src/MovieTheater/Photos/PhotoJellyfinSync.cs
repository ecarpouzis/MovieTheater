using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using MovieTheater.Db;
using MovieTheater.Services.Jellyfin;

namespace MovieTheater.Photos
{
    /// <summary>Which lane a <c>photos-sync-jellyfin</c> run is draining (docs/photos-plan.md §2.3).
    /// Ordered: nothing can be cleared before the stamping lane has said what is still present, and the
    /// audit reads paths the stamping lane has just confirmed.</summary>
    public enum PhotoJellyfinPass
    {
        /// <summary>Map the family library's items onto <see cref="PhotoAsset"/> rows by path and stamp
        /// <see cref="PhotoAsset.JellyfinItemId"/>.</summary>
        Items,

        /// <summary>Clear <see cref="PhotoAsset.JellyfinItemId"/> on rows whose item Jellyfin no longer
        /// reports — a stale id is a play button that 404s, which is worse than no button.</summary>
        Clear,

        /// <summary>The reserved-folder-name report (§2.3's ⚠ trap).</summary>
        Audit,
    }

    /// <summary>One item as the family Jellyfin library reports it. Deliberately not
    /// <see cref="JellyfinItem"/>: this engine needs an id and a path and nothing else, and a narrow
    /// shape is what lets the source be faked without a stand-in server.</summary>
    public sealed class PhotoJellyfinItem
    {
        public string Id { get; set; } = "";

        /// <summary>The ABSOLUTE path Jellyfin reports, in whatever vocabulary that server uses.</summary>
        public string Path { get; set; } = "";
    }

    /// <summary>
    /// Where the family library's items come from. A seam for the same reason
    /// <see cref="Services.IImmichApi"/> is one: no test, build or local smoke may contact the live
    /// Jellyfin server, and "the server is not reachable" has to be an ordinary state rather than an
    /// exception path.
    /// </summary>
    public interface IPhotoJellyfinSource
    {
        /// <summary>Every video item in the family library. One fetch per run — the chunking below
        /// happens over the fetched list, because the library is one library and paging it a second
        /// time per batch would ask the server the same question repeatedly.</summary>
        Task<IReadOnlyList<PhotoJellyfinItem>> ItemsAsync(CancellationToken cancel = default);

        /// <summary>A label for the run's log — server name/version, or whatever identifies the source.</summary>
        Task<string> DescribeAsync(CancellationToken cancel = default);
    }

    /// <summary>The real source: the configured family library, scoped by <c>PhotosJellyfinLibraryId</c>
    /// so this sweep can never see the movie library — the mirror image of
    /// <see cref="JellyfinFamilyExclusion"/>, and why neither pass can reach the other's files.</summary>
    public sealed class JellyfinPhotoSource : IPhotoJellyfinSource
    {
        private readonly JellyfinApi jellyfin;
        private readonly string libraryId;

        public JellyfinPhotoSource(JellyfinApi jellyfin, string libraryId)
        {
            this.jellyfin = jellyfin;
            this.libraryId = libraryId;
        }

        public async Task<IReadOnlyList<PhotoJellyfinItem>> ItemsAsync(CancellationToken cancel = default)
        {
            var items = await jellyfin.GetLibraryVideoItemsAsync(libraryId, cancel);
            return items
                .Where(i => !string.IsNullOrEmpty(i.Id) && !string.IsNullOrEmpty(i.Path))
                .Select(i => new PhotoJellyfinItem { Id = i.Id, Path = i.Path! })
                .ToList();
        }

        public async Task<string> DescribeAsync(CancellationToken cancel = default)
        {
            var info = await jellyfin.GetSystemInfoAsync(cancel);
            return $"{info.ServerName} {info.Version} (library {libraryId})";
        }
    }

    public sealed class PhotoJellyfinSyncOptions
    {
        /// <summary>Items (or rows) per batch.</summary>
        public int BatchSize = 200;

        /// <summary>Report what would be written and write nothing. The first run against the real
        /// collection is a human-supervised checkpoint, as every pass here.</summary>
        public bool DryRun;

        /// <summary>Marker for the audit's <see cref="PhotoCurationBatch"/> row, one per invocation.</summary>
        public string AuditBatchId = "";
    }

    /// <summary>
    /// The <c>photos-sync-jellyfin</c> engine (docs/photos-plan.md §2.3): stamps
    /// <see cref="PhotoAsset.JellyfinItemId"/> from the dedicated family library, clears it when an item
    /// vanishes, and audits the collection for folder names Jellyfin reserves.
    ///
    /// <para><b>Matched by PATH, exactly like the movie sync</b> (§2.3), which is what makes the two
    /// halves symmetrical: the movie sync excludes this collection by path prefix and this one includes
    /// only it. Neither depends on the other having run, and neither can produce a row the other would
    /// claim.</para>
    ///
    /// <para><b>Nothing here writes to disk.</b> The outcome of every lane is a database column — an
    /// item id, a cleared item id, or a report row. Jellyfin itself is only ever READ (a scoped item
    /// listing); no scan is triggered, because scans are disabled and running one is the owner's call.</para>
    ///
    /// <para><b>Bulk-job contract</b>, as every pass in this vertical: bounded work per call,
    /// <c>{processed, remaining, nextCursor}</c> per chunk, an audited cursor ordering, idempotent
    /// re-runs and a deterministic no-progress stop.</para>
    /// </summary>
    public sealed class PhotoJellyfinSync
    {
        private readonly Func<MovieDb> dbFactory;
        private readonly IPhotoJellyfinSource source;
        private readonly PhotoJellyfinPaths paths;
        private readonly PhotoJellyfinSyncOptions options;
        private readonly Action<string> log;

        /// <summary>The library's items, fetched ONCE per run. The lanes chunk over this list, so the
        /// cursor is an index into an order the server already fixed.</summary>
        private List<PhotoJellyfinItem>? items;

        /// <summary>Item ids seen this run, accumulated by the Items lane so the Clear lane knows what
        /// "still present" means without asking the server a second time.</summary>
        private readonly HashSet<string> seenItemIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public PhotoJellyfinSync(Func<MovieDb> dbFactory, IPhotoJellyfinSource source, PhotoJellyfinPaths paths,
            PhotoJellyfinSyncOptions options, Action<string> log)
        {
            this.dbFactory = dbFactory;
            this.source = source;
            this.paths = paths;
            this.options = options;
            this.log = log;
        }

        /// <summary>Paths Jellyfin reported that no row matched — "the media server knows a file the
        /// album does not", the first half of §2.3's two-sided unmatched report.</summary>
        public List<string> UnmatchedJellyfinPaths { get; } = new List<string>();

        /// <summary>Videos the album holds that no Jellyfin item covers — the second half. Filled by
        /// the Clear lane, which is the one that sees our rows.</summary>
        public List<string> UnmatchedAssetPaths { get; } = new List<string>();

        // ── Driver ───────────────────────────────────────────────────────────────────────────────

        /// <summary>Runs up to <paramref name="maxBatches"/> bounded batches of one lane (0 drains),
        /// printing the per-chunk line the standing bulk-job rule requires and stopping
        /// deterministically on no progress.</summary>
        public async Task<PhotoIngestBatchResult> RunAsync(PhotoJellyfinPass pass, string? cursor, int maxBatches,
            CancellationToken cancel = default)
        {
            var total = new PhotoIngestBatchResult { NextCursor = cursor ?? "" };
            var batches = 0;
            while (maxBatches <= 0 || batches < maxBatches)
            {
                var result = await BatchAsync(pass, batches == 0 ? cursor : total.NextCursor, cancel);
                batches++;
                total.Processed += result.Processed;
                total.Remaining = result.Remaining;
                total.NextCursor = result.NextCursor;
                foreach (var kv in result.Counts) total.Add(kv.Key, kv.Value);

                var counts = result.CountsText();
                log($"{{ processed: {result.Processed}, remaining: {result.Remaining}, nextCursor: \"{result.NextCursor}\" }}"
                    + (counts.Length > 0 ? $"  [{counts}]" : ""));

                if (result.Remaining <= 0) break;
                if (result.Processed <= 0)
                {
                    log("No progress in a batch while work remained — stopping.");
                    break;
                }
            }
            return total;
        }

        public Task<PhotoIngestBatchResult> BatchAsync(PhotoJellyfinPass pass, string? cursor,
            CancellationToken cancel = default) => pass switch
        {
            PhotoJellyfinPass.Items => ItemsBatchAsync(cursor, cancel),
            PhotoJellyfinPass.Clear => ClearBatchAsync(cursor, cancel),
            PhotoJellyfinPass.Audit => AuditBatchAsync(cursor, cancel),
            _ => throw new ArgumentOutOfRangeException(nameof(pass)),
        };

        // ── Items: stamp JellyfinItemId (§2.3) ───────────────────────────────────────────────────

        /// <summary>
        /// One bounded slice of the family library's items, mapped onto our rows by root-relative path.
        ///
        /// <para><b>Cursor-ordering audit (§6).</b> The item list is fetched once per run and the cursor
        /// is an INDEX into it in the order the server returned; the batch takes
        /// <c>[index, index + batch)</c> from that same order. One dimension, one direction, in the page
        /// and in the cursor. <c>remaining</c> is the rest of the list — a real count, because this lane
        /// holds the whole set rather than paging a wire.</para>
        ///
        /// <para><b>Idempotent by construction:</b> a row already carrying the right id is counted and
        /// left alone, and stamping is a column write with no side effect, so re-running converges.</para>
        /// </summary>
        private async Task<PhotoIngestBatchResult> ItemsBatchAsync(string? cursor, CancellationToken cancel)
        {
            var index = ParseMark(cursor);
            var result = new PhotoIngestBatchResult { NextCursor = "i:" + index.ToString(CultureInfo.InvariantCulture) };
            await EnsureItemsAsync(cancel);

            if (index >= items!.Count)
            {
                result.Remaining = 0;
                return result;
            }

            var take = Math.Min(Math.Max(1, options.BatchSize), items.Count - index);
            var slice = items.GetRange(index, take);

            using var db = dbFactory();

            // One lookup per batch rather than per item: the relative keys are computed first, then the
            // rows they name are fetched in a single IN.
            var keyed = new List<(PhotoJellyfinItem Item, string Key)>();
            foreach (var item in slice)
            {
                result.Processed++;
                seenItemIds.Add(item.Id);
                var key = paths.ToRootRelative(item.Path);
                if (key == null)
                {
                    // Outside every form of the collection root. Never guessed at — see PhotoJellyfinPaths.
                    result.Add("outside-root");
                    Remember(UnmatchedJellyfinPaths, item.Path);
                    continue;
                }
                keyed.Add((item, key));
            }

            var wanted = keyed.Select(k => k.Key).Distinct().ToList();
            var rows = wanted.Count == 0
                ? new List<PhotoAsset>()
                : await db.PhotoAssets.Where(a => wanted.Contains(a.Path)).ToListAsync(cancel);
            var byPath = rows
                .GroupBy(a => a.Path, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.OrderBy(a => a.Id).ToList(), StringComparer.OrdinalIgnoreCase);

            foreach (var (item, key) in keyed)
            {
                if (!byPath.TryGetValue(key, out var matches))
                {
                    result.Add("unmatched-in-album");
                    Remember(UnmatchedJellyfinPaths, item.Path);
                    continue;
                }
                if (matches.Count > 1)
                {
                    // Path is UNIQUE on PhotoAsset, so this cannot happen on SQL Server — it can on a
                    // case-insensitive collation disagreement, and guessing which row is the video is
                    // the §2.5 stance's exact prohibition.
                    result.Add("ambiguous-path");
                    Remember(UnmatchedJellyfinPaths, item.Path);
                    continue;
                }

                var asset = matches[0];
                if (asset.Kind != PhotoAssetKind.Video)
                {
                    // Jellyfin indexed something the album classified as a photo. Reported, not stamped:
                    // a JellyfinItemId on a still would put a play button on a photograph.
                    result.Add("not-a-video");
                    continue;
                }

                if (string.Equals(asset.JellyfinItemId, item.Id, StringComparison.OrdinalIgnoreCase))
                {
                    result.Add("already-stamped");
                    continue;
                }

                result.Add(asset.JellyfinItemId == null ? "stamped" : "re-stamped");
                if (!options.DryRun) asset.JellyfinItemId = Truncate(item.Id, 64);
            }

            if (!options.DryRun) await db.SaveChangesAsync(cancel);

            result.NextCursor = "i:" + (index + take).ToString(CultureInfo.InvariantCulture);
            result.Remaining = items.Count - (index + take);
            return result;
        }

        // ── Clear: drop ids for vanished items (§2.3) ────────────────────────────────────────────

        /// <summary>Our side of the clear lane's queue: every live video row, stamped or not. Stamped
        /// rows can lose their id; unstamped ones are what the "album knows a video Jellyfin does not"
        /// half of the report counts.</summary>
        private static IQueryable<PhotoAsset> VideoQueue(MovieDb db) =>
            db.PhotoAssets.Where(a => a.Kind == PhotoAssetKind.Video && a.MissingSinceUtc == null);

        /// <summary>
        /// One bounded batch of our video rows, checked against the item ids this run saw.
        ///
        /// <para><b>Cursor-ordering audit (§6).</b> <c>WHERE Id &gt; cursor ORDER BY Id</c> over OUR
        /// rows, cursor = the last id examined — one column, one direction, in the page query and in the
        /// cursor. <c>remaining</c> is counted from the database after the writes.</para>
        ///
        /// <para><b>It requires the item list, and refuses to run without it.</b> Clearing on the basis
        /// of a set we failed to fetch would unstamp the entire album on the first unreachable server —
        /// so the list is fetched here too (cached per run), and an empty library clears nothing while
        /// saying so.</para>
        /// </summary>
        private async Task<PhotoIngestBatchResult> ClearBatchAsync(string? cursor, CancellationToken cancel)
        {
            var afterId = ParseMark(cursor);
            var result = new PhotoIngestBatchResult { NextCursor = "c:" + afterId.ToString(CultureInfo.InvariantCulture) };
            await EnsureItemsAsync(cancel);

            // The live id set. Built from the fetched list rather than from `seenItemIds` so this lane
            // is correct whether or not the Items lane ran first in this invocation.
            var live = new HashSet<string>(items!.Select(i => i.Id), StringComparer.OrdinalIgnoreCase);
            if (live.Count == 0)
            {
                // "The library reported nothing" and "every video was deleted" are indistinguishable
                // from here, and only one of them justifies unstamping the album. Refuse.
                log("  ! the family library reported no items — nothing will be cleared (an empty answer is not evidence of deletion).");
                result.Remaining = 0;
                return result;
            }

            using var db = dbFactory();
            var rows = await VideoQueue(db)
                .Where(a => a.Id > afterId)
                .OrderBy(a => a.Id)
                .Take(Math.Max(1, options.BatchSize))
                .ToListAsync(cancel);
            if (rows.Count == 0)
            {
                result.Remaining = 0;
                return result;
            }

            var lastId = afterId;
            foreach (var row in rows)
            {
                lastId = row.Id;
                result.Processed++;

                if (row.JellyfinItemId == null)
                {
                    result.Add("never-stamped");
                    Remember(UnmatchedAssetPaths, row.Path);
                    continue;
                }
                if (live.Contains(row.JellyfinItemId))
                {
                    result.Add("still-present");
                    continue;
                }

                result.Add("cleared");
                Remember(UnmatchedAssetPaths, row.Path);
                if (!options.DryRun) row.JellyfinItemId = null;
            }

            if (!options.DryRun) await db.SaveChangesAsync(cancel);

            result.NextCursor = "c:" + lastId.ToString(CultureInfo.InvariantCulture);
            result.Remaining = await VideoQueue(db).CountAsync(a => a.Id > lastId, cancel);
            return result;
        }

        // ── Audit: reserved folder names (§2.3's ⚠ trap) ─────────────────────────────────────────

        /// <summary>
        /// One bounded batch of the reserved-folder-name audit: every VIDEO row whose path passes
        /// through a folder Jellyfin's core walk reserves for extras, recorded as
        /// <see cref="PhotoCurationBatchItem"/> rows under one
        /// <see cref="PhotoCurationBatchKind.JellyfinReserved"/> batch.
        ///
        /// <para><b>It reports and does nothing else.</b> There is no accept action, because the only
        /// remedies are a rename under the collection root (forbidden absolutely, §6) or a library
        /// configuration change on the Jellyfin side (not this pipeline's to make). The row set exists
        /// so the answer to "which family videos will never play" is a query rather than a discovery.</para>
        ///
        /// <para><b>Cursor-ordering audit (§6).</b> <c>WHERE Id &gt; cursor ORDER BY Id</c> over our
        /// rows, cursor = last id examined, and the batch row's own <c>Cursor</c> carries the same mark
        /// so a killed run resumes from the batch. <c>remaining</c> is re-counted from the database.</para>
        /// </summary>
        private async Task<PhotoIngestBatchResult> AuditBatchAsync(string? cursor, CancellationToken cancel)
        {
            var afterId = ParseMark(cursor);
            var result = new PhotoIngestBatchResult { NextCursor = "r:" + afterId.ToString(CultureInfo.InvariantCulture) };

            using var db = dbFactory();
            var rows = await VideoQueue(db)
                .Where(a => a.Id > afterId)
                .OrderBy(a => a.Id)
                .Select(a => new { a.Id, a.Path, a.Sha256 })
                .Take(Math.Max(1, options.BatchSize))
                .ToListAsync(cancel);
            if (rows.Count == 0)
            {
                if (!options.DryRun)
                {
                    var finished = await AuditBatchRowAsync(db, cancel);
                    finished.Complete = true;
                    await db.SaveChangesAsync(cancel);
                }
                result.Remaining = 0;
                return result;
            }

            var batch = options.DryRun ? null : await AuditBatchRowAsync(db, cancel);
            var existing = batch == null || batch.Id == 0
                ? new HashSet<int>()
                : (await db.PhotoCurationBatchItems
                    .Where(i => i.PhotoCurationBatchId == batch.Id)
                    .Select(i => i.PhotoAssetId)
                    .ToListAsync(cancel)).ToHashSet();

            var lastId = afterId;
            foreach (var row in rows)
            {
                lastId = row.Id;
                result.Processed++;

                var reserved = PhotoJellyfinReservedFolders.ReservedSegment(row.Path);
                if (reserved == null) continue;

                result.Add("reserved-" + reserved.Replace(' ', '-'));
                if (options.DryRun || batch == null) { result.Add("would-report"); continue; }
                if (!existing.Add(row.Id)) { result.Add("already-reported"); continue; }

                db.PhotoCurationBatchItems.Add(new PhotoCurationBatchItem
                {
                    PhotoCurationBatch = batch,
                    PhotoAssetId = row.Id,
                    Path = row.Path,
                    Sha256 = row.Sha256,
                    // The RULE is the reserved name itself, so the report groups the way §2.9's rule
                    // stamping does: one bad collision is one cluster, not scattered lines.
                    Rule = Truncate("jellyfin-reserved:" + reserved, 64)!,
                });
                result.Add("reported");
            }

            result.NextCursor = "r:" + lastId.ToString(CultureInfo.InvariantCulture);
            result.Remaining = await VideoQueue(db).CountAsync(a => a.Id > lastId, cancel);

            if (!options.DryRun && batch != null)
            {
                batch.Cursor = lastId.ToString(CultureInfo.InvariantCulture);
                // Marked complete on the batch that DRAINS the queue, not only on a subsequent empty
                // one — a run whose last chunk happens to finish the collection is still a complete
                // report, and a half-written one must be distinguishable from it.
                if (result.Remaining <= 0) batch.Complete = true;
                await db.SaveChangesAsync(cancel);
            }

            return result;
        }

        /// <summary>The run's audit batch row, created on first use. One per invocation
        /// (<see cref="PhotoJellyfinSyncOptions.AuditBatchId"/>), so re-running produces a NEW report
        /// rather than mutating the one somebody is reading.</summary>
        private async Task<PhotoCurationBatch> AuditBatchRowAsync(MovieDb db, CancellationToken cancel)
        {
            var batchId = string.IsNullOrWhiteSpace(options.AuditBatchId)
                ? "jellyfin-reserved-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss")
                : options.AuditBatchId;

            var row = await db.PhotoCurationBatches
                .FirstOrDefaultAsync(b => b.Kind == PhotoCurationBatchKind.JellyfinReserved && b.BatchId == batchId, cancel);
            if (row == null)
            {
                row = new PhotoCurationBatch
                {
                    Kind = PhotoCurationBatchKind.JellyfinReserved,
                    BatchId = Truncate(batchId, 128)!,
                    // Accepted, not Pending: there is no decision to make. A Pending row would sit in
                    // the review surface forever asking a question nobody is allowed to answer.
                    Status = PhotoCurationBatchStatus.Accepted,
                    CreatedUtc = DateTime.UtcNow,
                    DecidedUtc = DateTime.UtcNow,
                };
                db.PhotoCurationBatches.Add(row);
                await db.SaveChangesAsync(cancel);
            }
            return row;
        }

        // ── Shared ───────────────────────────────────────────────────────────────────────────────

        private async Task EnsureItemsAsync(CancellationToken cancel)
        {
            if (items != null) return;
            var fetched = await source.ItemsAsync(cancel);
            items = fetched
                .Where(i => !string.IsNullOrEmpty(i.Id) && !string.IsNullOrEmpty(i.Path))
                .GroupBy(i => i.Id, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .ToList();
            log($"  family library: {items.Count} video item(s); roots {(paths.Configured ? string.Join(" | ", paths.Roots) : "(none configured)")}");
        }

        /// <summary>Bounded remembering: the two-sided unmatched report is a diagnostic, and an
        /// unbounded list of every path in a mismatched configuration is a memory leak wearing a
        /// report's clothes. The counts in <c>Counts</c> stay exact either way.</summary>
        private const int MaxRemembered = 200;

        private static void Remember(List<string> into, string path)
        {
            if (into.Count < MaxRemembered) into.Add(path);
        }

        /// <summary>Cursors are <c>phase:mark</c>, the <see cref="PhotoDupePass"/>/<see cref="PhotoImmichSync"/>
        /// shape, so a cursor pasted into the wrong lane is visibly wrong rather than a silent restart.</summary>
        private static int ParseMark(string? cursor)
        {
            if (string.IsNullOrEmpty(cursor)) return 0;
            var colon = cursor!.IndexOf(':');
            var mark = colon < 0 ? cursor : cursor.Substring(colon + 1);
            return int.TryParse(mark, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) && v > 0 ? v : 0;
        }

        private static string? Truncate(string? value, int max) =>
            string.IsNullOrEmpty(value) ? value : (value!.Length <= max ? value : value.Substring(0, max));
    }
}
