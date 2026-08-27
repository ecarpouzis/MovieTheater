using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using MovieTheater.Books.Access;
using MovieTheater.Books.Controllers;
using MovieTheater.Books.Db;
using MovieTheater.Books.Identity;
using MovieTheater.Books.Projections;

namespace MovieTheater.Books.Tests
{
    /// <summary>
    /// The migrated synthetic v1 file, shared by every browse test in the class — one migrate, many reads.
    /// Everything is a throwaway file under the temp directory; nothing reads the real v2 files.
    /// </summary>
    public sealed class MigratedFixture : IDisposable
    {
        public readonly V1Fixture V1 = new();

        public MigratedFixture()
        {
            var summary = V1.Engine(V1.Options()).Run();
            if (summary.Stopped) throw new InvalidOperationException("fixture migration stopped: " + summary.StopReason);
        }

        public BooksDb Db() => V1.HotDb();
        public void Dispose() => V1.Dispose();
    }

    /// <summary>
    /// Slice 1's contract: the flat projection, the OData catalog (search, the directory drill, $count-safety) and
    /// the browse facets / two-phase group heads — all against a real migrated SQLite file, with the controllers
    /// instantiated directly under a fabricated principal (the same thing the cache warmer does).
    /// </summary>
    public class CatalogTests : IClassFixture<MigratedFixture>
    {
        private readonly MigratedFixture fixture;
        public CatalogTests(MigratedFixture fixture) => this.fixture = fixture;

        private static ClaimsPrincipal Owner(int ceiling = 3, bool isAdmin = false) =>
            BooksIdentity.Principal(1, "owner", isAdmin, ceiling);

        private static T Bind<T>(T controller, ClaimsPrincipal user) where T : ControllerBase
        {
            controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = user } };
            return controller;
        }

        private static IMemoryCache NewCache() => new MemoryCache(new MemoryCacheOptions { SizeLimit = 200 });

        private CatalogController Catalog(BooksDb db, ClaimsPrincipal? user = null) =>
            Bind(new CatalogController(db), user ?? Owner());

        private BrowseController Browse(BooksDb db, ClaimsPrincipal? user = null, IMemoryCache? cache = null) =>
            Bind(new BrowseController(db, cache ?? NewCache()), user ?? Owner());

        private static T Body<T>(IActionResult result) => Assert.IsType<T>(Assert.IsType<OkObjectResult>(result).Value);

        // ── the projection ────────────────────────────────────────────────────────────────────────────────────

        [Fact]
        public void The_projection_translates_and_is_count_safe()
        {
            using var db = fixture.Db();
            var query = Catalog(db).Get();

            // $count rides EnableQuery, which calls Count() on exactly this IQueryable: a collection or a
            // correlated subquery in the projection is what would break it, so counting it IS the assertion.
            var total = query.Count();
            Assert.Equal(6, total);   // 7 fixture comics, one of them an excluded shadow duplicate
            Assert.Equal(total, query.ToList().Count);

            var dredd = query.Single(s => s.Id == 1);
            Assert.Equal("2000 AD #1", dredd.Title);
            Assert.Equal("comic", dredd.Kind);
            Assert.Equal(1, dredd.SeriesId);
            Assert.Equal("2000 AD", dredd.Series);
            Assert.Equal(3, dredd.SeriesIssueCount);
            Assert.Equal(1977, dredd.SeriesYearStart);
            Assert.True(dredd.SeriesIsOngoing);
            Assert.Equal("2000 AD", dredd.Franchise);
            Assert.False(dredd.IsSingleIssueSeries);
            Assert.Equal("Rebellion", dredd.Publisher);
            Assert.Equal(1977, dredd.Year);
            Assert.Equal(2, dredd.Month);
            Assert.Equal(DatePrecision.Day, dredd.DatePrecision);
            Assert.Equal(84, dredd.Rating);
            Assert.Equal(86, dredd.SeriesRatingResolved);
            Assert.Equal(SynopsisSource.Cv, dredd.SynopsisSource);
            Assert.Equal("2000 AD #1.cbz", dredd.FileName);
            Assert.Equal(4, dredd.FolderId);
            Assert.Equal(2, dredd.TopFolderId);
            Assert.False(dredd.IsExcluded);

            // the one-issue series collapses into a single entity
            Assert.True(query.Single(s => s.Id == 6).IsSingleIssueSeries);
            // books are their own kind and are not in the comic catalog
            Assert.DoesNotContain(query.ToList(), s => s.Id == 101);
            Assert.Equal("book", Catalog(db).Get(kind: "book").Single(s => s.Id == 101).Kind);
        }

        [Fact]
        public void Count_true_reports_the_filtered_total_in_a_header()
        {
            using var db = fixture.Db();
            var controller = Catalog(db);
            controller.HttpContext.Request.QueryString = new QueryString("?$count=true&$filter=year eq 1987");
            controller.Get();
            // The OData @odata.count envelope needs an EDM-routed endpoint; this one is query-options-only, so the
            // total rides a header — computed through the same parser, so it honours $filter.
            Assert.Equal("2", controller.Response.Headers[CatalogController.TotalCountHeader].ToString());

            // no $count asked for ⇒ no extra COUNT query and no header
            var quiet = Catalog(db);
            quiet.HttpContext.Request.QueryString = new QueryString("?$top=2");
            quiet.Get();
            Assert.False(quiet.Response.Headers.ContainsKey(CatalogController.TotalCountHeader));
        }

        [Fact]
        public void An_ordered_page_ends_with_the_id_tiebreaker_and_is_stable()
        {
            using var db = fixture.Db();
            var query = Catalog(db).Get();
            var first = query.OrderBy(s => s.Series).ThenBy(s => s.Id).Take(3).Select(s => s.Id).ToList();
            var again = query.OrderBy(s => s.Series).ThenBy(s => s.Id).Take(3).Select(s => s.Id).ToList();
            Assert.Equal(first, again);
        }

        // ── search + the directory drill ──────────────────────────────────────────────────────────────────────

        [Fact]
        public void Search_finds_an_item_by_a_word_from_its_synopsis()
        {
            using var db = fixture.Db();
            // "Dredd" appears in item 1's own synopsis and in the series description item 2's synopsis resolves
            // to, so the search narrows to the 2000 AD run rather than returning the library.
            var hits = Catalog(db).Get(q: "Dredd").Select(h => h.Id).OrderBy(x => x).ToList();
            Assert.Equal(new[] { 1, 2 }, hits.ToArray());

            // Punctuation is a SEPARATOR, never FTS5 syntax: "B.P.R.D." must return results, not a syntax error
            // (a denylist that misses '.' is exactly how that query used to 500).
            Assert.NotEmpty(Catalog(db).Get(q: "B.P.R.D.").ToList());
            // a query with nothing searchable left in it returns nothing rather than the whole library
            Assert.Empty(Catalog(db).Get(q: "!!!").ToList());
        }

        [Fact]
        public void The_directory_drill_shows_the_shadow_duplicate_the_catalog_hides()
        {
            using var db = fixture.Db();
            // item 3 is the excluded duplicate of item 2; both live in folder 4
            Assert.DoesNotContain(Catalog(db).Get().ToList(), s => s.Id == 3);
            // a plain exclusion is hidden EVERYWHERE, the drill included
            Assert.Equal(new[] { 1, 2 }, Catalog(db).Get(directory: 4).Select(s => s.Id).OrderBy(x => x).ToArray());

            using var tx = db.Database.BeginTransaction();
            try
            {
                // ...but a SHADOW duplicate (KeepInDirectory) stays visible in the drill, because that view
                // mirrors the folder tree and the file genuinely lives there. The tile dims it.
                db.Items.Single(i => i.Id == 3).KeepInDirectory = true;
                db.SaveChanges();

                Assert.DoesNotContain(Catalog(db).Get().ToList(), s => s.Id == 3);
                var drill = Catalog(db).Get(directory: 4).ToList();
                Assert.Equal(new[] { 1, 2, 3 }, drill.Select(s => s.Id).OrderBy(x => x).ToArray());
                Assert.True(drill.Single(s => s.Id == 3).IsExcluded);
            }
            finally { tx.Rollback(); }
        }

        // ── the maturity gate ─────────────────────────────────────────────────────────────────────────────────

        [Fact]
        public void The_ceiling_decides_what_the_catalog_contains()
        {
            using var db = fixture.Db();
            // Ceiling 3 (and every admin) is a no-op: the whole library.
            Assert.Equal(6, Catalog(db, Owner(3)).Get().Count());
            Assert.Equal(6, Catalog(db, Owner(0, isAdmin: true)).Get().Count());

            // The fixture's series 2 (Batman) carries audience:teen on its current insight; series 1's current
            // insight carries no audience tag at all. Min-wins REQUIRES a known classification at-or-below the
            // ceiling, so a teen ceiling shows Batman and nothing else, and a kid ceiling shows nothing.
            Assert.Equal(new[] { 4, 5 }, Catalog(db, Owner(1)).Get().Select(s => s.Id).OrderBy(x => x).ToArray());
            Assert.Empty(Catalog(db, Owner(0)).Get().ToList());

            // Books gate on their OWN current insight's maturity, fail-safe when it has none: book 101 is
            // maturity 2, book 102 has no maturity at all.
            Assert.Equal(new[] { 101, 102 }, Catalog(db, Owner(3)).Get(kind: "book").Select(s => s.Id).OrderBy(x => x).ToArray());
            Assert.Equal(new[] { 101 }, Catalog(db, Owner(2)).Get(kind: "book").Select(s => s.Id).ToArray());
            Assert.Empty(Catalog(db, Owner(1)).Get(kind: "book").ToList());
        }

        [Fact]
        public void An_all_ages_series_passes_a_kid_ceiling_and_a_contradictory_one_does_not()
        {
            using var db = fixture.Db();
            using var tx = db.Database.BeginTransaction();
            try
            {
                // series 3 is all-ages; series 4 is the contradiction (all-ages AND adult) the hard block exists for
                db.SeriesTags.Add(new SeriesTag { SeriesId = 3, Category = "audience", Value = "all-ages", Source = TagSource.AI });
                db.SeriesTags.Add(new SeriesTag { SeriesId = 4, Category = "audience", Value = "all-ages", Source = TagSource.AI });
                db.SeriesTags.Add(new SeriesTag { SeriesId = 4, Category = "audience", Value = "adult", Source = TagSource.AI });
                db.SaveChanges();

                Assert.Equal(new[] { 6 }, Catalog(db, Owner(0)).Get().Select(s => s.Id).ToArray());
                // one level of overlap is normal descriptive spread, so all-ages + teen still passes a teen ceiling
                Assert.Equal(new[] { 4, 5, 6 }, Catalog(db, Owner(1)).Get().Select(s => s.Id).OrderBy(x => x).ToArray());
            }
            finally { tx.Rollback(); }
        }

        // ── facets ────────────────────────────────────────────────────────────────────────────────────────────

        [Fact]
        public async Task Facets_count_what_the_fixture_holds()
        {
            using var db = fixture.Db();
            var facets = Body<BrowseFacetsResult>(await Browse(db).GetFacets());

            // series: 4 canonical series over 6 visible items (the excluded duplicate is not counted)
            Assert.Equal(4, facets.Series.Count);
            Assert.Equal(2, facets.Series.Single(s => s.Id == 1).Count);
            Assert.Equal("2000 AD", facets.Series.Single(s => s.Id == 1).Value);
            Assert.Equal(2, facets.Series.Single(s => s.Id == 2).Count);
            Assert.Equal(6, facets.Series.Sum(s => s.Count));

            // publishers carry the Publisher row's id and full name where the resolved name matches one
            var rebellion = facets.Publishers.Single(p => p.Name == "Rebellion");
            Assert.Equal(1, rebellion.Id);
            Assert.Equal("Rebellion Developments", rebellion.Full);
            Assert.Equal(4, rebellion.Count);
            Assert.Equal(6, facets.Publishers.Sum(p => p.Count));
            Assert.Equal(2, facets.Publishers.Single(p => p.Name != "Rebellion").Count);

            // decades are chronological labels
            Assert.Equal(new[] { "1970s", "1980s", "2020s" }, facets.Decades.Select(d => d.Value).ToArray());
            Assert.Equal(2, facets.Decades.Single(d => d.Value == "1970s").Count);

            // collections are the depth-1 folder under a root, named from the Folder row (the fixture files every
            // comic under the same one)
            var collection = Assert.Single(facets.Collections);
            Assert.Equal(2, collection.Id);
            Assert.Equal("2000AD", collection.Name);
            Assert.Equal(6, collection.Count);

            // events + franchises come off ComicDetail and Series, restricted to what the caller may see
            Assert.Equal(2, facets.Events.Single(e => e.Value == "Year One").Count);
            Assert.Equal(2, facets.Franchises.Single(f => f.Value == "Batman").Count);

            // credits are ROWS: grouped on the normalized name, shown under a real one
            Assert.Contains(facets.Authors, a => a.Value == "Frank Miller" && a.Count == 2);
            Assert.Contains(facets.Authors, a => a.Value == "Pat Mills" && a.Count == 2);
            Assert.Contains(facets.Artists, a => a.Value == "Carlos Ezquerra");

            // tags are ROWS too — the item's own plus the ones its series carries
            Assert.Contains(facets.Tags, t => t.Value == "Science Fiction");
        }

        [Fact]
        public async Task Facets_shrink_with_the_ceiling()
        {
            using var db = fixture.Db();
            var restricted = Body<BrowseFacetsResult>(await Browse(db, Owner(1)).GetFacets());
            // a restricted account never sees a facet value or a count it is not allowed to browse
            Assert.Equal(new[] { 2 }, restricted.Series.Select(s => s.Id).ToArray());
            Assert.Equal(2, restricted.Series.Single().Count);
            Assert.DoesNotContain(restricted.Franchises, f => f.Value == "2000 AD");
        }

        [Fact]
        public async Task Facet_options_paginate_and_search_the_long_tail()
        {
            using var db = fixture.Db();
            var controller = Browse(db);
            var all = Assert.IsType<OkObjectResult>(await controller.GetFacetOptions("authors")).Value!;
            var total = (int)all.GetType().GetProperty("total")!.GetValue(all)!;
            Assert.True(total >= 3);

            var page = Assert.IsType<OkObjectResult>(await controller.GetFacetOptions("authors", q: "miller")).Value!;
            var items = (List<FacetOption>)page.GetType().GetProperty("items")!.GetValue(page)!;
            Assert.Equal(new[] { "Frank Miller" }, items.Select(i => i.Value).ToArray());

            Assert.IsType<BadRequestObjectResult>(await controller.GetFacetOptions("nonsense"));
        }

        // ── group heads / bands ───────────────────────────────────────────────────────────────────────────────

        [Fact]
        public async Task Group_heads_are_label_ordered_and_stable_across_calls()
        {
            using var db = fixture.Db();
            var cache = NewCache();
            var first = await Letters(Browse(db, cache: cache));
            var again = await Letters(Browse(db, cache: cache));   // the second call is a cache hit
            var cold = await Letters(Browse(db, cache: NewCache()));   // and a cold recompute agrees with it
            Assert.Equal(first, again);
            Assert.Equal(first, cold);

            var groups = Body<BrowseGroupsResponse>(await Browse(db).GetGroups(groupBy: "series", groupsTop: 10, perGroupTop: 5));
            Assert.Equal(4, groups.TotalGroups);
            Assert.Equal(new[] { "2000 AD", "Batman", "Doppelganger", "Fantastic Four" },
                groups.Groups.Select(g => g.Label).ToArray());
            var twoThousandAd = groups.Groups.Single(g => g.Key == "1");
            Assert.Equal(2, twoThousandAd.TotalItems);
            Assert.Equal(new[] { 1, 2 }, twoThousandAd.Items.Select(i => i.Id).OrderBy(x => x).ToArray());
            // the series card carries its CURRENT insight, not the whole append-only history
            Assert.Equal("Opus take on the weekly.", twoThousandAd.GroupDetail!.AiSynopsis);
        }

        private static async Task<string> Letters(BrowseController controller)
        {
            var value = Assert.IsType<OkObjectResult>(await controller.GetGroupLetters(groupBy: "series")).Value!;
            return System.Text.Json.JsonSerializer.Serialize(value);
        }

        /// <summary>The wire shape: the host's JSON is camelCase (web defaults), the bare serializer's is not.</summary>
        private static string WebJson(object value) =>
            System.Text.Json.JsonSerializer.Serialize(value, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));

        /// <summary>
        /// R9 S0: the flat strip. Buckets are contiguous runs in the flat catalog's own order (offsets are the
        /// cumulative counts), the row AT each offset really starts with that letter under the same LINQ ordering the
        /// OData pages use, the total is the filtered set, and a filter narrows the buckets with it.
        /// </summary>
        [Theory]
        [InlineData("series")]
        [InlineData("title")]
        [InlineData("publisher")]
        public async Task Flat_letters_bucket_the_ordered_catalog(string sort)
        {
            using var db = fixture.Db();
            var cache = NewCache();
            var doc = System.Text.Json.JsonDocument.Parse(WebJson(
                Assert.IsType<OkObjectResult>(await Browse(db, cache: cache).GetLetters(sort: sort)).Value!));
            var total = doc.RootElement.GetProperty("total").GetInt32();
            var letters = doc.RootElement.GetProperty("letters").EnumerateArray()
                .Select(l => (Letter: l.GetProperty("letter").GetString()!, Count: l.GetProperty("count").GetInt32(), Offset: l.GetProperty("offset").GetInt32()))
                .ToList();
            Assert.NotEmpty(letters);
            Assert.Equal(letters.Sum(l => l.Count), total);
            var running = 0;
            foreach (var l in letters) { Assert.Equal(running, l.Offset); running += l.Count; }

            var all = Catalog(db).Get();
            var keys = (sort switch
            {
                "title" => all.OrderBy(s => s.Title).ThenBy(s => s.Id).Select(s => s.Title),
                "publisher" => all.OrderBy(s => s.Publisher).ThenBy(s => s.Year).ThenBy(s => s.Id).Select(s => s.Publisher),
                _ => all.OrderBy(s => s.Series).ThenBy(s => s.Year).ThenBy(s => s.Id).Select(s => s.Series),
            }).ToList();
            Assert.Equal(keys.Count, total);
            foreach (var l in letters)
            {
                var k = keys[l.Offset] ?? "";
                var ch = k.Length > 0 ? char.ToUpperInvariant(k[0]) : '#';
                Assert.Equal(l.Letter, ch is >= 'A' and <= 'Z' ? ch.ToString() : "#");
            }

            // a second call is a cache hit that agrees; a filter narrows the set and the buckets with it
            var again = WebJson(Assert.IsType<OkObjectResult>(await Browse(db, cache: cache).GetLetters(sort: sort)).Value!);
            Assert.Equal(doc.RootElement.GetRawText(), System.Text.Json.JsonDocument.Parse(again).RootElement.GetRawText());
            var filtered = System.Text.Json.JsonDocument.Parse(WebJson(
                Assert.IsType<OkObjectResult>(await Browse(db).GetLetters(sort: sort, filter: "year eq 1987")).Value!));
            Assert.True(filtered.RootElement.GetProperty("total").GetInt32() < total);
        }

        [Fact]
        public async Task Every_grouping_pages_by_group_and_bands_its_items()
        {
            using var db = fixture.Db();
            foreach (var groupBy in new[] { "series", "publisher", "decade", "collection", "franchise" })
            {
                var response = Body<BrowseGroupsResponse>(
                    await Browse(db).GetGroups(groupBy: groupBy, groupsTop: 200, perGroupTop: 48));
                Assert.True(response.TotalGroups > 0, groupBy);
                Assert.Equal(response.TotalGroups, response.Groups.Count);
                foreach (var group in response.Groups)
                {
                    Assert.Equal(group.TotalItems, group.Items.Count);        // the fixture fits inside one page
                    Assert.All(group.Items, i => Assert.False(i.IsExcluded)); // shadows never enter a band
                }
            }

            // decades are chronological, everything else is label-ordered
            var decades = Body<BrowseGroupsResponse>(await Browse(db).GetGroups(groupBy: "decade", groupsTop: 200));
            Assert.Equal(new[] { "1970s", "1980s", "2020s" }, decades.Groups.Select(g => g.Label).ToArray());
        }

        /// <summary>
        /// R9 S8 — the writer / artist shelves. They are the FIRST many-per-item axes the host has: one issue stands
        /// under every writer AND every artist it credits, so the band bucketing had to grow from one key per row to
        /// a list. What is asserted is the contract the site's Group pill depends on: the axis survives normalization,
        /// a shelf's count IS its facet chip's count (rule 2 of the group-axes table), the band really contains that
        /// many items, one item can appear under two people, and the A–Z rail can index them.
        /// </summary>
        [Fact]
        public async Task Writer_and_artist_are_group_axes_and_one_item_can_stand_under_several()
        {
            using var db = fixture.Db();

            // The axis survives normalization — asserted through the wire, because that is the failure mode the
            // site guards against: a host that does NOT know an axis does not 400, it silently answers with
            // COLLECTIONS ("a stale host fails silently"). So "penciller" must look like the collection shelf and
            // "author" must NOT.
            var collections = Body<BrowseGroupsResponse>(await Browse(db).GetGroups(groupBy: "collection", groupsTop: 200));
            var nonsense = Body<BrowseGroupsResponse>(await Browse(db).GetGroups(groupBy: "penciller", groupsTop: 200));
            Assert.Equal(collections.Groups.Select(g => g.Label), nonsense.Groups.Select(g => g.Label));
            var asAuthor = Body<BrowseGroupsResponse>(await Browse(db).GetGroups(groupBy: "author", groupsTop: 200));
            Assert.NotEqual(collections.Groups.Select(g => g.Label), asAuthor.Groups.Select(g => g.Label));

            var facets = Body<BrowseFacetsResult>(await Browse(db).GetFacets());
            foreach (var (by, chips) in new[] { ("author", facets.Authors), ("artist", facets.Artists) })
            {
                var response = Body<BrowseGroupsResponse>(
                    await Browse(db).GetGroups(groupBy: by, groupsTop: 200, perGroupTop: 48));
                Assert.True(response.TotalGroups > 0, by);
                Assert.Equal(response.TotalGroups, response.Groups.Count);

                // label-ordered, and the label is the name a person reads (the KEY is the normalized one)
                Assert.Equal(response.Groups.Select(g => g.Label).OrderBy(x => x, StringComparer.OrdinalIgnoreCase),
                    response.Groups.Select(g => g.Label));

                foreach (var group in response.Groups)
                {
                    Assert.Equal(group.TotalItems, group.Items.Count);        // the fixture fits inside one page
                    Assert.All(group.Items, i => Assert.False(i.IsExcluded)); // shadows never enter a band
                    // …and the shelf agrees with the facet chip of the same name, exactly
                    var chip = chips.SingleOrDefault(c => c.Value == group.Label);
                    if (chip != null) Assert.Equal(chip.Count, group.TotalItems);
                }
            }

            var authors = Body<BrowseGroupsResponse>(await Browse(db).GetGroups(groupBy: "author", groupsTop: 200));
            var miller = authors.Groups.Single(g => g.Label == "Frank Miller");
            Assert.Equal(2, miller.TotalItems);

            // MANY-PER-ITEM: an item credited to two writers is in BOTH bands, which is the whole reason KeyOf
            // became KeysOf — the single-key bucketing would have filed it under one of them and lost the other.
            var shared = authors.Groups.SelectMany(g => g.Items.Select(i => i.Id))
                .GroupBy(id => id).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
            var creditsPerItem = await db.ItemCredits.AsNoTracking()
                .Where(c => c.Role != null && CreditRoles.Authors.Contains(c.Role) && c.NormalizedName != null)
                .Select(c => new { c.ItemId, c.NormalizedName }).Distinct().ToListAsync();
            var expectedShared = creditsPerItem.GroupBy(c => c.ItemId).Where(g => g.Count() > 1).Select(g => g.Key)
                .Where(id => authors.Groups.Any(g => g.Items.Any(i => i.Id == id))).ToList();
            Assert.Equal(expectedShared.OrderBy(x => x), shared.OrderBy(x => x));

            // the A–Z rail indexes them like every other label-ordered axis
            var letters = Assert.IsType<OkObjectResult>(await Browse(db).GetGroupLetters(groupBy: "artist")).Value!;
            Assert.Contains("\"letter\"", WebJson(letters));

            // and one shelf can be continued on its own (the "more of this group" call)
            var more = Assert.IsType<OkObjectResult>(
                await Browse(db).GetGroupItems("author", miller.Key, skip: 0, top: 1)).Value!;
            Assert.Equal(2, (int)more.GetType().GetProperty("total")!.GetValue(more)!);
        }

        /// <summary>
        /// The host ADVERTISES its group axes (R9 closing pass). It has to: a host that does not know an axis does
        /// not 400 on it, it silently answers with COLLECTIONS, so the site could never tell "understood" from
        /// "ignored" — and Writer/Artist shipped behind a hand-flipped SPA constant waiting on a deploy because of
        /// exactly that. The advertisement is the durable fix, so what is pinned here is that it cannot LIE: every
        /// axis on the list is one <c>NormalizeGroupBy</c> keeps, and every axis it keeps is on the list.
        /// </summary>
        [Fact]
        public async Task The_facets_advertise_exactly_the_group_axes_this_host_can_answer()
        {
            using var db = fixture.Db();
            var facets = Body<BrowseFacetsResult>(await Browse(db).GetFacets());

            Assert.Equal(
                new[] { "collection", "series", "publisher", "decade", "franchise", "author", "artist" },
                facets.GroupAxes.ToArray());

            // Advertised ⇒ ANSWERED, asserted through the wire because that is the failure mode: an axis this host
            // does not know comes back as the COLLECTION shelves, silently. So every advertised axis but that one
            // must produce a different set of labels, and an axis nobody advertises ("penciller") must produce the
            // same one. The regex is BUILT from the list, so this is the guard against the two drifting apart.
            var collections = Body<BrowseGroupsResponse>(await Browse(db).GetGroups(groupBy: "collection", groupsTop: 200));
            var collectionLabels = collections.Groups.Select(g => g.Label).ToArray();
            foreach (var axis in facets.GroupAxes.Where(a => a != "collection"))
            {
                var answered = Body<BrowseGroupsResponse>(await Browse(db).GetGroups(groupBy: axis, groupsTop: 200));
                Assert.NotEqual(collectionLabels, answered.Groups.Select(g => g.Label).ToArray());
            }
            var unknown = Body<BrowseGroupsResponse>(await Browse(db).GetGroups(groupBy: "penciller", groupsTop: 200));
            Assert.Equal(collectionLabels, unknown.Groups.Select(g => g.Label).ToArray());
        }

        [Fact]
        public async Task A_band_can_be_paged_and_continued_within_one_group()
        {
            using var db = fixture.Db();
            var page = Body<BrowseGroupsResponse>(
                await Browse(db).GetGroups(groupBy: "series", groupsTop: 1, groupsSkip: 1, perGroupTop: 1, orderby: "oldest"));
            Assert.Equal(4, page.TotalGroups);
            var batman = Assert.Single(page.Groups);
            Assert.Equal("Batman", batman.Label);
            Assert.Equal(2, batman.TotalItems);
            Assert.Equal(4, Assert.Single(batman.Items).Id);

            var more = Assert.IsType<OkObjectResult>(
                await Browse(db).GetGroupItems("series", batman.Key, skip: 1, top: 10, orderby: "oldest")).Value!;
            var items = (List<ItemSummary>)more.GetType().GetProperty("items")!.GetValue(more)!;
            Assert.Equal(2, (int)more.GetType().GetProperty("total")!.GetValue(more)!);
            Assert.Equal(5, Assert.Single(items).Id);
        }

        [Fact]
        public async Task Search_and_an_odata_filter_narrow_the_groups()
        {
            using var db = fixture.Db();
            var searched = Body<BrowseGroupsResponse>(await Browse(db).GetGroups(groupBy: "series", q: "Dredd"));
            Assert.Equal(1, searched.TotalGroups);
            var run = Assert.Single(searched.Groups);
            Assert.Equal("2000 AD", run.Label);
            Assert.Equal(new[] { 1, 2 }, run.Items.Select(i => i.Id).OrderBy(x => x).ToArray());

            var filtered = Body<BrowseGroupsResponse>(
                await Browse(db).GetGroups(groupBy: "series", filter: "year eq 1987"));
            Assert.Equal(1, filtered.TotalGroups);
            Assert.Equal("Batman", Assert.Single(filtered.Groups).Label);
        }

        [Fact]
        public async Task A_series_library_rating_answers_with_nulls_when_unrated()
        {
            using var db = fixture.Db();
            var rated = Assert.IsType<OkObjectResult>(await Browse(db).GetSeriesLibraryRating(2)).Value!;
            Assert.Equal(95, (int?)rated.GetType().GetProperty("rating")!.GetValue(rated));
            Assert.Equal("hand-set", (string?)rated.GetType().GetProperty("note")!.GetValue(rated));

            var unrated = Assert.IsType<OkObjectResult>(await Browse(db).GetSeriesLibraryRating(4)).Value!;
            Assert.Null(unrated.GetType().GetProperty("rating")!.GetValue(unrated));
            Assert.Null(unrated.GetType().GetProperty("note")!.GetValue(unrated));
        }

        // ── the exclusion + maturity helpers themselves ───────────────────────────────────────────────────────

        [Fact]
        public void The_exclusion_filters_differ_only_on_the_shadow_duplicate()
        {
            using var db = fixture.Db();
            var all = db.Items.AsNoTracking().Where(i => i.Kind == ItemKind.Comic);
            Assert.Equal(7, all.Count());
            Assert.Equal(6, all.ExcludeHidden().Count());
            Assert.Equal(7, all.ExcludeHidden(includeExcluded: true).Count());
            // the directory variant keeps only the SHADOW duplicates — an exclusion without KeepInDirectory is
            // hidden there too, which is the whole point of the flag
            Assert.Equal(6, all.ExcludeHiddenForDirectory().Count());
            using var tx = db.Database.BeginTransaction();
            try
            {
                db.Items.Single(i => i.Id == 3).KeepInDirectory = true;
                db.SaveChanges();
                Assert.Equal(6, all.ExcludeHidden().Count());
                Assert.Equal(7, all.ExcludeHiddenForDirectory().Count());
            }
            finally { tx.Rollback(); }
        }

        [Fact]
        public void The_maturity_rules_are_the_ported_min_wins_ones()
        {
            Assert.Equal(new[] { "all-ages" }, MaturityFilter.AllowedAtOrBelow(0));
            Assert.Equal(new[] { "all-ages", "teen" }, MaturityFilter.AllowedAtOrBelow(1));
            Assert.Equal(new[] { "mature", "mature-readers", "adult" }, MaturityFilter.HardBlockedAbove(0));
            Assert.Equal(new[] { "adult" }, MaturityFilter.HardBlockedAbove(1));
            Assert.Empty(MaturityFilter.HardBlockedAbove(2));
            Assert.Empty(MaturityFilter.AllowedAtOrBelow(3));
        }

        // ── the per-user mark filters (slice 4 wired these into the browse) ───────────────────────────────────

        [Fact]
        public async Task WantToReadOnly_restricts_the_browse_to_the_callers_own_queue()
        {
            using var db = fixture.Db();
            // The fixture's owner wants items 2, 4 and 5 and has finished item 1; they have marked series 1 and
            // series 2 READ at the series level.
            var wanted = Body<BrowseGroupsResponse>(
                await Browse(db).GetGroups(groupBy: "series", groupsTop: 200, wantToReadOnly: true));
            Assert.Equal(new[] { 2, 4, 5 },
                wanted.Groups.SelectMany(g => g.Items).Select(i => i.Id).OrderBy(x => x).ToArray());
            // The heads shrink with the items: a series with nothing wanted in it is not a group at all.
            Assert.Equal(2, wanted.TotalGroups);
            Assert.Equal(new[] { "1", "2" }, wanted.Groups.Select(g => g.Key).OrderBy(k => k).ToArray());

            // readOnly takes the finished item AND the issues of the series marked read — the two ways v2 says
            // the same thing.
            var read = Body<BrowseGroupsResponse>(
                await Browse(db).GetGroups(groupBy: "series", groupsTop: 200, readOnly: true));
            Assert.Equal(new[] { 1, 2, 4, 5 },
                read.Groups.SelectMany(g => g.Items).Select(i => i.Id).OrderBy(x => x).ToArray());

            // Both flags AND: only what is wanted and also read.
            var both = Body<BrowseGroupsResponse>(
                await Browse(db).GetGroups(groupBy: "series", groupsTop: 200, wantToReadOnly: true, readOnly: true));
            Assert.Equal(new[] { 2, 4, 5 },
                both.Groups.SelectMany(g => g.Items).Select(i => i.Id).OrderBy(x => x).ToArray());

            // The filter reaches the letter rail and the band continuation too, so no surface disagrees.
            var letters = Assert.IsType<OkObjectResult>(
                await Browse(db).GetGroupLetters(groupBy: "collection", wantToReadOnly: true)).Value!;
            Assert.Equal(1, (int)letters.GetType().GetProperty("totalGroups")!.GetValue(letters)!);
            var band = Assert.IsType<OkObjectResult>(
                await Browse(db).GetGroupItems("series", "1", wantToReadOnly: true)).Value!;
            Assert.Equal(1, (int)band.GetType().GetProperty("total")!.GetValue(band)!);
        }

        [Fact]
        public async Task A_mark_filtered_signature_is_never_cached()
        {
            using var db = fixture.Db();
            var cache = NewCache();

            // An unfiltered browse caches its heads; a mark-filtered one must not, because the answer changes on
            // every click and a stale shelf is worse than a recomputed one.
            await Browse(db, cache: cache).GetGroups(groupBy: "series", groupsTop: 200);
            var cachedEntries = ((MemoryCache)cache).Count;
            Assert.True(cachedEntries > 0);

            await Browse(db, cache: cache).GetGroups(groupBy: "series", groupsTop: 200, wantToReadOnly: true);
            await Browse(db, cache: cache).GetGroupLetters(groupBy: "series", readOnly: true);
            Assert.Equal(cachedEntries, ((MemoryCache)cache).Count);

            // And it really is per-caller: another account's browse is not the owner's queue.
            var stranger = Body<BrowseGroupsResponse>(await Browse(db, BooksIdentity.Principal(42, "someone", false, 3))
                .GetGroups(groupBy: "series", groupsTop: 200, wantToReadOnly: true));
            Assert.Empty(stranger.Groups);
        }

        [Fact]
        public async Task Group_heads_carry_the_callers_own_marks()
        {
            using var db = fixture.Db();
            var groups = Body<BrowseGroupsResponse>(await Browse(db).GetGroups(groupBy: "series", groupsTop: 200)).Groups;

            // The owner marked series 1 read + favourite with a rating of 80, and series 2 read.
            var dredd = groups.Single(g => g.Key == "1");
            Assert.NotNull(dredd.UserMeta);
            Assert.True(dredd.UserMeta!.IsRead);
            Assert.True(dredd.UserMeta.IsFavorite);
            Assert.Equal(80, dredd.UserMeta.Rating);

            Assert.True(groups.Single(g => g.Key == "2").UserMeta!.IsRead);
            // An unmarked group carries null, not a row of falses.
            Assert.Null(groups.Single(g => g.Key == "3").UserMeta);

            // Another account sees its own marks, which is none of these.
            var stranger = Body<BrowseGroupsResponse>(
                await Browse(db, BooksIdentity.Principal(42, "someone", false, 3)).GetGroups(groupBy: "series", groupsTop: 200));
            Assert.All(stranger.Groups, g => Assert.Null(g.UserMeta));
        }
    }
}
