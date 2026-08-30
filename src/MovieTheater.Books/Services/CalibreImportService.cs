using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using MovieTheater.Books.Db;

namespace MovieTheater.Books.Services
{
    /// <summary>What one Calibre-import batch did.</summary>
    public sealed record CalibreBatchResult(
        int Processed, long Remaining, long? NextCursor, int Matched, int Unmatched, int Filled,
        int Repathed = 0, int FoldersFixed = 0)
    {
        public bool Done => Processed == 0 || NextCursor == null;
        public override string ToString() =>
            $"{{ processed: {Processed}, remaining: {Remaining}, nextCursor: \"{NextCursor}\", unmatched: {Unmatched} }}"
            + $"  [matched: {Matched}, filled: {Filled}, repathed: {Repathed}, folders-fixed: {FoldersFixed}]";
    }

    /// <summary>
    /// Fill a book's Calibre-native identity from the Calibre library's own <c>metadata.db</c>.
    ///
    /// <para><b>Why this job exists at all.</b> `BookDetail.SeriesName` is NULL for every one of the 22,084
    /// books the migration carried, because v1 never had a column for it — the standalone site read Calibre's
    /// series live and threw it away. Publisher, published date, language, ISBN and the subject tags are in the
    /// same position. This job is what fills them, and until it has run the novels facets have no series rail.</para>
    ///
    /// <para><b>What it writes:</b> `Item.CalibreBookId`, `BookDetail` (Isbn, SeriesName, SeriesIndex, Publisher,
    /// PublishedOn, Language, Description), `ItemCredit(Source=Calibre, Role=Author)` and
    /// `ItemTag(Source=Calibre)`. Nothing else — and only rows whose Source is Calibre, so a re-run never
    /// disturbs another leg's credits or tags.</para>
    ///
    /// <para><b>How a book is matched</b>, in order: the link file's explicit <c>comicId → calibreId</c> pair
    /// (the standalone's own record of the pairing, so historical matches are preserved exactly); then an
    /// `Item.CalibreBookId` already stored; then the resolved file path. A book that matches nothing is COUNTED
    /// and reported, never guessed at.</para>
    ///
    /// <para><b>It also RE-PATHS what Calibre renamed.</b> Calibre is the source of truth for a book's folder
    /// and file name as well as its title, and it renames both whenever the title or the author changes. The id
    /// match still succeeds after such a rename (that is the point of storing `CalibreBookId`), but `Item.Path`,
    /// `Item.FileName` and the `Folder` rows the Directory view renders are then STALE, and nothing else repairs
    /// them short of a full `books-scan` over 54k listings on the share. So when a matched book's stored path is
    /// not one of the paths Calibre's own row composes, this job re-points the item and fixes the folder
    /// bookkeeping DB-only — see <see cref="RepathAsync"/>. Items with no Calibre identity are never touched.</para>
    ///
    /// <para><b>Chunked and idempotent</b> like every bulk job: the cursor is the Calibre book id, which is the
    /// batch query's own ordering, and re-running writes the same values (a second pass re-paths 0).</para>
    /// </summary>
    public sealed class CalibreImportService
    {
        public const string CursorKey = "books:calibre:cursor";
        public const string MatchedKey = "books:calibre:matched";
        public const string UnmatchedKey = "books:calibre:unmatched";
        public const int DefaultBatchSize = 500;

        private readonly ILogger<CalibreImportService> logger;
        public CalibreImportService(ILogger<CalibreImportService> logger) => this.logger = logger;

        /// <summary>One row of the standalone's <c>calibre_link.json</c> — the record of which item is which book.</summary>
        public sealed record Link(int ComicId, int CalibreId);

        /// <summary>
        /// Read the link file. Its shape is the standalone's:
        /// <c>[{ "comicId": 101, "calibreId": 844, "series": …, "seriesIndex": …, "isbn": …, "authors": [], "tags": [], "title": … }]</c>.
        /// Only the two ids are taken — every other field is re-read from Calibre itself, so a stale link file
        /// cannot pin stale metadata.
        /// </summary>
        public static List<Link> ReadLinks(string? path)
        {
            var links = new List<Link>();
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return links;
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return links;
            foreach (var e in doc.RootElement.EnumerateArray())
            {
                if (!e.TryGetProperty("comicId", out var c) || !e.TryGetProperty("calibreId", out var k)) continue;
                if (c.TryGetInt32(out var comicId) && k.TryGetInt32(out var calibreId)) links.Add(new Link(comicId, calibreId));
            }
            return links;
        }

        /// <summary>One Calibre book, as the metadata query returns it.</summary>
        public sealed record CalibreBook(
            int Id, string? Title, string? RelPath, string? FileName, string? PubDate, string? Isbn,
            string? Authors, string? Series, double? SeriesIndex, string? Description, string? Publisher,
            string? Tags, string? Language, string? Formats = null);

        /// <summary>
        /// Calibre's own schema, read-only. The scalar sub-selects mirror the standalone's query exactly, so a
        /// book with two authors or no series behaves the way it always did.
        /// </summary>
        private const string BookSql = @"
SELECT b.id, b.title, b.path, b.pubdate,
       (SELECT i.val FROM identifiers i WHERE i.book = b.id AND i.type = 'isbn' LIMIT 1) AS isbn, -- Calibre keeps ISBNs in identifiers, never on books
       (SELECT group_concat(a.name, ' & ') FROM books_authors_link bal JOIN authors a ON a.id = bal.author WHERE bal.book = b.id) AS authors,
       (SELECT d.name FROM data d WHERE d.book = b.id ORDER BY d.id LIMIT 1) AS file_name,
       (SELECT s.name FROM books_series_link bsl JOIN series s ON s.id = bsl.series WHERE bsl.book = b.id LIMIT 1) AS series,
       b.series_index,
       (SELECT c.text FROM comments c WHERE c.book = b.id LIMIT 1) AS comment,
       (SELECT p.name FROM books_publishers_link bpl JOIN publishers p ON p.id = bpl.publisher WHERE bpl.book = b.id LIMIT 1) AS publisher,
       (SELECT group_concat(t.name, ', ') FROM books_tags_link btl JOIN tags t ON t.id = btl.tag WHERE btl.book = b.id) AS tags,
       (SELECT l.lang_code FROM books_languages_link bll JOIN languages l ON l.id = bll.lang_code WHERE bll.book = b.id LIMIT 1) AS lang,
       (SELECT group_concat(lower(d.format), ',') FROM data d WHERE d.book = b.id) AS formats
FROM books b WHERE b.id > $after ORDER BY b.id LIMIT $n";

        public static SqliteConnection OpenCalibre(string metadataDbPath)
        {
            if (!File.Exists(metadataDbPath)) throw new FileNotFoundException($"Calibre metadata.db not found at {metadataDbPath}");
            var conn = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = metadataDbPath,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false,
            }.ToString());
            conn.Open();
            return conn;
        }

        public static List<CalibreBook> ReadBooks(SqliteConnection calibre, long after, int batchSize)
        {
            using var cmd = calibre.CreateCommand();
            cmd.CommandText = BookSql;
            cmd.Parameters.AddWithValue("$after", after);
            cmd.Parameters.AddWithValue("$n", batchSize);
            using var rd = cmd.ExecuteReader();
            var list = new List<CalibreBook>();
            while (rd.Read())
                list.Add(new CalibreBook(
                    rd.GetInt32(0),
                    Str(rd, 1), Str(rd, 2), Str(rd, 6), Str(rd, 3), Str(rd, 4), Str(rd, 5),
                    Str(rd, 7), rd.IsDBNull(8) ? null : rd.GetDouble(8),
                    Str(rd, 9), Str(rd, 10), Str(rd, 11), Str(rd, 12), Str(rd, 13)));
            return list;
        }

        private static string? Str(SqliteDataReader rd, int i) => rd.IsDBNull(i) ? null : rd.GetValue(i)?.ToString();

        public async Task ResetAsync(BooksDb db, CancellationToken ct = default)
        {
            foreach (var key in new[] { CursorKey, MatchedKey, UnmatchedKey })
            {
                var row = await db.SystemStates.FirstOrDefaultAsync(s => s.Key == key, ct);
                if (row != null) db.SystemStates.Remove(row);
            }
            await db.SaveChangesAsync(ct);
        }

        /// <summary>
        /// One bounded batch of Calibre books. <paramref name="apply"/> false counts what WOULD be filled and
        /// writes nothing — the dry run a destructive-looking job owes its caller.
        /// </summary>
        public async Task<CalibreBatchResult> RunBatchAsync(
            BooksDb db, string metadataDbPath, string? linkPath, int batchSize, bool apply = true, string? libraryRoot = null, long? after = null, CancellationToken ct = default)
        {
            batchSize = Math.Clamp(batchSize, 1, 5_000);
            using var calibre = OpenCalibre(metadataDbPath);
            // The path match composes <library root>\<calibre path>\<file> and compares it with Item.Path. When the
            // metadata.db in hand is a COPY (the house rule: never scan the share for what a copy already knows),
            // the caller passes the library's real root; the metadata's own folder is only the default.
            libraryRoot ??= Path.GetDirectoryName(Path.GetFullPath(metadataDbPath))!;

            // A dry run persists nothing, so its caller hands back the previous batch's cursor (`after`).
            var cursor = after ?? await ReadLongAsync(db, CursorKey, ct);
            var books = ReadBooks(calibre, cursor, batchSize);
            if (books.Count == 0)
            {
                var total = Scalar(calibre, "SELECT count(*) FROM books");
                return new CalibreBatchResult(0, 0, null, (int)await ReadLongAsync(db, MatchedKey, ct), (int)await ReadLongAsync(db, UnmatchedKey, ct), 0);
            }

            var itemByCalibreId = ReadLinks(linkPath).ToDictionary(l => l.CalibreId, l => l.ComicId);
            var calibreIds = books.Select(b => b.Id).ToList();
            var byStoredId = await db.Items.Where(i => i.CalibreBookId != null && calibreIds.Contains(i.CalibreBookId!.Value))
                .ToDictionaryAsync(i => i.CalibreBookId!.Value, ct);

            var linkedItemIds = books.Select(b => itemByCalibreId.GetValueOrDefault(b.Id, 0)).Where(id => id > 0).ToList();
            var byItemId = await db.Items.Where(i => linkedItemIds.Contains(i.Id)).ToDictionaryAsync(i => i.Id, ct);

            var paths = books.SelectMany(b => ResolvePaths(libraryRoot, b)).ToList();
            var byPath = await db.Items.Where(i => paths.Contains(i.Path))
                .ToDictionaryAsync(i => i.Path, StringComparer.OrdinalIgnoreCase, ct);

            int matched = 0, unmatched = 0, filled = 0, repathed = 0, foldersFixed = 0;
            // Folders emptied by a re-path are only PROVABLY empty once the batch's moves are saved, so the
            // candidates are collected here and swept after SaveChanges — never deleted on a guess.
            var huskCandidates = new HashSet<int>();
            foreach (var book in books)
            {
                ct.ThrowIfCancellationRequested();
                var (item, matchedBy) = Resolve(book, itemByCalibreId, byStoredId, byItemId, byPath, libraryRoot);
                if (item == null) { unmatched++; continue; }
                matched++;

                // A book matched BY PATH is by definition already at one of these paths; only the id-based
                // matches (link file / stored CalibreBookId) can be pointing at a folder Calibre has renamed.
                var candidates = ResolvePaths(libraryRoot, book);
                var needsRepath = matchedBy != MatchedBy.Path && candidates.Count > 0
                    && !candidates.Any(p => string.Equals(p, item.Path, StringComparison.OrdinalIgnoreCase));

                if (!apply) { filled++; if (needsRepath) repathed++; continue; }

                if (needsRepath)
                {
                    repathed++;
                    foldersFixed += await RepathAsync(db, item, candidates, huskCandidates, ct);
                }

                item.CalibreBookId = book.Id;
                if (item.Kind != ItemKind.Book) item.Kind = ItemKind.Book;

                // THE TITLE IS PART OF THE CALIBRE IDENTITY. Without this the scan's fallback
                // stands, and that fallback is the FILE NAME — the site then shows
                // "Planeswalker - Lynn Abbey.epub" and "[Survivalist 05] - Resurrecting Ho".
                // ItemResolver passes a book's Item.Title straight through to ResolvedTitle, so
                // nothing downstream repairs it either. Calibre already holds the real title and
                // reading it here is free, which beats the alternative the scanner used to rely
                // on: opening every EPUB to ask its embedded metadata for a title that Calibre
                // itself had written there.
                var calibreTitle = Blank(book.Title);
                if (calibreTitle != null && !string.Equals(item.Title, calibreTitle, StringComparison.Ordinal))
                {
                    item.Title = calibreTitle;
                    item.NormalizedTitle = LibraryScanner.Normalize(calibreTitle);
                }

                var detail = await db.BookDetails.FirstOrDefaultAsync(b => b.ItemId == item.Id, ct);
                if (detail == null) { detail = new BookDetail { ItemId = item.Id }; db.BookDetails.Add(detail); }
                detail.Isbn = Blank(book.Isbn) ?? detail.Isbn;
                detail.SeriesName = Blank(book.Series);
                detail.SeriesIndex = book.Series == null ? null : book.SeriesIndex;
                detail.Publisher = Blank(book.Publisher) ?? detail.Publisher;
                detail.PublishedOn = Blank(book.PubDate) ?? detail.PublishedOn;
                detail.Language = Blank(book.Language) ?? detail.Language;
                detail.Description = Blank(book.Description) ?? detail.Description;

                await RewriteCalibreRowsAsync(db, item.Id, book, ct);
                filled++;
            }

            var nextCursor = books[^1].Id;
            if (apply)
            {
                await WriteLongAsync(db, CursorKey, nextCursor, ct);
                await AddLongAsync(db, MatchedKey, matched, ct);
                await AddLongAsync(db, UnmatchedKey, unmatched, ct);
                await db.SaveChangesAsync(ct);
                foldersFixed += await SweepHusksAsync(db, huskCandidates, ct);
            }

            var remaining = Scalar(calibre, "SELECT count(*) FROM books WHERE id > " + nextCursor);
            logger.LogInformation("calibre batch: processed {N}, matched {Matched}, unmatched {Unmatched}, repathed {Repathed}, remaining {Remaining}",
                books.Count, matched, unmatched, repathed, remaining);
            return new CalibreBatchResult(books.Count, remaining, nextCursor, matched, unmatched, filled, repathed, foldersFixed);
        }

        /// <summary>
        /// Calibre's authors are a single <c>' &amp; '</c>-joined string and its tags a comma-joined one; both
        /// become ROWS, which is what lets `?author=` find a book by EITHER name.
        /// </summary>
        private static async Task RewriteCalibreRowsAsync(BooksDb db, int itemId, CalibreBook book, CancellationToken ct)
        {
            var oldCredits = await db.ItemCredits.Where(c => c.ItemId == itemId && c.Source == TagSource.Calibre).ToListAsync(ct);
            db.ItemCredits.RemoveRange(oldCredits);
            var ordinal = 0;
            foreach (var name in (book.Authors ?? "").Split(" & ", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                db.ItemCredits.Add(new ItemCredit
                {
                    ItemId = itemId, Source = TagSource.Calibre, Ordinal = ordinal++,
                    Role = "Author", Name = name, NormalizedName = LibraryScanner.Normalize(name),
                });

            var oldTags = await db.ItemTags.Where(t => t.ItemId == itemId && t.Source == TagSource.Calibre).ToListAsync(ct);
            db.ItemTags.RemoveRange(oldTags);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var tag in (book.Tags ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                if (seen.Add(tag))
                    db.ItemTags.Add(new ItemTag { ItemId = itemId, Category = "tag", Value = tag, Source = TagSource.Calibre });
        }

        /// <summary>How a book reached its item — the id-based matches are the ones whose PATH may be stale.</summary>
        public enum MatchedBy { None, Link, StoredId, Path }

        private static (Item? Item, MatchedBy By) Resolve(
            CalibreBook book, IReadOnlyDictionary<int, int> linkByCalibreId,
            IReadOnlyDictionary<int, Item> byStoredId, IReadOnlyDictionary<int, Item> byItemId,
            IReadOnlyDictionary<string, Item> byPath, string libraryRoot)
        {
            if (linkByCalibreId.TryGetValue(book.Id, out var itemId) && byItemId.TryGetValue(itemId, out var linked)) return (linked, MatchedBy.Link);
            if (byStoredId.TryGetValue(book.Id, out var stored)) return (stored, MatchedBy.StoredId);
            foreach (var path in ResolvePaths(libraryRoot, book))
                if (byPath.TryGetValue(path, out var byFile)) return (byFile, MatchedBy.Path);
            return (null, MatchedBy.None);
        }

        // ── re-path: what Calibre renamed, without a rescan ──────────────────────────────────────────────

        /// <summary>
        /// Move one item onto the path Calibre's own row composes, and repair the `Folder` rows the Directory
        /// view renders — DB-only, nothing on the share is touched or even read.
        ///
        /// <para>Three renames are possible and all three happen in one Calibre pass: the TITLE folder
        /// (<c>101 Places Not to See Before You D (1234)</c> → the untruncated name), the AUTHOR folder above it,
        /// and the file itself (<c>&lt;title&gt; - &lt;author&gt;.&lt;ext&gt;</c>). An author rename can also MERGE —
        /// <c>Adaptation (epub)</c> → <c>Unknown</c> when an <c>Unknown</c> folder already exists — so the leaf is
        /// re-parented into the survivor rather than a second row being created at the same path.</para>
        ///
        /// <para>Which candidate is chosen: the one whose extension is the item's own, so a book Calibre holds as
        /// both an EPUB and a PDF re-paths each item onto its OWN file instead of collapsing the two. The first
        /// candidate is the fallback (a format Calibre has since dropped).</para>
        ///
        /// <para>Returns how many FOLDER rows it touched. Idempotent: the second pass finds the item's path
        /// already among the candidates and never gets here.</para>
        /// </summary>
        private static async Task<int> RepathAsync(
            BooksDb db, Item item, IReadOnlyList<string> candidates, HashSet<int> huskCandidates, CancellationToken ct)
        {
            var chosen = candidates.FirstOrDefault(p =>
                             string.Equals(Path.GetExtension(p), item.Extension, StringComparison.OrdinalIgnoreCase))
                         ?? candidates[0];
            var newFolderPath = Path.GetDirectoryName(chosen);
            var fixes = 0;

            var folder = await db.Folders.FirstOrDefaultAsync(f => f.Id == item.FolderId, ct);
            if (folder != null && newFolderPath != null && !PathEq(folder.Path, newFolderPath))
            {
                var newParentPath = Path.GetDirectoryName(newFolderPath);
                var parent = folder.ParentId is int pid ? await db.Folders.FirstOrDefaultAsync(f => f.Id == pid, ct) : null;
                // Where the leaf ACTUALLY sits after step 1 — a re-parent moves the row without touching its
                // Path string, so this, not the stale string, is what step 2's guard has to read.
                var parentPath = parent?.Path ?? Path.GetDirectoryName(folder.Path);

                // 1. the AUTHOR folder. Renamed in place when nothing else holds the new name; when something
                //    does, this leaf moves under it and the emptied row becomes a husk candidate.
                //
                // The two guards are what keep this from touching rows it has no business touching. A library
                // ROOT is never renamed (ParentId == null), and the author folder must stay under the SAME
                // grandparent — a "rename" that moved it somewhere else entirely would mean the row is not
                // shaped the way this library is, and dragging its whole subtree along would take unrelated
                // items with it. When either guard fails the ITEM is still re-pointed (that is the path the
                // reader and the media plane use) and the Folder rows are left to books-scan, which owns them.
                if (parent != null && newParentPath != null && !PathEq(parent.Path, newParentPath)
                    && parent.ParentId != null
                    && PathEq(Path.GetDirectoryName(parent.Path), Path.GetDirectoryName(newParentPath)))
                {
                    var survivor = await db.Folders.FirstOrDefaultAsync(f => f.Path == newParentPath, ct);
                    if (survivor == null)
                    {
                        fixes += RenameFolder(db, parent, newParentPath);
                        parentPath = parent.Path;
                    }
                    else if (survivor.Id != parent.Id)
                    {
                        Reparent(folder, parent, survivor);
                        huskCandidates.Add(parent.Id);
                        parentPath = survivor.Path;
                        fixes++;
                    }
                }

                // 2. the TITLE folder. Same two shapes — the rename is the common one; the merge happens when a
                //    truncated name expands onto a folder that already exists. (Renaming the author folder above
                //    already carried this row's path with it, so this is a no-op unless the title changed too.)
                if (!PathEq(folder.Path, newFolderPath) && PathEq(parentPath, newParentPath))
                {
                    var survivor = await db.Folders.FirstOrDefaultAsync(f => f.Path == newFolderPath, ct);
                    if (survivor == null) fixes += RenameFolder(db, folder, newFolderPath);
                    else if (survivor.Id != folder.Id)
                    {
                        item.FolderId = survivor.Id;
                        folder.DescendantItemCount = Math.Max(0, folder.DescendantItemCount - 1);
                        survivor.DescendantItemCount += 1;
                        huskCandidates.Add(folder.Id);
                        folder = survivor;
                        fixes++;
                    }
                }

                item.TopFolderId = TopOf(folder);
            }

            // The prefix sweep above already carried the item's directory; this states the whole answer, which
            // also covers the case where ONLY the file name changed.
            item.Path = chosen;
            item.FileName = Path.GetFileName(chosen);
            item.Extension = Path.GetExtension(chosen).ToLowerInvariant();
            return fixes;
        }

        /// <summary>The "collection" a folder belongs to — a top folder is its own (see the scanner's aggregate pass).</summary>
        private static int TopOf(Folder f) => f.TopFolderId ?? f.Id;

        /// <summary>Move a folder under a new parent, carrying the counts both parents keep.</summary>
        private static void Reparent(Folder folder, Folder oldParent, Folder newParent)
        {
            folder.ParentId = newParent.Id;
            folder.TopFolderId = TopOf(newParent);
            oldParent.DirectChildCount = Math.Max(0, oldParent.DirectChildCount - 1);
            newParent.DirectChildCount += 1;
            oldParent.DescendantItemCount = Math.Max(0, oldParent.DescendantItemCount - folder.DescendantItemCount);
            newParent.DescendantItemCount += folder.DescendantItemCount;
        }

        /// <summary>
        /// Rename one folder row and re-prefix every descendant folder and item path under it. Returns the number
        /// of folder rows touched.
        ///
        /// <para>The descendant queries hit the DB, so EF hands back the TRACKED instances of rows this batch has
        /// already re-pathed — which no longer carry the old prefix. Every rewrite therefore re-checks the prefix
        /// first: without that, a row fixed a moment ago would have the old path's LENGTH sliced off its new
        /// path.</para>
        /// </summary>
        private static int RenameFolder(BooksDb db, Folder folder, string newPath)
        {
            var oldPath = folder.Path;
            var prefix = oldPath + Path.DirectorySeparatorChar;
            folder.Path = newPath;
            folder.Name = Path.GetFileName(newPath.TrimEnd('\\', '/'));
            folder.NormalizedName = LibraryScanner.Normalize(folder.Name);
            var touched = 1;

            foreach (var d in db.Folders.Where(f => f.Path.StartsWith(prefix)).ToList())
                if (d.Path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    d.Path = newPath + d.Path[oldPath.Length..];
                    touched++;
                }

            foreach (var i in db.Items.Where(x => x.Path.StartsWith(prefix)).ToList())
                if (i.Path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    i.Path = newPath + i.Path[oldPath.Length..];

            return touched;
        }

        /// <summary>
        /// Delete the folder rows a re-path emptied — and ONLY those: a row that still holds an item or a child
        /// folder stays. Run after the batch is saved, so the emptiness is a fact about the file and not about a
        /// change tracker that has not been flushed.
        /// </summary>
        private static async Task<int> SweepHusksAsync(BooksDb db, HashSet<int> candidates, CancellationToken ct)
        {
            if (candidates.Count == 0) return 0;
            var deleted = 0;
            foreach (var id in candidates)
            {
                var folder = await db.Folders.FirstOrDefaultAsync(f => f.Id == id, ct);
                if (folder == null) continue;
                if (await db.Items.AnyAsync(i => i.FolderId == id, ct)) continue;
                if (await db.Folders.AnyAsync(f => f.ParentId == id, ct)) continue;
                db.Folders.Remove(folder);
                deleted++;
            }
            if (deleted > 0) await db.SaveChangesAsync(ct);
            candidates.Clear();
            return deleted;
        }

        private static bool PathEq(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

        /// <summary>Calibre stores <c>&lt;library&gt;/&lt;book.path&gt;/&lt;data.name&gt;.epub</c> with forward slashes.</summary>
        /// <summary>The first candidate path (the book's first format); tests and the folder match use it.</summary>
        public static string? ResolvePath(string libraryRoot, CalibreBook book) => ResolvePaths(libraryRoot, book).FirstOrDefault();

        /// <summary>
        /// One candidate path per format Calibre holds for the book. A book with a PDF and an EPUB is two files on
        /// the share, and the catalog may hold either (or both, as separate items) - the first data row is not
        /// enough (that is how the 2026-08-26 import matched 0 books by path).
        /// </summary>
        public static IReadOnlyList<string> ResolvePaths(string libraryRoot, CalibreBook book)
        {
            if (string.IsNullOrWhiteSpace(book.RelPath) || string.IsNullOrWhiteSpace(book.FileName)) return Array.Empty<string>();
            var dir = Path.Combine(libraryRoot, book.RelPath.Replace('/', Path.DirectorySeparatorChar));
            var formats = (book.Formats ?? "epub").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            return formats.Select(f => Path.Combine(dir, book.FileName + "." + f)).ToList();
        }

        private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

        private static long Scalar(SqliteConnection conn, string sql)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            var o = cmd.ExecuteScalar();
            return o == null || o is DBNull ? 0 : Convert.ToInt64(o, System.Globalization.CultureInfo.InvariantCulture);
        }

        private static async Task<long> ReadLongAsync(BooksDb db, string key, CancellationToken ct)
        {
            var row = await db.SystemStates.AsNoTracking().FirstOrDefaultAsync(s => s.Key == key, ct);
            return long.TryParse(row?.Value, out var v) ? v : 0;
        }

        private static async Task WriteLongAsync(BooksDb db, string key, long value, CancellationToken ct)
        {
            var row = await db.SystemStates.FirstOrDefaultAsync(s => s.Key == key, ct);
            var text = value.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (row == null) db.SystemStates.Add(new SystemState { Key = key, Value = text });
            else row.Value = text;
        }

        private static async Task AddLongAsync(BooksDb db, string key, long delta, CancellationToken ct)
        {
            var row = await db.SystemStates.FirstOrDefaultAsync(s => s.Key == key, ct);
            var current = long.TryParse(row?.Value, out var v) ? v : 0;
            var text = (current + delta).ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (row == null) db.SystemStates.Add(new SystemState { Key = key, Value = text });
            else row.Value = text;
        }
    }
}
