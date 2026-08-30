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

        // ── the occupied target ──────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// The live shape: a scan that ran while the catalog still held the OLD path created a SECOND Item row
        /// for the same file at the NEW one. Item 101 is the linked, missing-flagged row; 300 is the healthy,
        /// unlinked copy sitting exactly where Calibre now says the book lives.
        /// </summary>
        private static async Task<int> AddScanBornDuplicateAsync(V1Fixture f, string relDir, string fileName, int? calibreBookId = null)
        {
            await using var db = f.HotDb();
            var folder = await db.Folders.FirstAsync(x => x.Id == TitleFolderId);
            const int id = 300;
            db.Items.Add(new Item
            {
                Id = id, RootId = folder.RootId, FolderId = TitleFolderId, TopFolderId = AuthorFolderId,
                Kind = ItemKind.Book, Path = $@"{LibraryRoot}\{relDir}\{fileName}", FileName = fileName,
                Extension = Path.GetExtension(fileName), Title = "Brave New World", NormalizedTitle = "brave new world",
                CalibreBookId = calibreBookId,
            });
            db.ItemStates.Add(new ItemState { ItemId = id, CoverWidth = 600, CoverHeight = 900 });
            db.BookDetails.Add(new BookDetail { ItemId = id, SeriesName = "Whatever" });
            db.ItemTags.Add(new ItemTag { ItemId = id, Category = "tag", Value = "scan-born", Source = TagSource.ComicInfo });
            // no FK holds these to the item, but a recycled id would inherit them
            db.Insights.Add(new Insight
            {
                Id = 9001, SubjectKind = SubjectKind.Item, SubjectId = id, ModelId = "test", Rank = 1,
                Confidence = Confidence.High, Recognized = true, Rating = 70, Synopsis = "scan-born", IsCurrent = true,
            });
            db.InsightTags.Add(new InsightTag { InsightId = 9001, Category = "genre", Value = "test" });
            db.Ratings.Add(new Rating { TargetKind = SubjectKind.Item, TargetId = id, Source = RatingSource.AI, Value = 70, ModelId = "test" });
            // the reader got further in the duplicate than in the linked row, and favourited it there
            db.UserItemStates.Add(new UserItemState
            {
                UserId = 1, ItemId = id, LastPage = 44, Status = ReadStatus.InProgress,
                Favorite = true, UpdatedAt = new DateTime(2026, 8, 28, 0, 0, 0, DateTimeKind.Utc),
            });
            // a second reader who has no row on the survivor at all
            db.UserItemStates.Add(new UserItemState
            {
                UserId = 2, ItemId = id, LastPage = 7, Status = ReadStatus.InProgress,
                WantToRead = true, UpdatedAt = new DateTime(2026, 8, 27, 0, 0, 0, DateTimeKind.Utc),
            });
            await db.SaveChangesAsync();
            return id;
        }

        /// <summary>Set one reader's state, whether or not the migration already left a row there (it does for item 101).</summary>
        private static async Task SetReaderStateAsync(V1Fixture f, int userId, int itemId, int lastPage, ReadStatus status,
            bool wantToRead, bool favorite, DateTime updatedAt)
        {
            await using var db = f.HotDb();
            var row = await db.UserItemStates.FirstOrDefaultAsync(s => s.UserId == userId && s.ItemId == itemId);
            if (row == null) { row = new UserItemState { UserId = userId, ItemId = itemId }; db.UserItemStates.Add(row); }
            row.LastPage = lastPage;
            row.LastSpineItemIndex = null;
            row.LastScrollPercent = null;
            row.Status = status;
            row.WantToRead = wantToRead;
            row.Favorite = favorite;
            row.UpdatedAt = updatedAt;
            await db.SaveChangesAsync();
        }

        /// <summary>The linked row's OLD file name — where a re-path has to move it FROM.</summary>
        private static string StalePath => $@"{LibraryRoot}\Aldous Huxley\Brave New World (844)\Brave New World.epub";

        private static async Task MakeLinkedRowStaleAsync(V1Fixture f)
        {
            await using var db = f.HotDb();
            var linked = await db.Items.FirstAsync(i => i.Id == 101);
            linked.Path = StalePath;
            linked.FileName = "Brave New World.epub";
            await db.SaveChangesAsync();
        }

        /// <summary>Flag the linked row the way the 08-29 scan did: missing at its old path, and excluded with it.</summary>
        private static async Task MarkLinkedRowMissingAsync(V1Fixture f)
        {
            await using var db = f.HotDb();
            await LibraryScanner.MarkMissingAsync(db, new[] { 101 });
        }

        [Fact]
        public async Task AScanBornDuplicateAtTheTargetIsMergedIntoTheLinkedRow()
        {
            using var f = Migrated();
            await ShapeLikeCalibreAsync(f);
            await MarkLinkedRowMissingAsync(f);

            // The reader's own row on the LINKED item — older, and not favourited.
            await SetReaderStateAsync(f, 1, 101, 12, ReadStatus.InProgress, wantToRead: true, favorite: false,
                new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc));

            const string newDir = "Aldous Huxley/Brave New World (844)";
            const string newFile = "Brave New World - Aldous Huxley.epub";
            // The linked row is still on the OLD file name; the scan row holds the one Calibre now names.
            await MakeLinkedRowStaleAsync(f);
            var dupeId = await AddScanBornDuplicateAsync(f, newDir.Replace('/', '\\'), newFile);
            var metadata = BuildCalibre(f, newDir, "Brave New World - Aldous Huxley");

            var r = await ImportAsync(f, metadata);
            Assert.Equal(1, r.Repathed);
            Assert.Equal(1, r.DuplicatesMerged);
            Assert.Equal(0, r.Collisions);
            Assert.Equal(1, r.Unbroken);

            await using (var db = f.HotDb())
            {
                // the LINKED row survived and took the path
                var survivor = await db.Items.AsNoTracking().FirstAsync(i => i.Id == 101);
                Assert.Equal($@"{LibraryRoot}\{newDir.Replace('/', '\\')}\{newFile}", survivor.Path);
                Assert.Equal(844, survivor.CalibreBookId);

                // the duplicate is gone, dependents and all
                Assert.Equal(0, await db.Items.CountAsync(i => i.Id == dupeId));
                Assert.Equal(0, await db.ItemStates.CountAsync(s => s.ItemId == dupeId));
                Assert.Equal(0, await db.BookDetails.CountAsync(b => b.ItemId == dupeId));
                Assert.Equal(0, await db.ItemTags.CountAsync(t => t.ItemId == dupeId));
                Assert.Equal(0, await db.UserItemStates.CountAsync(s => s.ItemId == dupeId));
                // the FK-less rows go too, so a recycled id cannot inherit them
                Assert.Equal(0, await db.Insights.CountAsync(x => x.SubjectKind == SubjectKind.Item && x.SubjectId == dupeId));
                Assert.Equal(0, await db.InsightTags.CountAsync(t => t.InsightId == 9001));
                Assert.Equal(0, await db.Ratings.CountAsync(r => r.TargetKind == SubjectKind.Item && r.TargetId == dupeId));

                // the reader's state came with it: flags OR'd, the NEWER position won
                var reader = await db.UserItemStates.AsNoTracking().FirstAsync(s => s.UserId == 1 && s.ItemId == 101);
                Assert.Equal(44, reader.LastPage);
                Assert.True(reader.Favorite);       // only the duplicate had it
                Assert.True(reader.WantToRead);     // only the survivor had it
                // and the reader who had NO row on the survivor now has one
                var second = await db.UserItemStates.AsNoTracking().FirstAsync(s => s.UserId == 2 && s.ItemId == 101);
                Assert.Equal(7, second.LastPage);
                Assert.True(second.WantToRead);
            }
        }

        [Fact]
        public async Task AFinishedPositionSurvivesANewerHalfReadOne()
        {
            using var f = Migrated();
            await ShapeLikeCalibreAsync(f);
            // -1 is the ONLY Finished signal the reader has; a newer in-progress page must not erase it.
            await SetReaderStateAsync(f, 1, 101, -1, ReadStatus.Finished, wantToRead: false, favorite: false,
                new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc));
            const string newDir = "Aldous Huxley/Brave New World (844)";
            await MakeLinkedRowStaleAsync(f);
            await AddScanBornDuplicateAsync(f, newDir.Replace('/', '\\'), "Brave New World - Aldous Huxley.epub");

            await ImportAsync(f, BuildCalibre(f, newDir, "Brave New World - Aldous Huxley"));

            await using (var db = f.HotDb())
            {
                var reader = await db.UserItemStates.AsNoTracking().FirstAsync(s => s.UserId == 1 && s.ItemId == 101);
                Assert.Equal(-1, reader.LastPage);
                Assert.Equal(ReadStatus.Finished, reader.Status);
            }
        }

        [Fact]
        public async Task ATargetHeldByAnotherCalibreBookIsSkippedAndCountedNeverThrown()
        {
            using var f = Migrated();
            await ShapeLikeCalibreAsync(f);
            const string newDir = "Aldous Huxley/Brave New World (844)";
            await MakeLinkedRowStaleAsync(f);
            // The occupant carries a Calibre identity of its OWN — two books claiming one file.
            var dupeId = await AddScanBornDuplicateAsync(f, newDir.Replace('/', '\\'), "Brave New World - Aldous Huxley.epub", calibreBookId: 777);

            var r = await ImportAsync(f, BuildCalibre(f, newDir, "Brave New World - Aldous Huxley"));
            Assert.Equal(0, r.Repathed);
            Assert.Equal(1, r.Collisions);
            Assert.Equal(0, r.DuplicatesMerged);
            Assert.Equal(1, r.Matched);          // it still filled the metadata; only the MOVE was refused

            await using (var db = f.HotDb())
            {
                // nothing moved, and neither row was destroyed
                Assert.Equal(StalePath, (await db.Items.AsNoTracking().FirstAsync(i => i.Id == 101)).Path);
                Assert.Equal(1, await db.Items.CountAsync(i => i.Id == dupeId));
            }
        }

        [Fact]
        public async Task ARepathClearsAStaleMissingFlagAndUnhidesTheItem()
        {
            using var f = Migrated();
            await ShapeLikeCalibreAsync(f);
            await MarkLinkedRowMissingAsync(f);

            await using (var db = f.HotDb())
            {
                Assert.True((await db.Items.AsNoTracking().FirstAsync(i => i.Id == 101)).IsExcluded);
                Assert.Equal("missing", (await db.ItemStates.AsNoTracking().FirstAsync(s => s.ItemId == 101)).BrokenReason);
            }

            var r = await ImportAsync(f, BuildCalibre(f, "Huxley, Aldous/Brave New World (844)", "Brave New World - Aldous Huxley"));
            Assert.Equal(1, r.Repathed);
            Assert.Equal(1, r.Unbroken);

            await using (var db = f.HotDb())
            {
                var item = await db.Items.AsNoTracking().FirstAsync(i => i.Id == 101);
                Assert.False(item.IsExcluded);
                var state = await db.ItemStates.AsNoTracking().FirstAsync(s => s.ItemId == 101);
                Assert.False(state.IsBroken);
                Assert.Null(state.BrokenReason);
                Assert.Null(state.ExclusionReason);
            }

            // a second run has nothing left to unbreak
            Assert.Equal(0, (await ImportAsync(f, BuildCalibre(f, "Huxley, Aldous/Brave New World (844)", "Brave New World - Aldous Huxley"))).Unbroken);
        }

        // ── retiring the books Calibre no longer has ─────────────────────────────────────────────────────

        /// <summary>
        /// Drain the whole walk, the way the CLI's loop does — the retirement sweep runs on the TERMINAL batch,
        /// which is the one a single RunBatchAsync call never reaches.
        /// </summary>
        private static async Task<CalibreBatchResult> DrainAsync(V1Fixture f, string metadata, bool apply = true, bool reset = true)
        {
            await using var db = f.HotDb();
            if (reset) await Importer().ResetAsync(db);
            CalibreBatchResult r;
            long? after = null;
            var guard = 0;
            do
            {
                r = await Importer().RunBatchAsync(db, metadata, f.CalibreLinkPath, 100, apply, LibraryRoot, apply ? null : after);
                if (!apply) after = r.NextCursor ?? after;
            } while (!r.Done && guard++ < 50);
            return r;   // the TERMINAL result — the one carrying Retired / RetireRefused
        }

        /// <summary>Give an item a Calibre id that the metadata.db does not have — a row merged away tonight.</summary>
        private static async Task LinkToDeletedBookAsync(V1Fixture f, int itemId, int calibreBookId)
        {
            await using var db = f.HotDb();
            var item = await db.Items.FirstAsync(i => i.Id == itemId);
            item.CalibreBookId = calibreBookId;
            await db.SaveChangesAsync();
        }

        [Fact]
        public async Task AnItemWhoseCalibreEntryIsGoneIsRetiredOnceAndOnlyOnce()
        {
            using var f = Migrated();
            await ShapeLikeCalibreAsync(f);
            // Item 102 (Dune) was linked to Calibre book 9001, which the dedup merge deleted tonight.
            await LinkToDeletedBookAsync(f, 102, 9001);

            // Book 844 IS still there, so 101 is only 1 of 2 linked rows — 50 % absent would trip the guard.
            // Three more surviving rows put the absent share at 1 in 5, which is inside it.
            await using (var db = f.HotDb())
            {
                var folder = await db.Folders.FirstAsync(x => x.Id == TitleFolderId);
                for (var n = 0; n < 3; n++)
                    db.Items.Add(new Item
                    {
                        Id = 400 + n, RootId = folder.RootId, FolderId = TitleFolderId, TopFolderId = AuthorFolderId,
                        Kind = ItemKind.Book, Path = $@"{LibraryRoot}\filler-{n}.epub", FileName = $"filler-{n}.epub",
                        Extension = ".epub", CalibreBookId = 5000 + n,   // CalibreBookId is UNIQUE
                    });
                await db.SaveChangesAsync();
            }
            // …and those three ids must EXIST in Calibre, or they count as absent too.
            var metadata = BuildCalibreWithExtras(f, "Aldous Huxley/Brave New World (844)", "Brave New World - Aldous Huxley", 5000, 5001, 5002);

            var r = await DrainAsync(f, metadata);
            Assert.Null(r.RetireRefused);
            Assert.Equal(1, r.Retired);

            await using (var db = f.HotDb())
            {
                var dune = await db.Items.AsNoTracking().FirstAsync(i => i.Id == 102);
                Assert.True(dune.IsExcluded);
                // The id is the forensic link back to the merge journal — it is NOT cleared.
                Assert.Equal(9001, dune.CalibreBookId);
                var state = await db.ItemStates.AsNoTracking().FirstAsync(s => s.ItemId == 102);
                Assert.True(state.IsBroken);
                Assert.Equal(LibraryScanner.MissingReason, state.BrokenReason);
                Assert.Equal(LibraryScanner.MissingReason, state.ExclusionReason);

                // the item whose Calibre row EXISTS was not touched
                var linked = await db.Items.AsNoTracking().FirstAsync(i => i.Id == 101);
                Assert.False(linked.IsExcluded);
            }

            // Idempotent: the second full walk finds it already marked.
            Assert.Equal(0, (await DrainAsync(f, metadata)).Retired);

            // …and nothing is deleted, ever.
            await using (var db = f.HotDb()) Assert.Equal(1, await db.Items.CountAsync(i => i.Id == 102));
        }

        [Fact]
        public async Task AMetadataDbMissingMostOfTheLibraryIsRefusedNotObeyed()
        {
            using var f = Migrated();
            await ShapeLikeCalibreAsync(f);
            // Both linked books point at ids this metadata.db has never heard of — the wrong --metadata path.
            await LinkToDeletedBookAsync(f, 101, 9001);
            await LinkToDeletedBookAsync(f, 102, 9002);

            var metadata = BuildCalibre(f, 844, "Aldous Huxley/Brave New World (844)", "Brave New World - Aldous Huxley");
            var r = await DrainAsync(f, metadata);

            Assert.Equal(0, r.Retired);
            Assert.NotNull(r.RetireRefused);
            Assert.Contains("guard", r.RetireRefused!, StringComparison.OrdinalIgnoreCase);

            await using var db = f.HotDb();
            Assert.False((await db.Items.AsNoTracking().FirstAsync(i => i.Id == 101)).IsExcluded);
            Assert.False((await db.Items.AsNoTracking().FirstAsync(i => i.Id == 102)).IsExcluded);
        }

        [Fact]
        public async Task TheDryRunCountsWhatItWouldRetireAndWritesNothing()
        {
            using var f = Migrated();
            await ShapeLikeCalibreAsync(f);
            await LinkToDeletedBookAsync(f, 102, 9001);
            await using (var db = f.HotDb())
            {
                var folder = await db.Folders.FirstAsync(x => x.Id == TitleFolderId);
                for (var n = 0; n < 3; n++)
                    db.Items.Add(new Item
                    {
                        Id = 400 + n, RootId = folder.RootId, FolderId = TitleFolderId, TopFolderId = AuthorFolderId,
                        Kind = ItemKind.Book, Path = $@"{LibraryRoot}\filler-{n}.epub", FileName = $"filler-{n}.epub",
                        Extension = ".epub", CalibreBookId = 5000 + n,
                    });
                await db.SaveChangesAsync();
            }
            var metadata = BuildCalibreWithExtras(f, "Aldous Huxley/Brave New World (844)", "Brave New World - Aldous Huxley", 5000, 5001, 5002);

            var r = await DrainAsync(f, metadata, apply: false);
            Assert.Equal(1, r.Retired);

            await using (var db = f.HotDb())
                Assert.False((await db.Items.AsNoTracking().FirstAsync(i => i.Id == 102)).IsExcluded);
        }

        /// <summary>Book 844 plus a handful of bare rows, so the guard's denominator is realistic.</summary>
        private static string BuildCalibreWithExtras(V1Fixture f, string relPath, string fileName, params int[] extraIds)
        {
            var path = BuildCalibre(f, 844, relPath, fileName);
            using var conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
            conn.Open();
            foreach (var id in extraIds)
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = $"INSERT INTO books (id, title, path, series_index) VALUES ({id}, 'Filler {id}', 'Filler/Filler ({id})', 1.0)";
                cmd.ExecuteNonQuery();
            }
            return path;
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
