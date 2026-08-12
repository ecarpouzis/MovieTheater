using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
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
    /// <summary>Which resumable queue a run is draining (photos-plan.md §2.5).</summary>
    public enum PhotoIngestPass
    {
        Walk,
        Metadata,
        Hash,
        Thumb,

        /// <summary>
        /// Videos: duration, display dimensions, container date and a poster frame (§2.3, Phase 5).
        ///
        /// <para>Its own pass rather than a branch inside <see cref="Metadata"/>/<see cref="Thumb"/>
        /// because it needs a capability those passes do not — external binaries on the host — and a
        /// host without them must still be able to drain the photo queues. It fills BOTH halves for a
        /// video (the readout and the derivatives) in one visit, since both come from the same two
        /// invocations against one file: reading a 4 GB clip off the NAS twice to answer two questions
        /// asked a second apart is the cost this collapses.</para>
        /// </summary>
        Video,
    }

    /// <summary>One bounded unit of work's outcome — the shape the standing bulk-job rule requires
    /// after EVERY chunk, so a driver can accumulate totals and a human can watch it advance.</summary>
    public sealed class PhotoIngestBatchResult
    {
        public int Processed;
        public int Remaining;
        public string NextCursor = "";
        public readonly Dictionary<string, int> Counts = new Dictionary<string, int>();

        public void Add(string key, int n = 1)
        {
            if (n == 0) return;
            Counts[key] = (Counts.TryGetValue(key, out var v) ? v : 0) + n;
        }

        public string CountsText() =>
            string.Join(", ", Counts.Where(kv => kv.Value != 0).OrderBy(kv => kv.Key).Select(kv => $"{kv.Key}: {kv.Value}"));
    }

    public sealed class PhotoIngestOptions
    {
        /// <summary>Absolute collection root as this host mounts it. Never hardcoded (§6) — it comes
        /// from <c>PhotosLibraryDir</c> or <c>--root</c>.</summary>
        public string Root = "";

        /// <summary>Absolute derivative cache directory (§2.2). Required only by the thumb pass.</summary>
        public string? ThumbCacheDir;

        /// <summary>Where the ambiguous-pairing review artifact is written (§2.5). Never the NAS.</summary>
        public string? ReportDir;

        public string? HomeTimeZone;

        /// <summary>Directories per walk batch; rows per queue batch.</summary>
        public int BatchSize = 50;

        /// <summary>Second bound for the byte-heavy passes: a batch stops once it has read this much,
        /// even if it has not filled <see cref="BatchSize"/>. Row counts are a poor bound when one row
        /// can be a 4 GB video and the next a 40 KB thumbnail.</summary>
        public long MaxBatchBytes = 2L * 1024 * 1024 * 1024;

        /// <summary>Writes nothing. Walk only — the other passes have nothing to show without writing.</summary>
        public bool DryRun;

        /// <summary>Re-queue rows a previous run stamped with an error. Off by default so a permanently
        /// unreadable file cannot turn a queue into an infinite retry.</summary>
        public bool RetryErrors;

        /// <summary>One marker per invocation (the ReviewBatch convention, §2.5): every row born in
        /// this run carries it, so a bulk insert stays reviewable and quarantinable.</summary>
        public string IngestBatch = "";

        /// <summary>
        /// The ffprobe/ffmpeg seam the <see cref="PhotoIngestPass.Video"/> pass runs on (§2.3). Null,
        /// or an implementation reporting <see cref="IPhotoVideoTools.Available"/> false, means the pass
        /// says so and changes nothing — a host with no binaries is a normal host, and the videos stay
        /// in <see cref="PhotoThumbState.VideoDeferred"/> exactly as Phase 1 left them.
        /// </summary>
        public IPhotoVideoTools? VideoTools;
    }

    /// <summary>
    /// The <c>photos-ingest</c> engine (photos-plan.md §2.5): four independent, resumable, read-only
    /// queues over the family collection. Deliberately free of CliFx and ASP.NET so the whole pipeline
    /// can be exercised against a generated fixture tree in the test suite — the NAS is never read by a
    /// build or a test, and the first real run is a human-supervised checkpoint.
    ///
    /// <para><b>Every pass obeys the same contract</b> (the standing bulk-job rule): a bounded amount
    /// of work per call, <c>{processed, remaining, nextCursor}</c> printed after each batch, idempotent
    /// resume from the cursor, and a deterministic stop — a row that fails is stamped with its error
    /// and leaves the queue, so "drain until remaining is 0" terminates instead of retrying a corrupt
    /// file forever.</para>
    ///
    /// <para><b>Nothing under the collection root is ever written, renamed, moved or deleted</b> (§6).
    /// Every read opens <c>FileAccess.Read</c> / <c>FileShare.ReadWrite</c>; existence is checked with
    /// literal APIs, never a pattern (the <c>[ ]</c> wildcard trap is real in this tree). A vanished
    /// file is a <c>MissingSinceUtc</c> stamp, never a DELETE.</para>
    /// </summary>
    public sealed class PhotoIngestPipeline
    {
        private readonly Func<MovieDb> dbFactory;
        private readonly PhotoIngestOptions options;
        private readonly Action<string> log;
        private readonly TimeZoneInfo homeZone;
        private readonly string root;

        public PhotoIngestPipeline(Func<MovieDb> dbFactory, PhotoIngestOptions options, Action<string> log)
        {
            this.dbFactory = dbFactory;
            this.options = options;
            this.log = log;
            homeZone = PhotoDates.ResolveHomeZone(options.HomeTimeZone);
            root = Path.GetFullPath(options.Root);
        }

        // ── Driver ───────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Runs up to <paramref name="maxBatches"/> bounded batches of one pass, printing the required
        /// per-chunk line after each. <paramref name="maxBatches"/> 0 drains the queue, guarded by a
        /// no-progress break: a batch that processes nothing while claiming work remains is a bug, and
        /// the loop must stop rather than spin on it.
        /// </summary>
        public async Task<PhotoIngestBatchResult> RunAsync(PhotoIngestPass pass, string? cursor, int maxBatches)
        {
            var total = new PhotoIngestBatchResult { NextCursor = cursor ?? "" };
            var batches = 0;
            while (maxBatches <= 0 || batches < maxBatches)
            {
                var result = pass == PhotoIngestPass.Walk
                    ? await WalkBatchAsync(batches == 0 ? cursor : total.NextCursor)
                    : await QueueBatchAsync(pass, ParseIdCursor(batches == 0 ? cursor : total.NextCursor));

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
                    // Deterministic stop condition. Reaching here means the queue claims rows but the
                    // batch moved none of them — report it loudly rather than looping.
                    log("No progress in a batch while work remained — stopping. Re-run with --retry-errors if rows are error-stamped.");
                    break;
                }
            }
            return total;
        }

        private static int ParseIdCursor(string? cursor) =>
            int.TryParse(cursor, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 0;

        // ── Pass 1: the inventory walk (§2.5 phase 1) ────────────────────────────────────────────

        /// <summary>
        /// One bounded slice of the inventory walk: <c>BatchSize</c> DIRECTORIES, in
        /// <see cref="PhotoWalkCursor"/> order, resuming strictly after <paramref name="cursor"/>.
        ///
        /// <para><b>Cursor-ordering audit (§6).</b> The candidate list is sorted with
        /// <see cref="PhotoWalkCursor.Comparer"/> and filtered with <see cref="PhotoWalkCursor.IsAfter"/>,
        /// which are defined in terms of the SAME key function — so the order the batch pages in and the
        /// order the cursor advances through are the same order by construction, not by coincidence.
        /// Nothing here orders by a database column: the walk's unit of work is a directory on disk, and
        /// its "done" mark is that directory's path. <c>remaining</c> is an INDEPENDENT count (pending
        /// directories minus this batch) rather than a decrement of a running total, so a drift between
        /// the two would show up as a stalled or negative number instead of a silent early "done".</para>
        ///
        /// <para>Only directories are enumerated to build the list — no file listing, no stat, no read —
        /// which is what keeps re-running the walk cheap enough to BE the new-photo discovery mechanism
        /// (§2.5).</para>
        /// </summary>
        public async Task<PhotoIngestBatchResult> WalkBatchAsync(string? cursor)
        {
            var result = new PhotoIngestBatchResult { NextCursor = cursor ?? "" };
            if (!System.IO.Directory.Exists(root))
                throw new DirectoryNotFoundException($"Photo collection root not found: {root}");

            var all = new List<string> { "" }; // the root itself holds loose files (§1)
            all.AddRange(System.IO.Directory
                .EnumerateDirectories(root, "*", SearchOption.AllDirectories)
                .Select(ToRelative));
            all.Sort(PhotoWalkCursor.Comparer);

            var pending = all.Where(d => PhotoWalkCursor.IsAfter(d, cursor)).ToList();
            var batch = pending.Take(Math.Max(1, options.BatchSize)).ToList();
            if (batch.Count == 0)
            {
                result.Remaining = 0;
                return result;
            }

            using var db = dbFactory();
            var newCandidates = new List<Candidate>();
            var now = DateTime.UtcNow;

            foreach (var relativeDir in batch)
            {
                var fullDir = relativeDir.Length == 0 ? root : Path.Combine(root, relativeDir.Replace('/', Path.DirectorySeparatorChar));
                List<string> files;
                try
                {
                    files = System.IO.Directory.EnumerateFiles(fullDir).ToList();
                }
                catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
                {
                    // An unreadable directory must not abort the walk — it is one folder, and the
                    // cursor has to keep moving or the whole pass wedges on it.
                    log($"  ! unreadable directory {relativeDir}: {e.Message}");
                    result.Add("unreadable-dirs");
                    continue;
                }

                var onDisk = new Dictionary<string, FileInfo>(StringComparer.OrdinalIgnoreCase);
                foreach (var file in files)
                {
                    var extension = Path.GetExtension(file);
                    if (!PhotoFileKinds.TryClassify(extension, out _))
                    {
                        if (!PhotoFileKinds.IsIgnored(extension)) result.Add("unknown-extension");
                        continue;
                    }
                    onDisk[ToRelative(file)] = new FileInfo(file);
                }

                var existing = await RowsDirectlyIn(db, relativeDir);
                foreach (var row in existing)
                {
                    if (onDisk.TryGetValue(row.Path, out var info))
                    {
                        onDisk.Remove(row.Path);
                        // Unchanged (path + size + mtime) short-circuits: this is what makes a re-walk
                        // of an already-ingested tree cost a directory listing and nothing else.
                        if (row.SizeBytes == info.Length && row.FileModifiedUtc == info.LastWriteTimeUtc)
                        {
                            if (row.MissingSinceUtc != null)
                            {
                                row.MissingSinceUtc = null;
                                result.Add("reappeared");
                            }
                            else result.Add("unchanged");
                            continue;
                        }

                        // The bytes changed: re-queue every derived pass rather than trusting stale
                        // EXIF, hashes or thumbs that describe the previous file.
                        row.SizeBytes = info.Length;
                        row.FileModifiedUtc = info.LastWriteTimeUtc;
                        row.MissingSinceUtc = null;
                        row.MetadataUpdatedUtc = null;
                        row.HashUpdatedUtc = null;
                        row.ThumbsUpdatedUtc = null;
                        row.ThumbState = PhotoThumbState.Pending;
                        row.IngestError = null;
                        result.Add("changed");
                        continue;
                    }

                    if (row.MissingSinceUtc == null)
                    {
                        row.MissingSinceUtc = now;
                        result.Add("went-missing");
                    }
                }

                foreach (var kv in onDisk)
                    newCandidates.Add(new Candidate(kv.Key, kv.Value));
            }

            await ResolveNewCandidatesAsync(db, newCandidates, now, result);

            if (!options.DryRun) await db.SaveChangesAsync();

            result.Processed = batch.Count;
            result.Remaining = pending.Count - batch.Count;
            result.NextCursor = batch[batch.Count - 1];
            return result;
        }

        private sealed class Candidate
        {
            public readonly string RelativePath;
            public readonly FileInfo Info;
            public Candidate(string relativePath, FileInfo info) { RelativePath = relativePath; Info = info; }
            public string FileName => Path.GetFileName(RelativePath);
        }

        /// <summary>
        /// The §2.5 identity re-pair, and the only place a row is ever born.
        ///
        /// <para><b>Content is identity, path is location.</b> Years of tags, dates, album entries and
        /// master picks hang off these ids, so a folder reorganization on the NAS must re-point an
        /// EXISTING row rather than orphan it and start a new one. Before hashes exist the matcher is
        /// filename + size — the same fingerprint <c>detect-fs-drift</c> earned the hard way — and where
        /// both sides already carry a SHA-256 the hashes must agree as well, so a later run cannot
        /// staple two same-named, same-sized but different files together.</para>
        ///
        /// <para>A move is detected in EITHER direction. The obvious case is source-first: the row was
        /// already stamped <c>MissingSinceUtc</c> by this batch or an earlier one. The other case —
        /// the destination directory sorting BEFORE the source, so the new path is seen while the old
        /// row still looks present — is caught by testing that candidate's same-name/same-size rows for
        /// a file that is no longer on disk. Without that test, half of all moves (whichever way the
        /// ordering fell) would silently become a new row plus an orphan.</para>
        ///
        /// <para><b>Ambiguity is never auto-applied.</b> N old paths matching M new paths goes to the
        /// review artifact and NOTHING is written for those candidates — no re-point, and no new row
        /// either, because §2.5 says a row is born only when no missing row matches its content. The
        /// same ambiguity re-reports on every run and changes nothing until a human resolves it, which
        /// is the intended stable state rather than a failure to converge.</para>
        /// </summary>
        private async Task ResolveNewCandidatesAsync(MovieDb db, List<Candidate> candidates, DateTime now, PhotoIngestBatchResult result)
        {
            if (candidates.Count == 0) return;

            // Bounded by this batch: only rows whose size some candidate could match are loaded.
            var sizes = candidates.Select(c => c.Info.Length).Distinct().ToList();
            var sameSize = await db.PhotoAssets
                .Where(a => sizes.Contains(a.SizeBytes))
                .ToListAsync();

            var byKey = new Dictionary<string, List<PhotoAsset>>(StringComparer.OrdinalIgnoreCase);
            foreach (var row in sameSize)
            {
                var key = MatchKey(Path.GetFileName(row.Path), row.SizeBytes);
                if (!byKey.TryGetValue(key, out var list)) byKey[key] = list = new List<PhotoAsset>();
                list.Add(row);
            }

            var repairs = new List<object>();
            foreach (var group in candidates.GroupBy(c => MatchKey(c.FileName, c.Info.Length), StringComparer.OrdinalIgnoreCase))
            {
                var newOnes = group.ToList();
                var sources = byKey.TryGetValue(group.Key, out var rows)
                    ? rows.Where(r => r.MissingSinceUtc != null || !File.Exists(FullPath(r.Path))).ToList()
                    : new List<PhotoAsset>();

                if (sources.Count == 0)
                {
                    foreach (var candidate in newOnes) InsertRow(db, candidate, now, result);
                    continue;
                }

                if (sources.Count > 1 || newOnes.Count > 1)
                {
                    result.Add("ambiguous-pairings", newOnes.Count);
                    repairs.Add(new
                    {
                        matchKey = group.Key,
                        oldPaths = sources.Select(s => s.Path).OrderBy(p => p, StringComparer.Ordinal).ToList(),
                        newPaths = newOnes.Select(c => c.RelativePath).OrderBy(p => p, StringComparer.Ordinal).ToList(),
                        note = "Same filename+size at several old and/or new paths — not auto-applied (§2.5).",
                    });
                    continue;
                }

                var source = sources[0];
                var target = newOnes[0];
                if (source.Sha256 != null)
                {
                    // Both sides hashed and disagreeing means these are different files that merely
                    // share a name and a length. The new one is genuinely new.
                    var candidateHash = SafeSha256(target);
                    if (candidateHash != null && !string.Equals(candidateHash, source.Sha256, StringComparison.OrdinalIgnoreCase))
                    {
                        InsertRow(db, target, now, result);
                        result.Add("hash-rejected-pairings");
                        continue;
                    }
                }

                log($"  ~ moved: {source.Path} -> {target.RelativePath}");
                source.Path = target.RelativePath;
                source.SizeBytes = target.Info.Length;
                source.FileModifiedUtc = target.Info.LastWriteTimeUtc;
                source.MissingSinceUtc = null;
                result.Add("re-paired");
            }

            if (repairs.Count > 0) WriteRepairReport(repairs);
        }

        private void InsertRow(MovieDb db, Candidate candidate, DateTime now, PhotoIngestBatchResult result)
        {
            var extension = Path.GetExtension(candidate.RelativePath);
            PhotoFileKinds.TryClassify(extension, out var kind);
            var row = new PhotoAsset
            {
                Path = candidate.RelativePath,
                SizeBytes = candidate.Info.Length,
                FileModifiedUtc = candidate.Info.LastWriteTimeUtc,
                Kind = kind,
                OriginalRenderable = kind == PhotoAssetKind.Photo && PhotoFileKinds.IsBrowserRenderable(extension),
                TakenAtSource = TakenAtSource.Unknown,
                FirstSeenUtc = now,
                IngestBatch = options.IngestBatch,
            };
            if (!options.DryRun) db.PhotoAssets.Add(row);
            result.Add("inserted");
        }

        /// <summary>Hash of a candidate file, or null if it cannot be read — an unreadable file must
        /// not abort a pairing decision, it just means the hash cannot cast its vote.</summary>
        private string? SafeSha256(Candidate candidate)
        {
            try { return PhotoHashes.Sha256File(candidate.Info.FullName); }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
        }

        private static string MatchKey(string fileName, long size) =>
            fileName.ToLowerInvariant() + "|" + size.ToString(CultureInfo.InvariantCulture);

        /// <summary>
        /// Rows whose file sits DIRECTLY in this directory — not in a subdirectory. Expressed as a
        /// prefix match plus "no further separator" so one query per level answers it; materializing
        /// the whole table to walk a tree would defeat the point of chunking.
        /// </summary>
        private static Task<List<PhotoAsset>> RowsDirectlyIn(MovieDb db, string relativeDir)
        {
            if (relativeDir.Length == 0)
                return db.PhotoAssets.Where(a => !a.Path.Contains("/")).ToListAsync();

            var prefix = relativeDir + "/";
            return db.PhotoAssets
                .Where(a => a.Path.StartsWith(prefix) && !a.Path.Substring(prefix.Length).Contains("/"))
                .ToListAsync();
        }

        private void WriteRepairReport(List<object> repairs)
        {
            var dir = options.ReportDir;
            if (string.IsNullOrWhiteSpace(dir)) return;
            try
            {
                System.IO.Directory.CreateDirectory(dir!);
                var file = Path.Combine(dir!, $"path-repair-{Sanitize(options.IngestBatch)}.json");
                var existing = new List<JsonElement>();
                if (File.Exists(file))
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(file));
                    existing.AddRange(doc.RootElement.EnumerateArray().Select(e => e.Clone()));
                }
                var merged = existing.Select(e => (object)e).Concat(repairs).ToList();
                File.WriteAllText(file, JsonSerializer.Serialize(merged, new JsonSerializerOptions { WriteIndented = true }));
                log($"  ! {repairs.Count} ambiguous pairing(s) recorded for review: {file}");
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is JsonException)
            {
                // The report is a courtesy; the console lines above are the record of last resort.
                log($"  ! could not write the path-repair report: {e.Message}");
            }
        }

        private static string Sanitize(string value)
        {
            var chars = value.ToCharArray();
            for (var i = 0; i < chars.Length; i++)
                if (Array.IndexOf(Path.GetInvalidFileNameChars(), chars[i]) >= 0) chars[i] = '_';
            return new string(chars);
        }

        // ── Passes 2–4: the row queues (§2.5 phases 2–4) ─────────────────────────────────────────

        /// <summary>
        /// One bounded batch of a row queue.
        ///
        /// <para><b>Cursor-ordering audit (§6).</b> The queue predicate is "this pass has not stamped
        /// the row", the page is <c>WHERE Id &gt; cursor ORDER BY Id</c>, and the cursor is the last Id
        /// processed — one column, one direction, in both places. A processed row also LEAVES the
        /// predicate (the stamp is written on failure too, with the reason), so the queue is
        /// self-draining and <c>remaining</c> is re-counted from the database each batch rather than
        /// decremented, which is what makes an early "done" impossible to fake.</para>
        /// </summary>
        public async Task<PhotoIngestBatchResult> QueueBatchAsync(PhotoIngestPass pass, int cursorId)
        {
            var result = new PhotoIngestBatchResult { NextCursor = cursorId.ToString(CultureInfo.InvariantCulture) };
            using var db = dbFactory();

            var queue = Queue(db, pass);
            var rows = await queue.Where(a => a.Id > cursorId).OrderBy(a => a.Id)
                .Take(Math.Max(1, options.BatchSize)).ToListAsync();

            long bytes = 0;
            var processed = 0;
            foreach (var row in rows)
            {
                switch (pass)
                {
                    case PhotoIngestPass.Metadata: RunMetadata(row, result); break;
                    case PhotoIngestPass.Hash: RunHash(row, result); break;
                    case PhotoIngestPass.Thumb: RunThumb(row, result); break;
                    case PhotoIngestPass.Video: RunVideo(row, result); break;
                    default: throw new ArgumentOutOfRangeException(nameof(pass));
                }
                processed++;
                result.NextCursor = row.Id.ToString(CultureInfo.InvariantCulture);
                bytes += row.SizeBytes;
                // The second bound: one 4 GB video is not the same unit of work as one 40 KB snapshot,
                // and a row-count-only cap would make a batch's duration unpredictable by three orders
                // of magnitude. At least one row always runs, so the queue can never wedge on a file
                // larger than the cap.
                if (bytes >= options.MaxBatchBytes) break;
            }

            await db.SaveChangesAsync();

            result.Processed = processed;
            // Counted independently, after the writes: the stamped rows have left the queue, so this is
            // the true outstanding total rather than an accumulator that could drift out of step.
            result.Remaining = await Queue(db, pass).CountAsync();
            return result;
        }

        /// <summary>
        /// The queue predicates.
        ///
        /// <para>Missing files are excluded from all three: a row whose file the walk cannot find has
        /// nothing to read, and leaving it in would make every pass retry it forever.</para>
        ///
        /// <para><b>The queues are INDEPENDENT.</b> A row is in a queue purely because that pass has not
        /// stamped it — an EXIF read that failed must not also stop the file being hashed or
        /// thumbnailed, which are different reads answering different questions. The failure is recorded
        /// (<c>IngestError</c>) and the row leaves THAT queue only. <c>--retry-errors</c> pulls
        /// error-stamped rows back into every queue: a deliberate re-run after the cause was fixed, not
        /// a default, because a default would turn a permanently corrupt file into an infinite retry.</para>
        /// </summary>
        private IQueryable<PhotoAsset> Queue(MovieDb db, PhotoIngestPass pass)
        {
            var rows = db.PhotoAssets.Where(a => a.MissingSinceUtc == null);
            var retry = options.RetryErrors;
            switch (pass)
            {
                case PhotoIngestPass.Metadata:
                    return rows.Where(a => a.MetadataUpdatedUtc == null || (retry && a.IngestError != null));
                case PhotoIngestPass.Hash:
                    return rows.Where(a => a.HashUpdatedUtc == null || (retry && a.IngestError != null));
                case PhotoIngestPass.Thumb:
                    return rows.Where(a => a.ThumbsUpdatedUtc == null || (retry && a.IngestError != null));
                case PhotoIngestPass.Video:
                    // Keyed on ThumbState rather than a timestamp: the Phase 1 passes already stamped
                    // MetadataUpdatedUtc and ThumbsUpdatedUtc on every video before this pass existed,
                    // so a timestamp predicate would find an empty queue on exactly the collection this
                    // is for. VideoDeferred is the state Phase 1 deliberately left them in, and a row
                    // leaves the queue by reaching Ready or Failed — self-draining either way.
                    return rows.Where(a => a.Kind == PhotoAssetKind.Video
                                           && (a.ThumbState == PhotoThumbState.VideoDeferred
                                               || a.ThumbState == PhotoThumbState.Pending
                                               || (retry && a.ThumbState == PhotoThumbState.Failed)));
                default: throw new ArgumentOutOfRangeException(nameof(pass));
            }
        }

        /// <summary>
        /// One column carries the last ingest failure, so a pass may only clear an error IT wrote —
        /// otherwise the hash pass succeeding would erase the metadata pass's complaint about the same
        /// file, and the admin readout would show a clean row that is quietly missing its EXIF.
        /// </summary>
        private static void ClearError(PhotoAsset row, string pass)
        {
            if (row.IngestError != null && row.IngestError.StartsWith(pass + ":", StringComparison.Ordinal))
                row.IngestError = null;
        }

        private static void SetError(PhotoAsset row, string pass, string message) =>
            row.IngestError = Truncate($"{pass}: {message}", 512);

        // ── Pass 2: metadata (§2.5 phase 2 + §2.7) ───────────────────────────────────────────────

        private void RunMetadata(PhotoAsset row, PhotoIngestBatchResult result)
        {
            row.MetadataUpdatedUtc = DateTime.UtcNow;
            ClearError(row, "metadata");

            if (row.Kind == PhotoAssetKind.Video)
            {
                // Phase 1 carries videos as skeleton rows (§5 Phase 1 vs Phase 5): no ffprobe, so no
                // dimensions, duration or container timestamps. The filename/folder date rules still
                // apply — they cost nothing and a phone video is named exactly like a phone photo.
                ApplyNameAndFolderDates(row, result);
                result.Add("videos-deferred");
                return;
            }

            var full = FullPath(row.Path);
            PhotoMetadataReader.Result meta;
            try
            {
                meta = PhotoMetadataReader.Read(full);
            }
            catch (Exception e)
            {
                SetError(row, "metadata", e.Message);
                result.Add("metadata-errors");
                // Still date it from its name: a container this build cannot parse says nothing about
                // whether the filename carries a stamp.
                ApplyNameAndFolderDates(row, result);
                return;
            }

            row.RawMetadataJson = meta.RawJson;
            row.CameraMake = Truncate(meta.CameraMake, 128);
            row.CameraModel = Truncate(meta.CameraModel, 128);
            row.GpsLat = meta.GpsLat;
            row.GpsLon = meta.GpsLon;

            // Dimensions the pipeline stores are DISPLAY dimensions (§2.2): the stored pixels with the
            // EXIF orientation applied. The justified grid lays out from these, and the derivatives are
            // auto-oriented to match, so the two cannot disagree about which way a photo is up.
            if (meta.Width == null && PhotoFileKinds.IsDecodable(Path.GetExtension(row.Path)))
            {
                try
                {
                    var info = Image.Identify(full);
                    meta.Width = info.Width;
                    meta.Height = info.Height;
                }
                catch (Exception e) when (e is IOException || e is UnknownImageFormatException || e is InvalidImageContentException)
                {
                    // Dimensions stay null; the row is still catalogued.
                }
            }
            PhotoMetadataReader.ApplyOrientation(meta);
            row.Width = meta.Width;
            row.Height = meta.Height;

            var isScan = PhotoDates.LooksLikeScan(row.Path, meta.CameraMake, meta.CameraModel);
            if (isScan) result.Add("scan-exif-distrusted");

            if (!isScan && meta.ExifTakenAt != null)
            {
                if (!MayDate(row, TakenAtSource.Exif)) { result.Add("date-kept"); return; }
                // EXIF carries no timezone, so it IS the wall clock (§2.7) — no conversion.
                row.TakenAt = meta.ExifTakenAt;
                row.TakenAtUtcRaw = null;
                row.TakenAtSource = TakenAtSource.Exif;
                result.Add("dated-exif");
                return;
            }

            if (!isScan && meta.UtcTakenAt != null)
            {
                if (!MayDate(row, TakenAtSource.Exif)) { result.Add("date-kept"); return; }
                // A TRUE UTC source (GPS date+time). Converted through the home zone and the raw UTC
                // kept beside it so the conversion stays revisitable (§2.7).
                row.TakenAtUtcRaw = meta.UtcTakenAt;
                row.TakenAt = PhotoDates.ToWallClock(meta.UtcTakenAt.Value, homeZone);
                row.TakenAtSource = TakenAtSource.Exif;
                result.Add("dated-gps-utc");
                return;
            }

            ApplyNameAndFolderDates(row, result);
        }

        /// <summary>
        /// The fallback rungs of §2.7's date cascade. A folder year sets BOUNDS only — a year is not a
        /// wall clock, and writing January 1st would pile thousands of photos onto one day, which is
        /// the "scattered at epoch 0" failure the plan forbids wearing a more convincing date.
        ///
        /// <para>Every rung asks <see cref="MayDate"/> first, the last one included: the "no date at
        /// all" branch used to NULL <c>TakenAt</c> and stamp <c>Unknown</c> unconditionally, so a
        /// re-walk of a hand-dated scan — whose filename and folder say nothing, which is exactly why a
        /// human dated it — erased the answer outright.</para>
        /// </summary>
        private static void ApplyNameAndFolderDates(PhotoAsset row, PhotoIngestBatchResult? result = null)
        {
            var parsed = PhotoDates.ParseFromFileName(Path.GetFileNameWithoutExtension(row.Path));
            if (parsed != null)
            {
                if (!MayDate(row, TakenAtSource.FilenameParsed)) { result?.Add("date-kept"); return; }
                row.TakenAt = parsed;
                row.TakenAtSource = TakenAtSource.FilenameParsed;
                row.YearMin = row.YearMax = parsed.Value.Year;
                return;
            }

            var year = PhotoDates.ParseYearFromFolders(row.Path);
            if (year != null)
            {
                if (!MayDate(row, TakenAtSource.FolderInferred)) { result?.Add("date-kept"); return; }
                row.TakenAt = null;
                row.TakenAtSource = TakenAtSource.FolderInferred;
                row.YearMin = row.YearMax = year;
                return;
            }

            if (!MayDate(row, TakenAtSource.Unknown)) { result?.Add("date-kept"); return; }
            row.TakenAt = null;
            row.TakenAtSource = TakenAtSource.Unknown;
        }

        // ── Pass 3: hashes (§2.5 phase 3) ────────────────────────────────────────────────────────

        private void RunHash(PhotoAsset row, PhotoIngestBatchResult result)
        {
            row.HashUpdatedUtc = DateTime.UtcNow;
            ClearError(row, "hash");
            ClearError(row, "phash");
            var full = FullPath(row.Path);

            try
            {
                row.Sha256 = PhotoHashes.Sha256File(full);
                result.Add("sha256");
            }
            catch (Exception e)
            {
                SetError(row, "hash", e.Message);
                result.Add("hash-errors");
                return;
            }

            // Perceptual hashes need decoded pixels. Videos get theirs from a mid-point frame in
            // Phase 5; formats this build has no decoder for never get one, and that is recorded by
            // their null PHash rather than by an error.
            if (row.Kind != PhotoAssetKind.Photo || !PhotoFileKinds.IsDecodable(Path.GetExtension(row.Path)))
                return;

            try
            {
                using var image = LoadOriented(full);
                row.DHash = PhotoHashes.DHash(image);
                row.PHash = PhotoHashes.PHash(image);
                result.Add("perceptual");
            }
            catch (Exception e)
            {
                SetError(row, "phash", e.Message);
                result.Add("phash-errors");
            }
        }

        // ── Pass 4: derivatives (§2.5 phase 4 + §2.2) ────────────────────────────────────────────

        private void RunThumb(PhotoAsset row, PhotoIngestBatchResult result)
        {
            row.ThumbsUpdatedUtc = DateTime.UtcNow;
            ClearError(row, "thumb");

            if (row.Kind == PhotoAssetKind.Video)
            {
                // Deferred to the Video pass, which needs binaries this one does not. A poster the
                // video pass has ALREADY written is left alone — re-running the photo thumb queue must
                // not demote a finished video back to a placeholder (the two passes stamp different
                // columns and would otherwise fight over one).
                if (row.ThumbState != PhotoThumbState.Ready)
                {
                    row.ThumbState = PhotoThumbState.VideoDeferred;
                    row.ThumbKey = null;
                    row.ThumbVariants = null;
                }
                result.Add("videos-deferred");
                return;
            }

            var extension = Path.GetExtension(row.Path);
            if (!PhotoFileKinds.IsDecodable(extension))
            {
                row.ThumbState = PhotoThumbState.UnsupportedFormat;
                row.ThumbKey = null;
                row.ThumbVariants = null;
                result.Add("undecodable");
                return;
            }

            if (string.IsNullOrWhiteSpace(options.ThumbCacheDir))
                throw new InvalidOperationException("The thumb pass needs PhotosThumbCacheDir (or --thumb-cache).");

            var key = PhotoThumbCache.KeyFor(row.Sha256, row.SizeBytes, row.FileModifiedUtc);
            var variants = PhotoThumbCache.VariantsFor(row.OriginalRenderable);
            try
            {
                using var image = LoadOriented(FullPath(row.Path));
                foreach (var size in variants) WriteDerivative(image, row.Id, key, size);

                // Written only after every derivative landed, so a half-emitted set is never advertised.
                row.ThumbKey = key;
                row.ThumbVariants = PhotoThumbCache.Join(variants);
                row.ThumbState = PhotoThumbState.Ready;
                // The decode is authoritative about the display size; a container whose EXIF disagreed
                // with its pixels would otherwise leave the grid laying out to the wrong aspect ratio.
                row.Width = image.Width;
                row.Height = image.Height;
                result.Add("thumbs");
            }
            catch (Exception e)
            {
                row.ThumbState = PhotoThumbState.Failed;
                row.ThumbKey = null;
                row.ThumbVariants = null;
                SetError(row, "thumb", e.Message);
                result.Add("thumb-errors");
            }
        }

        // ── Pass 5: videos (§2.3 + §2.5 phase 2's "videos via ffprobe") ──────────────────────────

        /// <summary>Derivatives a video gets. Never <c>zoom</c>: that derivative exists so an original a
        /// browser cannot render still has a deep-zoom target (§2.2), and nobody deep-zooms a poster
        /// frame — the video itself is the "full quality" action here.</summary>
        private static readonly string[] VideoVariants = { PhotoStreamRoutes.SizeGrid, PhotoStreamRoutes.SizeView };

        /// <summary>
        /// One video: what it is, when it was taken, and a frame to show for it (§2.3).
        ///
        /// <para><b>The poster is grabbed at the MIDPOINT</b>, falling back to one second and then to
        /// the first frame. A home video's first frame is a lens cap, a lap, or black — the midpoint is
        /// the only cheap guess that is usually the picture. Every attempt is one fast keyframe seek,
        /// so the fallbacks cost nothing when the first one works.</para>
        ///
        /// <para><b>The container date is a TRUE UTC source</b> and takes §2.7's conversion path:
        /// <c>TakenAtUtcRaw</c> keeps the instant, <c>TakenAt</c> holds the wall clock derived through
        /// the home zone, and the source is stamped <see cref="TakenAtSource.VideoContainer"/> so a
        /// later reader can tell it from EXIF. It does NOT overwrite a date a human set, or one parsed
        /// from the filename by an earlier run that had no better answer — a phone that names a file
        /// with the local date and stamps the container in UTC agrees with itself, and the container is
        /// the more precise of the two.</para>
        /// </summary>
        private void RunVideo(PhotoAsset row, PhotoIngestBatchResult result)
        {
            var tools = options.VideoTools;
            if (tools == null || !tools.Available)
            {
                // Not an error and not a stamp: leaving the row in the queue is correct, because the
                // work genuinely has not been done and a host that later gains ffprobe must find it.
                result.Add("no-video-tools");
                return;
            }

            ClearError(row, "video");
            var full = FullPath(row.Path);

            var info = tools.Probe(full);
            if (info == null)
            {
                row.ThumbState = PhotoThumbState.Failed;
                row.ThumbsUpdatedUtc = DateTime.UtcNow;
                row.MetadataUpdatedUtc ??= DateTime.UtcNow;
                SetError(row, "video", "ffprobe produced no usable answer for this file");
                result.Add("probe-errors");
                return;
            }

            row.MetadataUpdatedUtc = DateTime.UtcNow;
            row.DurationSec = info.DurationSec;
            if (info.Width != null) row.Width = info.Width;
            if (info.Height != null) row.Height = info.Height;
            // The readout is persisted rather than recomputed (§2.5): re-reading a multi-gigabyte file
            // off the NAS to answer "what codec was that" cannot be made cheap.
            if (info.Sections.Count > 0)
                row.RawMetadataJson = JsonSerializer.Serialize(info.Sections);
            if (info.DurationSec != null) result.Add("durations");

            if (info.CreationTimeUtc is DateTime createdUtc && MayDate(row, TakenAtSource.VideoContainer))
            {
                row.TakenAtUtcRaw = createdUtc;
                row.TakenAt = PhotoDates.ToWallClock(createdUtc, homeZone);
                row.TakenAtSource = TakenAtSource.VideoContainer;
                result.Add("dated-container-utc");
            }

            WriteVideoPoster(row, full, info, result);
        }

        /// <summary>
        /// Whether a pass may write <paramref name="candidate"/> over what the row already carries — the
        /// §2.7 source hierarchy, asked once, by every date-writing pass in this file.
        ///
        /// <para><b>The ranking is <see cref="PhotoGoogleMesh.SourceRank"/> and deliberately not a table
        /// of its own.</b> That method already exists because the enum's numeric order is WRONG
        /// (<see cref="TakenAtSource.VideoContainer"/> is 7 only because Phase 5 appended it to a live
        /// int column), and a second copy here would be a second place for the answer to drift. Phase 5
        /// carried a narrower fork of this rule — "not Manual and not Estimated" — that happened to
        /// agree; it is gone, and this is what both callers ask.</para>
        ///
        /// <para><b>Why the metadata pass needs it at all.</b> Re-reading a file's EXIF is not new
        /// information about a date a HUMAN typed. The pass re-runs whenever a file's mtime changes or
        /// <c>--retry-errors</c> is used, and it used to assign <c>TakenAt</c>, <c>TakenAtSource</c>,
        /// <c>YearMin</c> and <c>YearMax</c> unconditionally — so a touched file silently threw away a
        /// hand-set Manual date or a circa range, which is precisely the hand curation §2.11 promises
        /// survives everything. Equal rank still writes: re-reading the same EXIF is a refresh, not a
        /// downgrade.</para>
        /// </summary>
        private static bool MayDate(PhotoAsset row, TakenAtSource candidate) =>
            PhotoGoogleMesh.SourceRank(candidate) >= PhotoGoogleMesh.SourceRank(row.TakenAtSource);

        private void WriteVideoPoster(PhotoAsset row, string full, PhotoVideoInfo info, PhotoIngestBatchResult result)
        {
            if (string.IsNullOrWhiteSpace(options.ThumbCacheDir))
            {
                // The readout landed; the derivatives did not. Left in the queue on purpose — a host
                // that gains a cache directory should finish the job rather than see an empty queue.
                result.Add("no-thumb-cache");
                return;
            }

            var key = PhotoThumbCache.KeyFor(row.Sha256, row.SizeBytes, row.FileModifiedUtc);
            // Written into the CACHE, never beside the original (§6), and under a unique name so two
            // concurrent runs cannot hand each other a half-written frame.
            var scratch = Path.Combine(options.ThumbCacheDir!, "video-frames",
                $"{row.Id}-{Guid.NewGuid():N}.png");

            try
            {
                var grabbed = false;
                foreach (var at in PosterOffsets(info.DurationSec))
                {
                    if (options.VideoTools!.TryGrabFrame(full, at, scratch)) { grabbed = true; break; }
                }

                if (!grabbed)
                {
                    row.ThumbState = PhotoThumbState.Failed;
                    row.ThumbsUpdatedUtc = DateTime.UtcNow;
                    SetError(row, "video", "no frame could be decoded for a poster");
                    result.Add("poster-errors");
                    return;
                }

                using (var image = LoadOriented(scratch))
                {
                    foreach (var size in VideoVariants) WriteDerivative(image, row.Id, key, size);
                    // The decoded frame is authoritative about the aspect the grid lays out — a
                    // rotation the container declared and the decoder already applied would otherwise
                    // disagree with the probe's numbers.
                    row.Width = image.Width;
                    row.Height = image.Height;
                }

                row.ThumbKey = key;
                row.ThumbVariants = PhotoThumbCache.Join(VideoVariants);
                row.ThumbState = PhotoThumbState.Ready;
                row.ThumbsUpdatedUtc = DateTime.UtcNow;
                result.Add("posters");
            }
            catch (Exception e)
            {
                row.ThumbState = PhotoThumbState.Failed;
                row.ThumbsUpdatedUtc = DateTime.UtcNow;
                row.ThumbKey = null;
                row.ThumbVariants = null;
                SetError(row, "video", e.Message);
                result.Add("poster-errors");
            }
            finally
            {
                try { if (File.Exists(scratch)) File.Delete(scratch); }
                catch (IOException) { /* a leftover scratch frame is not worth failing a row over */ }
                catch (UnauthorizedAccessException) { }
            }
        }

        /// <summary>Where to look for a frame worth showing: the midpoint first (a home video's opening
        /// second is a lens cap), then one second in, then the very start for a clip too short to have
        /// a middle.</summary>
        private static IEnumerable<double> PosterOffsets(double? durationSec)
        {
            if (durationSec is double seconds && seconds > 2) yield return Math.Round(seconds / 2, 3);
            if (durationSec == null || durationSec > 1.5) yield return 1.0;
            yield return 0;
        }

        private void WriteDerivative(Image<Rgba32> source, int assetId, string key, string size)
        {
            var maxEdge = PhotoThumbCache.MaxEdgeFor(size);
            var relative = PhotoThumbCache.RelativePath(assetId, key, size);
            var destination = Path.Combine(options.ThumbCacheDir!, relative.Replace('/', Path.DirectorySeparatorChar));
            System.IO.Directory.CreateDirectory(Path.GetDirectoryName(destination)!);

            // Never upscale: a 300px scan blown up to 1600px is a bigger file that shows less.
            var scale = Math.Min(1.0, (double)maxEdge / Math.Max(source.Width, source.Height));
            var width = Math.Max(1, (int)Math.Round(source.Width * scale));
            var height = Math.Max(1, (int)Math.Round(source.Height * scale));

            using var resized = source.Clone(ctx => ctx.Resize(width, height, KnownResamplers.Lanczos3));
            // Temp-then-move: a killed pass must never leave a truncated file at a cache path, because
            // nothing downstream could tell it from a whole one — it would just be a photo that renders
            // half-drawn, forever.
            var temp = destination + "." + Guid.NewGuid().ToString("N") + ".part";
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                resized.SaveAsWebp(stream, new WebpEncoder { Quality = size == PhotoStreamRoutes.SizeGrid ? 78 : 84 });
            File.Move(temp, destination, overwrite: true);
        }

        /// <summary>Decode with the EXIF orientation applied (§2.2 — a naive resize ships sideways
        /// photos). Shared by the hash and thumb passes so a photo and its rotated twin hash alike and
        /// look alike.</summary>
        private static Image<Rgba32> LoadOriented(string fullPath)
        {
            using var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var image = Image.Load<Rgba32>(stream);
            image.Mutate(x => x.AutoOrient());
            return image;
        }

        // ── Helpers ──────────────────────────────────────────────────────────────────────────────

        private string ToRelative(string fullPath) =>
            Path.GetRelativePath(root, fullPath).Replace('\\', '/');

        private string FullPath(string relativePath) =>
            Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));

        private static string? Truncate(string? value, int max) =>
            string.IsNullOrEmpty(value) ? null : (value!.Length <= max ? value : value.Substring(0, max));
    }
}
