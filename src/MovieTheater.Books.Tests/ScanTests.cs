using System.IO.Compression;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using MovieTheater.Books.Archives;
using MovieTheater.Books.Db;
using MovieTheater.Books.Services;
using SixLabors.ImageSharp.PixelFormats;

namespace MovieTheater.Books.Tests
{
    /// <summary>
    /// A GENERATED library tree under the temp directory plus an empty v2 file. Nothing here reads a share and
    /// nothing writes outside the work directory — the scanner's file access goes through its own seam, and the
    /// tree it walks is one this fixture built two seconds earlier.
    /// </summary>
    public sealed class ScanFixture : IDisposable
    {
        public readonly string WorkDir, ComicsRoot, BooksRoot, HotPath;

        public ScanFixture()
        {
            WorkDir = Path.Combine(Path.GetTempPath(), "books-scan-tests", Guid.NewGuid().ToString("N"));
            ComicsRoot = Path.Combine(WorkDir, "comics");
            BooksRoot = Path.Combine(WorkDir, "books");
            HotPath = Path.Combine(WorkDir, "books.db");
            Directory.CreateDirectory(WorkDir);

            // \comics\Rebellion\2000AD (1977)\2000 AD 0001 (1977).cbz   (+ #2, and one with a ComicInfo)
            var series = Path.Combine(ComicsRoot, "Rebellion", "2000AD (1977)");
            Directory.CreateDirectory(series);
            WriteCbz(Path.Combine(series, "2000 AD 0001 (1977).cbz"), comicInfo: null);
            WriteCbz(Path.Combine(series, "2000 AD 0002 (1977).cbz"), comicInfo: null);
            WriteCbz(Path.Combine(series, "Tagged Issue.cbz"),
                comicInfo: "<Series>Judge Dredd</Series><Number>5</Number><Publisher>Rebellion</Publisher>" +
                           "<Writer>A Writer, B Writer</Writer><Genre>Science Fiction, Anthology</Genre>");

            // a second folder, so the folder walk has more than one level to page over
            var other = Path.Combine(ComicsRoot, "DC", "Batman (1940)");
            Directory.CreateDirectory(other);
            WriteCbz(Path.Combine(other, "Batman 404 (1987).cbz"), comicInfo: null);

            // a book root
            Directory.CreateDirectory(Path.Combine(BooksRoot, "Fiction"));
            File.WriteAllBytes(Path.Combine(BooksRoot, "Fiction", "A Novel.epub"), MinimalEpub());

            // a file the scanner must ignore
            File.WriteAllText(Path.Combine(series, "notes.txt"), "not a comic");

            using var db = new BooksDb(BooksDbOptions.Hot(HotPath));
            db.Database.Migrate();
            db.LibraryRoots.Add(new LibraryRoot { Id = 1, Path = ComicsRoot, Kind = ItemKind.Comic, Enabled = true });
            db.LibraryRoots.Add(new LibraryRoot { Id = 2, Path = BooksRoot, Kind = ItemKind.Book, Enabled = true });
            db.SaveChanges();
        }

        public BooksDb Db() => new(BooksDbOptions.Hot(HotPath));

        public LibraryScanner Scanner() => new(
            new IArchiveReader[] { new CbzArchiveReader(new SevenZipCliExtractor(new BooksOptions(), NullLogger<SevenZipCliExtractor>.Instance)) },
            NullLogger<LibraryScanner>.Instance);

        /// <summary>Drive the scanner to completion the way the CLI verb does, and report what it did.</summary>
        public async Task<(int Added, int Changed, int Removed, int Batches)> ScanAsync(int batchSize = 2, int? rootId = null)
        {
            var scanner = Scanner();
            await using var db = Db();
            await scanner.StartAsync(db, rootId);
            int added = 0, changed = 0, removed = 0, batches = 0;
            while (batches < 200)
            {
                var r = await scanner.RunBatchAsync(db, batchSize);
                batches++;
                added += r.Added; changed += r.Changed; removed += r.Removed;
                if (r.Done) break;
            }
            return (added, changed, removed, batches);
        }

        public static void WriteCbz(string path, string? comicInfo)
        {
            using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
            foreach (var name in new[] { "01.png", "02.png" })
            {
                using var s = zip.CreateEntry(name).Open();
                s.Write(ArchiveFixture.PngBytes(120, 180, new Rgb24(10, 20, 30)));
            }
            if (comicInfo == null) return;
            using var meta = zip.CreateEntry("ComicInfo.xml").Open();
            meta.Write(Encoding.UTF8.GetBytes("<?xml version=\"1.0\"?><ComicInfo>" + comicInfo + "</ComicInfo>"));
        }

        private static byte[] MinimalEpub()
        {
            using var ms = new MemoryStream();
            using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
            {
                void Text(string name, string content)
                {
                    using var s = zip.CreateEntry(name).Open();
                    s.Write(Encoding.UTF8.GetBytes(content));
                }
                Text("mimetype", "application/epub+zip");
                Text("META-INF/container.xml",
                    "<container xmlns=\"urn:oasis:names:tc:opendocument:xmlns:container\"><rootfiles>" +
                    "<rootfile full-path=\"OEBPS/book.opf\" media-type=\"application/oebps-package+xml\"/></rootfiles></container>");
                Text("OEBPS/book.opf",
                    "<package xmlns=\"http://www.idpf.org/2007/opf\" version=\"3.0\"><metadata xmlns:dc=\"http://purl.org/dc/elements/1.1/\">" +
                    "<dc:title>A Novel</dc:title><dc:language>en</dc:language></metadata>" +
                    "<manifest><item id=\"c1\" href=\"c1.xhtml\" media-type=\"application/xhtml+xml\"/></manifest>" +
                    "<spine><itemref idref=\"c1\"/></spine></package>");
                Text("OEBPS/c1.xhtml", "<html><body><p>Chapter one.</p></body></html>");
            }
            return ms.ToArray();
        }

        public void Dispose()
        {
            try { Directory.Delete(WorkDir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>The scan's contract: what it indexes, what it re-reads, what it deletes, and what it keeps.</summary>
    public class ScanTests
    {
        [Fact]
        public async Task AFreshScanIndexesTheTreeAndParsesEveryComic()
        {
            using var f = new ScanFixture();
            var (added, _, removed, batches) = await f.ScanAsync();
            Assert.Equal(5, added);          // four comics plus one book
            Assert.Equal(0, removed);
            Assert.True(batches > 1, "a batch size of 2 over this tree must take several batches");

            await using var db = f.Db();
            Assert.Equal(5, await db.Items.CountAsync());
            Assert.Equal(4, await db.Items.CountAsync(i => i.Kind == ItemKind.Comic));
            Assert.Equal(1, await db.Items.CountAsync(i => i.Kind == ItemKind.Book));
            // notes.txt is not a container and is not a book.
            Assert.False(await db.Items.AnyAsync(i => i.FileName.EndsWith(".txt")));

            // the folder tree, its parents and the counts a Directory drill reads
            Assert.True(await db.Folders.CountAsync() >= 5);
            Assert.True(await db.Folders.AnyAsync(x => x.ParentId != null));
            var seriesFolder = await db.Folders.FirstAsync(x => x.Name == "2000AD (1977)");
            Assert.Equal(3, seriesFolder.DescendantItemCount);
            Assert.NotNull(seriesFolder.TopFolderId);

            // the parse pipeline ran on every comic
            Assert.Equal(4, await db.ComicDetails.CountAsync());
            var prog = await db.Items.FirstAsync(i => i.FileName.StartsWith("2000 AD 0001"));
            var detail = await db.ComicDetails.FirstAsync(d => d.ItemId == prog.Id);
            Assert.Equal("2000 AD", detail.ParsedSeriesKey);
            Assert.Equal("1", detail.IssueNo);
            Assert.Equal(1977, detail.Year);
        }

        [Fact]
        public async Task AnEmbeddedComicInfoLandsAsItsOwnRowsAndItsCreditsAndTags()
        {
            using var f = new ScanFixture();
            await f.ScanAsync();

            await using var db = f.Db();
            var tagged = await db.Items.FirstAsync(i => i.FileName == "Tagged Issue.cbz");
            var embedded = await db.ComicEmbeddeds.FirstAsync(e => e.ItemId == tagged.Id);
            Assert.Equal("Judge Dredd", embedded.Series);
            Assert.Equal("5", embedded.Number);

            var detail = await db.ComicDetails.FirstAsync(d => d.ItemId == tagged.Id);
            Assert.Equal("Judge Dredd", detail.ParsedSeriesKey);   // metadata outranks the filename
            Assert.Equal(ParseSource.Metadata, detail.SeriesSource);

            // creators and genres become ROWS with ComicInfo as their source
            var credits = await db.ItemCredits.Where(c => c.ItemId == tagged.Id).ToListAsync();
            Assert.Equal(2, credits.Count);
            Assert.All(credits, c => Assert.Equal(TagSource.ComicInfo, c.Source));
            Assert.Contains(credits, c => c.Name == "A Writer" && c.Role == "Writer");

            var tags = await db.ItemTags.Where(t => t.ItemId == tagged.Id).ToListAsync();
            Assert.Contains(tags, t => t.Category == "genre" && t.Value == "Science Fiction");
            Assert.Contains(tags, t => t.Category == "genre" && t.Value == "Anthology");
        }

        [Fact]
        public async Task AnUnchangedRescanChangesNothingAndKeepsEveryId()
        {
            using var f = new ScanFixture();
            await f.ScanAsync();
            var before = await SnapshotAsync(f);

            var (added, changed, removed, _) = await f.ScanAsync();
            Assert.Equal(0, added);
            Assert.Equal(0, changed);
            Assert.Equal(0, removed);
            Assert.Equal(before, await SnapshotAsync(f));
        }

        [Fact]
        public async Task AModifiedFileIsSeenAsChangedAndReRead()
        {
            using var f = new ScanFixture();
            await f.ScanAsync();
            var path = Path.Combine(f.ComicsRoot, "Rebellion", "2000AD (1977)", "2000 AD 0002 (1977).cbz");
            int id;
            long sizeBefore;
            await using (var db = f.Db())
            {
                var item = await db.Items.FirstAsync(i => i.Path == path);
                id = item.Id;
                sizeBefore = item.FileSize;
            }

            // Re-write it with a ComicInfo block: the size and mtime both move, so it is a CHANGE.
            File.Delete(path);
            ScanFixture.WriteCbz(path, "<Series>Rewritten</Series><Number>2</Number>");
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(5));

            var (added, changed, removed, _) = await f.ScanAsync();
            Assert.Equal(0, added);
            Assert.Equal(1, changed);
            Assert.Equal(0, removed);

            await using var after = f.Db();
            var reread = await after.Items.FirstAsync(i => i.Id == id);
            Assert.NotEqual(sizeBefore, reread.FileSize);
            Assert.Equal("Rewritten", (await after.ComicDetails.FirstAsync(d => d.ItemId == id)).ParsedSeriesKey);
        }

        /// <summary>
        /// A removed file is MARKED, not deleted.
        ///
        /// <para>`UserItemState.ItemId` is a foreign key to `Item`, so "delete the item but keep the reader's
        /// rows" is not a state the schema can hold. Keeping the reader's position and marks is the requirement
        /// that matters, so the row stays and the item is excluded plus flagged broken — it vanishes from every
        /// browse surface through the gate that already exists, and comes back whole if the file does.</para>
        /// </summary>
        [Fact]
        public async Task ARemovedFileIsMarkedMissingAndKeepsTheReadersState()
        {
            using var f = new ScanFixture();
            await f.ScanAsync();

            var path = Path.Combine(f.ComicsRoot, "DC", "Batman (1940)", "Batman 404 (1987).cbz");
            var bytes = await File.ReadAllBytesAsync(path);
            int id;
            await using (var db = f.Db())
            {
                id = (await db.Items.FirstAsync(i => i.Path == path)).Id;
                // the reader has been here: a position and a want-to-read mark
                db.UserItemStates.Add(new UserItemState { UserId = 1, ItemId = id, LastPage = 3, Status = ReadStatus.InProgress, WantToRead = true, UpdatedAt = DateTime.UtcNow });
                await db.SaveChangesAsync();
            }

            File.Delete(path);
            var (_, _, removed, _) = await f.ScanAsync();
            Assert.Equal(1, removed);

            await using (var after = f.Db())
            {
                var item = await after.Items.FirstAsync(i => i.Id == id);
                Assert.True(item.IsExcluded);
                var state = await after.ItemStates.FirstAsync(st => st.ItemId == id);
                Assert.True(state.IsBroken);
                Assert.Equal(LibraryScanner.MissingReason, state.BrokenReason);

                // The reader's own row is untouched — that is the whole point of marking rather than deleting.
                var reader = await after.UserItemStates.FirstAsync(st => st.ItemId == id);
                Assert.Equal(3, reader.LastPage);
                Assert.True(reader.WantToRead);

                // A series is derived and shared; a missing file never deletes one.
                Assert.Equal(5, await after.Items.CountAsync());
            }

            // And the other half: the file comes back, and so does the item.
            await File.WriteAllBytesAsync(path, bytes);
            await f.ScanAsync();
            await using (var back = f.Db())
            {
                var item = await back.Items.FirstAsync(i => i.Id == id);
                Assert.False(item.IsExcluded);
                var state = await back.ItemStates.FirstAsync(st => st.ItemId == id);
                Assert.False(state.IsBroken);
                Assert.Null(state.BrokenReason);
                Assert.Equal(3, (await back.UserItemStates.FirstAsync(st => st.ItemId == id)).LastPage);
            }
        }

        [Fact]
        public async Task AScanKilledMidWalkResumesWhereItStopped()
        {
            using var f = new ScanFixture();
            var scanner = f.Scanner();

            await using (var db = f.Db())
            {
                await scanner.StartAsync(db);
                // Two batches only, then the "process dies".
                await scanner.RunBatchAsync(db, 1);
                await scanner.RunBatchAsync(db, 1);
                var status = await scanner.StatusAsync(db);
                Assert.NotEqual(ScanPhase.Done, status.Phase);
            }

            // A NEW scanner and a NEW context continue from the persisted cursor — nothing is in memory.
            var resumed = f.Scanner();
            await using (var db = f.Db())
            {
                var guard = 0;
                while (guard++ < 200)
                {
                    var r = await resumed.RunBatchAsync(db, 2);
                    if (r.Done) break;
                }
            }

            await using var after = f.Db();
            Assert.Equal(5, await after.Items.CountAsync());
            Assert.Equal(ScanPhase.Done, (await resumed.StatusAsync(after)).Phase);
        }

        [Fact]
        public async Task ADryRunCountsWhatWouldChangeAndWritesNothing()
        {
            using var f = new ScanFixture();
            var scanner = f.Scanner();
            await using var db = f.Db();

            var preview = await scanner.PreviewAsync(db);
            Assert.Equal(5, preview.WouldAdd);
            Assert.Equal(0, preview.WouldChange);
            Assert.Equal(0, preview.WouldRemove);
            Assert.Equal(0, await db.Items.CountAsync());

            await f.ScanAsync();
            File.Delete(Path.Combine(f.ComicsRoot, "DC", "Batman (1940)", "Batman 404 (1987).cbz"));
            var second = await scanner.PreviewAsync(db);
            Assert.Equal(0, second.WouldAdd);
            Assert.Equal(1, second.WouldRemove);
            Assert.Equal(5, await db.Items.CountAsync());   // still there: a preview writes nothing
        }

        [Fact]
        public async Task AnUnreachableRootRefusesToScanRatherThanEmptyTheCatalog()
        {
            using var f = new ScanFixture();
            await f.ScanAsync();

            await using var db = f.Db();
            var root = await db.LibraryRoots.FirstAsync(r => r.Id == 1);
            root.Path = Path.Combine(f.WorkDir, "gone");
            await db.SaveChangesAsync();

            var scanner = f.Scanner();
            await Assert.ThrowsAsync<InvalidOperationException>(() => scanner.StartAsync(db, 1));
            Assert.Equal(5, await db.Items.CountAsync());
        }

        [Fact]
        public async Task ScanningOneRootLeavesTheOtherRootsItemsAlone()
        {
            using var f = new ScanFixture();
            await f.ScanAsync();
            var (_, _, removed, _) = await f.ScanAsync(rootId: 1);
            Assert.Equal(0, removed);
            await using var db = f.Db();
            Assert.Equal(1, await db.Items.CountAsync(i => i.RootId == 2));
        }

        private static async Task<string> SnapshotAsync(ScanFixture f)
        {
            await using var db = f.Db();
            var items = await db.Items.AsNoTracking().OrderBy(i => i.Id)
                .Select(i => i.Id + "|" + i.Path + "|" + i.FileSize + "|" + i.Kind).ToListAsync();
            return string.Join(";", items);
        }
    }
}
