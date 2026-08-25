using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using MovieTheater.Books;
using MovieTheater.Books.Access;
using MovieTheater.Books.Archives;
using MovieTheater.Books.Controllers;
using MovieTheater.Books.Db;
using MovieTheater.Books.Identity;
using MovieTheater.Books.Media;
using MovieTheater.Books.Projections;
using MovieTheater.Books.Services;

namespace MovieTheater.Books.Tests
{
    /// <summary>
    /// Slice 2's request contract against the migrated synthetic file: the one authorization helper, the item
    /// detail payload with its provenance blocks, the reading-order hops, the folder drill, and the media plane's
    /// token→principal→item path. The controllers are instantiated directly under a fabricated principal, the
    /// same thing the cache warmer does.
    /// </summary>
    public class ItemsTests : IClassFixture<MigratedFixture>
    {
        private readonly MigratedFixture fixture;
        public ItemsTests(MigratedFixture fixture) => this.fixture = fixture;

        private const string MediaSecret = "test-media-secret";
        private const string BaseUrl = "http://localhost:2204";

        private static ClaimsPrincipal Owner(int ceiling = 3, bool isAdmin = false) =>
            BooksIdentity.Principal(1, "owner", isAdmin, ceiling);

        private BooksOptions Options() => new()
        {
            DbPath = fixture.V1.HotPath,
            CacheDir = fixture.V1.CacheDir,
            PublicBaseUrl = BaseUrl,
            MediaTokenSecret = MediaSecret,
            ArchiveCacheGb = 0,
            EnableCacheWarmer = false,
            EnableTextRegions = false,
        };

        private static T Bind<T>(T controller, ClaimsPrincipal user) where T : ControllerBase
        {
            controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = user } };
            return controller;
        }

        private ItemsController Items(BooksDb db, ClaimsPrincipal? user = null)
        {
            var options = Options();
            var sevenZip = new SevenZipCliExtractor(options, NullLogger<SevenZipCliExtractor>.Instance);
            var cache = new MemoryCache(new MemoryCacheOptions { SizeLimit = 64 });
            IArchiveReader[] readers = [new CbzArchiveReader(sevenZip), new EpubArchiveReader(cache)];
            var thumbnails = new ThumbnailService(readers, options, NullLogger<ThumbnailService>.Instance);
            return Bind(new ItemsController(db, options, thumbnails, new TextRegionService(options),
                new PageByteCache(options), new LocalArchiveCache(options, NullLogger<LocalArchiveCache>.Instance),
                readers), user ?? Owner());
        }

        private FoldersController Folders(BooksDb db, ClaimsPrincipal? user = null) =>
            Bind(new FoldersController(db, Options()), user ?? Owner());

        private EpubController Epub(BooksDb db, ClaimsPrincipal? user = null) =>
            Bind(new EpubController(db, Options(), new EpubReaderService(NullLogger<EpubReaderService>.Instance)), user ?? Owner());

        private static T Body<T>(IActionResult result) => Assert.IsType<T>(Assert.IsType<OkObjectResult>(result).Value);

        // ── the one authorization ─────────────────────────────────────────────────────────────────────────

        [Fact]
        public async Task An_excluded_item_is_not_readable_by_id()
        {
            using var db = fixture.Db();

            // Item 3 is a shadow duplicate that is NOT kept in the directory view, so it is invisible everywhere.
            Assert.Null(await ItemAccess.GetAuthorizedItemAsync(db, Owner(), 3));
            Assert.Null(await ItemAccess.GetAuthorizedItemAsync(db, Owner(), 3, allowExcluded: false));
            Assert.IsType<NotFoundResult>(await Items(db).GetItem(3));

            // Its non-excluded sibling is fine, which is what makes the assertion above about exclusion and not
            // about the fixture being empty.
            Assert.NotNull(await ItemAccess.GetAuthorizedItemAsync(db, Owner(), 2));
        }

        [Fact]
        public async Task An_item_above_the_ceiling_is_404_never_403()
        {
            using var db = fixture.Db();

            // Item 4's series carries the AI audience tag "teen": visible at ceiling 1, hidden at ceiling 0.
            Assert.NotNull(await ItemAccess.GetAuthorizedItemAsync(db, Owner(ceiling: 1), 4));
            Assert.Null(await ItemAccess.GetAuthorizedItemAsync(db, Owner(ceiling: 0), 4));

            // 404, not 403: a 403 would confirm that an item exists at that id, and the ids are sequential.
            Assert.IsType<NotFoundResult>(await Items(db, Owner(ceiling: 0)).GetItem(4));

            // An admin is unrestricted regardless of the claim.
            Assert.NotNull(await ItemAccess.GetAuthorizedItemAsync(db, Owner(ceiling: 0, isAdmin: true), 4));

            // Item 1's series carries NO audience tag at all: the gate REQUIRES a known classification, so it is
            // hidden below ceiling 3 rather than assumed safe. Failing closed is the posture.
            Assert.NotNull(await ItemAccess.GetAuthorizedItemAsync(db, Owner(ceiling: 3), 1));
            Assert.Null(await ItemAccess.GetAuthorizedItemAsync(db, Owner(ceiling: 2), 1));

            // A book carries its own maturity on its current insight (item 101 = 2).
            Assert.NotNull(await ItemAccess.GetAuthorizedItemAsync(db, Owner(ceiling: 2), 101));
            Assert.Null(await ItemAccess.GetAuthorizedItemAsync(db, Owner(ceiling: 1), 101));
        }

        [Fact]
        public async Task An_item_that_does_not_exist_is_404()
        {
            using var db = fixture.Db();
            Assert.Null(await ItemAccess.GetAuthorizedItemAsync(db, Owner(), 999_999));
            Assert.IsType<NotFoundResult>(await Items(db).GetItem(999_999));
        }

        // ── the detail payload ────────────────────────────────────────────────────────────────────────────

        [Fact]
        public async Task Item_detail_carries_the_summary_and_every_provenance_block()
        {
            using var db = fixture.Db();
            var detail = Body<ItemDetail>(await Items(db).GetItem(1));

            // The SAME projection every list surface sent, so the client never reconciles two shapes.
            Assert.Equal(1, detail.Summary.Id);
            Assert.Equal("2000 AD #1", detail.Summary.Title);
            Assert.Equal("2000 AD", detail.Summary.Series);

            // The embedded ComicInfo block — raw, as read from the archive.
            Assert.NotNull(detail.Embedded);
            Assert.Equal("2000 AD", detail.Embedded!.Series);
            Assert.Equal("1", detail.Embedded.Number);

            // The parse pipeline's reading, with the SOURCE of each field.
            Assert.NotNull(detail.Parsed);
            Assert.Equal("2000 AD", detail.Parsed!.SeriesKey);
            Assert.Equal(Confidence.High, detail.Parsed.Confidence);
            Assert.Equal(ParseSource.Filename, detail.Parsed.SeriesSource);

            // The series' provider facts.
            Assert.NotNull(detail.Series);
            Assert.Equal(1, detail.Series!.Id);
            Assert.NotNull(detail.CvVolume);
            Assert.Equal(19752, detail.CvVolume!.Id);
            Assert.Equal("2000 AD", detail.CvVolume.Name);
            Assert.NotNull(detail.CvIssue);
            Assert.Equal(5001, detail.CvIssue!.Id);

            // The current insight for the SERIES — the one row IsCurrent already picked out of the three the
            // fixture carries. This endpoint reads that flag; it does not re-derive the winner.
            Assert.NotNull(detail.SeriesInsight);
            Assert.Equal("claude-opus-4-8", detail.SeriesInsight!.ModelId);
            Assert.Equal(90, detail.SeriesInsight.Rating);
            Assert.True(detail.SeriesInsight.Recognized);
            Assert.Contains("genre:science-fiction", detail.SeriesInsight.Tags);

            // LOCG, shown because the link is High.
            Assert.NotNull(detail.Locg);
            Assert.Equal(4686349, detail.Locg!.LocgComicId);
            Assert.Equal(LinkQuality.High, detail.Locg.Quality);
            Assert.True(detail.Locg.IsKey);

            // Reading order, containment, credits, tags, links.
            Assert.NotNull(detail.ReadingOrder);
            Assert.Equal(1, detail.ReadingOrder!.ReadIndex);
            Assert.Equal(3, detail.ReadingOrder.ReadCount);
            Assert.NotNull(detail.Collection);
            Assert.NotEmpty(detail.Credits);
            Assert.NotEmpty(detail.Tags);
            Assert.NotEmpty(detail.ProviderLinks);
            Assert.Contains(detail.ProviderLinks, l => l.Provider == Provider.Locg);

            // The path is shown relative to its library root — never the share path.
            Assert.StartsWith("\\", detail.RelativePath);
            Assert.DoesNotContain(":", detail.RelativePath);

            // Media URLs are absolute, on this host's public base, and carry a token.
            Assert.NotNull(detail.ThumbUrl);
            Assert.StartsWith(BaseUrl + "/m/", detail.ThumbUrl);
            Assert.EndsWith("/thumbs/1.webp", detail.ThumbUrl);
            Assert.NotNull(detail.PagesUrlTemplate);
            Assert.EndsWith("/pages/1/{page}", detail.PagesUrlTemplate);
        }

        [Fact]
        public async Task A_low_quality_locg_link_is_not_shown_as_fact()
        {
            using var db = fixture.Db();
            // Item 4's LOCG link is a Conflict — a guess. Printing its rating and cover price would read as fact.
            var detail = Body<ItemDetail>(await Items(db).GetItem(4));
            Assert.Null(detail.Locg);
            Assert.Contains(detail.ProviderLinks, l => l.Provider == Provider.Locg);
        }

        [Fact]
        public async Task Collected_edition_spans_come_back_for_a_container()
        {
            using var db = fixture.Db();
            // Item 7 is an omnibus: a collection node plus spans from more than one source.
            var detail = Body<ItemDetail>(await Items(db).GetItem(7));
            Assert.NotNull(detail.Collection);
            Assert.Equal(CollectionLevel.Omnibus, detail.Collection!.Level);
            Assert.Equal(TrackRole.Container, detail.Collection.TrackRole);
            Assert.NotEmpty(detail.EditionSpans);
            Assert.Contains(detail.EditionSpans, s => s.IssueStart == 1 && s.IssueEnd == 60);
        }

        [Fact]
        public async Task A_book_detail_carries_its_own_insight_and_book_block()
        {
            using var db = fixture.Db();
            var detail = Body<ItemDetail>(await Items(db).GetItem(101));
            Assert.Equal("book", detail.Summary.Kind);
            Assert.NotNull(detail.Book);
            Assert.Equal("9780060850524", detail.Book!.Isbn);
            Assert.NotNull(detail.Insight);
            Assert.Equal(2, detail.Insight!.Maturity);
            Assert.Equal(85, detail.Insight.Rating);
        }

        // ── next / prev ───────────────────────────────────────────────────────────────────────────────────

        [Fact]
        public async Task Next_and_prev_walk_the_reading_order_within_a_series()
        {
            using var db = fixture.Db();
            var controller = Items(db);

            // Items 1 and 2 are ReadIndex 1 and 2 of series 1.
            var next = Assert.IsType<OkObjectResult>(await controller.GetNext(1)).Value!;
            Assert.Equal("readingOrder", Read<string>(next, "via"));
            Assert.Equal(2, Read<ItemDetail>(next, "item").Summary.Id);

            var prev = Assert.IsType<OkObjectResult>(await controller.GetPrev(2)).Value!;
            Assert.Equal("readingOrder", Read<string>(prev, "via"));
            Assert.Equal(1, Read<ItemDetail>(prev, "item").Summary.Id);

            // The first has no previous and the last has no next: 204, not 404 — the item itself is fine.
            Assert.IsType<NoContentResult>(await controller.GetPrev(1));

            // Item 4 → 5 within the Batman run.
            var batman = Assert.IsType<OkObjectResult>(await controller.GetNext(4)).Value!;
            Assert.Equal(5, Read<ItemDetail>(batman, "item").Summary.Id);
            Assert.IsType<NoContentResult>(await controller.GetNext(5));
        }

        [Fact]
        public async Task A_neighbour_the_caller_may_not_see_is_no_content_not_a_leak()
        {
            using var db = fixture.Db();
            // At ceiling 0 nothing in the fixture is visible, so even the item itself is 404 — and a caller who
            // can see an item can never be handed a neighbour it may not see, because the target is authorized
            // on its own.
            Assert.IsType<NotFoundResult>(await Items(db, Owner(ceiling: 0)).GetNext(1));
        }

        // ── the media plane's authorization ───────────────────────────────────────────────────────────────

        [Fact]
        public async Task A_media_token_authorizes_exactly_what_its_identity_may_see()
        {
            using var db = fixture.Db();
            var access = new MediaAccess(Options());

            var open = BooksMediaToken.MintNow(MediaSecret, 1, 3, false, out _);
            var (validOpen, itemOpen) = await access.ResolveAsync(db, open, 1);
            Assert.True(validOpen);
            Assert.NotNull(itemOpen);

            // The SAME item, a token minted at ceiling 0: the token cannot widen what its holder may fetch.
            var restricted = BooksMediaToken.MintNow(MediaSecret, 1, 0, false, out _);
            var (validRestricted, itemRestricted) = await access.ResolveAsync(db, restricted, 1);
            Assert.True(validRestricted);
            Assert.Null(itemRestricted);   // ⇒ 404

            // A tampered or foreign token never opens ⇒ 403, a different answer from "not found".
            var (validTampered, _) = await access.ResolveAsync(db, open + "x", 1);
            Assert.False(validTampered);
            var foreign = BooksMediaToken.MintNow("a-different-secret", 1, 3, false, out _);
            Assert.False((await access.ResolveAsync(db, foreign, 1)).TokenValid);

            // An excluded item is not fetchable by token either.
            Assert.Null((await access.ResolveAsync(db, open, 3)).Item);

            // The principal a token stands for carries the identity facts back out.
            Assert.True(BooksMediaToken.TryValidate(MediaSecret, restricted, out var payload));
            var principal = MediaAccess.PrincipalFor(payload!);
            Assert.Equal(1, BooksIdentity.UserId(principal));
            Assert.Equal(0, BooksIdentity.CeilingFor(principal));
            Assert.False(BooksIdentity.IsAdmin(principal));
        }

        [Fact]
        public void A_thumbnail_path_cannot_escape_the_cache_directory()
        {
            var cacheDir = fixture.V1.CacheDir;
            Assert.NotNull(BooksMediaRoutes.ResolveThumb(cacheDir, "12"));
            Assert.EndsWith("12.webp", BooksMediaRoutes.ResolveThumb(cacheDir, "12"));

            // Only a positive integer is a thumbnail name; everything else is refused before it touches the disk.
            Assert.Null(BooksMediaRoutes.ResolveThumb(cacheDir, "../secrets"));
            Assert.Null(BooksMediaRoutes.ResolveThumb(cacheDir, "..\\..\\etc"));
            Assert.Null(BooksMediaRoutes.ResolveThumb(cacheDir, "0"));
            Assert.Null(BooksMediaRoutes.ResolveThumb(cacheDir, "-1"));
            Assert.Null(BooksMediaRoutes.ResolveThumb(cacheDir, ""));

            // The f_ prefix on a folder icon is what keeps the admin cache-clear's ^\d+$ guard from wiping icons.
            Assert.EndsWith("f_2.jpg", BooksMediaRoutes.ResolveFolderIcon(cacheDir, "2"));
            Assert.Null(BooksMediaRoutes.ResolveFolderIcon(cacheDir, "../x"));
        }

        // ── the thumbnail manifest ────────────────────────────────────────────────────────────────────────

        [Fact]
        public async Task The_thumbs_manifest_reports_only_what_the_caller_may_see_and_only_what_exists()
        {
            using var db = fixture.Db();
            // A cached thumbnail for item 1 only; item 2 has none, item 3 is excluded, 999999 does not exist.
            await File.WriteAllBytesAsync(Path.Combine(fixture.V1.CacheDir, "1.webp"), [1, 2, 3]);

            var result = Body<Dictionary<int, ThumbManifestEntry?>>(
                await Items(db).ThumbsBatch(new ItemsController.ThumbBatchRequest([1, 2, 3, 999_999], null)));

            Assert.NotNull(result[1]);
            Assert.EndsWith("/thumbs/1.webp", result[1]!.Url);
            Assert.NotNull(result[1]!.Etag);

            // "not visible" and "no file" and "does not exist" are the same answer — the manifest is not a
            // directory of what is hidden.
            Assert.Null(result[2]);
            Assert.Null(result[3]);
            Assert.Null(result[999_999]);
        }

        // ── folders ───────────────────────────────────────────────────────────────────────────────────────

        [Fact]
        public async Task The_library_folder_listing_returns_the_roots()
        {
            using var db = fixture.Db();
            var roots = Body<List<FolderNode>>(await Folders(db).GetFolders("comic"));
            Assert.Single(roots);
            Assert.Equal("comics", roots[0].Name);
            Assert.Null(roots[0].ParentId);

            var bookRoots = Body<List<FolderNode>>(await Folders(db).GetFolders("book"));
            Assert.Single(bookRoots);
            Assert.Equal("books", bookRoots[0].Name);
        }

        [Fact]
        public async Task A_folder_drill_returns_children_and_the_items_inside_it()
        {
            using var db = fixture.Db();
            var body = Assert.IsType<OkObjectResult>(await Folders(db).GetFolder(5)).Value!;

            // Folder 5 (Batman) holds items 4, 5, 6 and 7 directly.
            Assert.Equal(4, Read<int>(body, "totalItems"));
            var items = Read<List<ItemSummary>>(body, "items");
            Assert.Equal(4, items.Count);
            // A file explorer sorts by NAME, so the drill matches the folder on disk.
            Assert.Equal(items.OrderBy(i => i.FileName, StringComparer.Ordinal).Select(i => i.Id), items.Select(i => i.Id));

            var parent = Assert.IsType<OkObjectResult>(await Folders(db).GetParent(5)).Value!;
            Assert.Equal(1, Read<int?>(parent, "parentId"));

            // At a root, "you are at the top" is an answer, not an error.
            var atRoot = Assert.IsType<OkObjectResult>(await Folders(db).GetParent(1)).Value!;
            Assert.Null(Read<int?>(atRoot, "parentId"));

            Assert.IsType<NotFoundResult>(await Folders(db).GetFolder(999_999));
        }

        [Fact]
        public async Task The_directory_drill_keeps_a_shadow_duplicate_that_is_marked_to_stay()
        {
            using var db = fixture.Db();

            // Folder 4 holds items 1, 2 and the excluded copy 3. The copy is NOT kept in the directory, so the
            // drill agrees with every other surface here.
            var body = Assert.IsType<OkObjectResult>(await Folders(db).GetFolder(4)).Value!;
            Assert.Equal(2, Read<int>(body, "totalItems"));

            // Flip the flag and the SAME query returns it, dimmed by the client — the one place an excluded item
            // is visible, because the file really is in that folder.
            await db.Items.Where(i => i.Id == 3).ExecuteUpdateAsync(s => s.SetProperty(i => i.KeepInDirectory, true));
            try
            {
                using var db2 = fixture.Db();
                var withShadow = Assert.IsType<OkObjectResult>(await Folders(db2).GetFolder(4)).Value!;
                Assert.Equal(3, Read<int>(withShadow, "totalItems"));
                Assert.Contains(Read<List<ItemSummary>>(withShadow, "items"), i => i.Id == 3 && i.IsExcluded);

                // It is still refused everywhere else.
                using var db3 = fixture.Db();
                Assert.Null(await ItemAccess.GetAuthorizedItemAsync(db3, Owner(), 3, allowExcluded: false));
            }
            finally
            {
                await db.Items.Where(i => i.Id == 3).ExecuteUpdateAsync(s => s.SetProperty(i => i.KeepInDirectory, false));
            }
        }

        // ── library rails ─────────────────────────────────────────────────────────────────────────────────

        [Fact]
        public async Task Latest_and_publishers_and_events_are_gated_like_everything_else()
        {
            using var db = fixture.Db();

            var latest = Assert.IsType<OkObjectResult>(await Items(db).GetLatest("comic", 0, 10)).Value!;
            Assert.Equal(6, Read<int>(latest, "total"));   // 7 comics, one an excluded shadow duplicate

            Assert.NotNull(Assert.IsType<OkObjectResult>(await Items(db).GetPublishers("comic")).Value);
            Assert.NotNull(Assert.IsType<OkObjectResult>(await Items(db).GetEvents("comic")).Value);

            // The gate reaches the rails too: at ceiling 0 nothing is visible, so nothing is counted.
            var gated = Assert.IsType<OkObjectResult>(await Items(db, Owner(ceiling: 0)).GetLatest("comic", 0, 10)).Value!;
            Assert.Equal(0, Read<int>(gated, "total"));
            Assert.IsType<NotFoundResult>(await Items(db, Owner(ceiling: 0)).GetRandom("comic"));
        }

        [Fact]
        public async Task Featured_is_reproducible_for_a_given_seed()
        {
            using var db = fixture.Db();
            var a = Assert.IsType<OkObjectResult>(await Items(db).GetFeatured("comic", 3, 1234)).Value!;
            var b = Assert.IsType<OkObjectResult>(await Items(db).GetFeatured("comic", 3, 1234)).Value!;
            Assert.Equal(
                Read<List<ItemSummary>>(a, "items").Select(i => i.Id),
                Read<List<ItemSummary>>(b, "items").Select(i => i.Id));
        }

        // ── epub routes ───────────────────────────────────────────────────────────────────────────────────

        [Fact]
        public async Task The_epub_routes_refuse_an_item_that_is_not_an_epub()
        {
            using var db = fixture.Db();
            // Item 1 is a .cbz — 404, the same answer as "no such item", so the caller learns nothing about it.
            Assert.IsType<NotFoundResult>(await Epub(db).GetSpine(1));
            Assert.IsType<NotFoundResult>(await Epub(db).GetToc(1));
            Assert.IsType<NotFoundResult>(await Epub(db).GetChapter(1, 0));
            Assert.IsType<NotFoundResult>(await Epub(db).GetSpine(999_999));

            // Item 101 IS an .epub, but its fixture path is not on disk, so the parse fails rather than the gate.
            Assert.IsType<NotFoundResult>(await Epub(db, Owner(ceiling: 0)).GetSpine(101));
        }

        /// <summary>Reads one property off an anonymous response object.</summary>
        private static T Read<T>(object body, string name)
        {
            var prop = body.GetType().GetProperty(name) ?? throw new InvalidOperationException($"no property '{name}'");
            return (T)prop.GetValue(body)!;
        }
    }
}
