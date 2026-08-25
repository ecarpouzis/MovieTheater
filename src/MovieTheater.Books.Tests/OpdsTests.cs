using System.Security.Claims;
using System.Xml.Linq;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using MovieTheater.Books;
using MovieTheater.Books.Controllers;
using MovieTheater.Books.Db;
using MovieTheater.Books.Identity;
using MovieTheater.Books.Media;
using MovieTheater.Books.Opds;

namespace MovieTheater.Books.Tests
{
    /// <summary>
    /// Slice 6's contract: the OPDS catalog, against the real migrated SQLite file.
    ///
    /// <para>The 14 standalone <c>OpdsFeedServiceTests</c> are ported here, re-pointed at v2's gate. Two of them
    /// changed shape on purpose: the per-folder ACL those tests exercised does not exist in v2 (it was a
    /// test-account artifact, dropped by the model), so "hides what the user is not authorized for" is now about
    /// the SHELVES a caller is offered and about the maturity ceiling, which is the gate that actually protects
    /// anything; and an unknown category is a null (⇒ 404) rather than a thrown exception.</para>
    ///
    /// <para>Every test executes against real SQLite, never an in-memory provider, so a projection that cannot be
    /// translated fails HERE rather than as a 500 on the one document every e-reader fetches first.</para>
    /// </summary>
    public class OpdsTests : IClassFixture<MigratedFixture>
    {
        private readonly MigratedFixture fixture;
        public OpdsTests(MigratedFixture fixture) => this.fixture = fixture;

        private const string MediaSecret = "test-media-secret";
        private const string MediaBase = "http://localhost:2204";

        /// <summary>The SITE origin — a reserved example name, never a real host.</summary>
        private const string SiteBase = "https://books.example";

        private static readonly XNamespace Atom = "http://www.w3.org/2005/Atom";
        private static readonly XNamespace Pse = "http://vaemendis.net/opds-pse/ns";
        private static readonly XNamespace OpenSearch = "http://a9.com/-/spec/opensearch/1.1/";

        // ── harness ───────────────────────────────────────────────────────────────────────────────────────

        private static ClaimsPrincipal Owner(int ceiling = 3, bool isAdmin = false) =>
            BooksIdentity.Principal(1, "owner", isAdmin, ceiling);

        /// <summary>A caller the header authenticated but carried no user id for — no personal shelves exist for it.</summary>
        private static ClaimsPrincipal NoUserId() =>
            new(new ClaimsIdentity([new Claim(BooksIdentity.MaturityClaim, "3")], "test"));

        private static string Token(int userId = 1, int ceiling = 3, bool isAdmin = false) =>
            BooksMediaToken.MintNow(MediaSecret, userId, ceiling, isAdmin, out _);

        private static OpdsContext Ctx(ClaimsPrincipal? user = null, int pageSize = 50, string? token = null, string? mediaBase = MediaBase)
        {
            var principal = user ?? Owner();
            return new OpdsContext(principal, SiteBase, mediaBase,
                token ?? Token(1, BooksIdentity.CeilingFor(principal), BooksIdentity.IsAdmin(principal)), pageSize);
        }

        private static OpdsFeedService Service(BooksDb db) => new(db);

        private BooksOptions Options() => new()
        {
            DbPath = fixture.V1.HotPath,
            CacheDir = fixture.V1.CacheDir,
            PublicBaseUrl = MediaBase,
            MediaTokenSecret = MediaSecret,
            EnableCacheWarmer = false,
        };

        private static IConfiguration Config(string? siteBase = SiteBase, bool enabled = true, int pageSize = 50) =>
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Opds:SiteBaseUrl"] = siteBase,
                ["Opds:Enabled"] = enabled ? "true" : "false",
                ["Opds:PageSize"] = pageSize.ToString(),
            }).Build();

        private OpdsController Controller(BooksDb db, ClaimsPrincipal? user = null, IConfiguration? config = null)
        {
            var http = new DefaultHttpContext { User = user ?? Owner() };
            http.Request.Scheme = "http";
            http.Request.Host = new HostString("localhost", 2204);
            return new OpdsController(db, Options(), config ?? Config())
            {
                ControllerContext = new ControllerContext { HttpContext = http },
            };
        }

        private static XElement Feed(string xml) => XDocument.Parse(xml).Root!;
        private static List<XElement> Entries(string xml) => Feed(xml).Elements(Atom + "entry").ToList();
        private static List<string> Titles(string xml) => Entries(xml).Select(e => e.Element(Atom + "title")!.Value).ToList();

        /// <summary>The item ids an acquisition feed carries, read out of the stable entry urns.</summary>
        private static List<int> ItemIds(string xml) => Entries(xml)
            .Select(e => e.Element(Atom + "id")!.Value)
            .Where(id => id.StartsWith("urn:mt-books:item:"))
            .Select(id => int.Parse(id["urn:mt-books:item:".Length..]))
            .ToList();

        private static List<XElement> Links(XElement element) => element.Elements(Atom + "link").ToList();
        private static XElement? Link(XElement element, string rel) => Links(element).FirstOrDefault(l => (string?)l.Attribute("rel") == rel);

        private static string Body(IActionResult result)
        {
            var content = Assert.IsType<ContentResult>(result);
            Assert.NotNull(content.Content);
            return content.Content!;
        }

        // ── the root feed ─────────────────────────────────────────────────────────────────────────────────

        [Fact]
        public async Task Root_feed_executes_and_offers_the_shelves_that_lead_somewhere()
        {
            // The regression guard the standalone site paid for in production: this document is the first thing
            // every OPDS client fetches, and it was a hard 500 because a string was shaped inside the LINQ tree.
            using var db = fixture.Db();
            var xml = await Service(db).BuildRootAsync(Ctx());

            var titles = Titles(xml);
            Assert.Contains("Recently added", titles);
            Assert.Contains("Comics", titles);
            Assert.Contains("Books", titles);
            Assert.Contains("Series", titles);
            Assert.Contains("Publishers", titles);
            Assert.Contains("Want to read", titles);
            Assert.Contains("In progress", titles);

            // The Kids shelf is offered only when there is something on it. Nothing in the fixture is tagged
            // all-ages, so a shelf that would open empty is not advertised at all.
            Assert.DoesNotContain("Kids", titles);

            // The publisher DRILL is reached from the Publishers feed, never from the root.
            Assert.DoesNotContain("Publisher", titles);

            // Navigation feeds are declared as navigation; acquisition shelves as acquisition.
            var series = Entries(xml).Single(e => e.Element(Atom + "title")!.Value == "Series");
            Assert.Equal(OpdsXml.NavigationType, (string?)Link(series, "subsection")!.Attribute("type"));
            var recent = Entries(xml).Single(e => e.Element(Atom + "title")!.Value == "Recently added");
            Assert.Equal(OpdsXml.AcquisitionType, (string?)Link(recent, "subsection")!.Attribute("type"));
        }

        [Fact]
        public async Task Root_feed_declares_utf8_never_utf16()
        {
            // The document is serialized as UTF-8; a "utf-16" prolog on UTF-8 bytes entitles a conforming parser
            // to reject the feed outright, and some readers do exactly that.
            using var db = fixture.Db();
            var xml = await Service(db).BuildRootAsync(Ctx());

            Assert.Contains("encoding=\"utf-8\"", xml, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("utf-16", xml, StringComparison.OrdinalIgnoreCase);
            Assert.False(xml.StartsWith('﻿'));   // no BOM in front of the declaration
        }

        [Fact]
        public async Task Root_feed_hides_the_personal_shelves_from_a_caller_with_no_user_id()
        {
            using var db = fixture.Db();
            var titles = Titles(await Service(db).BuildRootAsync(Ctx(NoUserId())));

            Assert.Contains("Comics", titles);
            Assert.DoesNotContain("Want to read", titles);
            Assert.DoesNotContain("In progress", titles);
        }

        [Fact]
        public async Task Root_feed_for_an_admin_offers_both_kind_shelves()
        {
            using var db = fixture.Db();
            var titles = Titles(await Service(db).BuildRootAsync(Ctx(Owner(ceiling: 0, isAdmin: true))));

            // An admin is unrestricted regardless of the ceiling claim — the same rule as everywhere else.
            Assert.Contains("Comics", titles);
            Assert.Contains("Books", titles);
        }

        [Fact]
        public async Task Root_feed_carries_a_search_link_to_the_opensearch_description()
        {
            using var db = fixture.Db();
            var search = Link(Feed(await Service(db).BuildRootAsync(Ctx())), "search");

            Assert.NotNull(search);
            Assert.Equal(OpdsXml.OpenSearchLinkType, (string?)search!.Attribute("type"));
            Assert.Equal($"{SiteBase}/opds/opensearch.xml", (string?)search.Attribute("href"));
        }

        // ── category feeds ────────────────────────────────────────────────────────────────────────────────

        [Fact]
        public async Task An_unknown_category_has_no_feed_and_the_route_answers_404()
        {
            using var db = fixture.Db();
            Assert.Null(await Service(db).BuildCategoryAsync("not-a-category", Ctx()));
            Assert.IsType<NotFoundResult>(await Controller(db).Category("not-a-category"));

            // A personal shelf without an identity is a 404 too — there is no such shelf, not a forbidden one.
            Assert.Null(await Service(db).BuildCategoryAsync(OpdsCategories.WantToRead, Ctx(NoUserId())));
            // …and so is the publisher drill without a key.
            Assert.Null(await Service(db).BuildCategoryAsync(OpdsCategories.Publisher, Ctx()));
        }

        [Fact]
        public async Task An_entry_links_its_bytes_to_the_media_plane_and_its_feeds_to_the_site()
        {
            using var db = fixture.Db();
            var token = Token();
            var xml = await Service(db).BuildCategoryAsync(OpdsCategories.Comics, Ctx(token: token));

            // FEED links go through the site: it is where an e-reader's Basic credentials are verified.
            Assert.Equal($"{SiteBase}/opds/comics?page=1", (string?)Link(Feed(xml!), "self")!.Attribute("href"));
            Assert.Equal($"{SiteBase}/opds", (string?)Link(Feed(xml!), "start")!.Attribute("href"));

            var entry = Entries(xml!).Single(e => e.Element(Atom + "id")!.Value == "urn:mt-books:item:1");

            // BYTE links go straight to this host's media plane, with a capability token in the path.
            var download = Link(entry, OpdsXml.AcquisitionRel)!;
            Assert.Equal($"{MediaBase}/m/{token}/download/1", (string?)download.Attribute("href"));
            Assert.Equal("application/vnd.comicbook+zip", (string?)download.Attribute("type"));

            var thumb = Link(entry, OpdsXml.ThumbnailRel)!;
            Assert.Equal($"{MediaBase}/m/{token}/thumbs/1.webp", (string?)thumb.Attribute("href"));
            Assert.Equal("image/webp", (string?)thumb.Attribute("type"));
            Assert.NotNull(Link(entry, OpdsXml.ImageRel));

            // Every href in the document is absolute: an OPDS client resolves relative links against the base it
            // fetched from, which after a proxy hop is not the base we mean.
            Assert.All(Feed(xml!).Descendants(Atom + "link"),
                l => Assert.StartsWith("http", (string?)l.Attribute("href")!));
        }

        [Fact]
        public async Task A_feeds_media_token_carries_the_callers_own_ceiling()
        {
            using var db = fixture.Db();
            var xml = await Service(db).BuildCategoryAsync(OpdsCategories.Comics, Ctx(Owner(ceiling: 1), token: Token(1, 1)));
            var href = (string)Link(Entries(xml!)[0], OpdsXml.ThumbnailRel)!.Attribute("href")!;

            var token = href[$"{MediaBase}/m/".Length..].Split('/')[0];
            Assert.True(BooksMediaToken.TryValidate(MediaSecret, token, out var payload));
            Assert.Equal(1, payload!.MaturityCeiling);   // a token can never widen what its holder may fetch
            Assert.Equal(1, payload.UserId);
        }

        [Fact]
        public async Task A_feed_carries_no_byte_links_when_the_host_has_no_media_configured()
        {
            using var db = fixture.Db();
            var xml = await Service(db).BuildCategoryAsync(OpdsCategories.Comics, Ctx(mediaBase: null, token: null));

            var entry = Entries(xml!)[0];
            Assert.Null(Link(entry, OpdsXml.AcquisitionRel));
            Assert.Null(Link(entry, OpdsXml.ThumbnailRel));
            // The catalog still works as a catalog, and page streaming still points at the site.
            Assert.NotNull(Link(entry, OpdsXml.PseStreamRel));
        }

        // ── OPDS-PSE ──────────────────────────────────────────────────────────────────────────────────────

        [Fact]
        public async Task The_pse_link_carries_the_template_the_count_and_the_last_read_page()
        {
            // The standalone site's old link was a fixed "?page=0" URL with no pse:count and no namespace
            // declaration, so every page-streaming client ignored it and fell back to a whole-archive download.
            using var db = fixture.Db();
            var xml = await Service(db).BuildCategoryAsync(OpdsCategories.Comics, Ctx());

            var reading = Entries(xml!).Single(e => e.Element(Atom + "id")!.Value == "urn:mt-books:item:2");
            var stream = Link(reading, OpdsXml.PseStreamRel)!;

            Assert.Equal("32", (string?)stream.Attribute(Pse + "count"));
            Assert.Contains("{pageNumber}", (string?)stream.Attribute("href"));
            Assert.StartsWith($"{SiteBase}/opds/pages/2/", (string?)stream.Attribute("href"));

            // pse:lastRead is 1-BASED; the stored position is a 0-based index. Item 2 stopped on index 12.
            Assert.Equal("13", (string?)stream.Attribute(Pse + "lastRead"));
            Assert.NotNull(stream.Attribute(Pse + "lastReadDate"));

            // A FINISHED book reports the last page — that is what "read to the end" means to a reader app.
            var finished = Entries(xml!).Single(e => e.Element(Atom + "id")!.Value == "urn:mt-books:item:1");
            Assert.Equal("32", (string?)Link(finished, OpdsXml.PseStreamRel)!.Attribute(Pse + "lastRead"));

            // An untouched book reports nothing: lastRead=1 everywhere would make every cover look half-read.
            var untouched = Entries(xml!).Single(e => e.Element(Atom + "id")!.Value == "urn:mt-books:item:6");
            Assert.Null(Link(untouched, OpdsXml.PseStreamRel)!.Attribute(Pse + "lastRead"));
        }

        [Fact]
        public async Task The_pse_link_is_omitted_when_the_page_count_is_unknown()
        {
            // The fixture's books carry no indexed page count. A stream link without a count is ignored by every
            // client, so emitting one would only hide the acquisition link behind a dead feature.
            using var db = fixture.Db();
            var xml = await Service(db).BuildCategoryAsync(OpdsCategories.Books, Ctx());

            Assert.NotEmpty(Entries(xml!));
            Assert.All(Entries(xml!), e => Assert.Null(Link(e, OpdsXml.PseStreamRel)));
        }

        [Fact]
        public async Task The_page_route_converts_pse_1_based_to_the_media_planes_0_based_index()
        {
            using var db = fixture.Db();
            var controller = Controller(db);

            var redirect = Assert.IsType<RedirectResult>(await controller.Page(2, 1));
            Assert.Contains("/pages/2/0", redirect.Url);           // page ONE is page index ZERO
            Assert.StartsWith($"{MediaBase}/m/", redirect.Url);

            Assert.Contains("maxWidth=1080", Assert.IsType<RedirectResult>(await controller.Page(2, 4, "1080")).Url);
            // A client that does not implement the {maxWidth} substitution sends the placeholder literally: that
            // is an unspecific request, not a bad one.
            Assert.DoesNotContain("maxWidth", Assert.IsType<RedirectResult>(await controller.Page(2, 4, "{maxWidth}")).Url);

            Assert.IsType<NotFoundResult>(await controller.Page(2, 0));          // PSE pages start at 1
            Assert.IsType<NotFoundResult>(await controller.Page(3, 1));          // an excluded item
            Assert.IsType<NotFoundResult>(await controller.Page(999_999, 1));    // absent
        }

        // ── the gate ──────────────────────────────────────────────────────────────────────────────────────

        [Fact]
        public async Task An_excluded_item_never_appears_in_a_feed()
        {
            // Item 3 is a shadow duplicate the dedup pass hid. It stays visible in the site's Directory drill;
            // OPDS has no such view, so it is simply absent.
            using var db = fixture.Db();
            var ids = ItemIds((await Service(db).BuildCategoryAsync(OpdsCategories.Comics, Ctx()))!);

            Assert.Equal([1, 2, 4, 5, 6, 7], ids);
            Assert.DoesNotContain(3, ids);
        }

        [Fact]
        public async Task A_restricted_caller_sees_only_what_the_ceiling_allows()
        {
            // The hole this closes was real: the standalone site's OPDS enforced folder authorisation ONLY, so a
            // restricted account could enumerate through a reader app exactly what the web catalog hid from it.
            using var db = fixture.Db();

            // The Batman series carries the AI audience tag "teen": visible at ceiling 1 and above.
            var teen = ItemIds((await Service(db).BuildCategoryAsync(OpdsCategories.Comics, Ctx(Owner(ceiling: 1))))!);
            Assert.Equal([4, 5], teen);

            // Ceiling 0 allows all-ages only, and nothing in the fixture is tagged all-ages.
            Assert.Empty(ItemIds((await Service(db).BuildCategoryAsync(OpdsCategories.Comics, Ctx(Owner(ceiling: 0))))!));
        }

        [Fact]
        public async Task A_restricted_caller_never_sees_an_unclassified_title()
        {
            // Fail-safe: the gate REQUIRES a known classification rather than assuming a missing one is safe.
            // The 2000 AD series' current insight carries no audience tag, so it is hidden below ceiling 3.
            using var db = fixture.Db();

            Assert.DoesNotContain(1, ItemIds((await Service(db).BuildCategoryAsync(OpdsCategories.Comics, Ctx(Owner(ceiling: 2))))!));
            Assert.Contains(1, ItemIds((await Service(db).BuildCategoryAsync(OpdsCategories.Comics, Ctx(Owner(ceiling: 3))))!));
        }

        [Fact]
        public async Task An_unrestricted_caller_sees_everything()
        {
            using var db = fixture.Db();
            Assert.Equal(6, ItemIds((await Service(db).BuildCategoryAsync(OpdsCategories.Comics, Ctx()))!).Count);
            Assert.Equal(2, ItemIds((await Service(db).BuildCategoryAsync(OpdsCategories.Books, Ctx()))!).Count);
        }

        // ── paging ────────────────────────────────────────────────────────────────────────────────────────

        [Fact]
        public async Task A_page_with_more_behind_it_emits_a_next_link_and_no_previous()
        {
            using var db = fixture.Db();
            var xml = (await Service(db).BuildCategoryAsync(OpdsCategories.Comics, Ctx(pageSize: 2), page: 1))!;
            var feed = Feed(xml);

            Assert.Equal(2, Entries(xml).Count);
            Assert.Equal($"{SiteBase}/opds/comics?page=2", (string?)Link(feed, "next")!.Attribute("href"));
            Assert.Null(Link(feed, "previous"));

            // The OpenSearch paging block is what a reader shows as "1–2 of 6".
            Assert.Equal("6", feed.Element(OpenSearch + "totalResults")!.Value);
            Assert.Equal("2", feed.Element(OpenSearch + "itemsPerPage")!.Value);
            Assert.Equal("0", feed.Element(OpenSearch + "startIndex")!.Value);
        }

        [Fact]
        public async Task The_last_page_has_a_previous_link_and_no_next()
        {
            using var db = fixture.Db();
            var xml = (await Service(db).BuildCategoryAsync(OpdsCategories.Comics, Ctx(pageSize: 2), page: 3))!;
            var feed = Feed(xml);

            Assert.Equal([6, 7], ItemIds(xml));
            Assert.Equal($"{SiteBase}/opds/comics?page=2", (string?)Link(feed, "previous")!.Attribute("href"));
            Assert.Null(Link(feed, "next"));
        }

        // ── series, publishers, personal shelves ──────────────────────────────────────────────────────────

        [Fact]
        public async Task The_series_list_offers_every_series_the_caller_can_see_something_from()
        {
            using var db = fixture.Db();
            var xml = (await Service(db).BuildCategoryAsync(OpdsCategories.SeriesList, Ctx()))!;

            Assert.Equal(["2000 AD", "Batman", "Doppelganger", "Fantastic Four"], Titles(xml));
            var batman = Entries(xml).Single(e => e.Element(Atom + "title")!.Value == "Batman");
            Assert.Equal($"{SiteBase}/opds/series/2?page=1", (string?)Link(batman, "subsection")!.Attribute("href"));
            Assert.Contains("2 issues held", batman.Element(Atom + "content")!.Value);

            // A restricted caller's series list narrows with the ceiling — the shelf cannot leak what the gate hides.
            var teen = Titles((await Service(db).BuildCategoryAsync(OpdsCategories.SeriesList, Ctx(Owner(ceiling: 1))))!);
            Assert.Equal(["Batman"], teen);
        }

        [Fact]
        public async Task A_series_feed_lists_its_issues_in_reading_order_and_404s_when_the_gate_refuses()
        {
            using var db = fixture.Db();

            var xml = await Service(db).BuildSeriesFeedAsync(1, Ctx());
            Assert.Equal([1, 2], ItemIds(xml!));                    // ReadIndex 1, 2 — the derived order
            Assert.Contains("2000 AD", Feed(xml!).Element(Atom + "title")!.Value);

            // Not visible at this ceiling, and not existing at all, answer identically: 404, never 403.
            Assert.Null(await Service(db).BuildSeriesFeedAsync(1, Ctx(Owner(ceiling: 1))));
            Assert.Null(await Service(db).BuildSeriesFeedAsync(999_999, Ctx()));
            Assert.IsType<NotFoundResult>(await Controller(db, Owner(ceiling: 1)).Series(1));
            Assert.NotNull(Body(await Controller(db).Series(2)));
        }

        [Fact]
        public async Task The_publisher_list_drills_into_one_publishers_titles()
        {
            using var db = fixture.Db();
            var list = (await Service(db).BuildCategoryAsync(OpdsCategories.PublisherList, Ctx()))!;

            var rebellion = Entries(list).Single(e => e.Element(Atom + "title")!.Value == "Rebellion");
            var href = (string?)Link(rebellion, "subsection")!.Attribute("href")!;
            Assert.Equal($"{SiteBase}/opds/publisher?page=1&key=Rebellion", href);

            var drill = (await Service(db).BuildCategoryAsync(OpdsCategories.Publisher, Ctx(), key: "Rebellion"))!;
            Assert.NotEmpty(ItemIds(drill));
            Assert.All(ItemIds(drill), id => Assert.Contains(id, new[] { 1, 2, 4, 5, 6, 7 }));
        }

        [Fact]
        public async Task The_personal_shelves_carry_the_callers_own_marks_and_positions()
        {
            using var db = fixture.Db();

            var want = ItemIds((await Service(db).BuildCategoryAsync(OpdsCategories.WantToRead, Ctx()))!);
            Assert.Equal([2, 4, 5], want.OrderBy(id => id).ToArray());

            var reading = ItemIds((await Service(db).BuildCategoryAsync(OpdsCategories.InProgress, Ctx()))!);
            Assert.Contains(2, reading);
            Assert.DoesNotContain(1, reading);    // finished is not in progress

            // Another user's marks are not this user's shelf.
            var other = ItemIds((await Service(db).BuildCategoryAsync(
                OpdsCategories.WantToRead, Ctx(BooksIdentity.Principal(2, "other", false, 3))))!);
            Assert.Empty(other);
        }

        // ── search ────────────────────────────────────────────────────────────────────────────────────────

        [Fact]
        public async Task Search_answers_from_the_same_index_the_web_catalog_uses()
        {
            using var db = fixture.Db();

            Assert.Equal([4, 5], ItemIds(await Service(db).BuildSearchAsync("batman", Ctx())));
            // Both 2000 AD issues match: the FTS body carries the SYNOPSIS the resolver's pointer names, not
            // just the title — the same reach the web catalog's q= has.
            Assert.Equal([1, 2], ItemIds(await Service(db).BuildSearchAsync("Dredd", Ctx())));

            // A restricted caller's search is gated like every other surface.
            Assert.Empty(ItemIds(await Service(db).BuildSearchAsync("Dredd", Ctx(Owner(ceiling: 1)))));

            // Whatever the user typed is a query, never an error: an empty or punctuation-only term is an empty
            // feed that still parses.
            Assert.Empty(ItemIds(await Service(db).BuildSearchAsync("", Ctx())));
            Assert.Empty(ItemIds(await Service(db).BuildSearchAsync("!!!", Ctx())));
        }

        [Fact]
        public void The_opensearch_description_advertises_the_search_template()
        {
            var xml = OpdsFeedService.BuildOpenSearchDescription(Ctx());
            var url = XDocument.Parse(xml).Root!.Element(OpenSearch + "Url")!;

            Assert.Equal($"{SiteBase}/opds/search?q={{searchTerms}}", (string?)url.Attribute("template"));
            Assert.Equal(OpdsXml.AcquisitionType, (string?)url.Attribute("type"));
            Assert.Contains("encoding=\"utf-8\"", xml, StringComparison.OrdinalIgnoreCase);
        }

        // ── the controller ────────────────────────────────────────────────────────────────────────────────

        [Fact]
        public async Task Every_document_is_well_formed_and_declares_its_opds_profile()
        {
            using var db = fixture.Db();
            var controller = Controller(db);

            var cases = new (IActionResult Result, string Type)[]
            {
                (await controller.Root(), OpdsXml.NavigationContentType),
                (await controller.Category(OpdsCategories.Recent), OpdsXml.AcquisitionContentType),
                (await controller.Category(OpdsCategories.SeriesList), OpdsXml.NavigationContentType),
                (await controller.Category(OpdsCategories.PublisherList), OpdsXml.NavigationContentType),
                (await controller.Series(1), OpdsXml.AcquisitionContentType),
                (await controller.Search("batman"), OpdsXml.AcquisitionContentType),
                (controller.OpenSearch(), OpdsXml.OpenSearchContentType),
            };

            foreach (var (result, type) in cases)
            {
                var content = Assert.IsType<ContentResult>(result);
                Assert.Equal(type, content.ContentType);
                Assert.NotNull(XDocument.Parse(content.Content!).Root);   // parses, or the test fails here
                Assert.Contains("charset=utf-8", content.ContentType!);
            }
        }

        [Fact]
        public async Task The_controller_reads_its_site_origin_from_configuration_and_falls_back_to_the_request()
        {
            using var db = fixture.Db();

            var configured = Feed(Body(await Controller(db).Root()));
            Assert.Equal($"{SiteBase}/opds", (string?)Link(configured, "self")!.Attribute("href"));

            // Unconfigured, the feed base is the origin the request actually arrived on — never the documented
            // "https://<site>" placeholder, which would hand every reader a dead link.
            var fallback = Feed(Body(await Controller(db, config: Config(siteBase: null)).Root()));
            Assert.Equal("http://localhost:2204/opds", (string?)Link(fallback, "self")!.Attribute("href"));
        }

        [Fact]
        public async Task The_whole_surface_can_be_switched_off()
        {
            using var db = fixture.Db();
            var off = Controller(db, config: Config(enabled: false));

            Assert.IsType<NotFoundResult>(await off.Root());
            Assert.IsType<NotFoundResult>(await off.Category(OpdsCategories.Comics));
            Assert.IsType<NotFoundResult>(await off.Series(1));
            Assert.IsType<NotFoundResult>(await off.Search("batman"));
            Assert.IsType<NotFoundResult>(off.OpenSearch());
            Assert.IsType<NotFoundResult>(await off.Page(1, 1));
        }
    }
}
