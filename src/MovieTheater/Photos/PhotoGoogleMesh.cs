using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using MovieTheater.Core;
using MovieTheater.Db;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Webp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace MovieTheater.Photos
{
    /// <summary>Which resumable queue a <c>photos-google-mesh</c> run is draining (docs/photos-plan.md
    /// §2.10). Ordered: nothing can be matched before it has been scanned, and nothing may be downloaded
    /// before every item has been matched or ruled out.</summary>
    public enum PhotoGoogleMeshPass
    {
        /// <summary>Walk the extracted archive, pair media with sidecars, upsert
        /// <see cref="PhotoGoogleItem"/> rows. Cursor: the last completed archive-relative directory.</summary>
        Scan,

        /// <summary>Decide each Pending row against the local library — name+size, then SHA-256, then
        /// pHash — and backfill what the sidecar knows onto whatever it matched. Cursor: row id.</summary>
        Match,

        /// <summary>Emit grid/view WebPs for the Google-only items FROM THE ARCHIVE, so the review list
        /// can show a picture we do not own. Cursor: row id.</summary>
        Thumbs,

        /// <summary>The one additive NAS write (§2.10). Opt-in, guarded three ways, never the default.</summary>
        Download,
    }

    public sealed class PhotoGoogleMeshOptions
    {
        /// <summary>Absolute root of an EXTRACTED Takeout archive. Read-only, always.</summary>
        public string TakeoutDir = "";

        /// <summary>
        /// Absolute destination for the download lane. <b>No default anywhere in this codebase</b> — the
        /// lane refuses to run when it is null (§2.10), because a defaulted path is how a pipeline that
        /// promised never to write to the collection ends up writing to it.
        /// </summary>
        public string? SyncDir;

        /// <summary>Where Google-only derivatives are written (§2.2's cache, <c>google/</c> namespace).</summary>
        public string? ThumbCacheDir;

        public string? HomeTimeZone;

        /// <summary>Directories per scan batch; rows per queue batch.</summary>
        public int BatchSize = 50;

        /// <summary>Byte bound for the passes that READ archive bytes (match's hash rungs, thumbs,
        /// download). A row count is a poor bound when one row is a 4 GB clip and the next a 40 KB
        /// thumbnail — the ingest's own rule (§2.5), applied to the archive.</summary>
        public long MaxBatchBytes = 2L * 1024 * 1024 * 1024;

        /// <summary>
        /// pHash Hamming threshold for the third rung. Defaults to the near lane's own 8 deliberately:
        /// "the same picture, re-encoded" is one question, and two passes answering it with different
        /// numbers would group a pair the mesh refused to match (§2.6).
        /// </summary>
        public int PHashDistance = 8;

        /// <summary>
        /// How far a sidecar date may sit from the local one before it counts as a DISAGREEMENT. Sixty
        /// minutes by default: it absorbs sub-minute rounding and a DST edge without swallowing a real
        /// difference of day. ⚠ A photo taken in another timezone legitimately disagrees by hours — the
        /// count is a report, not a fault, and travel-heavy folders will produce some.
        /// </summary>
        public int ConflictToleranceMinutes = 60;

        /// <summary>Report what WOULD change and write nothing. The mesh's default posture is already
        /// report-only for the download lane; this extends it to the row writes.</summary>
        public bool DryRun;
    }

    /// <summary>
    /// The <c>photos-google-mesh</c> engine (docs/photos-plan.md §2.10).
    ///
    /// <para><b>Why Takeout at all.</b> The Google Photos Library API lost third-party read access on
    /// 2025-03-31; the replacement Picker API is a manual, session-based selection. There is no
    /// API-driven mesh left to build, so the lane is an archive Google can be scheduled to produce, and
    /// everything here is written to survive being run again over the next one.</para>
    ///
    /// <para><b>The sidecar is worth having even for photos we already own.</b> Google's per-item JSON is
    /// frequently RICHER than the media file's own EXIF, because some upload/download paths strip it —
    /// which is why the matched half of this pass is a metadata backfill rather than a no-op.</para>
    ///
    /// <para><b>Bulk-job contract</b>, as every pass in this vertical: bounded work per call,
    /// <c>{processed, remaining, nextCursor}</c> after each chunk, an audited cursor ordering, idempotent
    /// resume, and a deterministic no-progress stop.</para>
    ///
    /// <para><b>Nothing under the collection root is written</b> (§6). The archive is read-only; the only
    /// write this class can perform outside the database and the derivative cache is
    /// <see cref="PhotoGoogleMeshPass.Download"/>, which is opt-in, refuses to overwrite, and refuses to
    /// run at all until the match pass has fully drained.</para>
    /// </summary>
    public sealed class PhotoGoogleMesh
    {
        private readonly Func<MovieDb> dbFactory;
        private readonly PhotoGoogleMeshOptions options;
        private readonly Action<string> log;
        private readonly TimeZoneInfo homeZone;
        private readonly string archiveRoot;

        /// <summary>
        /// The local library projected into the three lookups the cascade needs, built ONCE per run.
        ///
        /// <para>Cost, stated because it is paid up front: one projection query over
        /// (id, path, size, sha256, phash) — no rows, no bytes — plus a dictionary and a BK-tree index.
        /// Tens of MB and about a second at 150k photos, the same profile the near lane's hash index and
        /// the Immich lane's path index already pay. The alternative is a query per archive item (a size
        /// filter plus a trailing-path LIKE, a scan each), which is what makes a real archive
        /// unfinishable.</para>
        /// </summary>
        private PhotoGoogleLocalIndex? localIndex;

        public PhotoGoogleMesh(Func<MovieDb> dbFactory, PhotoGoogleMeshOptions options, Action<string> log)
        {
            this.dbFactory = dbFactory;
            this.options = options;
            this.log = log;
            homeZone = PhotoDates.ResolveHomeZone(options.HomeTimeZone);
            archiveRoot = string.IsNullOrWhiteSpace(options.TakeoutDir) ? "" : Path.GetFullPath(options.TakeoutDir);
        }

        // ── Driver ───────────────────────────────────────────────────────────────────────────────

        /// <summary>Runs up to <paramref name="maxBatches"/> bounded batches of one pass (0 drains),
        /// printing the per-chunk line the standing rule requires and stopping deterministically.</summary>
        public async Task<PhotoIngestBatchResult> RunAsync(PhotoGoogleMeshPass pass, string? cursor, int maxBatches,
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
                    // Deterministic stop: the queue claims work but the batch moved none of it.
                    log("No progress in a batch while work remained — stopping.");
                    break;
                }
            }
            return total;
        }

        public Task<PhotoIngestBatchResult> BatchAsync(PhotoGoogleMeshPass pass, string? cursor,
            CancellationToken cancel = default) => pass switch
        {
            PhotoGoogleMeshPass.Scan => ScanBatchAsync(cursor, cancel),
            PhotoGoogleMeshPass.Match => MatchBatchAsync(cursor, cancel),
            PhotoGoogleMeshPass.Thumbs => ThumbBatchAsync(cursor, cancel),
            PhotoGoogleMeshPass.Download => DownloadBatchAsync(cursor, cancel),
            _ => throw new ArgumentOutOfRangeException(nameof(pass)),
        };

        // ── Pass 1: scan the archive (§2.10 step 1) ──────────────────────────────────────────────

        /// <summary>
        /// One bounded slice of the archive walk: <c>BatchSize</c> DIRECTORIES, paired and upserted.
        ///
        /// <para><b>Cursor-ordering audit (§6).</b> The candidate directories are sorted with
        /// <see cref="PhotoWalkCursor.Comparer"/> and filtered with <see cref="PhotoWalkCursor.IsAfter"/>,
        /// both defined in terms of the SAME key function — so the order the batch pages in and the order
        /// the cursor advances through are one order by construction. (A plain ordinal compare of paths
        /// would be a different sequence from a depth-first walk whenever a sibling name contains a
        /// space, which a Takeout tree full of album titles certainly does.) <c>remaining</c> is an
        /// INDEPENDENT count of directories still after the cursor, not a decrement of a running total.</para>
        ///
        /// <para><b>Idempotent by construction.</b> Every item is looked up on §2.10's identity triple
        /// BEFORE it is inserted — never by leaning on the unique index, because two of its three columns
        /// are nullable and SQL Server therefore filters the index to rows where both are non-null. An
        /// item whose sidecar supplied neither a date nor a size is not constrained by the database at
        /// all, and a blind insert would duplicate it on every export.</para>
        /// </summary>
        private async Task<PhotoIngestBatchResult> ScanBatchAsync(string? cursor, CancellationToken cancel)
        {
            var result = new PhotoIngestBatchResult { NextCursor = cursor ?? "" };
            if (!Directory.Exists(archiveRoot))
                throw new DirectoryNotFoundException($"Takeout archive not found: {archiveRoot}");

            var directories = EnumerateDirectories()
                .Where(d => PhotoWalkCursor.IsAfter(d, string.IsNullOrEmpty(cursor) ? null : cursor))
                .OrderBy(d => d, PhotoWalkCursor.Comparer)
                .ToList();
            // The root itself is the empty relative path, which no cursor can be "after"; it is visited
            // exactly once, on the run that starts with no cursor at all.
            if (cursor == null) directories.Insert(0, "");

            if (directories.Count == 0) return result;

            var batch = directories.Take(Math.Max(1, options.BatchSize)).ToList();
            result.Remaining = directories.Count - batch.Count;

            using var db = dbFactory();
            // Rows produced anywhere in THIS batch, so a Takeout item that appears in two of its
            // directories is upserted once rather than inserted twice (see UpsertAsync).
            var seenThisBatch = new List<PhotoGoogleItem>();
            foreach (var relativeDirectory in batch)
            {
                cancel.ThrowIfCancellationRequested();
                var full = relativeDirectory.Length == 0
                    ? archiveRoot
                    : Path.Combine(archiveRoot, relativeDirectory.Replace('/', Path.DirectorySeparatorChar));

                var items = PhotoGoogleSidecar.ReadDirectory(full, archiveRoot, out var unparseable);
                result.Add("sidecar-unparseable", unparseable);
                await UpsertAsync(db, items, seenThisBatch, result, cancel);
                result.Processed++;
                result.NextCursor = relativeDirectory;
            }

            if (!options.DryRun) await db.SaveChangesAsync(cancel);
            return result;
        }

        /// <summary>Every directory under the archive root, archive-relative with forward slashes. Only
        /// directories are enumerated — no file listing, no stat — so building the candidate list stays
        /// cheap enough to repeat on every batch.</summary>
        private IEnumerable<string> EnumerateDirectories()
        {
            IEnumerable<string> all;
            try { all = Directory.EnumerateDirectories(archiveRoot, "*", SearchOption.AllDirectories); }
            catch (UnauthorizedAccessException) { yield break; }

            foreach (var directory in all)
                yield return Path.GetRelativePath(archiveRoot, directory).Replace('\\', '/');
        }

        /// <param name="seenThisBatch">Rows this batch has already produced, across directories. See the
        /// duplicate note below.</param>
        private async Task UpsertAsync(MovieDb db, List<PhotoGoogleArchiveItem> items,
            List<PhotoGoogleItem> seenThisBatch, PhotoIngestBatchResult result, CancellationToken cancel)
        {
            if (items.Count == 0) return;

            // One query for the whole directory rather than one per item; the triple is then matched in
            // memory, which is also where the nullable-column caveat above is actually honoured.
            var names = items.Select(i => i.FileName).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var existing = await db.PhotoGoogleItems
                .Where(i => names.Contains(i.TakeoutFileName))
                .ToListAsync(cancel);

            // Rows an EARLIER DIRECTORY IN THIS SAME BATCH produced are not in the database yet — the
            // batch saves once, at the end. Google's albums are COPIES of the same photograph, so one
            // Takeout item routinely appears in two directories; without this the second directory's
            // lookup missed it and inserted a second row under the identical identity triple, in a
            // single run, which no amount of re-running would then clean up. Kept as an explicit
            // batch-local list rather than read off the change tracker so a --dry-run — which adds
            // nothing to track — counts the same item once too, instead of previewing a phantom insert.
            var nameSet = new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);
            foreach (var pending in seenThisBatch)
                if (nameSet.Contains(pending.TakeoutFileName)) existing.Add(pending);

            var now = DateTime.UtcNow;
            foreach (var item in items)
            {
                if (item.Sidecar == null) result.Add("no-sidecar");
                else result.Add("sidecar-" + item.SidecarMatch);

                var takenUtc = item.Sidecar?.PhotoTakenUtc;
                long? size = item.SizeBytes > 0 ? item.SizeBytes : null;

                var row = existing.FirstOrDefault(i =>
                    string.Equals(i.TakeoutFileName, item.FileName, StringComparison.OrdinalIgnoreCase)
                    && i.TakenAtUtc == takenUtc
                    && i.SizeBytes == size);

                if (row == null)
                {
                    result.Add("new");
                    row = new PhotoGoogleItem
                    {
                        TakeoutFileName = Truncate(item.FileName, 400)!,
                        TakeoutRelativePath = Truncate(item.RelativePath, 850),
                        TakenAtUtc = takenUtc,
                        SizeBytes = size,
                        SidecarJson = item.Sidecar?.Json,
                        Status = PhotoGoogleItemStatus.Pending,
                        FirstSeenUtc = now,
                        LastSeenUtc = now,
                    };
                    // Built even in a dry run, and remembered either way: it is what makes the SECOND
                    // directory holding this same item recognize it rather than count it again.
                    existing.Add(row);
                    seenThisBatch.Add(row);
                    if (!options.DryRun) db.PhotoGoogleItems.Add(row);
                    continue;
                }

                // Seen before. The archive re-presenting an item is not new information about where it
                // stands against the library, so the STATUS is never re-decided here: an item once
                // Matched stays Matched (§2.10), an Ignored one stays ignored, and a Downloaded one is
                // not offered again. Only the archive-side facts are refreshed.
                result.Add("unchanged");
                if (options.DryRun) continue;
                row.LastSeenUtc = now;
                row.TakeoutRelativePath = Truncate(item.RelativePath, 850);
                if (item.Sidecar != null) row.SidecarJson = item.Sidecar.Json;
            }
        }

        // ── Pass 2: match (§2.10 step 2) ─────────────────────────────────────────────────────────

        /// <summary>
        /// One bounded slice of the match queue: Pending rows, cheapest rung first.
        ///
        /// <para><b>Cursor-ordering audit (§6).</b> The page is <c>WHERE Status = Pending AND Id &gt;
        /// cursor ORDER BY Id</c> and the cursor is the last id of the page — one column, one direction,
        /// in the query and in the cursor. The queue is additionally SELF-DRAINING (every row examined
        /// leaves Pending), so a resume with no cursor at all is correct too; the cursor exists so a run
        /// killed mid-batch does not re-read the bytes it already hashed. <c>remaining</c> is counted
        /// independently from the database.</para>
        ///
        /// <para><b>The cascade, and why it is ordered this way</b> (§2.10): name+size costs nothing and
        /// settles the overwhelming majority; SHA-256 costs one read and is proof; pHash costs a decode
        /// and is the safety net for the media Google re-encoded. A pHash match records the DISTANCE it
        /// was accepted at, because it is the only rung whose answer is a resemblance rather than an
        /// identity.</para>
        ///
        /// <para><b>Several local candidates resolve to the lowest id, counted as ambiguous</b> — and
        /// this is deliberately NOT the "refuse rather than guess" stance the Immich lane takes. There,
        /// a wrong map attaches a stranger's face to a family photograph: a visible falsehood. Here, the
        /// candidates are local files of the same name and the same size — copies of one picture — and
        /// the only consequence is which copy receives a sidecar date (which §2.6 redirects to the group
        /// master anyway). Refusing would instead push the item onto the Google-only list, and the
        /// download lane would offer to fetch a photograph the family already owns: the precise failure
        /// §2.10's drain guard exists to prevent.</para>
        /// </summary>
        private async Task<PhotoIngestBatchResult> MatchBatchAsync(string? cursor, CancellationToken cancel)
        {
            var cursorId = ParseIdCursor(cursor);
            var result = new PhotoIngestBatchResult { NextCursor = cursorId.ToString(CultureInfo.InvariantCulture) };
            using var db = dbFactory();

            await EnsureLocalIndexAsync(db, cancel);

            var page = await db.PhotoGoogleItems
                .Where(i => i.Status == PhotoGoogleItemStatus.Pending && i.Id > cursorId)
                .OrderBy(i => i.Id)
                .Take(Math.Max(1, options.BatchSize))
                .ToListAsync(cancel);

            if (page.Count == 0)
            {
                result.Remaining = await db.PhotoGoogleItems
                    .CountAsync(i => i.Status == PhotoGoogleItemStatus.Pending && i.Id > cursorId, cancel);
                return result;
            }

            long bytesRead = 0;
            foreach (var row in page)
            {
                cancel.ThrowIfCancellationRequested();
                result.Processed++;
                result.NextCursor = row.Id.ToString(CultureInfo.InvariantCulture);

                var decision = Decide(row, ref bytesRead, result);
                if (decision != null)
                {
                    row.MatchedPhotoAssetId = decision.AssetId;
                    row.MatchMethod = decision.Method;
                    row.MatchDistance = decision.Distance;
                    row.Status = PhotoGoogleItemStatus.Matched;
                    result.Add("matched-" + decision.Method);
                    await BackfillAsync(db, row, decision.AssetId, result, cancel);
                }
                else
                {
                    row.Status = PhotoGoogleItemStatus.Unmatched;
                    result.Add("google-only");
                }

                // Byte bound: a batch stops reading once it has read its share, even part-way through
                // its row count. The rows it did not reach stay Pending and are the next batch.
                if (bytesRead >= options.MaxBatchBytes) break;
            }

            if (options.DryRun)
            {
                // Nothing is persisted — the in-memory status flips above are discarded. But the
                // REMAINING count is still counted honestly: every row this batch previewed is at or
                // before the cursor, so "Pending after the cursor" is exactly what a real run would
                // still have to do. Reporting 0 stopped the driver after ONE batch and printed
                // "remaining: 0", so a preview of the first page read as a preview of the whole
                // archive — the one thing a dry run exists to tell the truth about.
                db.ChangeTracker.Clear();
                result.Add("dry-run");
            }
            else await db.SaveChangesAsync(cancel);

            var lastId = ParseIdCursor(result.NextCursor);
            result.Remaining = await db.PhotoGoogleItems
                .CountAsync(i => i.Status == PhotoGoogleItemStatus.Pending && i.Id > lastId, cancel);
            return result;
        }

        private sealed class MatchDecision
        {
            public int AssetId;

            public string Method = "";

            public int? Distance;
        }

        /// <summary>Synchronous on purpose: it reads local files and consults an in-memory index, and a
        /// <c>ref</c> byte budget cannot cross an async boundary.</summary>
        private MatchDecision? Decide(PhotoGoogleItem row, ref long bytesRead, PhotoIngestBatchResult result)
        {
            // ── Rung 1: name + size. No bytes at all. ──
            var byName = localIndex!.ByNameAndSize(row.TakeoutFileName, row.SizeBytes);
            if (byName.Count > 0)
            {
                if (byName.Count > 1) result.Add("ambiguous-name");
                return new MatchDecision { AssetId = byName[0], Method = "name+size" };
            }

            var full = ArchivePath(row);
            if (full == null || !File.Exists(full))
            {
                result.Add("archive-file-missing");
                return null;
            }

            // ── Rung 2: SHA-256. One read; proof rather than resemblance. ──
            string sha;
            try
            {
                sha = PhotoHashes.Sha256File(full);
                bytesRead += row.SizeBytes ?? 0;
            }
            catch (IOException e)
            {
                log($"  unreadable in archive: {row.TakeoutRelativePath} ({e.Message})");
                result.Add("archive-read-errors");
                return null;
            }

            var bySha = localIndex.BySha(sha);
            if (bySha.Count > 0)
            {
                if (bySha.Count > 1) result.Add("ambiguous-sha");
                return new MatchDecision { AssetId = bySha[0], Method = "sha256" };
            }

            // ── Rung 3: pHash. Google re-encodes some media, so pixel similarity is the safety net. ──
            if (!PhotoFileKinds.IsDecodable(Path.GetExtension(full)))
            {
                result.Add("phash-undecodable");
                return null;
            }

            long phash;
            try
            {
                using var image = LoadOriented(full);
                phash = PhotoHashes.PHash(image);
            }
            catch (Exception e) when (e is IOException || e is ImageFormatException || e is NotSupportedException
                                      || e is InvalidOperationException)
            {
                result.Add("phash-errors");
                return null;
            }

            var neighbours = localIndex.NearestByPHash(phash);
            if (neighbours.Count == 0) return null;

            // Nearest first, id second — the index sorts that way, so the pick is deterministic.
            var best = neighbours[0];
            if (neighbours.Count > 1 && neighbours[1].Distance == best.Distance) result.Add("ambiguous-phash");
            return new MatchDecision { AssetId = best.AssetId, Method = "phash", Distance = best.Distance };
        }

        // ── Backfill (§2.10 step 3, §2.7's source hierarchy) ─────────────────────────────────────

        /// <summary>
        /// How much a <c>TakenAt</c> is worth, as a RANK rather than the enum's numeric order (§2.7).
        ///
        /// <para>The enum cannot be used directly and that is the trap this method exists to close:
        /// <see cref="TakenAtSource.VideoContainer"/> is 7 — numerically the highest value in the enum —
        /// because Phase 5 appended it to an int column shared with production, while what it actually
        /// is is a peer of <see cref="TakenAtSource.Exif"/>. Comparing enum values would let a Takeout
        /// sidecar be outranked correctly by a container date and a HUMAN's answer be outranked by
        /// nothing, but only by accident; this table says what the ordering IS.</para>
        /// </summary>
        public static int SourceRank(TakenAtSource source) => source switch
        {
            TakenAtSource.Unknown => 0,
            TakenAtSource.FolderInferred => 1,
            TakenAtSource.FilenameParsed => 2,
            TakenAtSource.GoogleSidecar => 3,
            // Both are a capture-time stamp written by the device that took the picture.
            TakenAtSource.Exif => 4,
            TakenAtSource.VideoContainer => 4,
            // A human outranks every machine and is never overwritten by one (§2.7).
            TakenAtSource.Estimated => 5,
            TakenAtSource.Manual => 6,
            _ => 0,
        };

        /// <summary>
        /// Writes what the sidecar knows onto the asset it matched, under one rule stated in both
        /// directions (§2.10's "flag-but-write on conflicts", §2.7's hierarchy):
        ///
        /// <list type="bullet">
        /// <item><b>The sidecar WINS</b> when the local date came from a strictly weaker source
        /// (Unknown / FolderInferred / FilenameParsed). It is WRITTEN — and when it displaced a real
        /// date that disagreed, the write is FLAGGED on the item row
        /// (<c>takenAt-overwritten:&lt;source&gt;</c>) and counted. Flag-but-write, not
        /// flag-and-skip: a filename guess losing to Google's own record of when the shutter fired is
        /// the improvement this pass exists for, and the flag is how a human finds it later.</item>
        /// <item><b>The sidecar LOSES</b> to an equal-or-stronger source (GoogleSidecar / Exif /
        /// VideoContainer / Estimated / Manual). NOTHING is written; if the two disagree beyond the
        /// tolerance the disagreement is recorded on the item row (<c>takenAt:&lt;source&gt;</c>) and
        /// counted, because a Google date that contradicts a camera is a fact worth surfacing and not a
        /// reason to overwrite the camera.</item>
        /// <item><b>GPS is written ONLY where both coordinates are null</b>, and
        /// <c>LocationLabel</c> only where it is null (source-stamped
        /// <see cref="PhotoLocationSource.GoogleSidecar"/>). An existing coordinate that differs is
        /// recorded as <c>gps</c> and left alone — coordinates carry no source column, so anything
        /// already there is treated as at least as strong.</item>
        /// <item><b>The description has nowhere on the asset to go</b> — <c>PhotoAsset</c> has no
        /// caption column and Phase 6 adds no migration for one. It stays on the item row inside the
        /// verbatim sidecar and the asset-detail endpoint surfaces it from there.</item>
        /// </list>
        /// </summary>
        private async Task BackfillAsync(MovieDb db, PhotoGoogleItem row, int assetId,
            PhotoIngestBatchResult result, CancellationToken cancel)
        {
            var sidecar = ParseSidecar(row.SidecarJson);
            if (sidecar == null) return;

            var asset = await db.PhotoAssets.FirstOrDefaultAsync(a => a.Id == assetId, cancel);
            if (asset == null) return;

            var flags = new List<string>();
            var tolerance = TimeSpan.FromMinutes(Math.Max(0, options.ConflictToleranceMinutes));

            if (sidecar.PhotoTakenUtc is DateTime takenUtc)
            {
                var wallClock = PhotoDates.ToWallClock(takenUtc, homeZone);
                // Captured BEFORE the write: the flag has to name what the sidecar DISPLACED, and a
                // source read after the assignment would only ever say "GoogleSidecar".
                var previousSource = asset.TakenAtSource;
                var localRank = SourceRank(previousSource);
                var disagrees = asset.TakenAt != null
                                && (asset.TakenAt.Value - wallClock).Duration() > tolerance;

                if (localRank < SourceRank(TakenAtSource.GoogleSidecar))
                {
                    if (!options.DryRun)
                    {
                        asset.TakenAtUtcRaw = takenUtc;
                        asset.TakenAt = wallClock;
                        asset.TakenAtSource = TakenAtSource.GoogleSidecar;
                    }
                    result.Add("dated");
                    if (disagrees)
                    {
                        flags.Add("takenAt-overwritten:" + previousSource);
                        result.Add("date-conflicts-written");
                    }
                }
                else if (disagrees)
                {
                    flags.Add("takenAt:" + previousSource);
                    result.Add("date-disagreements");
                }
            }

            if (sidecar.Latitude is double lat && sidecar.Longitude is double lon)
            {
                if (asset.GpsLat == null && asset.GpsLon == null)
                {
                    if (!options.DryRun)
                    {
                        asset.GpsLat = lat;
                        asset.GpsLon = lon;
                    }
                    result.Add("gps");
                }
                else if (Math.Abs((asset.GpsLat ?? 0) - lat) > 0.001 || Math.Abs((asset.GpsLon ?? 0) - lon) > 0.001)
                {
                    // ~100 m at the equator: closer than that is two devices agreeing, not a conflict.
                    flags.Add("gps");
                    result.Add("gps-disagreements");
                }
            }

            if (!options.DryRun)
            {
                // Rewritten wholesale rather than appended to, so a disagreement a human resolved
                // disappears on the next run instead of accumulating forever.
                row.Disagreements = flags.Count == 0 ? null : Truncate(string.Join(",", flags), 256);
            }
        }

        // ── Pass 3: Google-only derivatives (§2.10 step 4 + §2.2) ────────────────────────────────

        /// <summary>
        /// Grid and view WebPs for the items we do NOT own, written from the archive into the derivative
        /// cache's <c>google/</c> namespace so the review list can show a picture through the existing
        /// gateway route (§2.2). The gateway is unchanged: it still joins a relative path onto its
        /// thumb-cache mount and serves the bytes.
        ///
        /// <para><b>Cursor-ordering audit (§6):</b> paged <c>WHERE Status IN (Unmatched, Ignored) AND
        /// Id &gt; cursor ORDER BY Id</c>, cursor = the page's last id. Same column, same direction.
        /// Unlike the other queues this one is NOT self-draining — "has a thumb" is a fact about the
        /// cache directory, not a column — so an item whose derivatives already exist is skipped in
        /// microseconds and counted, and a full re-run is a cheap existence check per row rather than a
        /// re-decode.</para>
        /// </summary>
        private async Task<PhotoIngestBatchResult> ThumbBatchAsync(string? cursor, CancellationToken cancel)
        {
            var cursorId = ParseIdCursor(cursor);
            var result = new PhotoIngestBatchResult { NextCursor = cursorId.ToString(CultureInfo.InvariantCulture) };
            if (string.IsNullOrWhiteSpace(options.ThumbCacheDir))
                throw new InvalidOperationException("Google-only thumbs need PhotosThumbCacheDir (or --thumb-cache).");

            using var db = dbFactory();
            var page = await db.PhotoGoogleItems
                .Where(i => (i.Status == PhotoGoogleItemStatus.Unmatched || i.Status == PhotoGoogleItemStatus.Ignored)
                            && i.Id > cursorId)
                .OrderBy(i => i.Id)
                .Take(Math.Max(1, options.BatchSize))
                .ToListAsync(cancel);

            if (page.Count == 0) return result;

            long bytesRead = 0;
            foreach (var row in page)
            {
                cancel.ThrowIfCancellationRequested();
                result.Processed++;
                result.NextCursor = row.Id.ToString(CultureInfo.InvariantCulture);

                var key = GoogleThumbKey(row);
                var wanted = PhotoThumbCache.GoogleVariants;
                if (wanted.All(size => File.Exists(GoogleThumbPath(row.Id, key, size))))
                {
                    result.Add("already");
                    continue;
                }

                var full = ArchivePath(row);
                if (full == null || !File.Exists(full)) { result.Add("archive-file-missing"); continue; }
                if (!PhotoFileKinds.IsDecodable(Path.GetExtension(full))) { result.Add("undecodable"); continue; }
                if (options.DryRun) { result.Add("would-write"); continue; }

                try
                {
                    using var image = LoadOriented(full);
                    foreach (var size in wanted) WriteDerivative(image, GoogleThumbPath(row.Id, key, size), size);
                    bytesRead += row.SizeBytes ?? 0;
                    result.Add("thumbs");
                }
                catch (Exception e) when (e is IOException || e is ImageFormatException || e is NotSupportedException
                                          || e is InvalidOperationException)
                {
                    log($"  thumb failed for {row.TakeoutRelativePath}: {e.Message}");
                    result.Add("thumb-errors");
                }

                if (bytesRead >= options.MaxBatchBytes) break;
            }

            var lastId = ParseIdCursor(result.NextCursor);
            result.Remaining = await db.PhotoGoogleItems
                .CountAsync(i => (i.Status == PhotoGoogleItemStatus.Unmatched || i.Status == PhotoGoogleItemStatus.Ignored)
                                 && i.Id > lastId, cancel);
            return result;
        }

        /// <summary>The derivative key for a Google-only item. Derived from the identity triple's own
        /// two measurable halves, so it changes if and only if the archive is presenting different
        /// bytes — the same contract <see cref="PhotoThumbCache.KeyFor"/> gives an asset.</summary>
        public static string GoogleThumbKey(PhotoGoogleItem row) =>
            PhotoThumbCache.KeyFor(null, row.SizeBytes ?? 0,
                row.TakenAtUtc ?? new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        private string GoogleThumbPath(int itemId, string key, string size) =>
            Path.Combine(options.ThumbCacheDir!,
                PhotoThumbCache.GoogleRelativePath(itemId, key, size).Replace('/', Path.DirectorySeparatorChar));

        // ── Pass 4: the download lane (§2.10's one additive NAS write) ───────────────────────────

        /// <summary>
        /// Copies Google-only items into the configured sync directory, foldered by the sidecar's year.
        ///
        /// <para><b>This is the only write to the collection host in the entire vertical</b> (§6), and it
        /// is guarded three ways, all of them refusals rather than warnings:</para>
        /// <list type="number">
        /// <item><b>No <c>PhotosGoogleSyncDir</c>, no run.</b> There is no default and there never will
        /// be one.</item>
        /// <item><b>An undrained archive, no run.</b> While ANY item is still Pending, the match cascade
        /// — including its pHash rung — has not finished ruling that item out, and a half-matched
        /// archive would download photographs the family already owns. §2.10 states this guard
        /// explicitly and it is checked before a single byte moves.</item>
        /// <item><b>An existing destination is never overwritten.</b> A collision is a per-item error:
        /// skipped, counted, and reported. Not an overwrite, not a rename, not a "(1)" — the file that
        /// is already there wins, unconditionally.</item>
        /// </list>
        ///
        /// <para>Ignored items are excluded: "I do not want this one" is an answer, and the lane must
        /// not re-ask it. Downloaded files are NOT special-cased afterwards — the ordinary
        /// <c>photos-ingest</c> walk finds them like any other file on the next run, which is what keeps
        /// this lane a copy rather than a second ingest path.</para>
        /// </summary>
        private async Task<PhotoIngestBatchResult> DownloadBatchAsync(string? cursor, CancellationToken cancel)
        {
            var cursorId = ParseIdCursor(cursor);
            var result = new PhotoIngestBatchResult { NextCursor = cursorId.ToString(CultureInfo.InvariantCulture) };
            using var db = dbFactory();

            var refusal = await DownloadRefusalAsync(db, cancel);
            if (refusal != null)
            {
                log(refusal);
                result.Add("refused");
                return result;
            }

            var syncRoot = Path.GetFullPath(options.SyncDir!);
            var page = await db.PhotoGoogleItems
                .Where(i => i.Status == PhotoGoogleItemStatus.Unmatched && i.Id > cursorId)
                .OrderBy(i => i.Id)
                .Take(Math.Max(1, options.BatchSize))
                .ToListAsync(cancel);

            if (page.Count == 0)
            {
                result.Remaining = 0;
                return result;
            }

            long bytesCopied = 0;
            foreach (var row in page)
            {
                cancel.ThrowIfCancellationRequested();
                result.Processed++;
                result.NextCursor = row.Id.ToString(CultureInfo.InvariantCulture);

                var source = ArchivePath(row);
                if (source == null || !File.Exists(source))
                {
                    log($"  MISSING  {row.TakeoutFileName} — not in the archive at {row.TakeoutRelativePath}");
                    result.Add("archive-file-missing");
                    continue;
                }

                var destination = Path.Combine(syncRoot, YearFolder(row), SafeFileName(row.TakeoutFileName));
                if (File.Exists(destination))
                {
                    // Per-item error, never an overwrite (§2.10).
                    log($"  EXISTS   {row.TakeoutFileName} → {destination} (left untouched)");
                    result.Add("exists-skipped");
                    continue;
                }

                if (options.DryRun)
                {
                    log($"  WOULD    {row.TakeoutFileName} → {destination}");
                    result.Add("would-download");
                    continue;
                }

                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    // overwrite: false is the API-level half of the guard above — the existence check
                    // and the copy are not atomic together, and this is what closes the gap.
                    File.Copy(source, destination, overwrite: false);
                }
                catch (IOException e)
                {
                    log($"  FAILED   {row.TakeoutFileName} → {destination}: {e.Message}");
                    result.Add("copy-errors");
                    continue;
                }
                catch (UnauthorizedAccessException e)
                {
                    log($"  FAILED   {row.TakeoutFileName} → {destination}: {e.Message}");
                    result.Add("copy-errors");
                    continue;
                }

                row.Status = PhotoGoogleItemStatus.Downloaded;
                row.DownloadedPath = Truncate(destination, 850);
                bytesCopied += row.SizeBytes ?? 0;
                log($"  COPIED   {row.TakeoutFileName} → {destination}");
                result.Add("downloaded");

                if (bytesCopied >= options.MaxBatchBytes) break;
            }

            if (!options.DryRun) await db.SaveChangesAsync(cancel);
            var lastId = ParseIdCursor(result.NextCursor);
            result.Remaining = await db.PhotoGoogleItems
                .CountAsync(i => i.Status == PhotoGoogleItemStatus.Unmatched && i.Id > lastId, cancel);
            return result;
        }

        /// <summary>The refusal message, or null when the lane may run. Public shape kept as a string so
        /// the command prints exactly what the engine decided rather than re-deriving it.</summary>
        public async Task<string?> DownloadRefusalAsync(MovieDb db, CancellationToken cancel = default)
        {
            if (string.IsNullOrWhiteSpace(options.SyncDir))
                return "REFUSED: --download needs PhotosGoogleSyncDir (or --sync-dir). There is no default: "
                       + "this is the one lane in the whole vertical that writes to the collection host.";

            var pending = await db.PhotoGoogleItems.CountAsync(i => i.Status == PhotoGoogleItemStatus.Pending, cancel);
            if (pending > 0)
                return $"REFUSED: {pending} archive item(s) have not been through the match pass yet. "
                       + "Downloading now would fetch photos the library already holds (§2.10). "
                       + "Run --pass match to drain them first.";

            return null;
        }

        /// <summary>Which dated folder an item lands in. The sidecar's UTC instant converted to the home
        /// zone's wall clock (§2.7) — never the raw UTC year, which would file a New Year's Eve photo
        /// under the following year. No date at all lands in <c>undated</c>, which is the same honest
        /// shelf the timeline gives an undated photo rather than a guessed year.</summary>
        private string YearFolder(PhotoGoogleItem row) =>
            row.TakenAtUtc is DateTime utc
                ? PhotoDates.ToWallClock(utc, homeZone).Year.ToString(CultureInfo.InvariantCulture)
                : "undated";

        /// <summary>The archive's own name, with anything a file system would refuse replaced. Never a
        /// rename for uniqueness: a collision is refused above, not worked around.</summary>
        private static string SafeFileName(string name)
        {
            var cleaned = new string(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c).ToArray());
            return cleaned.Length == 0 ? "item" : cleaned;
        }

        // ── Shared helpers ───────────────────────────────────────────────────────────────────────

        private async Task EnsureLocalIndexAsync(MovieDb db, CancellationToken cancel)
        {
            if (localIndex != null) return;
            localIndex = await PhotoGoogleLocalIndex.BuildAsync(db, options.PHashDistance, cancel);
            log($"local index: {localIndex.Count} asset(s), {localIndex.HashedCount} with a pHash "
                + $"(threshold {options.PHashDistance} bits)");
        }

        private string? ArchivePath(PhotoGoogleItem row)
        {
            if (string.IsNullOrEmpty(row.TakeoutRelativePath) || archiveRoot.Length == 0) return null;
            return PhotoPathConfinement.Resolve(archiveRoot,
                row.TakeoutRelativePath!.Replace('\\', '/'));
        }

        private static PhotoGoogleSidecarData? ParseSidecar(string? json) =>
            string.IsNullOrWhiteSpace(json) ? null : PhotoGoogleSidecar.ParseJson(json!);

        private static int ParseIdCursor(string? cursor) =>
            int.TryParse(cursor, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 0;

        private static Image<Rgba32> LoadOriented(string fullPath)
        {
            using var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var image = Image.Load<Rgba32>(stream);
            image.Mutate(x => x.AutoOrient());
            return image;
        }

        /// <summary>The ingest's derivative writer, applied to a destination this class chose. Same
        /// never-upscale rule, same temp-then-move so a killed pass cannot leave a truncated WebP at a
        /// cache path where nothing downstream could tell it from a whole one.</summary>
        private static void WriteDerivative(Image<Rgba32> source, string destination, string size)
        {
            var maxEdge = PhotoThumbCache.MaxEdgeFor(size);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);

            var scale = Math.Min(1.0, (double)maxEdge / Math.Max(source.Width, source.Height));
            var width = Math.Max(1, (int)Math.Round(source.Width * scale));
            var height = Math.Max(1, (int)Math.Round(source.Height * scale));

            using var resized = source.Clone(ctx => ctx.Resize(width, height, KnownResamplers.Lanczos3));
            var temp = destination + "." + Guid.NewGuid().ToString("N") + ".part";
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                resized.SaveAsWebp(stream, new WebpEncoder { Quality = size == PhotoStreamRoutes.SizeGrid ? 78 : 84 });
            File.Move(temp, destination, overwrite: true);
        }

        private static string? Truncate(string? value, int max) =>
            string.IsNullOrEmpty(value) ? null : (value!.Length <= max ? value : value.Substring(0, max));
    }

    /// <summary>
    /// The local library, projected once per run into exactly the three lookups §2.10's cascade needs.
    /// See <see cref="PhotoGoogleMesh"/> for the cost note — this is the same up-front-index shape the
    /// near lane and the Immich lane already pay for, and for the same reason.
    /// </summary>
    public sealed class PhotoGoogleLocalIndex
    {
        private readonly Dictionary<string, List<int>> byNameSize = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, List<int>> bySha = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
        private readonly PhotoHashIndex hashes;

        private PhotoGoogleLocalIndex(int threshold) => hashes = new PhotoHashIndex(threshold);

        public int Count { get; private set; }

        public int HashedCount => hashes.Count;

        public static async Task<PhotoGoogleLocalIndex> BuildAsync(MovieDb db, int threshold, CancellationToken cancel)
        {
            var index = new PhotoGoogleLocalIndex(Math.Clamp(threshold, 0, 32));
            // Missing rows are excluded: matching an archive item to a file the walk stopped finding
            // would mark it as owned while nothing on disk backs that up.
            var rows = await db.PhotoAssets
                .Where(a => a.MissingSinceUtc == null)
                .Select(a => new { a.Id, a.Path, a.SizeBytes, a.Sha256, a.PHash })
                .ToListAsync(cancel);

            foreach (var row in rows)
            {
                index.Count++;
                var slash = row.Path.LastIndexOf('/');
                var name = slash >= 0 ? row.Path.Substring(slash + 1) : row.Path;
                Append(index.byNameSize, NameSizeKey(name, row.SizeBytes), row.Id);
                if (!string.IsNullOrEmpty(row.Sha256)) Append(index.bySha, row.Sha256!, row.Id);
                if (row.PHash is long phash) index.hashes.Add(row.Id, phash);
            }

            // Deterministic order inside every bucket, so an ambiguous match resolves the same way on
            // every run and on every host.
            foreach (var list in index.byNameSize.Values) list.Sort();
            foreach (var list in index.bySha.Values) list.Sort();
            return index;
        }

        public IReadOnlyList<int> ByNameAndSize(string fileName, long? size)
        {
            if (size == null) return Array.Empty<int>();
            return byNameSize.TryGetValue(NameSizeKey(fileName, size.Value), out var ids)
                ? ids
                : (IReadOnlyList<int>)Array.Empty<int>();
        }

        public IReadOnlyList<int> BySha(string sha256) =>
            bySha.TryGetValue(sha256, out var ids) ? ids : (IReadOnlyList<int>)Array.Empty<int>();

        public List<PhotoHashNeighbour> NearestByPHash(long phash) => hashes.Query(phash);

        private static string NameSizeKey(string fileName, long size) =>
            fileName + "|" + size.ToString(CultureInfo.InvariantCulture);

        private static void Append(Dictionary<string, List<int>> map, string key, int id)
        {
            if (!map.TryGetValue(key, out var list)) map[key] = list = new List<int>();
            list.Add(id);
        }
    }
}
