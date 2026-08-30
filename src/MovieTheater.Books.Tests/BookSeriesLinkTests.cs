using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using MovieTheater.Books.Controllers;
using MovieTheater.Books.Db;
using MovieTheater.Books.Identity;
using MovieTheater.Books.Projections;
using MovieTheater.Books.Migration;
using MovieTheater.Books.Resolve;
using MovieTheater.Books.Services;

namespace MovieTheater.Books.Tests
{
    /// <summary>
    /// <c>books-resolve --book-series</c> — the BOOK series links, and the promise that comes with them: not one
    /// comic outcome moves. Every test here runs against the migrated synthetic file with a mixed fixture bolted
    /// on: a COMIC series "Star Wars" (from <c>ComicDetail.ParsedSeriesKey</c>) and a BOOK series of the same
    /// name (from <c>BookDetail.SeriesName</c>) — the collision that would prove the two pipelines share a bucket
    /// if they did.
    /// </summary>
    public class BookSeriesLinkTests
    {
        private const int ComicSeriesId = 50;
        private const int ComicItemA = 51, ComicItemB = 52;
        private const int BookOne = 201, BookTwo = 202, BookThree = 203, BookJunk = 204;

        private static V1Fixture Migrated()
        {
            var f = new V1Fixture();
            var summary = f.Engine(f.Options()).Run();
            if (summary.Stopped) throw new InvalidOperationException("fixture migration stopped: " + summary.StopReason);
            return f;
        }

        private static UnitCounts RebuildComics(V1Fixture f, int batchSize = 3)
        {
            using var hot = new TargetWriter(f.HotPath, MappingContract.Load(), dryRun: false);
            return SeriesRebuildJob.RunAll(hot, batchSize, _ => { });
        }

        private static UnitCounts LinkBooks(V1Fixture f, int batchSize = 3)
        {
            using var hot = new TargetWriter(f.HotPath, MappingContract.Load(), dryRun: false);
            return BookSeriesLinkJob.RunAll(hot, batchSize, _ => { });
        }

        /// <summary>The mixed fixture: one comic series and four books, three of them in a series of the same name.</summary>
        private static async Task SeedAsync(V1Fixture f)
        {
            await using var db = f.HotDb();
            var comicFolder = await db.Folders.FirstAsync(x => x.Kind == ItemKind.Comic && x.ParentId != null);
            var bookFolder = await db.Folders.FirstAsync(x => x.Kind == ItemKind.Book && x.ParentId != null);

            db.Series.Add(new Series
            {
                Id = ComicSeriesId, ParsedKey = "Star Wars", CanonicalKey = "parsed:star wars", Name = "Star Wars",
            });

            Item Row(int id, ItemKind kind, Folder folder, string title) => new()
            {
                Id = id, RootId = folder.RootId, FolderId = folder.Id, TopFolderId = folder.TopFolderId ?? folder.Id,
                Kind = kind, Path = $@"{folder.Path}\{title}{(kind == ItemKind.Comic ? ".cbz" : ".epub")}",
                FileName = $"{title}{(kind == ItemKind.Comic ? ".cbz" : ".epub")}",
                Extension = kind == ItemKind.Comic ? ".cbz" : ".epub",
                Title = title, NormalizedTitle = LibraryScanner.Normalize(title),
            };

            db.Items.AddRange(
                Row(ComicItemA, ItemKind.Comic, comicFolder, "Star Wars #1"),
                Row(ComicItemB, ItemKind.Comic, comicFolder, "Star Wars #2"),
                Row(BookOne, ItemKind.Book, bookFolder, "Heir to the Empire"),
                Row(BookTwo, ItemKind.Book, bookFolder, "Dark Force Rising"),
                Row(BookThree, ItemKind.Book, bookFolder, "The Last Command"),
                Row(BookJunk, ItemKind.Book, bookFolder, "A Loose Scan"));

            db.ComicDetails.AddRange(
                new ComicDetail { ItemId = ComicItemA, ParsedSeriesKey = "Star Wars", IssueNo = "1", Year = 1977 },
                new ComicDetail { ItemId = ComicItemB, ParsedSeriesKey = "Star Wars", IssueNo = "2", Year = 1978 });

            db.BookDetails.AddRange(
                // Two spellings of one series: "Star Wars" wins on count, "star wars" is the same normalized key.
                // The indices deliberately do NOT follow the item ids: the run has to order by Calibre's number.
                new BookDetail { ItemId = BookOne, SeriesName = "Star Wars", SeriesIndex = 3, PublishedOn = "1991-05-01" },
                new BookDetail { ItemId = BookTwo, SeriesName = "Star Wars", SeriesIndex = 1, PublishedOn = "1992-05-01" },
                new BookDetail { ItemId = BookThree, SeriesName = "star wars", SeriesIndex = 2, PublishedOn = "1993-05-01" },
                // A format token Calibre's series field picked up. Two characters is not a series.
                new BookDetail { ItemId = BookJunk, SeriesName = "SS", PublishedOn = "0101-01-01" });

            await db.SaveChangesAsync();
        }

        // ── the promise: comics do not move ──────────────────────────────────────────────────────────────

        [Fact]
        public async Task ABookSeriesAndAComicSeriesOfTheSameNameAreTwoRowsAndTheComicsNeverMove()
        {
            using var f = Migrated();
            await SeedAsync(f);

            RebuildComics(f);
            var comicsBefore = await ComicLinksAsync(f);
            var comicIdentityBefore = ComicIdentity(f);

            LinkBooks(f);

            // Distinct rows for the same NAME — the whole point.
            await using (var db = f.HotDb())
            {
                var comic = await db.Series.AsNoTracking().FirstAsync(s => s.CanonicalKey == "parsed:star wars");
                var book = await db.Series.AsNoTracking().FirstAsync(s => s.CanonicalKey == "book:star wars");
                Assert.NotEqual(comic.Id, book.Id);
                Assert.Equal("Star Wars", comic.Name);
                Assert.Equal("Star Wars", book.Name);
                Assert.Null(book.ParsedKey);
            }

            // Every comic keeps the SeriesId the comic rebuild gave it, and the identity is untouched.
            Assert.Equal(comicsBefore, await ComicLinksAsync(f));
            Assert.Equal(comicIdentityBefore, ComicIdentity(f));

            // Running the COMIC rebuild again after the book link must not disturb either side.
            var booksAfterLink = await BookRowsAsync(f);
            RebuildComics(f);
            Assert.Equal(comicsBefore, await ComicLinksAsync(f));
            Assert.Equal(comicIdentityBefore, ComicIdentity(f));
            Assert.Equal(booksAfterLink, await BookRowsAsync(f));   // no book: row deleted, renamed or re-keyed

            using (var hot = f.Hot()) Assert.Equal(0, SeriesResolver.Diff(hot).Total);

            // …and the book link is itself idempotent.
            var once = await BookSnapshotAsync(f);
            LinkBooks(f);
            Assert.Equal(once, await BookSnapshotAsync(f));
            Assert.Equal(comicsBefore, await ComicLinksAsync(f));
        }

        [Fact]
        public async Task TheComicRebuildIsBitForBitTheSameWithAndWithoutBookSeriesPresent()
        {
            // Two fixtures from the same seed: one where the book link ran, one where it never did. The comic
            // half of the file must be identical.
            using var withBooks = Migrated();
            await SeedAsync(withBooks);
            RebuildComics(withBooks);
            LinkBooks(withBooks);
            RebuildComics(withBooks);

            using var without = Migrated();
            await SeedAsync(without);
            RebuildComics(without);
            RebuildComics(without);

            Assert.Equal(ComicIdentity(without), ComicIdentity(withBooks));
            Assert.Equal(await ComicLinksAsync(without), await ComicLinksAsync(withBooks));
        }

        // ── the links themselves ─────────────────────────────────────────────────────────────────────────

        [Fact]
        public async Task EveryBookWithASeriesNameGetsARowAndTheMostFrequentSpellingNamesIt()
        {
            using var f = Migrated();
            await SeedAsync(f);
            RebuildComics(f);
            LinkBooks(f);

            await using var db = f.HotDb();
            var series = await db.Series.AsNoTracking().FirstAsync(s => s.CanonicalKey == "book:star wars");
            Assert.Equal("Star Wars", series.Name);          // 2 books spell it that way, 1 does not
            Assert.Equal(3, series.IssueCount);
            Assert.Equal(1991, series.YearStart);
            Assert.Equal(1993, series.YearEnd);
            Assert.False(series.IsOngoing);                  // books have no publication schedule to be current with

            foreach (var id in new[] { BookOne, BookTwo, BookThree })
                Assert.Equal(series.Id, (await db.Items.AsNoTracking().FirstAsync(i => i.Id == id)).SeriesId);
        }

        [Fact]
        public async Task ATwoCharacterSeriesNameIsAFormatTokenAndNeverASeries()
        {
            using var f = Migrated();
            await SeedAsync(f);
            RebuildComics(f);
            LinkBooks(f);

            await using var db = f.HotDb();
            Assert.Null((await db.Items.AsNoTracking().FirstAsync(i => i.Id == BookJunk)).SeriesId);
            Assert.Equal(0, await db.Series.CountAsync(s => s.CanonicalKey == "book:ss"));
        }

        [Fact]
        public async Task ABookWhoseSeriesIsClearedIsUnlinkedAndTheEmptiedRowGoes()
        {
            using var f = Migrated();
            await SeedAsync(f);
            RebuildComics(f);
            LinkBooks(f);

            int seriesId;
            await using (var db = f.HotDb())
            {
                seriesId = (await db.Series.AsNoTracking().FirstAsync(s => s.CanonicalKey == "book:star wars")).Id;
                // The Calibre worker clears the series at the SOURCE; the next import writes NULL here.
                foreach (var d in await db.BookDetails.Where(b => b.ItemId >= BookOne && b.ItemId <= BookThree).ToListAsync())
                    d.SeriesName = null;
                await db.SaveChangesAsync();
            }

            LinkBooks(f);

            await using (var db = f.HotDb())
            {
                foreach (var id in new[] { BookOne, BookTwo, BookThree })
                    Assert.Null((await db.Items.AsNoTracking().FirstAsync(i => i.Id == id)).SeriesId);
                Assert.Equal(0, await db.Series.CountAsync(s => s.Id == seriesId));
                // and the comic series of the same name is still standing
                Assert.Equal(1, await db.Series.CountAsync(s => s.CanonicalKey == "parsed:star wars"));
            }
        }

        [Fact]
        public async Task TheLinkIsResumableAndTheCursorMatchesTheBatchOrdering()
        {
            using var f = Migrated();
            await SeedAsync(f);
            RebuildComics(f);

            var cursor = BookSeriesLinkJob.IdentityCursor;
            var counts = new UnitCounts();
            using (var hot = new TargetWriter(f.HotPath, MappingContract.Load(), dryRun: false))
            {
                for (var i = 0; i < 2; i++)
                {
                    hot.Begin();
                    BookSeriesLinkJob.RunStep(hot, cursor, 100, _ => { }, counts, out cursor);
                    hot.Commit();
                }
                Assert.True(cursor >= BookSeriesLinkJob.RepointBase, "expected to be mid re-point");
            }

            // A NEW process resuming from the same cursor finishes the job.
            using (var hot = new TargetWriter(f.HotPath, MappingContract.Load(), dryRun: false))
            {
                var guard = 0;
                while (guard++ < 200)
                {
                    hot.Begin();
                    var done = BookSeriesLinkJob.RunStep(hot, cursor, 100, _ => { }, counts, out cursor);
                    hot.Commit();
                    if (done) break;
                }
            }

            var resumed = await BookSnapshotAsync(f);
            LinkBooks(f);
            Assert.Equal(resumed, await BookSnapshotAsync(f));
        }

        [Fact]
        public async Task TheLinkStampsTheRegistryRowsItWrites()
        {
            using var f = Migrated();
            await SeedAsync(f);
            RebuildComics(f);
            LinkBooks(f);

            using var hot = f.Hot();
            // Item.SeriesId now counts books too, so the stamp must have been re-taken after the book pass.
            var linked = hot.Scalar<long>("SELECT count(*) FROM Item WHERE SeriesId IS NOT NULL");
            Assert.Equal(linked, hot.Scalar<long>("SELECT RowCount FROM DerivedTable WHERE Name = 'Item.SeriesId'"));
            Assert.Equal(hot.Scalar<long>("SELECT count(*) FROM Series"),
                hot.Scalar<long>("SELECT RowCount FROM DerivedTable WHERE Name = 'Series'"));
        }

        // ── the consumers ────────────────────────────────────────────────────────────────────────────────

        [Fact]
        public async Task TheRunOfABookSeriesInfersItsKindAndOrdersByTheSeriesIndex()
        {
            using var f = Migrated();
            await SeedAsync(f);
            RebuildComics(f);
            LinkBooks(f);
            using (var hot = new TargetWriter(f.HotPath, MappingContract.Load(), dryRun: false))
            {
                hot.Begin();
                ResolvePipeline.RunAll(hot, 500, _ => { });
                hot.Commit();
            }

            await using var db = f.HotDb();
            var browse = Bind(new BrowseController(db, new MemoryCache(new MemoryCacheOptions { SizeLimit = 200 })));
            var seriesId = await db.Series.AsNoTracking().Where(s => s.CanonicalKey == "book:star wars")
                .Select(s => s.Id).FirstAsync();

            // No ?kind= — the series' own key says "book", and without that inference the default is COMIC and
            // this run comes back empty for a series that is right there.
            var run = Value(await browse.GetSeriesRun(seriesId));
            Assert.Equal("book", Read<string>(run, "kind"));
            var rows = Read<List<SeriesRunRow>>(run, "items");
            Assert.Equal(new[] { BookTwo, BookThree, BookOne }, rows.Select(r => r.Item.Id).ToArray());

            // The head the series modal reads follows the same inference, so the modal has a label and a count.
            var head = Assert.Single(Body<BrowseGroupsResponse>(
                await browse.GetGroups(groupBy: "series", singleGroupKey: seriesId.ToString(), perGroupTop: 1)).Groups);
            Assert.Equal("Star Wars", head.Label);
            Assert.Equal(3, head.TotalItems);

            // A COMIC series is untouched by any of it: still comic, still in reading order.
            var comicRun = Value(await browse.GetSeriesRun(1));
            Assert.Equal("comic", Read<string>(comicRun, "kind"));
            Assert.Equal(new[] { 1, 2 }, Read<List<SeriesRunRow>>(comicRun, "items").Select(r => r.Item.Id).ToArray());
        }

        private static T Bind<T>(T controller) where T : ControllerBase
        {
            controller.ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = BooksIdentity.Principal(1, V1Fixture.Owner, false, 3) },
            };
            return controller;
        }

        private static object Value(IActionResult result) => Assert.IsType<OkObjectResult>(result).Value!;
        private static T Body<T>(IActionResult result) => Assert.IsType<T>(Assert.IsType<OkObjectResult>(result).Value);
        private static T Read<T>(object body, string property) => (T)body.GetType().GetProperty(property)!.GetValue(body)!;

        // ── snapshots ────────────────────────────────────────────────────────────────────────────────────

        /// <summary>Every COMIC item's series link — the thing that must not move.</summary>
        private static async Task<string> ComicLinksAsync(V1Fixture f)
        {
            await using var db = f.HotDb();
            var rows = await db.Items.AsNoTracking().Where(i => i.Kind == ItemKind.Comic).OrderBy(i => i.Id)
                .Select(i => $"{i.Id}={i.SeriesId}").ToListAsync();
            return string.Join(";", rows);
        }

        /// <summary>The comic identity: survivors, aliases, redirects — book rows deliberately excluded.</summary>
        private static string ComicIdentity(V1Fixture f)
        {
            using var w = f.Hot();
            var parts = new List<string>();
            foreach (var (id, row) in w.Pairs(
                "SELECT Id, CanonicalKey || '|' || coalesce(Name,'') || '|' || IssueCount || '|' || coalesce(YearStart,-1) || '|' || coalesce(YearEnd,-1) || '|' || IsOngoing"
                + " FROM Series WHERE CanonicalKey NOT LIKE 'book:%' ORDER BY Id"))
                parts.Add($"S{id}={row}");
            foreach (var (sid, key) in w.Pairs("SELECT SeriesId, ParsedKey FROM SeriesAlias ORDER BY ParsedKey"))
                parts.Add($"A{key}={sid}");
            foreach (var (oldId, newId) in w.Pairs("SELECT OldSeriesId, CAST(NewSeriesId AS TEXT) FROM SeriesMerge ORDER BY OldSeriesId"))
                parts.Add($"M{oldId}={newId}");
            return string.Join(";", parts);
        }

        private static async Task<string> BookRowsAsync(V1Fixture f)
        {
            await using var db = f.HotDb();
            var rows = await db.Series.AsNoTracking().Where(s => s.CanonicalKey.StartsWith("book:")).OrderBy(s => s.Id)
                .Select(s => $"{s.Id}={s.CanonicalKey}|{s.Name}|{s.IssueCount}|{s.YearStart}|{s.YearEnd}|{s.IsOngoing}")
                .ToListAsync();
            return string.Join(";", rows);
        }

        /// <summary>The book half: the rows plus every book item's link.</summary>
        private static async Task<string> BookSnapshotAsync(V1Fixture f)
        {
            await using var db = f.HotDb();
            var items = await db.Items.AsNoTracking().Where(i => i.Kind == ItemKind.Book).OrderBy(i => i.Id)
                .Select(i => $"I{i.Id}={i.SeriesId}").ToListAsync();
            return await BookRowsAsync(f) + "//" + string.Join(";", items);
        }
    }
}
