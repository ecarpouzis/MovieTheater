using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using MovieTheater.Books;
using MovieTheater.Books.Archives;
using MovieTheater.Books.Controllers;
using MovieTheater.Books.Db;
using MovieTheater.Books.Identity;
using MovieTheater.Books.Projections;
using MovieTheater.Books.Services;

namespace MovieTheater.Books.Tests
{
    /// <summary>
    /// The novels surface: the books-only list, its exact-equality filters, the facets over the gated set, and
    /// the detail that must be the same payload <c>/items/{id}</c> returns.
    ///
    /// <para>The fixture's two books are the whole point of the maturity assertions: <b>Brave New World</b> (101)
    /// carries a current insight with maturity 2, and <b>Dune</b> (102) has an insight with NO maturity at all —
    /// so 102 is hidden below ceiling 3 and 101 below ceiling 2. An unclassified book is never assumed safe.</para>
    /// </summary>
    public class NovelsTests : IClassFixture<MigratedFixture>
    {
        private readonly MigratedFixture fixture;
        public NovelsTests(MigratedFixture fixture) => this.fixture = fixture;

        private static ClaimsPrincipal Owner(int ceiling = 3, bool isAdmin = false) =>
            BooksIdentity.Principal(1, "owner", isAdmin, ceiling);

        private static readonly BooksOptions NoMedia = new();

        private static NovelsController Novels(BooksDb db, ClaimsPrincipal? user = null, IMemoryCache? cache = null)
        {
            var thumbnails = new ThumbnailService(
                Array.Empty<IArchiveReader>(), NoMedia,
                Microsoft.Extensions.Logging.Abstractions.NullLogger<ThumbnailService>.Instance);
            var controller = new NovelsController(db, cache ?? new MemoryCache(new MemoryCacheOptions { SizeLimit = 50 }),
                NoMedia, thumbnails);
            controller.ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = user ?? Owner() },
            };
            return controller;
        }

        private static object Value(IActionResult result) => Assert.IsType<OkObjectResult>(result).Value!;

        private static T Read<T>(object body, string property) =>
            (T)body.GetType().GetProperty(property)!.GetValue(body)!;

        private static async Task<int[]> ListIds(NovelsController controller, string? author = null,
            string? series = null, string? publisher = null, string? decade = null, string? tag = null,
            string? q = null, string? orderby = null)
        {
            var body = Value(await controller.List(author, series, publisher, decade, tag, q, 0, 60, orderby));
            return Read<List<ItemSummary>>(body, "items").Select(i => i.Id).ToArray();
        }

        // ── the gate ─────────────────────────────────────────────────────────────────────────────────────────

        [Fact]
        public async Task An_unrated_book_is_hidden_below_the_top_ceiling()
        {
            using var db = fixture.Db();
            Assert.Equal(new[] { 101, 102 }, (await ListIds(Novels(db, Owner(3)))).OrderBy(x => x).ToArray());
            // 101 is maturity 2; 102 has no maturity, so it disappears the moment a ceiling is applied at all.
            Assert.Equal(new[] { 101 }, await ListIds(Novels(db, Owner(2))));
            Assert.Empty(await ListIds(Novels(db, Owner(1))));
            Assert.Empty(await ListIds(Novels(db, Owner(0))));
            // An admin is unrestricted.
            Assert.Equal(2, (await ListIds(Novels(db, Owner(0, isAdmin: true)))).Length);
        }

        [Fact]
        public async Task Only_books_are_here()
        {
            using var db = fixture.Db();
            Assert.All(Read<List<ItemSummary>>(Value(await Novels(db).List()), "items"),
                s => Assert.Equal("book", s.Kind));
        }

        // ── filters ──────────────────────────────────────────────────────────────────────────────────────────

        [Fact]
        public async Task The_filters_are_exact_equality_multi_valued_and_and_across()
        {
            using var db = fixture.Db();
            var novels = Novels(db);

            // author = ItemCredit(Source = Calibre, Role = Author). Calibre's "A & B" is two credits, so either
            // name finds the book — which a substring match on a single column could never do correctly.
            Assert.Equal(new[] { 102 }, await ListIds(novels, author: "Frank Herbert"));
            Assert.Equal(new[] { 102 }, await ListIds(novels, author: "Brian Herbert"));
            Assert.Empty(await ListIds(novels, author: "Herbert"));            // exact equality, not contains
            Assert.Equal(2, (await ListIds(novels, author: "Frank Herbert,Aldous Huxley")).Length);   // OR within

            // series / publisher come off BookDetail (Calibre's own fields).
            Assert.Equal(new[] { 101 }, await ListIds(novels, series: "Classics"));
            Assert.Equal(new[] { 101 }, await ListIds(novels, publisher: "Harper"));
            Assert.Equal(new[] { 102 }, await ListIds(novels, publisher: "Chilton"));

            // AND across facets: a real pairing survives, a crossed one does not.
            Assert.Equal(new[] { 101 }, await ListIds(novels, author: "Aldous Huxley", publisher: "Harper"));
            Assert.Empty(await ListIds(novels, author: "Aldous Huxley", publisher: "Chilton"));
        }

        [Fact]
        public async Task Decades_come_off_the_resolved_year_not_a_date_string()
        {
            using var db = fixture.Db();
            var novels = Novels(db);
            Assert.Equal(new[] { 101 }, await ListIds(novels, decade: "2000s"));
            Assert.Equal(new[] { 102 }, await ListIds(novels, decade: "1960s"));
            Assert.Equal(2, (await ListIds(novels, decade: "2000s,1960s")).Length);
            // "1960" and "1965" both mean the 1960s; nonsense is dropped rather than guessed at.
            Assert.Equal(new[] { 102 }, await ListIds(novels, decade: "1965"));
            Assert.Equal(2, (await ListIds(novels, decade: "not-a-decade")).Length);
        }

        [Fact]
        public async Task Tags_match_by_category_and_value_or_by_value_alone()
        {
            using var db = fixture.Db();
            var novels = Novels(db);
            Assert.Equal(new[] { 102 }, await ListIds(novels, tag: "setting:desert"));
            Assert.Equal(new[] { 102 }, await ListIds(novels, tag: "desert"));
            Assert.Empty(await ListIds(novels, tag: "genre:desert"));   // the category is honoured when given
        }

        [Fact]
        public async Task Search_rides_the_full_text_index()
        {
            using var db = fixture.Db();
            var novels = Novels(db);
            Assert.Equal(new[] { 102 }, await ListIds(novels, q: "Dune"));
            Assert.Equal(new[] { 101 }, await ListIds(novels, q: "Huxley"));
            Assert.Empty(await ListIds(novels, q: "nothingmatchesthis"));
        }

        [Fact]
        public async Task Every_sort_ends_with_the_id_and_paging_reports_the_true_total()
        {
            using var db = fixture.Db();
            var novels = Novels(db);

            Assert.Equal(new[] { 101, 102 }, await ListIds(novels, orderby: "title"));
            Assert.Equal(new[] { 101, 102 }, await ListIds(novels, orderby: "rating"));   // 85, then unrated
            Assert.Equal(new[] { 101, 102 }, await ListIds(novels, orderby: "newest"));   // 2006, then 1965
            Assert.Equal(new[] { 102, 101 }, await ListIds(novels, orderby: "oldest"));

            var page = Value(await novels.List(skip: 1, top: 1, orderby: "title"));
            Assert.Equal(2, Read<int>(page, "total"));
            Assert.Equal(new[] { 102 }, Read<List<ItemSummary>>(page, "items").Select(i => i.Id).ToArray());
        }

        // ── facets ───────────────────────────────────────────────────────────────────────────────────────────

        [Fact]
        public async Task Facets_count_the_gated_set_and_shrink_with_the_ceiling()
        {
            using var db = fixture.Db();
            var facets = Assert.IsType<NovelFacets>(Value(await Novels(db).Facets()));

            Assert.Contains(facets.Authors, a => a.Value == "Frank Herbert" && a.Count == 1);
            Assert.Contains(facets.Authors, a => a.Value == "Aldous Huxley");
            Assert.Contains(facets.Series, s => s.Value == "Classics" && s.Count == 1);
            Assert.Contains(facets.Publishers, p => p.Value == "Harper");
            // Decades stay chronological, newest first — never count-sorted.
            Assert.Equal(new[] { "2000s", "1960s" }, facets.Decades.Select(d => d.Value).ToArray());
            // A tag facet's value is the composite the ?tag= filter takes, so a chip round-trips unchanged.
            Assert.Contains(facets.Tags, t => t.Value == "setting:desert");

            // A restricted account never learns that a facet value it is gated out of exists.
            var restricted = Assert.IsType<NovelFacets>(Value(await Novels(db, Owner(2)).Facets()));
            Assert.DoesNotContain(restricted.Authors, a => a.Value == "Frank Herbert");
            Assert.Equal(new[] { "2000s" }, restricted.Decades.Select(d => d.Value).ToArray());
        }

        [Fact]
        public async Task Facets_are_cached_per_caller()
        {
            using var db = fixture.Db();
            var cache = new MemoryCache(new MemoryCacheOptions { SizeLimit = 50 });
            var first = Value(await Novels(db, cache: cache).Facets());
            var second = Value(await Novels(db, cache: cache).Facets());
            Assert.Same(first, second);
            // A different ceiling is a different key — a cached facet list must never cross the gate.
            Assert.NotSame(first, Value(await Novels(db, Owner(2), cache).Facets()));
        }

        // ── detail ───────────────────────────────────────────────────────────────────────────────────────────

        [Fact]
        public async Task The_detail_is_the_item_detail_and_a_comic_id_is_404()
        {
            using var db = fixture.Db();
            var detail = Assert.IsType<ItemDetail>(Value(await Novels(db).Get(101)));
            Assert.Equal(101, detail.Summary.Id);
            Assert.Equal("Brave New World", detail.Summary.Title);
            Assert.NotNull(detail.Book);
            Assert.Equal("Harper", detail.Book!.Publisher);
            Assert.Equal(2, detail.Insight!.Maturity);

            // A comic at that id is not "the wrong kind" from outside — it is simply not here.
            Assert.IsType<NotFoundResult>(await Novels(db).Get(1));
            Assert.IsType<NotFoundResult>(await Novels(db).Get(999999));
            // And the gate applies to the by-id read exactly as to the list.
            Assert.IsType<NotFoundResult>(await Novels(db, Owner(1)).Get(101));
        }
    }
}
