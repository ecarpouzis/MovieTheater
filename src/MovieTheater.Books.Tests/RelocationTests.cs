using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using MovieTheater.Books.Archives;
using MovieTheater.Books.Db;
using MovieTheater.Books.Services;

namespace MovieTheater.Books.Tests
{
    /// <summary>
    /// A moved or replaced file must keep its item — and with it the curated identity keyed by the item id — and a
    /// re-scan of a changed file must not re-parse over that identity. Driven over the generated ScanFixture tree.
    /// </summary>
    public class RelocationTests
    {
        private static RelocationService Relocator(ScanFixture fx, string? cacheDir = null)
        {
            var readers = new IArchiveReader[] { new CbzArchiveReader(new SevenZipCliExtractor(new BooksOptions(), NullLogger<SevenZipCliExtractor>.Instance)) };
            var thumbs = new ThumbnailService(readers, new BooksOptions { CacheDir = cacheDir }, NullLogger<ThumbnailService>.Instance);
            return new RelocationService(fx.Scanner(readers), thumbs, NullLogger<RelocationService>.Instance);
        }

        private static async Task Curate(ScanFixture fx, int itemId)
        {
            await using var db = fx.Db();
            var d = await db.ComicDetails.SingleAsync(x => x.ItemId == itemId);
            d.ParsedSeriesKey = "curated split key"; d.IssueNo = "99"; d.IssueSource = (ParseSource)8;
            await db.SaveChangesAsync();
        }

        private static string Tsv(ScanFixture fx, params (int Id, string NewPath)[] rows)
        {
            var p = Path.Combine(fx.WorkDir, Guid.NewGuid().ToString("N") + ".tsv");
            File.WriteAllLines(p, new[] { "ItemId\tNewPath\tClass" }.Concat(rows.Select(r => $"{r.Id}\t{r.NewPath}\tmove")));
            return p;
        }

        private static async Task<RelocationBatchResult> Run(ScanFixture fx, RelocationService svc, string tsv, bool apply, StringWriter? journal = null)
        {
            await using var db = fx.Db();
            return await svc.RunBatchAsync(db, tsv, 100, apply, 0, journal: journal);
        }

        [Fact]
        public async Task A_changed_file_at_a_known_path_keeps_its_curated_comic_detail()
        {
            using var fx = new ScanFixture();
            await fx.ScanAsync(rootId: 1);
            var path = Path.Combine(fx.ComicsRoot, "Rebellion", "2000AD (1977)", "2000 AD 0001 (1977).cbz");
            int id;
            await using (var db = fx.Db()) id = (await db.Items.SingleAsync(i => i.Path == path)).Id;
            await Curate(fx, id);

            File.Delete(path);
            ScanFixture.WriteCbz(path, "<Series>2000 AD</Series><Number>1</Number><Writer>New Writer</Writer>");
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(5));
            var (added, changed, _, _) = await fx.ScanAsync(rootId: 1);

            Assert.Equal(0, added);
            Assert.Equal(1, changed);
            await using var check = fx.Db();
            var d = await check.ComicDetails.SingleAsync(x => x.ItemId == id);
            Assert.Equal("curated split key", d.ParsedSeriesKey);
            Assert.Equal("99", d.IssueNo);
            // the FILE facts did refresh: the new ComicInfo is read
            Assert.Equal("New Writer", (await check.ComicEmbeddeds.SingleAsync(e => e.ItemId == id)).Writers);
        }

        [Fact]
        public async Task A_moved_file_keeps_its_item_id_and_the_next_scan_adds_and_removes_nothing()
        {
            using var fx = new ScanFixture();
            await fx.ScanAsync(rootId: 1);
            var oldPath = Path.Combine(fx.ComicsRoot, "Rebellion", "2000AD (1977)", "2000 AD 0002 (1977).cbz");
            int id;
            await using (var db = fx.Db()) id = (await db.Items.SingleAsync(i => i.Path == oldPath)).Id;
            await Curate(fx, id);

            var newDir = Path.Combine(fx.ComicsRoot, "Rebellion", "2000 AD", "Progs 0001-0100");
            Directory.CreateDirectory(newDir);
            var newPath = Path.Combine(newDir, "2000 AD 0002 (1977).cbz");
            File.Move(oldPath, newPath);
            var tsv = Tsv(fx, (id, Path.GetRelativePath(fx.ComicsRoot, newPath)));
            var svc = Relocator(fx);

            var dry = await Run(fx, svc, tsv, apply: false);
            Assert.Equal(1, dry.Moved);
            await using (var db = fx.Db()) Assert.Equal(oldPath, (await db.Items.SingleAsync(i => i.Id == id)).Path);   // dry run wrote nothing

            var journal = new StringWriter();
            var r = await Run(fx, svc, tsv, apply: true, journal);
            Assert.Equal(1, r.Moved);
            Assert.Equal(0, r.Refreshed);       // same bytes
            Assert.Equal(2, r.FoldersCreated);  // "2000 AD" and "Progs 0001-0100"
            Assert.Contains(oldPath, journal.ToString());

            var (added, _, removed, _) = await fx.ScanAsync(rootId: 1);
            Assert.Equal(0, added);
            Assert.Equal(0, removed);
            await using var check = fx.Db();
            var item = await check.Items.SingleAsync(i => i.Id == id);
            Assert.Equal(newPath, item.Path);
            Assert.False(item.IsExcluded);
            Assert.Equal("curated split key", (await check.ComicDetails.SingleAsync(x => x.ItemId == id)).ParsedSeriesKey);
            Assert.Equal(1, await check.Folders.CountAsync(f => f.Path == newDir));   // the scan adopted the folder, no twin

            var again = await Run(fx, svc, tsv, apply: true);   // idempotent
            Assert.Equal(1, again.Unchanged);
        }

        [Fact]
        public async Task A_new_rip_of_the_same_book_refreshes_file_facts_and_drops_the_cover()
        {
            using var fx = new ScanFixture();
            await fx.ScanAsync(rootId: 1);
            var oldPath = Path.Combine(fx.ComicsRoot, "DC", "Batman (1940)", "Batman 404 (1987).cbz");
            int id;
            await using (var db = fx.Db()) id = (await db.Items.SingleAsync(i => i.Path == oldPath)).Id;
            await Curate(fx, id);
            var cache = Path.Combine(fx.WorkDir, "thumbs");
            Directory.CreateDirectory(cache);
            File.WriteAllText(Path.Combine(cache, $"{id}.webp"), "old cover");

            File.Delete(oldPath);
            var newPath = Path.Combine(fx.ComicsRoot, "DC", "Batman (1940)", "Batman 404 (1987) (F).cbz");
            ScanFixture.WriteCbz(newPath, "<Series>Batman</Series><Number>404</Number><Writer>Frank Miller</Writer>");

            var r = await Run(fx, Relocator(fx, cache), Tsv(fx, (id, newPath)), apply: true);
            Assert.Equal(1, r.Refreshed);
            Assert.False(File.Exists(Path.Combine(cache, $"{id}.webp")));
            await using var check = fx.Db();
            Assert.Equal("Frank Miller", (await check.ComicEmbeddeds.SingleAsync(e => e.ItemId == id)).Writers);
            Assert.Equal("99", (await check.ComicDetails.SingleAsync(x => x.ItemId == id)).IssueNo);
        }

        [Fact]
        public async Task Unsafe_lines_are_refused_and_write_nothing()
        {
            using var fx = new ScanFixture();
            await fx.ScanAsync(rootId: 1);
            var series = Path.Combine(fx.ComicsRoot, "Rebellion", "2000AD (1977)");
            int one, two;
            await using (var db = fx.Db())
            {
                one = (await db.Items.SingleAsync(i => i.FileName == "2000 AD 0001 (1977).cbz")).Id;
                two = (await db.Items.SingleAsync(i => i.FileName == "2000 AD 0002 (1977).cbz")).Id;
            }
            var copy = Path.Combine(series, "2000 AD 0001 (1977) copy.cbz");
            File.Copy(Path.Combine(series, "2000 AD 0001 (1977).cbz"), copy);

            var r = await Run(fx, Relocator(fx), Tsv(fx,
                (one, copy),                                                // old file still exists -> a copy
                (two, Path.Combine(series, "nowhere.cbz")),                 // new file missing
                (two, Path.Combine(series, "2000 AD 0001 (1977).cbz")),     // held by another item
                (999999, copy)), apply: true);                              // no such item (and a second claim)

            Assert.Equal(4, r.Refused);
            Assert.Equal(0, r.Moved);
        }
    }
}
