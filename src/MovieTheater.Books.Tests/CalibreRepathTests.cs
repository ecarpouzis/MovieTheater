using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using MovieTheater.Books.Db;
using MovieTheater.Books.Services;

namespace MovieTheater.Books.Tests
{
    /// <summary>
    /// <c>books-import-calibre</c>'s PATH RE-SYNC: Calibre renames a book's title folder, its author folder and
    /// its file whenever the metadata changes, and the id match survives that while `Item.Path` and the `Folder`
    /// rows the Directory view renders do not. These tests drive the rename through a throwaway Calibre
    /// <c>metadata.db</c> and assert the catalog follows — without a rescan and without touching a share.
    /// </summary>
    public class CalibreRepathTests
    {
        private const string LibraryRoot = @"\\nas\books";
        private const int AuthorFolderId = 20, TitleFolderId = 21, OtherAuthorFolderId = 30, OtherTitleFolderId = 31;
        private const int OtherItemId = 103;

        private static V1Fixture Migrated()
        {
            var f = new V1Fixture();
            var summary = f.Engine(f.Options()).Run();
            if (summary.Stopped) throw new InvalidOperationException("fixture migration stopped: " + summary.StopReason);
            return f;
        }

        private static CalibreImportService Importer() => new(NullLogger<CalibreImportService>.Instance);

        /// <summary>
        /// Reshape the fixture's Calibre book (item 101) into the layout the real library has:
        /// <c>&lt;root&gt;\&lt;author&gt;\&lt;title (calibreId)&gt;\&lt;title&gt; - &lt;author&gt;.&lt;ext&gt;</c>,
        /// with the author folder as the item's top folder. Plus a SECOND author folder holding its own book, so
        /// a merge has something real to merge into.
        /// </summary>
        private static async Task ShapeLikeCalibreAsync(V1Fixture f, string ext = ".epub")
        {
            await using var db = f.HotDb();
            var root = await db.Folders.FirstAsync(x => x.Path == LibraryRoot);

            Folder Add(int id, int? parentId, string path, int topFolderId, int depth) => new()
            {
                Id = id, RootId = root.RootId, ParentId = parentId, Kind = ItemKind.Book, Path = path,
                Name = Path.GetFileName(path), NormalizedName = LibraryScanner.Normalize(Path.GetFileName(path)),
                Depth = depth, TopFolderId = topFolderId, DirectChildCount = 0, DescendantItemCount = 0,
            };

            var author = Add(AuthorFolderId, root.Id, $@"{LibraryRoot}\Aldous Huxley", AuthorFolderId, 1);
            author.DirectChildCount = 1;
            author.DescendantItemCount = 1;
            var title = Add(TitleFolderId, AuthorFolderId, $@"{LibraryRoot}\Aldous Huxley\Brave New World (844)", AuthorFolderId, 2);
            title.DescendantItemCount = 1;

            var other = Add(OtherAuthorFolderId, root.Id, $@"{LibraryRoot}\Unknown", OtherAuthorFolderId, 1);
            other.DirectChildCount = 1;
            other.DescendantItemCount = 1;
            var otherTitle = Add(OtherTitleFolderId, OtherAuthorFolderId, $@"{LibraryRoot}\Unknown\Some Other Book (900)", OtherAuthorFolderId, 2);
            otherTitle.DescendantItemCount = 1;

            db.Folders.AddRange(author, title, other, otherTitle);

            var item = await db.Items.FirstAsync(i => i.Id == 101);
            item.FolderId = TitleFolderId;
            item.TopFolderId = AuthorFolderId;
            item.Path = $@"{title.Path}\Brave New World - Aldous Huxley{ext}";
            item.FileName = Path.GetFileName(item.Path);
            item.Extension = ext;
            item.CalibreBookId = 844;

            // The neighbour under the OTHER author folder — it must survive every merge untouched.
            db.Items.Add(new Item
            {
                Id = OtherItemId, RootId = item.RootId, FolderId = OtherTitleFolderId, TopFolderId = OtherAuthorFolderId,
                Kind = ItemKind.Book, Path = $@"{otherTitle.Path}\Some Other Book.epub", FileName = "Some Other Book.epub",
                Extension = ".epub", Title = "Some Other Book", NormalizedTitle = "some other book",
            });
            await db.SaveChangesAsync();
        }

        /// <summary>A throwaway Calibre metadata.db holding exactly the one book, at the path/name/formats given.</summary>
        private static string BuildCalibre(V1Fixture f, string relPath, string fileName, params string[] formats) =>
            BuildCalibre(f, 844, relPath, fileName, formats);

        private static string BuildCalibre(V1Fixture f, int bookId, string relPath, string fileName, params string[] formats)
        {
            var dir = Path.Combine(f.WorkDir, "calibre-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "metadata.db");
            using var conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
            conn.Open();
            void Exec(string sql)
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = sql;
                cmd.ExecuteNonQuery();
            }
            Exec(@"
CREATE TABLE books (id INTEGER PRIMARY KEY, title TEXT, path TEXT, pubdate TEXT, series_index REAL);
CREATE TABLE identifiers (id INTEGER PRIMARY KEY, book INTEGER, type TEXT, val TEXT);
CREATE TABLE authors (id INTEGER PRIMARY KEY, name TEXT);
CREATE TABLE books_authors_link (book INTEGER, author INTEGER);
CREATE TABLE series (id INTEGER PRIMARY KEY, name TEXT);
CREATE TABLE books_series_link (book INTEGER, series INTEGER);
CREATE TABLE publishers (id INTEGER PRIMARY KEY, name TEXT);
CREATE TABLE books_publishers_link (book INTEGER, publisher INTEGER);
CREATE TABLE tags (id INTEGER PRIMARY KEY, name TEXT);
CREATE TABLE books_tags_link (book INTEGER, tag INTEGER);
CREATE TABLE languages (id INTEGER PRIMARY KEY, lang_code TEXT);
CREATE TABLE books_languages_link (book INTEGER, lang_code INTEGER);
CREATE TABLE comments (id INTEGER PRIMARY KEY, book INTEGER, text TEXT);
CREATE TABLE data (id INTEGER PRIMARY KEY, book INTEGER, format TEXT, name TEXT);");
            Exec($"INSERT INTO books (id, title, path, pubdate, series_index) VALUES ({bookId}, 'Brave New World', '{relPath.Replace("'", "''")}', '2006-10-17', 3.0)");
            Exec($"INSERT INTO authors (id, name) VALUES (1, 'Aldous Huxley'); INSERT INTO books_authors_link (book, author) VALUES ({bookId}, 1);");
            var id = 1;
            foreach (var fmt in formats.Length == 0 ? new[] { "EPUB" } : formats)
                Exec($"INSERT INTO data (id, book, format, name) VALUES ({id++}, {bookId}, '{fmt}', '{fileName.Replace("'", "''")}')");
            return path;
        }

        private static async Task<CalibreBatchResult> ImportAsync(V1Fixture f, string metadata, bool apply = true)
        {
            await using var db = f.HotDb();
            await Importer().ResetAsync(db);
            return await Importer().RunBatchAsync(db, metadata, f.CalibreLinkPath, 100, apply, LibraryRoot);
        }

        [Fact]
        public async Task AMatchedBookAlreadyAtItsCalibrePathIsNotRepathed()
        {
            using var f = Migrated();
            await ShapeLikeCalibreAsync(f);
            var metadata = BuildCalibre(f, "Aldous Huxley/Brave New World (844)", "Brave New World - Aldous Huxley");

            var r = await ImportAsync(f, metadata);
            Assert.Equal(1, r.Matched);
            Assert.Equal(0, r.Repathed);
            Assert.Equal(0, r.FoldersFixed);
        }

        [Fact]
        public async Task RenamingTheTitleFolderMovesTheItemAndItsFolderRow()
        {
            using var f = Migrated();
            await ShapeLikeCalibreAsync(f);
            // The truncated-name case: Calibre expands the folder AND the file name; the author is unchanged.
            var metadata = BuildCalibre(f, "Aldous Huxley/Brave New World Revisited (844)", "Brave New World Revisited - Aldous Huxley");

            var r = await ImportAsync(f, metadata);
            Assert.Equal(1, r.Repathed);
            Assert.Equal(1, r.FoldersFixed);

            await using var db = f.HotDb();
            var item = await db.Items.AsNoTracking().FirstAsync(i => i.Id == 101);
            Assert.Equal($@"{LibraryRoot}\Aldous Huxley\Brave New World Revisited (844)\Brave New World Revisited - Aldous Huxley.epub", item.Path);
            Assert.Equal("Brave New World Revisited - Aldous Huxley.epub", item.FileName);
            Assert.Equal(".epub", item.Extension);
            Assert.Equal(TitleFolderId, item.FolderId);       // the SAME row, renamed — never a second row
            Assert.Equal(AuthorFolderId, item.TopFolderId);

            var title = await db.Folders.AsNoTracking().FirstAsync(x => x.Id == TitleFolderId);
            Assert.Equal($@"{LibraryRoot}\Aldous Huxley\Brave New World Revisited (844)", title.Path);
            Assert.Equal("Brave New World Revisited (844)", title.Name);
            Assert.Equal("brave new world revisited 844", title.NormalizedName);
            Assert.Equal(AuthorFolderId, title.ParentId);

            // the author folder is untouched
            var author = await db.Folders.AsNoTracking().FirstAsync(x => x.Id == AuthorFolderId);
            Assert.Equal($@"{LibraryRoot}\Aldous Huxley", author.Path);
        }

        [Fact]
        public async Task RenamingTheAuthorFolderCarriesEveryDescendantPathWithIt()
        {
            using var f = Migrated();
            await ShapeLikeCalibreAsync(f);
            var metadata = BuildCalibre(f, "Huxley, Aldous/Brave New World (844)", "Brave New World - Aldous Huxley");

            var r = await ImportAsync(f, metadata);
            Assert.Equal(1, r.Repathed);
            Assert.Equal(2, r.FoldersFixed);   // the author row plus the title row it carried

            await using var db = f.HotDb();
            var author = await db.Folders.AsNoTracking().FirstAsync(x => x.Id == AuthorFolderId);
            Assert.Equal($@"{LibraryRoot}\Huxley, Aldous", author.Path);
            Assert.Equal("Huxley, Aldous", author.Name);
            Assert.Equal("huxley aldous", author.NormalizedName);

            var title = await db.Folders.AsNoTracking().FirstAsync(x => x.Id == TitleFolderId);
            Assert.Equal($@"{LibraryRoot}\Huxley, Aldous\Brave New World (844)", title.Path);
            Assert.Equal("Brave New World (844)", title.Name);   // its OWN name did not change

            var item = await db.Items.AsNoTracking().FirstAsync(i => i.Id == 101);
            Assert.Equal($@"{LibraryRoot}\Huxley, Aldous\Brave New World (844)\Brave New World - Aldous Huxley.epub", item.Path);
            Assert.Equal(TitleFolderId, item.FolderId);
        }

        [Fact]
        public async Task AnAuthorFolderRenamedOntoAnExistingOneMergesInsteadOfDuplicating()
        {
            using var f = Migrated();
            await ShapeLikeCalibreAsync(f);
            // "Adaptation (epub)" -> "Unknown", where an Unknown folder ALREADY holds books.
            var metadata = BuildCalibre(f, "Unknown/Brave New World (844)", "Brave New World - Aldous Huxley");

            var r = await ImportAsync(f, metadata);
            Assert.Equal(1, r.Repathed);

            await using var db = f.HotDb();
            // exactly ONE folder row at the surviving path
            Assert.Equal(1, await db.Folders.CountAsync(x => x.Path == $@"{LibraryRoot}\Unknown"));

            var title = await db.Folders.AsNoTracking().FirstAsync(x => x.Id == TitleFolderId);
            Assert.Equal(OtherAuthorFolderId, title.ParentId);
            Assert.Equal(OtherAuthorFolderId, title.TopFolderId);
            Assert.Equal($@"{LibraryRoot}\Unknown\Brave New World (844)", title.Path);

            var item = await db.Items.AsNoTracking().FirstAsync(i => i.Id == 101);
            Assert.Equal($@"{LibraryRoot}\Unknown\Brave New World (844)\Brave New World - Aldous Huxley.epub", item.Path);
            Assert.Equal(TitleFolderId, item.FolderId);
            Assert.Equal(OtherAuthorFolderId, item.TopFolderId);

            // the emptied author folder is GONE — it holds no item and no child folder
            Assert.Equal(0, await db.Folders.CountAsync(x => x.Id == AuthorFolderId));

            // the survivor's counts grew, and its own book was left exactly where it was
            var survivor = await db.Folders.AsNoTracking().FirstAsync(x => x.Id == OtherAuthorFolderId);
            Assert.Equal(2, survivor.DirectChildCount);
            Assert.Equal(2, survivor.DescendantItemCount);
            var neighbour = await db.Items.AsNoTracking().FirstAsync(i => i.Id == OtherItemId);
            Assert.Equal($@"{LibraryRoot}\Unknown\Some Other Book (900)\Some Other Book.epub", neighbour.Path);
            Assert.Equal(OtherTitleFolderId, neighbour.FolderId);
        }

        [Fact]
        public async Task ABookWithTwoFormatsRepathsOntoTheItemsOwnExtension()
        {
            using var f = Migrated();
            await ShapeLikeCalibreAsync(f, ".pdf");
            var metadata = BuildCalibre(f, "Aldous Huxley/Brave New World Revisited (844)", "Brave New World Revisited - Aldous Huxley", "EPUB", "PDF");

            var r = await ImportAsync(f, metadata);
            Assert.Equal(1, r.Repathed);

            await using var db = f.HotDb();
            var item = await db.Items.AsNoTracking().FirstAsync(i => i.Id == 101);
            // The EPUB is the FIRST candidate; the item is a PDF, and the file it names is the PDF.
            Assert.EndsWith(".pdf", item.Path, StringComparison.Ordinal);
            Assert.Equal(".pdf", item.Extension);
            Assert.Equal($@"{LibraryRoot}\Aldous Huxley\Brave New World Revisited (844)\Brave New World Revisited - Aldous Huxley.pdf", item.Path);
        }

        [Fact]
        public async Task TheSecondRunRepathsNothingAndChangesNothing()
        {
            using var f = Migrated();
            await ShapeLikeCalibreAsync(f);
            var metadata = BuildCalibre(f, "Unknown/Brave New World (844)", "Brave New World - Aldous Huxley");

            var first = await ImportAsync(f, metadata);
            Assert.Equal(1, first.Repathed);
            var once = await SnapshotAsync(f);

            var second = await ImportAsync(f, metadata);
            Assert.Equal(1, second.Matched);
            Assert.Equal(0, second.Repathed);
            Assert.Equal(0, second.FoldersFixed);
            Assert.Equal(once, await SnapshotAsync(f));
        }

        [Fact]
        public async Task TheDryRunCountsTheRepathAndWritesNothing()
        {
            using var f = Migrated();
            await ShapeLikeCalibreAsync(f);
            var metadata = BuildCalibre(f, "Huxley, Aldous/Brave New World (844)", "Brave New World - Aldous Huxley");
            var before = await SnapshotAsync(f);

            var r = await ImportAsync(f, metadata, apply: false);
            Assert.Equal(1, r.Repathed);
            Assert.Equal(0, r.FoldersFixed);
            Assert.Equal(before, await SnapshotAsync(f));
        }

        [Fact]
        public async Task AnItemWithNoCalibreIdentityIsNeverRepathed()
        {
            using var f = Migrated();
            await ShapeLikeCalibreAsync(f);
            // Book 901 is in no link file and is nobody's stored CalibreBookId, and its composed path matches no
            // Item — so it is REPORTED unmatched. Item 102 (Dune), which has no Calibre identity at all, must
            // not be dragged anywhere by it.
            var metadata = BuildCalibre(f, 901, "Frank Herbert/Dune (901)", "Dune - Frank Herbert");
            var before = await SnapshotAsync(f);

            CalibreBatchResult r;
            await using (var db = f.HotDb())
            {
                await Importer().ResetAsync(db);
                r = await Importer().RunBatchAsync(db, metadata, f.CalibreLinkPath, 100, true, LibraryRoot);
            }

            Assert.Equal(0, r.Matched);
            Assert.Equal(1, r.Unmatched);
            Assert.Equal(0, r.Repathed);

            var dune = await DuneAsync(f);
            Assert.Equal(@"\\nas\books\Fiction\Dune.epub", dune.Path);
            Assert.Null(dune.CalibreBookId);
            Assert.Equal(before, await SnapshotAsync(f));
        }

        private static async Task<Item> DuneAsync(V1Fixture f)
        {
            await using var db = f.HotDb();
            return await db.Items.AsNoTracking().FirstAsync(i => i.Id == 102);
        }

        /// <summary>Every path this feature can move, as one comparable string.</summary>
        private static async Task<string> SnapshotAsync(V1Fixture f)
        {
            await using var db = f.HotDb();
            var folders = await db.Folders.AsNoTracking().OrderBy(x => x.Id)
                .Select(x => $"F{x.Id}={x.Path}|{x.Name}|{x.NormalizedName}|{x.ParentId}|{x.TopFolderId}|{x.DirectChildCount}|{x.DescendantItemCount}")
                .ToListAsync();
            var items = await db.Items.AsNoTracking().OrderBy(x => x.Id)
                .Select(x => $"I{x.Id}={x.Path}|{x.FileName}|{x.Extension}|{x.FolderId}|{x.TopFolderId}")
                .ToListAsync();
            return string.Join(";", folders.Concat(items));
        }
    }
}
