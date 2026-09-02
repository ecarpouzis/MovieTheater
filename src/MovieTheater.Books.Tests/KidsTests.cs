using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using MovieTheater.Books;
using MovieTheater.Books.Access;
using MovieTheater.Books.Controllers;
using MovieTheater.Books.Db;
using MovieTheater.Books.Identity;
using MovieTheater.Books.Projections;

namespace MovieTheater.Books.Tests
{
    /// <summary>
    /// The kids surfaces: the admin allow-list, the blocked-audience floor on top of it, the forced ceiling 0,
    /// and the shelf/landing shapes.
    ///
    /// <para>The fixture ships the two real <c>KidSafeTag</c> rows — <c>audience: all-ages</c> for comics and
    /// <c>audience: children</c> for books — but nothing that carries them, so the default state of these tests
    /// is "no kid content", which is the correct default for a gate. Each test that needs kid content grants it
    /// inside a transaction and rolls it back, the way <c>CatalogTests</c> does for the maturity rules.</para>
    /// </summary>
    public class KidsTests : IClassFixture<MigratedFixture>
    {
        private readonly MigratedFixture fixture;
        public KidsTests(MigratedFixture fixture) => this.fixture = fixture;

        private static ClaimsPrincipal Owner(int ceiling = 3, bool isAdmin = false) =>
            BooksIdentity.Principal(1, "owner", isAdmin, ceiling);

        private static readonly BooksOptions NoMedia = new();

        private static T Bind<T>(T controller, ClaimsPrincipal user) where T : ControllerBase
        {
            controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = user } };
            return controller;
        }

        private static KidsController Kids(BooksDb db, ClaimsPrincipal? user = null) =>
            Bind(new KidsController(db, NoMedia), user ?? Owner());

        private static ExploreController Explore(BooksDb db, ClaimsPrincipal? user = null) =>
            Bind(new ExploreController(db, new MemoryCache(new MemoryCacheOptions { SizeLimit = 50 }), NoMedia), user ?? Owner());

        private static object Value(IActionResult result) => Assert.IsType<OkObjectResult>(result).Value!;

        private static T Read<T>(object body, string property) =>
            (T)body.GetType().GetProperty(property)!.GetValue(body)!;

        /// <summary>Clear series 1 for kids and contradict series 2, the two halves of the policy.</summary>
        private static void GrantKidTags(BooksDb db)
        {
            db.SeriesTags.Add(new SeriesTag { SeriesId = 1, Category = "audience", Value = "all-ages", Source = TagSource.AI });
            db.SeriesTags.Add(new SeriesTag { SeriesId = 2, Category = "audience", Value = "all-ages", Source = TagSource.AI });
            db.SeriesTags.Add(new SeriesTag { SeriesId = 2, Category = "audience", Value = "mature", Source = TagSource.AI });
            db.SaveChanges();
        }

        // ── the allow-list ───────────────────────────────────────────────────────────────────────────────────

        [Fact]
        public async Task Nothing_is_kid_safe_until_the_allow_listed_tag_is_on_the_series()
        {
            using var db = fixture.Db();
            // Series 1 carries audience:teen. Teen is not blocked (min-wins), but it is not the allow-listed
            // all-ages either — the allow-list decides INCLUSION, so nothing clears.
            var body = Value(await Kids(db).Browse());
            Assert.Equal(0, Read<int>(body, "totalGroups"));
            Assert.Empty(Read<List<BrowseGroupItem>>(body, "groups"));
        }

        [Fact]
        public async Task The_allow_list_admits_and_the_blocked_floor_overrules_it()
        {
            using var db = fixture.Db();
            using var tx = db.Database.BeginTransaction();
            try
            {
                GrantKidTags(db);

                var body = Value(await Kids(db).Browse());
                var groups = Read<List<BrowseGroupItem>>(body, "groups");

                // Series 1 clears (all-ages, and its teen tag is descriptive spread, not a contradiction).
                // Series 2 carries all-ages AND mature — a two-level spread — so the floor overrules the
                // allow-list and it never appears.
                var shelf = Assert.Single(groups);
                Assert.Equal("1", shelf.Key);
                Assert.Equal("2000 AD", shelf.Label);
                // Its two live issues; the shadow duplicate (item 3) is excluded from the kids browse like
                // from every other list surface.
                Assert.Equal(new[] { 1, 2 }, shelf.Items.Select(i => i.Id).OrderBy(x => x).ToArray());
                Assert.Equal(2, shelf.TotalItems);
            }
            finally { tx.Rollback(); }
        }

        [Fact]
        public async Task The_kids_view_is_the_same_for_a_child_an_adult_and_an_admin()
        {
            using var db = fixture.Db();
            using var tx = db.Database.BeginTransaction();
            try
            {
                GrantKidTags(db);
                // The ceiling is the view's, not the caller's: that is what makes the shelf checkable before a
                // child is handed it.
                var asChild = Read<List<BrowseGroupItem>>(Value(await Kids(db, Owner(0)).Browse()), "groups");
                var asAdult = Read<List<BrowseGroupItem>>(Value(await Kids(db, Owner(3)).Browse()), "groups");
                var asAdmin = Read<List<BrowseGroupItem>>(Value(await Kids(db, Owner(3, isAdmin: true)).Browse()), "groups");

                Assert.Equal(asChild.Select(g => g.Key), asAdult.Select(g => g.Key));
                Assert.Equal(asChild.Select(g => g.Key), asAdmin.Select(g => g.Key));
                Assert.Single(asChild);
            }
            finally { tx.Rollback(); }
        }

        [Fact]
        public async Task A_kid_cleared_book_rides_as_the_trailing_group()
        {
            using var db = fixture.Db();
            using var tx = db.Database.BeginTransaction();
            try
            {
                GrantKidTags(db);
                // A book carries its own clearance (ItemTag) AND its own maturity (its current Insight). Both
                // have to say yes: the tag alone would not be enough, which is the whole fail-safe.
                db.ItemTags.Add(new ItemTag { ItemId = 102, Category = "audience", Value = "children", Source = TagSource.AI });
                db.SaveChanges();
                Assert.DoesNotContain(Read<List<BrowseGroupItem>>(Value(await Kids(db).Browse()), "groups"),
                    g => g.Key == KidsController.BooksGroupKey);

                db.Database.ExecuteSqlRaw(
                    "UPDATE Insight SET Maturity = 0 WHERE SubjectKind = 0 AND SubjectId = 102 AND IsCurrent = 1");

                var groups = Read<List<BrowseGroupItem>>(Value(await Kids(db).Browse()), "groups");
                var books = groups.Single(g => g.Key == KidsController.BooksGroupKey);
                Assert.Equal("Books", books.Label);
                Assert.Equal(new[] { 102 }, books.Items.Select(i => i.Id).ToArray());
                // Books come after the series shelves — the standalone's order, kept.
                Assert.Equal(KidsController.BooksGroupKey, groups.Last().Key);
            }
            finally { tx.Rollback(); }
        }

        [Fact]
        public async Task A_book_is_cleared_only_by_an_AI_tag_never_by_a_provider_tag_that_spells_the_same_word()
        {
            using var db = fixture.Db();
            using var tx = db.Database.BeginTransaction();
            try
            {
                GrantKidTags(db);
                db.Database.ExecuteSqlRaw("UPDATE Insight SET Maturity = 0 WHERE SubjectKind = 0 AND SubjectId = 102 AND IsCurrent = 1");
                // A Calibre subject reading "children" is a shelving word, not a safety verdict — the series
                // path has always filtered Source = AI and the book path now does too.
                db.ItemTags.Add(new ItemTag { ItemId = 102, Category = "audience", Value = "children", Source = TagSource.Calibre });
                db.SaveChanges();
                Assert.DoesNotContain(102, await KidsPolicy.KidBookIdsAsync(db));

                db.ItemTags.Add(new ItemTag { ItemId = 102, Category = "audience", Value = "children", Source = TagSource.AI });
                db.SaveChanges();
                Assert.Contains(102, await KidsPolicy.KidBookIdsAsync(db));

                // And the blocked floor is read from AI tags only as well: an External "mature" does not block.
                db.ItemTags.Add(new ItemTag { ItemId = 102, Category = "audience", Value = "mature", Source = TagSource.External });
                db.SaveChanges();
                Assert.Contains(102, await KidsPolicy.KidBookIdsAsync(db));
                db.ItemTags.Add(new ItemTag { ItemId = 102, Category = "audience", Value = "mature", Source = TagSource.AI });
                db.SaveChanges();
                Assert.DoesNotContain(102, await KidsPolicy.KidBookIdsAsync(db));
            }
            finally { tx.Rollback(); }
        }

        // ── paging + the drill ───────────────────────────────────────────────────────────────────────────────

        [Fact]
        public async Task The_browse_pages_by_group_and_caps_the_shelf()
        {
            using var db = fixture.Db();
            using var tx = db.Database.BeginTransaction();
            try
            {
                GrantKidTags(db);

                var page = Value(await Kids(db).Browse(groupsSkip: 0, groupsTop: 1, perGroupTop: 1));
                var shelf = Assert.Single(Read<List<BrowseGroupItem>>(page, "groups"));
                Assert.Single(shelf.Items);
                // The head still reports the shelf's TRUE size — a progress figure must not shrink with the page.
                Assert.Equal(2, shelf.TotalItems);

                // Past the end is an empty page, never an error.
                Assert.Empty(Read<List<BrowseGroupItem>>(Value(await Kids(db).Browse(groupsSkip: 50)), "groups"));

                // Only the series grouping exists here.
                Assert.IsType<BadRequestObjectResult>(await Kids(db).Browse(groupBy: "publisher"));
            }
            finally { tx.Rollback(); }
        }

        [Fact]
        public async Task A_shelf_drills_and_a_blocked_series_is_404_not_403()
        {
            using var db = fixture.Db();
            using var tx = db.Database.BeginTransaction();
            try
            {
                GrantKidTags(db);

                var body = Value(await Kids(db).SeriesItems(1, top: 1));
                Assert.Equal(2, Read<int>(body, "total"));
                Assert.Single(Read<List<ItemSummary>>(body, "items"));

                // Series 2 is blocked by the floor and series 3 was never cleared: both are simply absent.
                Assert.IsType<NotFoundResult>(await Kids(db).SeriesItems(2));
                Assert.IsType<NotFoundResult>(await Kids(db).SeriesItems(3));
            }
            finally { tx.Rollback(); }
        }

        // ── the kids landing ─────────────────────────────────────────────────────────────────────────────────

        [Fact]
        public async Task The_kids_landing_answers_the_same_explore_envelope()
        {
            using var db = fixture.Db();
            Assert.Empty(Assert.IsType<ExploreResponse>(
                Assert.IsType<OkObjectResult>(await Explore(db).GetKids()).Value).Spotlight);

            using var tx = db.Database.BeginTransaction();
            try
            {
                GrantKidTags(db);

                var response = Assert.IsType<ExploreResponse>(
                    Assert.IsType<OkObjectResult>(await Explore(db).GetKids(seed: 11)).Value);
                Assert.Equal(11, response.Seed);
                // The hero series' issues are the spotlight; with one cleared series there is no body rail left.
                Assert.Equal(new[] { 1, 2 }, response.Spotlight.Select(c => c.Id).OrderBy(x => x).ToArray());
                Assert.Empty(response.Rails);

                // Ceiling 0 is forced, so an unrestricted caller still gets only kid content.
                var asAdmin = Assert.IsType<ExploreResponse>(
                    Assert.IsType<OkObjectResult>(await Explore(db, Owner(3, isAdmin: true)).GetKids(seed: 11)).Value);
                Assert.Equal(response.Spotlight.Select(c => c.Key), asAdmin.Spotlight.Select(c => c.Key));
            }
            finally { tx.Rollback(); }
        }

        // ── the policy itself ────────────────────────────────────────────────────────────────────────────────

        [Fact]
        public async Task The_policy_reads_the_admin_table_and_the_shared_blocked_floor()
        {
            using var db = fixture.Db();
            Assert.Equal([("audience", "all-ages")], (await KidsPolicy.AllowedPairsAsync(db, ItemKind.Comic)).ToList());
            Assert.Equal([("audience", "children")], (await KidsPolicy.AllowedPairsAsync(db, ItemKind.Book)).ToList());
            // The floor is the maturity gate's own list at ceiling 0 — one edit, not two.
            Assert.Equal(MaturityFilter.HardBlockedAbove(KidsPolicy.Ceiling), MaturityFilter.HardBlockedAbove(0));
            Assert.Equal(0, KidsPolicy.Ceiling);
        }
    }
}
