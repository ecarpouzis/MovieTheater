using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using MovieTheater.Books.Archives;
using MovieTheater.Books.Db;
using MovieTheater.Books.Parse;

namespace MovieTheater.Books.Services
{
    /// <summary>What one scan batch did — the whole observability contract.</summary>
    public sealed record ScanBatchResult(
        string Phase, int Processed, long Remaining, string? NextCursor,
        int Added, int Changed, int Removed, int Failed)
    {
        public bool Done => Phase == ScanPhase.Done;
        public override string ToString() =>
            $"{{ processed: {Processed}, remaining: {Remaining}, nextCursor: \"{NextCursor}\", failed: {Failed} }}" +
            $"  [{Phase}, added: {Added}, changed: {Changed}, removed: {Removed}]";
    }

    public static class ScanPhase
    {
        public const string Folders = "folders";
        public const string Files = "files";
        public const string Removals = "removals";
        public const string Aggregate = "aggregate";
        public const string Done = "done";
    }

    /// <summary>
    /// The library scan: walk the configured <see cref="LibraryRoot"/>s READ-ONLY and reconcile the catalog with
    /// what is on the share.
    ///
    /// <para><b>Four phases behind one persisted cursor</b>, because a 141k-file / 54k-folder walk cannot run
    /// inside one call and survive anything:</para>
    /// <list type="number">
    /// <item><b>folders</b> — a breadth-first walk whose FRONTIER (the directories still to visit) is persisted in
    /// <c>SystemState</c> and committed with each batch's `Folder` upserts. A kill costs at most one batch.</item>
    /// <item><b>files</b> — page over the folders this run stamped, by `Folder.Id`, indexing their DIRECT files:
    /// new / changed items by path + size + mtime, embedded ComicInfo through the readers, the parse pipeline.</item>
    /// <item><b>removals</b> — page over the items this run did NOT stamp and delete the ones whose file is
    /// genuinely gone. Guarded: an unreachable ROOT aborts the phase rather than emptying the catalog.</item>
    /// <item><b>aggregate</b> — folder counts and `TopFolderId`, then the audit row is closed.</item>
    /// </list>
    ///
    /// <para><b>It never writes to the library.</b> The share is opened for reading only — no rename, no delete,
    /// no thumbnail written beside a file. Everything the scan produces lands in the catalog.</para>
    ///
    /// <para><b>A removed file is MARKED, never deleted</b> — <c>Item.IsExcluded</c> takes it out of every browse and
    /// <c>ItemState.IsBroken</c> carries the reason "missing"; its details, credits, tags, links and the reader's
    /// <c>UserItemState</c> all stay, so a file that comes back is whole again with the next scan. <c>Series</c> is
    /// derived and shared and is never touched here. The post-scan re-resolve (`books-resolve --series` then
    /// `books-resolve`) is a separate, registered job that the caller runs afterwards.</para>
    /// </summary>
    public sealed class LibraryScanner
    {
        public const string PhaseKey = "books:scan:phase";
        public const string FrontierKey = "books:scan:frontier";
        public const string CursorKey = "books:scan:cursor";
        public const string RunStampKey = "books:scan:runStamp";
        public const string RunIdKey = "books:scan:runId";
        public const string RootsKey = "books:scan:roots";
        public const string AddedKey = "books:scan:added";
        public const string ChangedKey = "books:scan:changed";
        public const string RemovedKey = "books:scan:removed";
        public const string FailedKey = "books:scan:failed";
        public const string SeenKey = "books:scan:seen";
        public const string KeptStateKey = "books:scan:keptUserState";

        public const int DefaultBatchSize = 200;

        /// <summary>The ASCII unit separator packs the frontier triple into one line; it cannot occur in a path.</summary>
        private const string Sep = "";

        /// <summary>The container extensions the catalog indexes. Anything else on the share is not a book.</summary>
        public static readonly HashSet<string> SupportedExtensions =
            new(StringComparer.OrdinalIgnoreCase) { ".cbz", ".cbr", ".zip", ".rar", ".pdf", ".epub", ".mobi", ".azw3", ".cb7", ".7z" };

        private readonly IEnumerable<IArchiveReader> readers;
        private readonly ILogger<LibraryScanner> logger;

        public LibraryScanner(IEnumerable<IArchiveReader> readers, ILogger<LibraryScanner> logger)
        {
            this.readers = readers;
            this.logger = logger;
        }

        /// <summary>The filesystem, behind a seam so a test drives a generated temp tree and never a real share.</summary>
        public interface IFileSystem
        {
            bool DirectoryExists(string path);
            IEnumerable<string> EnumerateDirectories(string path);
            IEnumerable<string> EnumerateFiles(string path);
            bool FileExists(string path);
            (long Length, DateTime ModifiedUtc) FileInfo(string path);
            DateTime DirectoryModifiedUtc(string path);
        }

        public sealed class PhysicalFileSystem : IFileSystem
        {
            public static readonly PhysicalFileSystem Instance = new();
            public bool DirectoryExists(string path) => Directory.Exists(path);
            public IEnumerable<string> EnumerateDirectories(string path) => Directory.EnumerateDirectories(path);
            public IEnumerable<string> EnumerateFiles(string path) => Directory.EnumerateFiles(path);
            public bool FileExists(string path) => File.Exists(path);
            public (long, DateTime) FileInfo(string path) { var fi = new FileInfo(path); return (fi.Length, Truncate(fi.LastWriteTimeUtc)); }
            public DateTime DirectoryModifiedUtc(string path) => Truncate(Directory.GetLastWriteTimeUtc(path));
        }

        /// <summary>Second resolution: a share's mtimes round, and a sub-second difference is not a change.</summary>
        public static DateTime Truncate(DateTime dt) => new(dt.Ticks - dt.Ticks % TimeSpan.TicksPerSecond, dt.Kind);

        public IFileSystem Fs { get; set; } = PhysicalFileSystem.Instance;

        // ── status / reset ───────────────────────────────────────────────────────────────────────────────

        public async Task<ScanBatchResult> StatusAsync(BooksDb db, CancellationToken ct = default)
        {
            var phase = await ReadAsync(db, PhaseKey, ct) ?? ScanPhase.Done;
            var frontier = ParseFrontier(await ReadAsync(db, FrontierKey, ct));
            return new ScanBatchResult(phase,
                (int)await ReadLongAsync(db, SeenKey, ct),
                frontier.Count,
                await ReadAsync(db, CursorKey, ct),
                (int)await ReadLongAsync(db, AddedKey, ct),
                (int)await ReadLongAsync(db, ChangedKey, ct),
                (int)await ReadLongAsync(db, RemovedKey, ct),
                (int)await ReadLongAsync(db, FailedKey, ct));
        }

        public async Task ResetAsync(BooksDb db, CancellationToken ct = default)
        {
            foreach (var key in new[] { PhaseKey, FrontierKey, CursorKey, RunStampKey, RunIdKey, RootsKey, AddedKey, ChangedKey, RemovedKey, FailedKey, SeenKey, KeptStateKey })
            {
                var row = await db.SystemStates.FirstOrDefaultAsync(s => s.Key == key, ct);
                if (row != null) db.SystemStates.Remove(row);
            }
            await db.SaveChangesAsync(ct);
        }

        /// <summary>
        /// Open a run: stamp it, seed the frontier with the enabled roots (optionally one), and open the
        /// <see cref="ScanRun"/> audit row. Refuses when a root is unreachable — a scan that cannot see the
        /// share must never proceed to the removal phase.
        /// </summary>
        public async Task<ScanBatchResult> StartAsync(BooksDb db, int? rootId = null, CancellationToken ct = default)
        {
            var roots = await db.LibraryRoots.AsNoTracking()
                .Where(r => r.Enabled && (rootId == null || r.Id == rootId))
                .OrderBy(r => r.Id).ToListAsync(ct);
            if (roots.Count == 0) throw new InvalidOperationException("No enabled library root to scan.");

            var unreachable = roots.Where(r => !Fs.DirectoryExists(r.Path)).Select(r => r.Id).ToList();
            if (unreachable.Count > 0)
                throw new InvalidOperationException($"Library root {string.Join(", ", unreachable)} is unreachable; refusing to scan.");

            await ResetAsync(db, ct);

            // Ids in this file are never generated by the database (the migration preserved v1's), so the
            // audit row allocates its own.
            var runId = (await db.ScanRuns.AsNoTracking().Select(r => (int?)r.Id).MaxAsync(ct) ?? 0) + 1;
            var run = new ScanRun { Id = runId, RootId = rootId, Kind = "library", StartedAt = DateTime.UtcNow };
            db.ScanRuns.Add(run);
            await db.SaveChangesAsync(ct);

            var stamp = DateTime.UtcNow;
            await WriteAsync(db, RunIdKey, run.Id.ToString(System.Globalization.CultureInfo.InvariantCulture), ct);
            await WriteAsync(db, RunStampKey, stamp.ToString("O"), ct);
            await WriteAsync(db, RootsKey, string.Join(",", roots.Select(r => r.Id)), ct);
            await WriteAsync(db, PhaseKey, ScanPhase.Folders, ct);
            await WriteAsync(db, FrontierKey, string.Join("\n", roots.Select(r => r.Id + Sep + r.Path)), ct);
            await db.SaveChangesAsync(ct);

            return new ScanBatchResult(ScanPhase.Folders, 0, roots.Count, null, 0, 0, 0, 0);
        }

        // ── the batch ────────────────────────────────────────────────────────────────────────────────────

        /// <summary>One bounded batch of whichever phase the run is in. The caller loops until <c>Done</c>.</summary>
        public async Task<ScanBatchResult> RunBatchAsync(BooksDb db, int batchSize, bool apply = true, CancellationToken ct = default)
        {
            batchSize = Math.Clamp(batchSize, 1, 5_000);
            var phase = await ReadAsync(db, PhaseKey, ct) ?? ScanPhase.Done;
            return phase switch
            {
                ScanPhase.Folders => await FolderBatchAsync(db, batchSize, apply, ct),
                ScanPhase.Files => await FileBatchAsync(db, batchSize, apply, ct),
                ScanPhase.Removals => await RemovalBatchAsync(db, batchSize, apply, ct),
                ScanPhase.Aggregate => await AggregateAsync(db, apply, ct),
                _ => new ScanBatchResult(ScanPhase.Done, 0, 0, null, 0, 0, 0, 0),
            };
        }

        // ── phase 1: folders ─────────────────────────────────────────────────────────────────────────────

        private async Task<ScanBatchResult> FolderBatchAsync(BooksDb db, int batchSize, bool apply, CancellationToken ct)
        {
            var stamp = await RunStampAsync(db, ct);
            var frontier = ParseFrontier(await ReadAsync(db, FrontierKey, ct));
            if (frontier.Count == 0)
            {
                await WriteAsync(db, PhaseKey, ScanPhase.Files, ct);
                await WriteAsync(db, CursorKey, "0", ct);
                await db.SaveChangesAsync(ct);
                return new ScanBatchResult(ScanPhase.Files, 0, await CountStampedFoldersAsync(db, stamp, ct), "0", 0, 0, 0, 0);
            }

            var take = Math.Min(batchSize, frontier.Count);
            var slice = frontier.Take(take).ToList();
            var rest = frontier.Skip(take).ToList();

            var kindByRoot = await db.LibraryRoots.AsNoTracking().ToDictionaryAsync(r => r.Id, r => r.Kind, ct);
            var paths = slice.Select(f => f.Path).ToList();
            var existing = await db.Folders.Where(f => paths.Contains(f.Path)).ToListAsync(ct);
            var byPath = existing.ToDictionary(f => f.Path, StringComparer.OrdinalIgnoreCase);

            var nextId = await NextIdAsync(db.Folders.Select(f => (long?)f.Id), ct);
            int added = 0, changed = 0, failed = 0;

            foreach (var (rootId, path, parentId) in slice)
            {
                ct.ThrowIfCancellationRequested();
                if (!Fs.DirectoryExists(path)) { failed++; continue; }

                var name = Path.GetFileName(path.TrimEnd('\\', '/'));
                DateTime modified;
                try { modified = Fs.DirectoryModifiedUtc(path); }
                catch (IOException) { failed++; continue; }
                catch (UnauthorizedAccessException) { failed++; continue; }

                if (!byPath.TryGetValue(path, out var folder))
                {
                    folder = new Folder { Id = (int)nextId++, Path = path };
                    db.Folders.Add(folder);
                    byPath[path] = folder;
                    added++;
                }
                else if (folder.ParentId != parentId || folder.Name != name || folder.FolderModifiedAt != modified)
                    changed++;

                folder.RootId = rootId;
                folder.ParentId = parentId;
                folder.Kind = kindByRoot.GetValueOrDefault(rootId, ItemKind.Comic);
                folder.Name = name;
                folder.NormalizedName = Normalize(name);
                folder.Depth = parentId == null ? 0 : (byPath.Values.FirstOrDefault(f => f.Id == parentId)?.Depth ?? 0) + 1;
                folder.FolderModifiedAt = modified;
                folder.IndexedAt = stamp;

                if (apply) await db.SaveChangesAsync(ct);   // the child rows below need this folder's id

                foreach (var child in SafeEnumerate(() => Fs.EnumerateDirectories(path)))
                    rest.Add((rootId, child, folder.Id));
            }

            await WriteAsync(db, FrontierKey, string.Join("\n", rest.Select(f => f.RootId + Sep + f.Path + Sep + (f.ParentId?.ToString() ?? ""))), ct);
            await AddLongAsync(db, SeenKey, slice.Count, ct);
            await AddLongAsync(db, FailedKey, failed, ct);
            if (apply) await db.SaveChangesAsync(ct);

            // Added / Changed count ITEMS across the whole run — folder structure is reported through Processed
            // and the frontier length, so a caller can sum the run's counters without double-counting.
            _ = added; _ = changed;
            return new ScanBatchResult(ScanPhase.Folders, slice.Count, rest.Count, rest.Count.ToString(System.Globalization.CultureInfo.InvariantCulture), 0, 0, 0, failed);
        }

        // ── phase 2: files ───────────────────────────────────────────────────────────────────────────────

        private async Task<ScanBatchResult> FileBatchAsync(BooksDb db, int batchSize, bool apply, CancellationToken ct)
        {
            var stamp = await RunStampAsync(db, ct);
            var cursor = (int)await ReadLongAsync(db, CursorKey, ct);

            var folders = await db.Folders.AsNoTracking()
                .Where(f => f.IndexedAt >= stamp && f.Id > cursor)
                .OrderBy(f => f.Id).Take(batchSize)
                .ToListAsync(ct);

            if (folders.Count == 0)
            {
                await WriteAsync(db, PhaseKey, ScanPhase.Removals, ct);
                await WriteAsync(db, CursorKey, "0", ct);
                await db.SaveChangesAsync(ct);
                return new ScanBatchResult(ScanPhase.Removals, 0, await CountStaleItemsAsync(db, stamp, ct), "0", 0, 0, 0, 0);
            }

            var roots = await db.LibraryRoots.AsNoTracking().ToListAsync(ct);
            var rootPaths = roots.Select(r => r.Path).ToList();
            var folderIds = folders.Select(f => f.Id).ToList();
            var existing = await db.Items.Where(i => folderIds.Contains(i.FolderId)).ToListAsync(ct);
            var byPath = existing.ToDictionary(i => i.Path, StringComparer.OrdinalIgnoreCase);

            var nextId = await NextIdAsync(db.Items.Select(i => (long?)i.Id), ct);
            int added = 0, changed = 0, failed = 0, seen = 0;

            foreach (var folder in folders)
            {
                ct.ThrowIfCancellationRequested();
                var root = roots.FirstOrDefault(r => r.Id == folder.RootId);
                foreach (var filePath in SafeEnumerate(() => Fs.EnumerateFiles(folder.Path)))
                {
                    var ext = Path.GetExtension(filePath).ToLowerInvariant();
                    if (!SupportedExtensions.Contains(ext)) continue;
                    seen++;

                    long size;
                    DateTime modified;
                    try { (size, modified) = Fs.FileInfo(filePath); }
                    catch (IOException) { failed++; continue; }

                    byPath.TryGetValue(filePath, out var item);
                    var isNew = item == null;
                    var unchanged = item != null && item.FileSize == size && item.FileModifiedAt == modified;

                    if (item == null)
                    {
                        item = new Item { Id = (int)nextId++, Path = filePath };
                        db.Items.Add(item);
                        byPath[filePath] = item;
                        added++;
                    }
                    else if (!unchanged) changed++;

                    item.RootId = folder.RootId;
                    item.FolderId = folder.Id;
                    item.Kind = folder.Kind;
                    item.FileName = Path.GetFileName(filePath);
                    item.Extension = ext;
                    item.ContainerFormat = ContainerFor(ext);
                    item.FileSize = size;
                    item.FileModifiedAt = modified;
                    item.IndexedAt = stamp;

                    // A file that was marked missing and is back clears both flags here — BEFORE the unchanged
                    // fast path, because a file that reappears untouched is exactly the case that would
                    // otherwise stay excluded forever.
                    if (apply && !isNew && item.IsExcluded)
                    {
                        var prior = await db.ItemStates.FirstOrDefaultAsync(st => st.ItemId == item.Id, ct);
                        await ClearMissingAsync(db, item, prior, ct);
                    }

                    // An UNCHANGED file is re-stamped (so the removal phase does not take it) and nothing else
                    // is re-read — that is what makes a re-scan of a settled library cheap.
                    if (unchanged && !isNew) continue;

                    if (apply && !await IndexFileAsync(db, item, filePath, ext, root, rootPaths, ct)) failed++;
                }
                if (apply) await db.SaveChangesAsync(ct);
            }

            var nextCursor = folders[^1].Id;
            await WriteLongAsync(db, CursorKey, nextCursor, ct);
            await AddLongAsync(db, AddedKey, added, ct);
            await AddLongAsync(db, ChangedKey, changed, ct);
            await AddLongAsync(db, FailedKey, failed, ct);
            if (apply) await db.SaveChangesAsync(ct);

            var remaining = await db.Folders.AsNoTracking().CountAsync(f => f.IndexedAt >= stamp && f.Id > nextCursor, ct);
            return new ScanBatchResult(ScanPhase.Files, seen, remaining, nextCursor.ToString(System.Globalization.CultureInfo.InvariantCulture), added, changed, 0, failed);
        }

        /// <summary>
        /// Read one file's container: the embedded ComicInfo (or OPF / document info), the page count, and the
        /// parse pipeline's reading. Every write lands on the item's own rows; the file is opened READ-ONLY, and
        /// an unreadable archive is RECORDED in <see cref="ItemState"/>, never thrown — one bad file must not
        /// stop a walk with 141k of them to do.
        /// </summary>
        private async Task<bool> IndexFileAsync(BooksDb db, Item item, string filePath, string ext, LibraryRoot? root, IReadOnlyList<string> rootPaths, CancellationToken ct)
        {
            var reader = readers.FirstOrDefault(r => r.CanHandle(ext));
            ArchiveMetadata? meta = null;
            string? brokenReason = null;
            var pageCount = 0;

            // THE READER IS FOR COMICS. Reading an EPUB's metadata costs a full eager VersOne
            // ReadBook — every HTML file and every image decompressed into memory — because
            // ReadMetadataAsync counts spine images to produce a page count. For a comic that
            // count IS the page count and the reader is page-based, so it earns its cost.
            //
            // For a text book it does not. The count is the number of embedded images (~1 for a
            // novel), the EPUB reader is spine-based and never asks for it, and every other field
            // the read yields — Language, Description, Isbn — is overwritten by
            // books-import-calibre, which reads Calibre's metadata.db directly. Measured over a
            // 104k-book import this ran ~1.4s per file: ~56 hours to compute a number nothing
            // reads. IsBroken is skipped with it deliberately — VersOne's validator false-flagged
            // 1,136 readable novels (see the note below), so its verdict on a novel is noise.
            if (reader != null && item.Kind == ItemKind.Comic)
            {
                try { meta = await reader.ReadMetadataAsync(filePath); }
                catch (Exception ex) { brokenReason = ex.Message; }

                if (meta?.PageCount is > 0) { pageCount = meta.PageCount.Value; brokenReason = null; }
                else
                {
                    try { pageCount = await reader.GetPageCountAsync(filePath); brokenReason = null; }
                    catch (Exception ex) { brokenReason ??= ex.Message; }
                }
            }

            item.PageCount = pageCount;
            item.Title = meta?.IssueTitle ?? Path.GetFileNameWithoutExtension(filePath);
            item.NormalizedTitle = Normalize(item.Title);

            var state = await LoadAsync(db.ItemStates, s => s.ItemId == item.Id, () => new ItemState { ItemId = item.Id }, db, ct);
            if (reader != null)
            {
                // A PARSER'S COMPLAINT IS NOT A BROKEN FILE. This flag once meant "whatever the metadata read
                // threw", so VersOne's EPUB validator — which rejects a missing TOC or a manifest naming a cover
                // the file does not ship — flagged 1,136 perfectly readable novels, every one of them with a
                // cover thumbnail already on disk, against 21 genuinely broken comics. The flag means "the
                // CONTAINER will not open", so ask the BYTES, not the parser; a container that cannot be sniffed
                // (PDF, MOBI) offers no opinion and the reader's verdict stands.
                //
                // A BOOK never ran the parser above, so there is no complaint to corroborate — the sniff IS the
                // whole verdict. It stays for books because it is the cheap half: the central directory, not the
                // eager whole-book read the metadata parse costs. Skipping it would let a truncated novel index
                // as healthy.
                var opens = ArchiveFormatSniffer.CanOpenContainer(filePath);
                var unreadable = item.Kind == ItemKind.Comic
                    ? brokenReason != null && opens != true
                    : opens == false;
                if (brokenReason != null && !unreadable)
                    logger.LogDebug("scan: {Path} opens; metadata parse said \"{Reason}\" — not flagged broken.", filePath, brokenReason);

                state.IsBroken = unreadable;
                state.BrokenReason = unreadable
                    ? Truncate(brokenReason ?? "container does not open", 500)
                    : null;
                state.BrokenCheckedAt = DateTime.UtcNow;
            }

            if (item.Kind == ItemKind.Comic)
            {
                await WriteComicRowsAsync(db, item, filePath, meta, rootPaths, ct);
            }
            else
            {
                // A book's Calibre-native identity is filled by books-import-calibre; the scanner only
                // establishes the row so the importer has something to fill.
                var book = await LoadAsync(db.BookDetails, b => b.ItemId == item.Id, () => new BookDetail { ItemId = item.Id }, db, ct);
                book.Language ??= meta?.Language;
                book.Description ??= meta?.Description;
                if (meta?.Identifier != null) book.Isbn ??= meta.Identifier;
            }

            _ = root;
            // "failed" counts files the scan could not READ — a recorded metadata complaint is not one.
            return !state.IsBroken;
        }

        private async Task WriteComicRowsAsync(BooksDb db, Item item, string filePath, ArchiveMetadata? meta, IReadOnlyList<string> rootPaths, CancellationToken ct)
        {
            if (meta != null)
            {
                var embedded = await LoadAsync(db.ComicEmbeddeds, e => e.ItemId == item.Id, () => new ComicEmbedded { ItemId = item.Id }, db, ct);
                embedded.Series = meta.Series; embedded.Number = meta.SeriesIndex;
                embedded.AltSeries = meta.AltSeries; embedded.AltNumber = meta.AltSeriesIndex;
                embedded.Volume = meta.Volume; embedded.Title = meta.IssueTitle; embedded.Summary = meta.Description;
                embedded.Publisher = meta.Publisher; embedded.Imprint = meta.Imprint;
                embedded.Genre = meta.Genre; embedded.Tags = meta.Tags;
                embedded.Characters = meta.Characters; embedded.Teams = meta.Teams; embedded.Locations = meta.Locations;
                embedded.StoryArc = meta.StoryArc; embedded.Web = meta.Web; embedded.Language = meta.Language;
                embedded.Format = meta.Format; embedded.PublicationDate = meta.PublicationDate;
                embedded.Writers = meta.Writers; embedded.Pencillers = meta.Pencillers; embedded.Inker = meta.Inker;
                embedded.Colorist = meta.Colorist; embedded.Letterer = meta.Letterer; embedded.CoverArtist = meta.CoverArtist;
                embedded.Editor = meta.Editor; embedded.BlackAndWhite = meta.BlackAndWhite; embedded.Manga = meta.Manga;
                embedded.Rating = meta.Rating; embedded.Identifier = meta.Identifier; embedded.Notes = meta.Notes;
                embedded.Count = meta.Count; embedded.AgeRating = meta.AgeRating;

                await RewriteComicInfoRowsAsync(db, item.Id, meta, ct);
            }

            var parsed = ComicTitleParser.Parse(
                Path.GetFileName(filePath), filePath,
                meta == null ? null : new ComicTitleParser.Embedded(
                    meta.Series, meta.SeriesIndex, meta.AltSeries, meta.AltSeriesIndex,
                    meta.Volume, meta.PublicationDate, meta.Publisher, meta.Format),
                rootPaths);

            var detail = await LoadAsync(db.ComicDetails, d => d.ItemId == item.Id, () => new ComicDetail { ItemId = item.Id }, db, ct);
            detail.ParsedSeriesKey = parsed.ParsedSeriesKey;
            detail.IssueNo = parsed.IssueNo;
            detail.Year = parsed.Year;
            detail.VolumeNo = parsed.VolumeNo;
            detail.Publisher = parsed.Publisher;
            detail.Format = parsed.Format;
            detail.FormatRaw = parsed.FormatRaw;
            detail.IsCollection = parsed.IsCollection;
            detail.IssueTitle = meta?.IssueTitle;
            detail.Confidence = parsed.Confidence;
            detail.SeriesSource = parsed.SeriesSource;
            detail.IssueSource = parsed.IssueSource;
            detail.YearSource = parsed.YearSource;
            detail.PublisherSource = parsed.PublisherSource;
            detail.FolderSeries = parsed.FolderSeries;
            detail.FolderYear = parsed.FolderYear;
            detail.ParseNotes = parsed.ParseNotes;
            detail.ParsedAt = DateTime.UtcNow;
        }

        /// <summary>
        /// The ComicInfo-sourced credit and tag ROWS. Only <c>Source = ComicInfo</c> rows are touched: every other
        /// leg's rows belong to its own job, and a re-scan must not wipe them.
        /// </summary>
        private static async Task RewriteComicInfoRowsAsync(BooksDb db, int itemId, ArchiveMetadata meta, CancellationToken ct)
        {
            var oldCredits = await db.ItemCredits.Where(c => c.ItemId == itemId && c.Source == TagSource.ComicInfo).ToListAsync(ct);
            db.ItemCredits.RemoveRange(oldCredits);
            var oldTags = await db.ItemTags.Where(t => t.ItemId == itemId && t.Source == TagSource.ComicInfo).ToListAsync(ct);
            db.ItemTags.RemoveRange(oldTags);

            var ordinal = 0;
            void AddCredits(string role, string? blob)
            {
                foreach (var name in SplitNames(blob))
                    db.ItemCredits.Add(new ItemCredit
                    {
                        ItemId = itemId, Source = TagSource.ComicInfo, Ordinal = ordinal++,
                        Role = role, Name = name, NormalizedName = Normalize(name),
                    });
            }
            AddCredits("Writer", meta.Writers);
            AddCredits("Penciller", meta.Pencillers);
            AddCredits("Inker", meta.Inker);
            AddCredits("Colorist", meta.Colorist);
            AddCredits("Letterer", meta.Letterer);
            AddCredits("Cover Artist", meta.CoverArtist);
            AddCredits("Editor", meta.Editor);

            var seen = new HashSet<(string, string)>();
            void AddTags(string category, string? blob)
            {
                foreach (var value in SplitNames(blob))
                {
                    // A CVDB#### token is an unresolved ComicVine entity id, not a genre. It is resolved into a
                    // real name by the CVDB job and must never reach a facet as a literal.
                    if (value.StartsWith("CVDB", StringComparison.OrdinalIgnoreCase)) continue;
                    if (!seen.Add((category, value.ToLowerInvariant()))) continue;
                    db.ItemTags.Add(new ItemTag { ItemId = itemId, Category = category, Value = value, Source = TagSource.ComicInfo });
                }
            }
            AddTags("genre", meta.Genre);
            AddTags("tag", meta.Tags);
        }

        // ── phase 3: removals ────────────────────────────────────────────────────────────────────────────

        private async Task<ScanBatchResult> RemovalBatchAsync(BooksDb db, int batchSize, bool apply, CancellationToken ct)
        {
            var stamp = await RunStampAsync(db, ct);
            var cursor = (int)await ReadLongAsync(db, CursorKey, ct);
            var rootIds = (await ReadAsync(db, RootsKey, ct) ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries).Select(int.Parse).ToList();

            // The guard that keeps a dropped share from marking the whole catalog missing: every root in scope
            // must still be there before a single row is touched.
            var roots = await db.LibraryRoots.AsNoTracking().Where(r => rootIds.Contains(r.Id)).ToListAsync(ct);
            foreach (var r in roots)
                if (!Fs.DirectoryExists(r.Path))
                    throw new InvalidOperationException($"Library root {r.Id} went unreachable mid-scan; refusing to touch anything.");

            var stale = await db.Items.AsNoTracking()
                .Where(i => rootIds.Contains(i.RootId) && (i.IndexedAt == null || i.IndexedAt < stamp) && i.Id > cursor)
                .OrderBy(i => i.Id).Take(batchSize)
                .Select(i => new { i.Id, i.Path })
                .ToListAsync(ct);

            if (stale.Count == 0)
            {
                await WriteAsync(db, PhaseKey, ScanPhase.Aggregate, ct);
                await db.SaveChangesAsync(ct);
                return new ScanBatchResult(ScanPhase.Aggregate, 0, 0, null, 0, 0, 0, 0);
            }

            var gone = stale.Where(s => !Fs.FileExists(s.Path)).Select(s => s.Id).ToList();
            var keptState = 0;
            if (apply && gone.Count > 0) keptState = await MarkMissingAsync(db, gone, ct);

            var nextCursor = stale[^1].Id;
            await WriteLongAsync(db, CursorKey, nextCursor, ct);
            await AddLongAsync(db, RemovedKey, gone.Count, ct);
            await AddLongAsync(db, KeptStateKey, keptState, ct);
            if (apply) await db.SaveChangesAsync(ct);

            var remaining = await db.Items.AsNoTracking()
                .CountAsync(i => rootIds.Contains(i.RootId) && (i.IndexedAt == null || i.IndexedAt < stamp) && i.Id > nextCursor, ct);
            return new ScanBatchResult(ScanPhase.Removals, stale.Count, remaining, nextCursor.ToString(System.Globalization.CultureInfo.InvariantCulture), 0, 0, gone.Count, 0);
        }

        /// <summary>
        /// A file that is gone is MARKED, never deleted.
        ///
        /// <para><b>Why marking and not deleting.</b> The brief asked for "delete the item subtree but keep the
        /// user's rows", and those two cannot both hold: <c>UserItemState.ItemId</c> is a foreign key to
        /// <c>Item</c>, so deleting the item either cascades the reader's position and marks away or fails the
        /// constraint. Keeping the reader's state is the requirement that matters — a file that comes back must
        /// come back to the page they were on — so the ROW stays and the item is flagged instead.</para>
        ///
        /// <para><b>What the flag does.</b> <c>Item.IsExcluded</c> takes it out of every browse, search, shelf
        /// and Explore surface through the existing <see cref="Access.ItemAccess"/> gate — no new exclusion
        /// semantics, no new predicate anywhere — and <c>ItemState.IsBroken</c> with the reason "missing" is
        /// what the admin's broken-file panel lists it under. The id, the position and the marks survive
        /// untouched, and <see cref="ClearMissingAsync"/> undoes both the moment a later scan finds the file.</para>
        ///
        /// <para>Nothing on the share is touched, and no derived row is deleted: the resolver and the reading
        /// order simply stop counting an excluded item.</para>
        /// </summary>
        public const string MissingReason = "missing";

        public static async Task<int> MarkMissingAsync(BooksDb db, IReadOnlyList<int> itemIds, CancellationToken ct = default)
        {
            var kept = await db.UserItemStates.CountAsync(s => itemIds.Contains(s.ItemId), ct);
            var now = DateTime.UtcNow;

            foreach (var item in await db.Items.Where(i => itemIds.Contains(i.Id)).ToListAsync(ct))
                item.IsExcluded = true;

            var states = await db.ItemStates.Where(s => itemIds.Contains(s.ItemId)).ToDictionaryAsync(s => s.ItemId, ct);
            foreach (var id in itemIds)
            {
                if (!states.TryGetValue(id, out var state))
                {
                    state = new ItemState { ItemId = id };
                    db.ItemStates.Add(state);
                }
                state.IsBroken = true;
                state.BrokenReason = MissingReason;
                state.BrokenCheckedAt = now;
                state.ExclusionReason = MissingReason;
                state.ExcludedAt = now;
            }
            await db.SaveChangesAsync(ct);
            return kept;
        }

        /// <summary>The other half of the mark: a file that is back clears both flags, so it returns to every
        /// surface it was on with its id, its position and its marks intact.</summary>
        public static async Task ClearMissingAsync(BooksDb db, Item item, ItemState? state, CancellationToken ct = default)
        {
            if (state == null || state.BrokenReason != MissingReason) return;
            item.IsExcluded = false;
            state.IsBroken = false;
            state.BrokenReason = null;
            state.BrokenCheckedAt = DateTime.UtcNow;
            state.ExclusionReason = null;
            state.ExcludedAt = null;
            await Task.CompletedTask;
        }

        // ── phase 4: aggregate ───────────────────────────────────────────────────────────────────────────

        private async Task<ScanBatchResult> AggregateAsync(BooksDb db, bool apply, CancellationToken ct)
        {
            if (apply)
            {
                await RefreshFolderAggregatesAsync(db, ct);
                await BackfillPublishersAsync(db, ct);
            }

            var runId = (int)await ReadLongAsync(db, RunIdKey, ct);
            var run = await db.ScanRuns.FirstOrDefaultAsync(r => r.Id == runId, ct);
            var added = (int)await ReadLongAsync(db, AddedKey, ct);
            var changed = (int)await ReadLongAsync(db, ChangedKey, ct);
            var removed = (int)await ReadLongAsync(db, RemovedKey, ct);
            if (run != null)
            {
                run.FinishedAt = DateTime.UtcNow;
                run.ItemsSeen = (int)await ReadLongAsync(db, SeenKey, ct);
                run.Added = added; run.Changed = changed; run.Removed = removed;
            }
            await WriteAsync(db, PhaseKey, ScanPhase.Done, ct);
            await StampFolderRegistryAsync(db, ct);
            await db.SaveChangesAsync(ct);

            logger.LogInformation("scan finished: added {Added}, changed {Changed}, removed {Removed}", added, changed, removed);
            // The counters are reported PER BATCH by the phases that earn them; the closing phase reports zero so
            // a driver that accumulates them (the CLI verb, the JobRunner) cannot double-count the run.
            return new ScanBatchResult(ScanPhase.Done, 0, 0, null, 0, 0, 0, 0);
        }

        /// <summary>`TopFolderId` (the "collection" a file belongs to), the direct-child count and the subtree item
        /// count — the three numbers the Directory view reads, computed here rather than at boot.</summary>
        public static async Task RefreshFolderAggregatesAsync(BooksDb db, CancellationToken ct = default)
        {
            var folders = await db.Folders.ToListAsync(ct);
            var byId = folders.ToDictionary(f => f.Id);
            var childrenOf = folders.Where(f => f.ParentId != null).GroupBy(f => f.ParentId!.Value)
                .ToDictionary(g => g.Key, g => g.ToList());

            foreach (var f in folders)
            {
                // The TOP folder is the highest ancestor below the root — a root's own child. That is the
                // "collection" the browse groups by.
                var node = f;
                var guard = 0;
                while (node.ParentId != null && byId.TryGetValue(node.ParentId.Value, out var parent) && parent.ParentId != null && guard++ < 64)
                    node = parent;
                f.TopFolderId = f.ParentId == null ? null : node.Id;
                f.DirectChildCount = childrenOf.GetValueOrDefault(f.Id)?.Count ?? 0;
            }

            var directItems = await db.Items.AsNoTracking().Where(i => !i.IsExcluded)
                .GroupBy(i => i.FolderId).Select(g => new { FolderId = g.Key, N = g.Count() }).ToListAsync(ct);
            var direct = directItems.ToDictionary(x => x.FolderId, x => x.N);

            // Deepest-first, so a parent's subtree total is the sum of finished children.
            var subtree = new Dictionary<int, int>();
            foreach (var f in folders.OrderByDescending(f => f.Depth))
                subtree[f.Id] = direct.GetValueOrDefault(f.Id)
                    + (childrenOf.GetValueOrDefault(f.Id)?.Sum(c => subtree.GetValueOrDefault(c.Id)) ?? 0);
            foreach (var f in folders) f.DescendantItemCount = subtree.GetValueOrDefault(f.Id);

            foreach (var i in await db.Items.ToListAsync(ct))
                i.TopFolderId = byId.TryGetValue(i.FolderId, out var fo) ? fo.TopFolderId ?? fo.Id : null;

            await db.SaveChangesAsync(ct);
        }

        /// <summary>Point each item at a normalized <see cref="Publisher"/> row, creating the ones that are new.</summary>
        public static async Task BackfillPublishersAsync(BooksDb db, CancellationToken ct = default)
        {
            var publishers = await db.Publishers.ToListAsync(ct);
            var byName = publishers.ToDictionary(p => p.Name, StringComparer.OrdinalIgnoreCase);
            var nextId = publishers.Count == 0 ? 1 : publishers.Max(p => p.Id) + 1;

            var rows = await (from i in db.Items
                              where i.PublisherId == null
                              join cd in db.ComicDetails on i.Id equals cd.ItemId into cds
                              from cd in cds.DefaultIfEmpty()
                              join bd in db.BookDetails on i.Id equals bd.ItemId into bds
                              from bd in bds.DefaultIfEmpty()
                              select new { i.Id, Comic = cd != null ? cd.Publisher : null, Book = bd != null ? bd.Publisher : null })
                             .ToListAsync(ct);

            var updates = new Dictionary<int, int>();
            foreach (var r in rows)
            {
                var name = (r.Comic ?? r.Book)?.Trim();
                if (string.IsNullOrEmpty(name)) continue;
                if (!byName.TryGetValue(name, out var p))
                {
                    p = new Publisher { Id = nextId++, Name = name };
                    db.Publishers.Add(p);
                    byName[name] = p;
                }
                updates[r.Id] = p.Id;
            }
            await db.SaveChangesAsync(ct);

            foreach (var item in await db.Items.Where(i => updates.Keys.Contains(i.Id)).ToListAsync(ct))
                item.PublisherId = updates[item.Id];
            await db.SaveChangesAsync(ct);
        }

        private static async Task StampFolderRegistryAsync(BooksDb db, CancellationToken ct)
        {
            const string name = "Folder.TopFolderId/Counts";
            var entry = DerivedTables.All.FirstOrDefault(e => e.Name == name);
            if (entry == null) return;
            var row = await db.DerivedTables.FirstOrDefaultAsync(d => d.Name == name, ct);
            if (row == null) { row = new DerivedTable { Name = name }; db.DerivedTables.Add(row); }
            row.RebuildJob = entry.RebuildJob;
            row.InputFingerprint = (await db.Folders.CountAsync(ct)) + ":" + DateTime.UtcNow.ToString("O");
            row.LastRebuiltAt = DateTime.UtcNow;
            row.RowCount = await db.Folders.CountAsync(ct);
        }

        // ── plumbing ─────────────────────────────────────────────────────────────────────────────────────

        public static ContainerFormat ContainerFor(string ext) => ext.ToLowerInvariant() switch
        {
            ".cbz" or ".zip" => ContainerFormat.Cbz,
            ".cbr" or ".rar" or ".cb7" or ".7z" => ContainerFormat.Cbr,
            ".pdf" => ContainerFormat.Pdf,
            ".epub" => ContainerFormat.Epub,
            ".mobi" or ".azw3" => ContainerFormat.Mobi,
            _ => ContainerFormat.Unknown,
        };

        /// <summary>Lower-case, punctuation folded to spaces, whitespace collapsed — the sort/search key form.</summary>
        public static string Normalize(string? s)
        {
            if (string.IsNullOrWhiteSpace(s)) return "";
            var chars = s.Trim().ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : ' ');
            return string.Join(' ', new string(chars.ToArray()).Split(' ', StringSplitOptions.RemoveEmptyEntries));
        }

        public static IEnumerable<string> SplitNames(string? blob)
        {
            if (string.IsNullOrWhiteSpace(blob)) yield break;
            foreach (var part in blob.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var name = part.Trim();
                if (name.Length > 0) yield return name;
            }
        }

        private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];

        private IEnumerable<string> SafeEnumerate(Func<IEnumerable<string>> enumerate)
        {
            IEnumerator<string> it;
            try { it = enumerate().GetEnumerator(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { logger.LogWarning("scan: {Message}", ex.Message); yield break; }
            using (it)
            {
                while (true)
                {
                    try { if (!it.MoveNext()) break; }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { logger.LogWarning("scan: {Message}", ex.Message); break; }
                    yield return it.Current;
                }
            }
        }

        private static List<(int RootId, string Path, int? ParentId)> ParseFrontier(string? raw)
        {
            var list = new List<(int, string, int?)>();
            if (string.IsNullOrEmpty(raw)) return list;
            foreach (var line in raw.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var p = line.Split(Sep[0]);
                if (p.Length < 2) continue;
                int? parent = p.Length > 2 && int.TryParse(p[2], out var pid) ? pid : null;
                list.Add((int.Parse(p[0]), p[1], parent));
            }
            return list;
        }

        private static async Task<TEntity> LoadAsync<TEntity>(
            DbSet<TEntity> set, System.Linq.Expressions.Expression<Func<TEntity, bool>> predicate,
            Func<TEntity> create, BooksDb db, CancellationToken ct) where TEntity : class
        {
            var row = await set.FirstOrDefaultAsync(predicate, ct);
            if (row != null) return row;
            row = create();
            set.Add(row);
            return row;
        }

        /// <summary>The next free id. `Item.Id` and `Folder.Id` are never generated by the database — the
        /// migration preserved v1's ids and the 141k thumbnail files are named by them — so the scanner
        /// allocates. `MaxAsync` over a nullable projection is what SQLite can translate for an empty table.</summary>
        private static async Task<long> NextIdAsync(IQueryable<long?> ids, CancellationToken ct) =>
            (await ids.MaxAsync(ct) ?? 0) + 1;

        private async Task<DateTime> RunStampAsync(BooksDb db, CancellationToken ct) =>
            DateTime.TryParse(await ReadAsync(db, RunStampKey, ct), null, System.Globalization.DateTimeStyles.RoundtripKind, out var d)
                ? d : DateTime.UtcNow;

        private static Task<long> CountStampedFoldersAsync(BooksDb db, DateTime stamp, CancellationToken ct) =>
            db.Folders.AsNoTracking().LongCountAsync(f => f.IndexedAt >= stamp, ct);

        private static Task<long> CountStaleItemsAsync(BooksDb db, DateTime stamp, CancellationToken ct) =>
            db.Items.AsNoTracking().LongCountAsync(i => i.IndexedAt == null || i.IndexedAt < stamp, ct);

        private static Task<SystemState?> RowAsync(BooksDb db, string key, CancellationToken ct) =>
            db.SystemStates.FirstOrDefaultAsync(s => s.Key == key, ct);

        private static async Task<string?> ReadAsync(BooksDb db, string key, CancellationToken ct) =>
            (await db.SystemStates.AsNoTracking().FirstOrDefaultAsync(s => s.Key == key, ct))?.Value;

        private static async Task<long> ReadLongAsync(BooksDb db, string key, CancellationToken ct) =>
            long.TryParse(await ReadAsync(db, key, ct), out var v) ? v : 0;

        private static async Task WriteAsync(BooksDb db, string key, string value, CancellationToken ct)
        {
            var row = await RowAsync(db, key, ct);
            if (row == null) db.SystemStates.Add(new SystemState { Key = key, Value = value });
            else row.Value = value;
        }

        private static Task WriteLongAsync(BooksDb db, string key, long value, CancellationToken ct) =>
            WriteAsync(db, key, value.ToString(System.Globalization.CultureInfo.InvariantCulture), ct);

        private static async Task AddLongAsync(BooksDb db, string key, long delta, CancellationToken ct)
        {
            var row = await RowAsync(db, key, ct);
            var current = long.TryParse(row?.Value, out var v) ? v : 0;
            var next = (current + delta).ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (row == null) db.SystemStates.Add(new SystemState { Key = key, Value = next });
            else row.Value = next;
        }

        /// <summary>A dry-run preview: what a scan WOULD add, change and remove, without writing a row.</summary>
        public sealed record ScanPreview(int WouldAdd, int WouldChange, int WouldRemove, int Folders, int Files)
        {
            public override string ToString() =>
                $"{{ wouldAdd: {WouldAdd}, wouldChange: {WouldChange}, wouldRemove: {WouldRemove}, folders: {Folders}, files: {Files} }}";
        }

        /// <summary>
        /// Count what a scan would do, touching nothing. This is what `books-scan` prints WITHOUT `--apply`:
        /// a destructive job states its damage before it does it.
        /// </summary>
        public async Task<ScanPreview> PreviewAsync(BooksDb db, int? rootId = null, CancellationToken ct = default)
        {
            var roots = await db.LibraryRoots.AsNoTracking()
                .Where(r => r.Enabled && (rootId == null || r.Id == rootId)).ToListAsync(ct);
            var known = await db.Items.AsNoTracking()
                .Where(i => roots.Select(r => r.Id).Contains(i.RootId))
                .Select(i => new { i.Id, i.Path, i.FileSize, i.FileModifiedAt }).ToListAsync(ct);
            var byPath = known.ToDictionary(i => i.Path, StringComparer.OrdinalIgnoreCase);

            int add = 0, change = 0, folders = 0, files = 0;
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var root in roots)
            {
                if (!Fs.DirectoryExists(root.Path)) throw new InvalidOperationException($"Library root {root.Id} is unreachable.");
                var stack = new Stack<string>();
                stack.Push(root.Path);
                while (stack.Count > 0)
                {
                    ct.ThrowIfCancellationRequested();
                    var dir = stack.Pop();
                    folders++;
                    foreach (var sub in SafeEnumerate(() => Fs.EnumerateDirectories(dir))) stack.Push(sub);
                    foreach (var file in SafeEnumerate(() => Fs.EnumerateFiles(dir)))
                    {
                        if (!SupportedExtensions.Contains(Path.GetExtension(file))) continue;
                        files++;
                        seen.Add(file);
                        if (!byPath.TryGetValue(file, out var row)) { add++; continue; }
                        var (size, modified) = Fs.FileInfo(file);
                        if (row.FileSize != size || row.FileModifiedAt != modified) change++;
                    }
                }
            }
            var remove = known.Count(i => !seen.Contains(i.Path));
            return new ScanPreview(add, change, remove, folders, files);
        }

        /// <summary>Deserialize the frontier for a status report (used by the admin's scan panel).</summary>
        public static int FrontierLength(string? raw) => ParseFrontier(raw).Count;

        internal static string Json(object o) => JsonSerializer.Serialize(o);
    }
}
