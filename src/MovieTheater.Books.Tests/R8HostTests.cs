using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using MovieTheater.Books.Access;
using MovieTheater.Books.Archives;
using MovieTheater.Books.Controllers;
using MovieTheater.Books.Db;
using MovieTheater.Books.Identity;
using MovieTheater.Books.Projections;
using MovieTheater.Books.Services;

namespace MovieTheater.Books.Tests
{
    /// <summary>
    /// R8's host follow-ups (S0): the exact facet filters the flat projection cannot express, the series run
    /// (reading order + containment per issue), the per-series progress, the per-item mark read, and the Novels
    /// additions. All against the migrated synthetic v1 file: comics 1–2 = series 1 (writers Pat Mills / John
    /// Wagner, LOCG credits on 1), 3 = an excluded duplicate, 4–5 = series 2 (Frank Miller, event "Year One"),
    /// 6 = series 3, 7 = the omnibus (series 4, a container over series 2's #405); every comic pencilled by Carlos
    /// Ezquerra; 101–102 = the two novels.
    /// </summary>
    public class ExactFilterTests : IClassFixture<MigratedFixture>
    {
        private readonly MigratedFixture fixture;
        public ExactFilterTests(MigratedFixture fixture) => this.fixture = fixture;

        private static ClaimsPrincipal Owner() => BooksIdentity.Principal(1, "owner", false, 3);

        private static T Bind<T>(T controller) where T : ControllerBase
        {
            controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = Owner() } };
            return controller;
        }

        private static IMemoryCache NewCache() => new MemoryCache(new MemoryCacheOptions { SizeLimit = 200 });
        private static CatalogController Catalog(BooksDb db) => Bind(new CatalogController(db));
        private static BrowseController Browse(BooksDb db, IMemoryCache? cache = null) => Bind(new BrowseController(db, cache ?? NewCache()));
        private static T Body<T>(IActionResult result) => Assert.IsType<T>(Assert.IsType<OkObjectResult>(result).Value);
        private static object Value(IActionResult result) => Assert.IsType<OkObjectResult>(result).Value!;
        private static T Read<T>(object body, string property) => (T)body.GetType().GetProperty(property)!.GetValue(body)!;

        private static int[] Ids(IQueryable<ItemSummary> q) => q.Select(s => s.Id).OrderBy(id => id).ToArray();

        [Fact]
        public void Credits_filter_by_role_on_the_rows_the_facets_count()
        {
            using var db = fixture.Db();
            var catalog = Catalog(db);

            Assert.Equal(new[] { 1, 2 }, Ids(catalog.Get(author: new[] { "Pat Mills" })));
            Assert.Equal(new[] { 1, 2 }, Ids(catalog.Get(author: new[] { "pat  mills" })));            // normalized, not exact-cased
            Assert.Equal(new[] { 4, 5 }, Ids(catalog.Get(author: new[] { "Frank Miller" })));
            Assert.Equal(new[] { 1, 2, 4, 5 }, Ids(catalog.Get(author: new[] { "Pat Mills", "Frank Miller" })));   // OR within
            Assert.Empty(Ids(catalog.Get(author: new[] { "Miller" })));                                 // exact person, never a substring
            // a writer is not an artist: the role decides which facet a chip came from
            Assert.Empty(Ids(catalog.Get(artist: new[] { "Frank Miller" })));
            Assert.Equal(new[] { 1, 2, 4, 5, 6, 7 }, Ids(catalog.Get(artist: new[] { "Carlos Ezquerra" })));
            // excludes are NOT EXISTS over the same rows
            Assert.Equal(new[] { 1, 2, 6, 7 }, Ids(catalog.Get(exAuthor: new[] { "Frank Miller" })));
        }

        [Fact]
        public void Tags_and_events_filter_exactly_and_facets_AND_across()
        {
            using var db = fixture.Db();
            var catalog = Catalog(db);

            var sciFi = Ids(catalog.Get(tag: new[] { "Science Fiction" }));
            Assert.Contains(1, sciFi);
            Assert.Contains(2, sciFi);
            Assert.DoesNotContain(4, sciFi);
            Assert.Equal(new[] { 4, 5 }, Ids(catalog.Get(tag: new[] { "genre:Superhero" })));          // a pinned category
            Assert.Empty(Ids(catalog.Get(tag: new[] { "Fiction" })));                                  // no substring match

            Assert.Equal(new[] { 4, 5 }, Ids(catalog.Get(eventName: new[] { "Year One" })));
            Assert.Equal(new[] { 1, 2, 6, 7 }, Ids(catalog.Get(exEvent: new[] { "Year One" })));

            // AND across facets: a real pairing survives, a crossed one is empty
            Assert.Equal(new[] { 4, 5 }, Ids(catalog.Get(author: new[] { "Frank Miller" }, eventName: new[] { "Year One" })));
            Assert.Empty(Ids(catalog.Get(author: new[] { "Pat Mills" }, eventName: new[] { "Year One" })));
        }

        [Fact]
        public async Task The_group_heads_honour_the_exact_filters_and_cache_them_apart()
        {
            using var db = fixture.Db();
            var cache = NewCache();
            var browse = Browse(db, cache);

            var all = Body<BrowseGroupsResponse>(await browse.GetGroups(groupBy: "series"));
            Assert.Equal(4, all.TotalGroups);

            var miller = Body<BrowseGroupsResponse>(await browse.GetGroups(groupBy: "series", author: new[] { "Frank Miller" }));
            Assert.Equal(1, miller.TotalGroups);
            Assert.Equal("2", Assert.Single(miller.Groups).Key);

            // the unfiltered signature is untouched by the filtered one (the same cache instance answered both)
            Assert.Equal(4, Body<BrowseGroupsResponse>(await browse.GetGroups(groupBy: "series")).TotalGroups);

            var letters = Value(await browse.GetGroupLetters(groupBy: "series", eventName: new[] { "Year One" }));
            Assert.Equal(1, Read<int>(letters, "totalGroups"));

            var items = Value(await browse.GetGroupItems("series", "2", tag: new[] { "genre:Superhero" }));
            Assert.Equal(2, Read<int>(items, "total"));
            Assert.Empty(Read<List<ItemSummary>>(Value(await browse.GetGroupItems("series", "2", tag: new[] { "Science Fiction" })), "items"));
        }

        [Fact]
        public async Task The_series_run_carries_reading_order_and_containment_in_reading_order()
        {
            using var db = fixture.Db();
            var browse = Browse(db);

            var run = Value(await browse.GetSeriesRun(1));
            Assert.Equal(2, Read<int>(run, "total"));
            var rows = Read<List<SeriesRunRow>>(run, "items");
            Assert.Equal(new[] { 1, 2 }, rows.Select(r => r.Item.Id).ToArray());
            Assert.Equal(1, rows[0].ReadingOrder!.ReadIndex);
            Assert.Equal(TrackRole.Primary, rows[0].Collection!.TrackRole);
            Assert.Null(rows[1].Collection);

            // the omnibus is a container over series 2's #405, which points back at it
            var omnibus = Read<List<SeriesRunRow>>(Value(await browse.GetSeriesRun(4)), "items");
            var container = Assert.Single(omnibus);
            Assert.Equal(7, container.Item.Id);
            Assert.Equal(CollectionLevel.Omnibus, container.Collection!.Level);
            Assert.Equal(TrackRole.Container, container.Collection.TrackRole);
            Assert.Equal((1, 60), (container.Collection.SpanStart, container.Collection.SpanEnd));
            var batman = Read<List<SeriesRunRow>>(Value(await browse.GetSeriesRun(2)), "items");
            Assert.Equal(7, batman.Single(r => r.Item.Id == 5).Collection!.ParentItemId);

            // the `reading` sort follows the same readIndex inside a series group
            var ordered = Read<List<ItemSummary>>(Value(await browse.GetGroupItems("series", "1", orderby: "reading")), "items");
            Assert.Equal(new[] { 1, 2 }, ordered.Select(s => s.Id).ToArray());
        }
    }

    public class UserStateReadTests : IClassFixture<UserActivityFixture>
    {
        private readonly UserActivityFixture fixture;
        public UserStateReadTests(UserActivityFixture fixture) => this.fixture = fixture;

        private static T Bind<T>(T controller) where T : ControllerBase
        {
            controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = BooksIdentity.Principal(1, "owner", false, 3) } };
            return controller;
        }

        private static T Body<T>(IActionResult result) => Assert.IsType<T>(Assert.IsType<OkObjectResult>(result).Value);
        private static object Value(IActionResult result) => Assert.IsType<OkObjectResult>(result).Value!;
        private static T Read<T>(object body, string property) => (T)body.GetType().GetProperty(property)!.GetValue(body)!;

        [Fact]
        public async Task Series_progress_lists_the_finished_and_in_progress_issues()
        {
            using var db = fixture.Fresh();
            var progress = Value(await Bind(new ShelfController(db)).SeriesProgress(1));
            Assert.Equal(2, Read<int>(progress, "total"));
            Assert.Equal(new List<int> { 1 }, Read<List<int>>(progress, "finishedIds"));
            Assert.Equal(new List<int> { 2 }, Read<List<int>>(progress, "inProgressIds"));
            Assert.Equal(1, Read<int>(progress, "finishedCount"));
        }

        [Fact]
        public async Task An_item_mark_reads_back_alone_with_defaults_for_an_unmarked_item()
        {
            using var db = fixture.Fresh();
            var marks = Bind(new MarksController(db));

            var wanted = Body<ItemMarkResult>(await marks.GetItem(4));
            Assert.True(wanted.WantToRead);
            Assert.True(wanted.Favorite);
            Assert.Equal(30, wanted.Rating);

            var bare = Body<ItemMarkResult>(await marks.GetItem(6));
            Assert.False(bare.WantToRead);
            Assert.Null(bare.Rating);
            Assert.Equal("unread", bare.Status);

            Assert.IsType<NotFoundResult>(await marks.GetItem(999_999));
        }
    }

    public class NovelsAdditionsTests : IClassFixture<MigratedFixture>
    {
        private readonly MigratedFixture fixture;
        public NovelsAdditionsTests(MigratedFixture fixture) => this.fixture = fixture;

        private static readonly BooksOptions NoMedia = new();

        private static NovelsController Novels(BooksDb db)
        {
            var thumbnails = new ThumbnailService(Array.Empty<IArchiveReader>(), NoMedia,
                Microsoft.Extensions.Logging.Abstractions.NullLogger<ThumbnailService>.Instance);
            var controller = new NovelsController(db, new MemoryCache(new MemoryCacheOptions { SizeLimit = 50 }), NoMedia, thumbnails);
            controller.ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = BooksIdentity.Principal(1, "owner", false, 3) },
            };
            return controller;
        }

        private static object Value(IActionResult result) => Assert.IsType<OkObjectResult>(result).Value!;
        private static T Read<T>(object body, string property) => (T)body.GetType().GetProperty(property)!.GetValue(body)!;

        private static async Task<object> List(NovelsController c, string? excludeTag = null, int? minRating = null, bool unknown = false) =>
            Value(await c.List(null, null, null, null, null, null, 0, 60, null, excludeTag, minRating, unknown));

        [Fact]
        public async Task Exclude_tag_removes_a_tagged_book_and_the_rows_carry_their_maturity()
        {
            using var db = fixture.Db();
            var novels = Novels(db);

            var all = Read<List<ItemSummary>>(await List(novels), "items").Select(i => i.Id).OrderBy(i => i).ToArray();
            Assert.Equal(new[] { 101, 102 }, all);

            var noSciFi = Read<List<ItemSummary>>(await List(novels, excludeTag: "Science Fiction"), "items").Select(i => i.Id).ToArray();
            Assert.DoesNotContain(102, noSciFi);
            Assert.Contains(101, noSciFi);

            var maturity = Read<Dictionary<string, int?>>(await List(novels), "maturity");
            Assert.Equal(new[] { "101", "102" }, maturity.Keys.OrderBy(k => k).ToArray());
        }

        [Fact]
        public async Task A_rating_floor_and_the_unknown_pile_partition_the_shelf()
        {
            using var db = fixture.Db();
            var novels = Novels(db);

            var rated = Read<List<ItemSummary>>(await List(novels, minRating: 1), "items");
            Assert.All(rated, i => Assert.True(i.Rating >= 1));

            var unknown = Read<List<ItemSummary>>(await List(novels, unknown: true), "items");
            var known = Read<List<ItemSummary>>(await List(novels), "items").Select(i => i.Id).Except(unknown.Select(i => i.Id)).ToList();
            // the two piles never overlap and together they are the shelf
            Assert.Equal(2, unknown.Count + known.Count);
            Assert.All(unknown, i => Assert.False(db.Insights.Any(n => n.SubjectKind == SubjectKind.Item && n.SubjectId == i.Id && n.IsCurrent)));
        }
    }
}
