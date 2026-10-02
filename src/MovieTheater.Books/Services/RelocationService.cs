using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using MovieTheater.Books.Db;

namespace MovieTheater.Books.Services
{
    /// <summary>What one relocation batch did.</summary>
    public sealed record RelocationBatchResult(int Processed, long Remaining, long? NextCursor,
        int Moved, int Refreshed, int Unchanged, int Refused, int FoldersCreated)
    {
        public bool Done => Processed == 0;
        public override string ToString() =>
            $"{{ processed: {Processed}, remaining: {Remaining}, nextCursor: \"{NextCursor}\" }}  " +
            $"[relocate, moved: {Moved}, refreshed: {Refreshed}, unchanged: {Unchanged}, refused: {Refused}, folders+: {FoldersCreated}]";
    }

    /// <summary>
    /// <c>books-relocate</c> — move an EXISTING item onto the file that now holds it, so the item keeps its id and
    /// therefore everything keyed by that id: the identity pass's per-file work (provider links, judged spans,
    /// hand-read issue numbers, split-lane keys, format reads), the reader's position and marks, insights, the
    /// cached cover. Without it a moved or replaced file reaches the catalog through <c>books-scan</c> as a NEW
    /// item with none of that, and the old row is marked missing.
    ///
    /// <para><b>Input: TSV</b> with a header naming at least <c>ItemId</c> and <c>NewPath</c> (absolute, or relative
    /// to the item's library root). The pairing that produces it is decided outside this verb
    /// (<c>docs/books/rescan/</c>); this verb only proves each line is safe and applies it.</para>
    ///
    /// <para><b>Guards</b> (a refused line is reported with its reason and never half-applied): the item exists and
    /// the new path is under its own root; the new file EXISTS; when the path changes the OLD file must be GONE
    /// (a file still present is a copy, not a move — that is a second item for the scan); no other item holds the
    /// new path (the path index is binary, so the check is case-folded too); a path claimed by two lines is
    /// refused the second time.</para>
    ///
    /// <para><b>What it writes</b>, DB-only: <c>Item.Path/FileName/Extension/ContainerFormat/FolderId/FileSize/
    /// FileModifiedAt/IndexedAt</c>; the missing <c>Folder</c> chain for the new directory (the next scan's folder
    /// phase adopts those rows by path); and when the bytes differ from the ones indexed, a re-read of the FILE
    /// facts through <see cref="LibraryScanner.RefreshFileFactsAsync"/> (ComicDetail kept) plus a dropped thumbnail
    /// and cover dimensions so <c>books-thumbs</c> draws the new cover. Size + mtime are written from the disk, so
    /// the following <c>books-scan</c> sees the item as unchanged.</para>
    ///
    /// <para>Dry run by default; chunked by input line with the cursor in the verb (<c>--after</c>); idempotent — a
    /// line already in place counts as unchanged. Every applied line is journalled (old and new path, folder,
    /// size, mtime) and the journal is flushed after each batch commits.</para>
    /// </summary>
    public sealed class RelocationService
    {
        public const int DefaultBatchSize = 200;

        private readonly LibraryScanner scanner;
        private readonly ThumbnailService thumbnails;
        private readonly ILogger<RelocationService> logger;

        public RelocationService(LibraryScanner scanner, ThumbnailService thumbnails, ILogger<RelocationService> logger)
        {
            this.scanner = scanner;
            this.thumbnails = thumbnails;
            this.logger = logger;
        }

        public LibraryScanner.IFileSystem Fs { get; set; } = LibraryScanner.PhysicalFileSystem.Instance;

        public const string JournalHeader = "ItemId\tOldPath\tOldFolderId\tOldSize\tOldMtime\tNewPath\tNewFolderId\tNewSize\tNewMtime\tRefreshed";

        public async Task<RelocationBatchResult> RunBatchAsync(BooksDb db, string tsvPath, int batchSize, bool apply, long after,
            TextWriter? report = null, TextWriter? journal = null, CancellationToken ct = default)
        {
            batchSize = Math.Clamp(batchSize, 1, 5_000);
            var lines = await File.ReadAllLinesAsync(tsvPath, ct);
            if (lines.Length == 0) return new RelocationBatchResult(0, 0, null, 0, 0, 0, 0, 0);
            var header = lines[0].Split('\t').Select(h => h.Trim()).ToList();
            var idCol = header.FindIndex(h => h.Equals("ItemId", StringComparison.OrdinalIgnoreCase));
            var pathCol = header.FindIndex(h => h.Equals("NewPath", StringComparison.OrdinalIgnoreCase));
            if (idCol < 0 || pathCol < 0) throw new InvalidOperationException("The TSV header must name ItemId and NewPath.");

            // Paths claimed by EARLIER lines of the file — a second claim on one path is refused even across batches.
            var claimed = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var start = (int)Math.Max(after, 1);   // line 1 is the header; the cursor is 1-based
            for (var i = 1; i < start && i < lines.Length; i++)
            {
                var c = lines[i].Split('\t');
                if (c.Length > Math.Max(idCol, pathCol) && int.TryParse(c[idCol], out var cid)) claimed.TryAdd(c[pathCol].Trim(), cid);
            }

            var page = new List<(int Line, string[] Cols)>();
            var cursor = start;
            for (; cursor < lines.Length && page.Count < batchSize; cursor++)
            {
                if (string.IsNullOrWhiteSpace(lines[cursor])) continue;
                page.Add((cursor + 1, lines[cursor].Split('\t')));
            }
            if (page.Count == 0) return new RelocationBatchResult(0, 0, null, 0, 0, 0, 0, 0);

            var roots = await db.LibraryRoots.AsNoTracking().ToDictionaryAsync(r => r.Id, ct);
            int moved = 0, refreshed = 0, unchanged = 0, refused = 0, foldersCreated = 0;
            var journalLines = new List<string>();
            var now = DateTime.UtcNow;

            foreach (var (line, cols) in page)
            {
                ct.ThrowIfCancellationRequested();
                void Refuse(string why) { refused++; report?.WriteLine($"{line}\t{(cols.Length > idCol ? cols[idCol] : "")}\trefused\t{why}"); }

                if (cols.Length <= Math.Max(idCol, pathCol) || !int.TryParse(cols[idCol], NumberStyles.Integer, CultureInfo.InvariantCulture, out var itemId))
                { Refuse("unparseable line"); continue; }
                var rawPath = cols[pathCol].Trim();
                var item = await db.Items.FirstOrDefaultAsync(x => x.Id == itemId, ct);
                if (item == null) { Refuse("no such item"); continue; }
                if (!roots.TryGetValue(item.RootId, out var root)) { Refuse("item has no library root"); continue; }

                var rootPrefix = root.Path.TrimEnd('\\', '/') + "\\";
                var newPath = Path.IsPathFullyQualified(rawPath) ? rawPath : rootPrefix + rawPath.TrimStart('\\', '/');
                if (!newPath.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase)) { Refuse("new path is outside the item's root"); continue; }

                if (claimed.TryGetValue(rawPath, out var other) && other != itemId) { Refuse($"path already claimed by item {other} on an earlier line"); continue; }
                claimed[rawPath] = itemId;

                if (!Fs.FileExists(newPath)) { Refuse("new file does not exist"); continue; }
                var (size, mtime) = Fs.FileInfo(newPath);
                var samePath = string.Equals(item.Path, newPath, StringComparison.Ordinal);

                if (samePath && item.FileSize == size && item.FileModifiedAt == mtime) { unchanged++; continue; }
                if (!string.Equals(item.Path, newPath, StringComparison.OrdinalIgnoreCase) && Fs.FileExists(item.Path))
                { Refuse("old file still exists (a copy, not a move)"); continue; }

                var lower = newPath.ToLowerInvariant();
                var occupant = await db.Items.AsNoTracking()
                    .Where(x => x.Id != itemId && (x.Path == newPath || x.Path.ToLower() == lower))
                    .Select(x => (int?)x.Id).FirstOrDefaultAsync(ct);
                if (occupant != null) { Refuse($"new path is held by item {occupant}"); continue; }

                var (folder, created) = await EnsureFolderAsync(db, root, Path.GetDirectoryName(newPath)!, apply, now, ct);
                foldersCreated += created;
                if (folder == null) { Refuse("could not place the new folder under the root"); continue; }

                var bytesChanged = item.FileSize != size || item.FileModifiedAt != mtime;
                var old = (item.Path, item.FolderId, item.FileSize, item.FileModifiedAt);
                moved += samePath ? 0 : 1;
                if (!apply)
                {
                    if (bytesChanged) refreshed++;
                    report?.WriteLine($"{line}\t{itemId}\t{(bytesChanged ? "would-move+refresh" : "would-move")}\t{newPath}");
                    continue;
                }

                var ext = Path.GetExtension(newPath).ToLowerInvariant();
                item.Path = newPath;
                item.FileName = Path.GetFileName(newPath);
                item.Extension = ext;
                item.ContainerFormat = LibraryScanner.ContainerFor(ext);
                item.FolderId = folder.Id;
                item.TopFolderId = folder.TopFolderId ?? folder.Id;
                item.FileSize = size;
                item.FileModifiedAt = mtime;
                item.IndexedAt = now;

                var state = await db.ItemStates.FirstOrDefaultAsync(s => s.ItemId == itemId, ct);
                if (item.IsExcluded) await LibraryScanner.ClearMissingAsync(db, item, state, ct);

                if (bytesChanged)
                {
                    await scanner.RefreshFileFactsAsync(db, item, ct);
                    state ??= await db.ItemStates.FirstOrDefaultAsync(s => s.ItemId == itemId, ct);
                    if (state != null)
                    {
                        state.ThumbnailError = null;
                        state.CoverWidth = null;
                        state.CoverHeight = null;
                        state.CoverDimsComputedFor = null;
                    }
                    refreshed++;
                }
                journalLines.Add(string.Join('\t', itemId, old.Path, old.FolderId, old.FileSize,
                    old.FileModifiedAt?.ToString("O", CultureInfo.InvariantCulture), newPath, folder.Id, size,
                    mtime.ToString("O", CultureInfo.InvariantCulture), bytesChanged ? 1 : 0));
            }

            if (apply)
            {
                await db.SaveChangesAsync(ct);
                // The cover is dropped only after the row that points at the new bytes is committed.
                foreach (var j in journalLines.Where(j => j.EndsWith("\t1", StringComparison.Ordinal)))
                    thumbnails.Delete(int.Parse(j[..j.IndexOf('\t')], CultureInfo.InvariantCulture));
                if (journal != null)
                {
                    foreach (var j in journalLines) await journal.WriteLineAsync(j);
                    await journal.FlushAsync(ct);
                }
            }

            var next = cursor;   // 0-based index of the next unread line == 1-based number of the last read one
            var remaining = Math.Max(0, lines.Skip(next).Count(l => !string.IsNullOrWhiteSpace(l)));
            return new RelocationBatchResult(page.Count, remaining, next, moved, refreshed, unchanged, refused, foldersCreated);
        }

        /// <summary>Find the Folder row for <paramref name="dir"/>, creating it and any missing ancestors below the
        /// root. Ids are allocated like the scanner's (max + 1); the scan's folder phase later adopts the rows by path.</summary>
        private async Task<(Folder? Folder, int Created)> EnsureFolderAsync(BooksDb db, LibraryRoot root, string dir, bool apply, DateTime now, CancellationToken ct)
        {
            var rootPath = root.Path.TrimEnd('\\', '/');
            var chain = new List<string>();
            var cur = dir.TrimEnd('\\', '/');
            Folder? anchor = null;
            while (true)
            {
                anchor = await FindFolderAsync(db, cur, ct);
                if (anchor != null) break;
                if (cur.Length <= rootPath.Length || !cur.StartsWith(rootPath, StringComparison.OrdinalIgnoreCase)) return (null, 0);
                chain.Add(cur);
                cur = Path.GetDirectoryName(cur)!;
            }
            if (chain.Count == 0) return (anchor, 0);
            if (!apply) return (anchor, chain.Count);   // the dry run counts what it would create

            var nextId = (await db.Folders.MaxAsync(f => (int?)f.Id, ct) ?? 0) + 1;
            nextId = Math.Max(nextId, db.Folders.Local.Select(f => f.Id).DefaultIfEmpty(0).Max() + 1);
            var parent = anchor;
            chain.Reverse();
            foreach (var path in chain)
            {
                var name = Path.GetFileName(path);
                DateTime? modified = null;
                try { modified = Fs.DirectoryModifiedUtc(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                var f = new Folder
                {
                    Id = nextId++, RootId = root.Id, ParentId = parent.Id, Kind = root.Kind, Path = path,
                    Name = name, NormalizedName = LibraryScanner.Normalize(name), Depth = parent.Depth + 1,
                    TopFolderId = parent.ParentId == null ? null : parent.TopFolderId ?? parent.Id,
                    FolderModifiedAt = modified, IndexedAt = now,
                };
                if (f.TopFolderId == null && parent.ParentId == null) f.TopFolderId = f.Id;
                db.Folders.Add(f);
                parent = f;
            }
            await db.SaveChangesAsync(ct);
            logger.LogInformation("relocate: created {N} folder(s) down to {Dir}", chain.Count, dir);
            return (parent, chain.Count);
        }

        private static async Task<Folder?> FindFolderAsync(BooksDb db, string path, CancellationToken ct)
        {
            var local = db.Folders.Local.FirstOrDefault(f => string.Equals(f.Path, path, StringComparison.OrdinalIgnoreCase));
            if (local != null) return local;
            var exact = await db.Folders.FirstOrDefaultAsync(f => f.Path == path, ct);
            if (exact != null) return exact;
            var lower = path.ToLowerInvariant();
            return await db.Folders.FirstOrDefaultAsync(f => f.Path.ToLower() == lower, ct);
        }
    }
}
