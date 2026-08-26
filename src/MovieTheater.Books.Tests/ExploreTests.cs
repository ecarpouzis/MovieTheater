using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using MovieTheater.Books;
using MovieTheater.Books.Controllers;
using MovieTheater.Books.Db;
using MovieTheater.Books.Identity;
using MovieTheater.Books.Media;
using MovieTheater.Books.Projections;

namespace MovieTheater.Books.Tests
{
    /// <summary>
    /// Slice 4's Explore contract: the site-wide <see cref="ExploreResponse"/> envelope, the standalone Home's
    /// rails on v2 thresholds, seed reproducibility, and the maturity gate reaching every rail.
    ///
    /// <para>The fixture is deliberately tiny, which is useful here: a threshold that excludes everything is as
    /// testable as one that includes something, and the two together prove the rule rather than the row.</para>
    /// </summary>
    public class ExploreTests : IClassFixture<MigratedFixture>
    {
        private readonly MigratedFixture fixture;
        public ExploreTests(MigratedFixture fixture) => this.fixture = fixture;

        private static ClaimsPrincipal Owner(int ceiling = 3, bool isAdmin = false) =>
            BooksIdentity.Principal(1, "owner", isAdmin, ceiling);

        private static IMemoryCache NewCache() => new MemoryCache(new MemoryCacheOptions { SizeLimit = 200 });

        /// <summary>No media configuration: every card's URL is null and nothing else changes.</summary>
        private static readonly BooksOptions NoMedia = new();

        private static ExploreController Explore(BooksDb db, ClaimsPrincipal? user = null,
            IMemoryCache? cache = null, BooksOptions? options = null)
        {
            var controller = new ExploreController(db, cache ?? NewCache(), options ?? NoMedia);
            controller.ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = user ?? Owner() },
            };
            return controller;
        }

        private static ExploreResponse Body(IActionResult result) =>
            Assert.IsType<ExploreResponse>(Assert.IsType<OkObjectResult>(result).Value);

        private static ExploreRail? Rail(ExploreResponse r, string key) => r.Rails.FirstOrDefault(x => x.Key == key);

        private static int[] Ids(IEnumerable<CardItem> cards) => cards.Select(c => c.Id).OrderBy(x => x).ToArray();

        // ── the envelope ─────────────────────────────────────────────────────────────────────────────────────

        [Fact]
        public async Task The_response_is_the_site_wide_explore_envelope()
        {
            using var db = fixture.Db();
            var response = Body(await Explore(db).Get(seed: 7));

            Assert.Equal(7, response.Seed);
            // Every rail declares a layout the SPA knows, and an empty rail is never sent — a heading over a
            // blank row is worse than no heading.
            Assert.All(response.Rails, r => Assert.Contains(r.Kind, new[] { "strip", "wall", "grid" }));
            Assert.All(response.Rails, r => Assert.NotEmpty(r.Items));

            // Cards carry the contract's identity fields; `raw` is the section's own row, untouched.
            var card = response.Spotlight.First();
            Assert.Equal($"{card.Kind}:{card.Id}", card.Key);
            Assert.Equal("comic", card.Kind);
            Assert.IsType<ItemSummary>(card.Raw);
            Assert.True(card.Aspect > 0);
        }

        [Fact]
        public async Task The_spotlight_is_the_best_rated_prose_carrying_titles_one_per_series()
        {
            using var db = fixture.Db();
            var response = Body(await Explore(db).Get());

            // Rated >= 75 with a resolved synopsis leg: items 4/5 (series 2, rating 95) and 1/2 (series 1, 86 and
            // 84). One per series, best first, so the two representatives are item 4 and item 2.
            Assert.Equal(new[] { 2, 4 }, Ids(response.Spotlight));
        }

        [Fact]
        public async Task The_rails_are_the_standalone_home_rails()
        {
            using var db = fixture.Db();
            var response = Body(await Explore(db).Get());

            // The omnibus: CollectionNode.ContainsCount 60, well past the "big collection" floor of 6.
            var editions = Rail(response, "collected-editions");
            Assert.NotNull(editions);
            Assert.Equal(new[] { 7 }, Ids(editions!.Items));
            Assert.Contains(editions.Items[0].Badges!, b => b.Label.Contains("60") || b.Label.Contains('#'));

            // The counterpart shelf on a comics page is the books: only the rated one clears 60.
            var reads = Rail(response, "top-shelf-reads");
            Assert.NotNull(reads);
            Assert.Equal(new[] { 101 }, Ids(reads!.Items));
            Assert.Equal("book", reads.Items[0].Kind);

            // Fresh arrivals are NOT rotated — every visible comic, newest first, id as the tiebreaker.
            var fresh = Rail(response, "fresh-arrivals");
            Assert.NotNull(fresh);
            Assert.Equal(new[] { 1, 2, 4, 5, 6, 7 }, Ids(fresh!.Items));
            Assert.Equal(7, fresh.Items[0].Id);
            Assert.Equal("wall", fresh.Kind);

            // Every rail that CAN point at a browse does; containment cannot be expressed as a browse filter, so
            // that rail deliberately has no "more" rather than a link that leads somewhere else.
            Assert.NotNull(Rail(response, "fresh-arrivals")!.More);
            Assert.Null(editions.More);
        }

        [Fact]
        public async Task The_series_rail_needs_a_run_the_library_actually_holds()
        {
            using var db = fixture.Db();

            // The fixture holds 3 and 2 issues of its two rated series — below the "at least 4" floor that keeps
            // the rail from headlining one-shots — so the rail is absent.
            Assert.Null(Rail(Body(await Explore(db).Get()), "top-series"));

            using var tx = db.Database.BeginTransaction();
            try
            {
                db.Database.ExecuteSqlRaw("UPDATE Series SET IssueCount = 4 WHERE Id IN (1, 2)");

                var rail = Rail(Body(await Explore(db).Get()), "top-series");
                Assert.NotNull(rail);
                // Series 2 carries the hand-set override of 95, series 1 the computed 86; both clear 72.
                Assert.Equal(new[] { 1, 2 }, Ids(rail!.Items));
                var batman = rail.Items.Single(c => c.Id == 2);
                Assert.Equal("series", batman.Kind);
                Assert.Equal("series:2", batman.Key);
                Assert.Equal(95, batman.Rating);
                // The card is drawn with its cover ISSUE — the first in reading order — without becoming it.
                Assert.Equal("Batman", batman.Title);
            }
            finally { tx.Rollback(); }
        }

        // ── seeds ────────────────────────────────────────────────────────────────────────────────────────────

        [Fact]
        public async Task The_same_seed_composes_the_same_page_and_the_payload_is_cached()
        {
            using var db = fixture.Db();

            // Two controllers, two SEPARATE caches: identical output can only come from the seed.
            var first = Body(await Explore(db, cache: NewCache()).Get(seed: 4242));
            var second = Body(await Explore(db, cache: NewCache()).Get(seed: 4242));
            Assert.Equal(first.Spotlight.Select(c => c.Key), second.Spotlight.Select(c => c.Key));
            Assert.Equal(first.Rails.Select(r => r.Key), second.Rails.Select(r => r.Key));
            Assert.Equal(
                first.Rails.SelectMany(r => r.Items.Select(i => i.Key)),
                second.Rails.SelectMany(r => r.Items.Select(i => i.Key)));

            // A shared cache answers the second call with the page it composed for the first (stamped per call —
            // see The_cached_page_carries_no_media_token_clock — so equal by content, not by reference).
            var cache = NewCache();
            var a = Body(await Explore(db, cache: cache).Get(seed: 99));
            var b = Body(await Explore(db, cache: cache).Get(seed: 99));
            Assert.Equal(a.Spotlight.Select(c => c.Key), b.Spotlight.Select(c => c.Key));
            Assert.Equal(a.Rails.SelectMany(r => r.Items.Select(i => i.Key)), b.Rails.SelectMany(r => r.Items.Select(i => i.Key)));

            // The default seed is the UTC day number, so the page rotates once a day rather than per render.
            Assert.Equal(ExploreController.DaySeed(), Body(await Explore(db).Get()).Seed);
        }

        /// <summary>
        /// The regression that darkened the live Explore: the warm at boot composed the page with a real 12 h
        /// media token baked into every image URL and cached it for a day; twelve hours later every cover was a
        /// 403. The cache must hold the sentinel form, and each response must carry a token minted for THIS call.
        /// </summary>
        [Fact]
        public async Task The_cached_page_carries_no_media_token_clock()
        {
            using var db = fixture.Db();
            var options = new BooksOptions { PublicBaseUrl = "https://books.example", MediaTokenSecret = "media-secret-for-the-test" };
            var cache = NewCache();

            var served = Body(await Explore(db, cache: cache, options: options).Get(seed: 5));
            var url = served.Spotlight.Concat(served.Rails.SelectMany(r => r.Items)).Select(c => c.ImageUrl).First(u => u != null)!;
            Assert.DoesNotContain(MediaUrls.TokenSentinel, url);
            var token = url.Split("/m/")[1].Split('/')[0];
            Assert.True(BooksMediaToken.TryValidate("media-secret-for-the-test", token, out var payload));
            Assert.Equal(1, payload!.UserId);

            // What the cache holds is the sentinel form — nothing in it can expire.
            Assert.True(cache.TryGetValue($"books:explore:1:3:0:{ItemKind.Comic}:5", out ExploreResponse? cached));
            var cachedUrl = cached!.Spotlight.Concat(cached.Rails.SelectMany(r => r.Items)).Select(c => c.ImageUrl).First(u => u != null)!;
            Assert.Contains(MediaUrls.TokenSentinel, cachedUrl);

            // A second call on the warm cache is stamped again, for its caller, and still validates.
            var again = Body(await Explore(db, cache: cache, options: options).Get(seed: 5));
            var url2 = again.Spotlight.Concat(again.Rails.SelectMany(r => r.Items)).Select(c => c.ImageUrl).First(u => u != null)!;
            Assert.DoesNotContain(MediaUrls.TokenSentinel, url2);
            Assert.True(BooksMediaToken.TryValidate("media-secret-for-the-test", url2.Split("/m/")[1].Split('/')[0], out _));
        }

        [Fact]
        public void The_seeded_pick_is_a_deterministic_shuffle_of_the_ranked_pool()
        {
            var pool = Enumerable.Range(1, 40).ToList();
            Assert.Equal(ExploreController.SeededPick(pool, 1234, 6), ExploreController.SeededPick(pool, 1234, 6));
            Assert.NotEqual(ExploreController.SeededPick(pool, 1234, 6), ExploreController.SeededPick(pool, 5678, 6));
            Assert.Equal(6, ExploreController.SeededPick(pool, 1, 6).Count);
            // Taking more than the pool holds is not an error — a small library simply gets a short rail.
            Assert.Equal(3, ExploreController.SeededPick(new[] { 1, 2, 3 }, 1, 10).Count);
        }

        // ── the gate ─────────────────────────────────────────────────────────────────────────────────────────

        [Fact]
        public async Task The_ceiling_reaches_every_rail()
        {
            using var db = fixture.Db();

            // Nothing in the fixture is classified all-ages, so a kid ceiling composes an empty page — the gate
            // failing closed, not a bug.
            var closed = Body(await Explore(db, Owner(0)).Get());
            Assert.Empty(closed.Spotlight);
            Assert.Empty(closed.Rails);

            using var tx = db.Database.BeginTransaction();
            try
            {
                // Series 3 (item 6) is cleared for kids; series 1 and 2 stay teen-or-unclassified.
                db.SeriesTags.Add(new SeriesTag { SeriesId = 3, Category = "audience", Value = "all-ages", Source = TagSource.AI });
                db.SaveChanges();

                var gated = Body(await Explore(db, Owner(0)).Get());
                var everyCard = gated.Spotlight.Concat(gated.Rails.SelectMany(r => r.Items)).ToList();
                Assert.NotEmpty(everyCard);
                // Not one card from the mature-or-unclassified series, and no book (both are gated away at 0).
                Assert.All(everyCard, c => Assert.Equal(6, c.Id));

                // An admin is unrestricted, exactly as everywhere else.
                Assert.NotEmpty(Body(await Explore(db, Owner(0, isAdmin: true)).Get()).Rails);
            }
            finally { tx.Rollback(); }
        }

        [Fact]
        public async Task A_books_explore_swaps_the_kinds_and_drops_the_comics_only_rails()
        {
            using var db = fixture.Db();
            var response = Body(await Explore(db).Get(kind: "book"));

            // Series identity and containment are the comics spine: those rails are absent rather than empty.
            Assert.Null(Rail(response, "top-series"));
            Assert.Null(Rail(response, "collected-editions"));

            var fresh = Rail(response, "fresh-arrivals");
            Assert.NotNull(fresh);
            Assert.All(fresh!.Items, c => Assert.Equal("book", c.Kind));

            // The counterpart shelf is now the comics.
            var reads = Rail(response, "top-shelf-reads");
            Assert.NotNull(reads);
            Assert.All(reads!.Items, c => Assert.Equal("comic", c.Kind));
        }

        // ── media URLs ───────────────────────────────────────────────────────────────────────────────────────

        [Fact]
        public async Task Cards_carry_media_urls_minted_for_the_caller_when_the_host_is_configured()
        {
            using var db = fixture.Db();
            Assert.All(Body(await Explore(db).Get()).Spotlight, c => Assert.Null(c.ImageThumbUrl));

            var configured = new BooksOptions { PublicBaseUrl = "https://host.example/books", MediaTokenSecret = "s3cret" };
            var card = Body(await Explore(db, options: configured).Get()).Spotlight.First();
            Assert.StartsWith("https://host.example/books/m/", card.ImageThumbUrl);
            Assert.EndsWith($"/thumbs/{card.Id}.webp", card.ImageThumbUrl);
            // One rendition: the generated WebP IS the cover the site shows.
            Assert.Equal(card.ImageUrl, card.ImageThumbUrl);
        }
    }
}
