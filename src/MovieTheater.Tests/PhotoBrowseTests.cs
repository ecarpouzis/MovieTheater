using System;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using MovieTheater.Db;
using Xunit;

namespace MovieTheater.Tests
{
    /// <summary>
    /// The offset browse and the grouped browse obey the timeline's exclusions (shelf, hidden unless an
    /// admin asks, dupe collapse, quarantine) — the year rail, the offset pages and the year groups must
    /// all count the same photographs.
    /// </summary>
    public class PhotoBrowseTests : IDisposable
    {
        private readonly PhotoIngestFixture fixture = new PhotoIngestFixture();
        public void Dispose() => fixture.Dispose();

        private static void Seed(MovieDb db, string path, DateTime? takenAt, bool hidden = false, PhotoShelf shelf = PhotoShelf.Timeline)
        {
            db.PhotoAssets.Add(new PhotoAsset
            {
                Path = path,
                SizeBytes = 4096,
                FileModifiedUtc = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                Kind = PhotoAssetKind.Photo,
                FirstSeenUtc = DateTime.UtcNow,
                Hidden = hidden,
                Shelf = shelf,
                TakenAt = takenAt,
                TakenAtSource = takenAt == null ? TakenAtSource.Unknown : TakenAtSource.Exif,
                ThumbState = PhotoThumbState.Ready,
            });
            db.SaveChanges();
        }

        private static int[] Ids(JsonElement items) => items.EnumerateArray().Select(i => i.GetProperty("id").GetInt32()).ToArray();

        [Fact]
        public async Task The_rail_filter_narrows_the_browse_and_the_groups_and_the_facets_count_the_reachable_scope()
        {
            using var db = fixture.NewDb();
            Seed(db, "Beach/1.jpg", new DateTime(2011, 7, 4, 10, 0, 0));
            Seed(db, "Beach/2.jpg", new DateTime(2014, 3, 12, 10, 0, 0));
            Seed(db, "Home/3.mp4", new DateTime(2014, 5, 1, 10, 0, 0));
            Seed(db, "Home/4.jpg", new DateTime(2019, 8, 1, 10, 0, 0));
            Seed(db, "Art/brom.jpg", new DateTime(2016, 9, 1, 10, 0, 0), shelf: PhotoShelf.Archive);
            var ids = db.PhotoAssets.OrderBy(a => a.Path).ToDictionary(a => a.Path, a => a.Id);
            var video = db.PhotoAssets.Single(a => a.Path == "Home/3.mp4");
            video.Kind = PhotoAssetKind.Video;
            db.PhotoAssets.Single(a => a.Path == "Beach/1.jpg").CameraModel = "Canon EOS";
            db.PhotoAssets.Single(a => a.Path == "Beach/2.jpg").CameraModel = "iPhone 6";
            var album = new PhotoAlbum { Title = "Summer", Slug = "summer", Shelf = PhotoShelf.Timeline, CreatedUtc = DateTime.UtcNow };
            db.PhotoAlbums.Add(album);
            db.SaveChanges();
            db.PhotoAlbumEntries.Add(new PhotoAlbumEntry { PhotoAlbumId = album.Id, PhotoAssetId = ids["Beach/1.jpg"], SortOrder = 0 });
            db.PhotoAlbumEntries.Add(new PhotoAlbumEntry { PhotoAlbumId = album.Id, PhotoAssetId = ids["Beach/2.jpg"], SortOrder = 1 });
            var grandma = new FamilyPerson { Name = "Grandma", CreatedUtc = DateTime.UtcNow };
            db.FamilyPeople.Add(grandma);
            db.SaveChanges();
            db.PhotoPersonTags.Add(new PhotoPersonTag { PhotoAssetId = ids["Beach/2.jpg"], FamilyPersonId = grandma.Id, Source = PhotoTagSource.Manual, CreatedUtc = DateTime.UtcNow });
            db.PhotoPersonTags.Add(new PhotoPersonTag { PhotoAssetId = ids["Home/4.jpg"], FamilyPersonId = grandma.Id, Source = PhotoTagSource.Suggested, CreatedUtc = DateTime.UtcNow });
            db.SaveChanges();

            var c = PhotosControllerHarness.Build(fixture, db);
            var byAlbum = PhotosControllerHarness.Body(await c.Browse(filter: new MovieTheater.Web.PhotoBrowseFilterQuery { album = new[] { "summer" } }));
            Assert.Equal(new[] { ids["Beach/2.jpg"], ids["Beach/1.jpg"] }, Ids(byAlbum.GetProperty("items")));
            // A suggestion is a question: only the affirmed tag counts as "a photo of Grandma".
            var byPerson = PhotosControllerHarness.Body(await c.Browse(filter: new MovieTheater.Web.PhotoBrowseFilterQuery { person = new[] { grandma.Id } }));
            Assert.Equal(new[] { ids["Beach/2.jpg"] }, Ids(byPerson.GetProperty("items")));
            var notPerson = PhotosControllerHarness.Body(await c.Browse(filter: new MovieTheater.Web.PhotoBrowseFilterQuery { exPerson = new[] { grandma.Id } }));
            Assert.Equal(3, notPerson.GetProperty("total").GetInt32());
            var videos = PhotosControllerHarness.Body(await c.Browse(filter: new MovieTheater.Web.PhotoBrowseFilterQuery { kind = "video" }));
            Assert.Equal(new[] { ids["Home/3.mp4"] }, Ids(videos.GetProperty("items")));
            var years = PhotosControllerHarness.Body(await c.Browse(filter: new MovieTheater.Web.PhotoBrowseFilterQuery { yearMin = 2014, yearMax = 2014, kind = "photo" }));
            Assert.Equal(new[] { ids["Beach/2.jpg"] }, Ids(years.GetProperty("items")));
            var camera = PhotosControllerHarness.Body(await c.Browse(filter: new MovieTheater.Web.PhotoBrowseFilterQuery { camera = new[] { "Canon EOS" } }));
            Assert.Equal(new[] { ids["Beach/1.jpg"] }, Ids(camera.GetProperty("items")));
            var text = PhotosControllerHarness.Body(await c.Browse(filter: new MovieTheater.Web.PhotoBrowseFilterQuery { q = "Home" }));
            Assert.Equal(2, text.GetProperty("total").GetInt32());

            // The groups ride the same filter: only 2014 remains, with the one album photograph of it.
            var groups = PhotosControllerHarness.Body(await PhotosControllerHarness.Build(fixture, db).BrowseGroups(groupBy: "year", filter: new MovieTheater.Web.PhotoBrowseFilterQuery { album = new[] { "summer" }, yearMin = 2012 }));
            var heads = groups.GetProperty("groups").EnumerateArray().Select(g => (g.GetProperty("key").GetString(), g.GetProperty("totalItems").GetInt32())).ToArray();
            Assert.Equal(new[] { ("2014", 1) }, heads);
            var albumGroups = PhotosControllerHarness.Body(await PhotosControllerHarness.Build(fixture, db).BrowseGroups(groupBy: "album", filter: new MovieTheater.Web.PhotoBrowseFilterQuery { camera = new[] { "iPhone 6" } }));
            Assert.Equal(1, albumGroups.GetProperty("groups")[0].GetProperty("totalItems").GetInt32());

            // The facets describe the reachable scope (the archive shelf never counts; the suggestion does not).
            var facets = PhotosControllerHarness.Body(await PhotosControllerHarness.Build(fixture, db).Facets());
            Assert.Equal(4, facets.GetProperty("total").GetInt32());
            Assert.Equal(new[] { "2010" }, facets.GetProperty("decades").EnumerateArray().Select(d => d.GetProperty("value").GetString()).ToArray());
            Assert.Equal(2, facets.GetProperty("albums")[0].GetProperty("count").GetInt32());
            Assert.Equal(1, facets.GetProperty("people")[0].GetProperty("count").GetInt32());
            Assert.Equal("Grandma", facets.GetProperty("people")[0].GetProperty("label").GetString());
            Assert.Equal(new[] { 3, 1 }, facets.GetProperty("kinds").EnumerateArray().Select(k => k.GetProperty("count").GetInt32()).ToArray());
            Assert.Equal(2, facets.GetProperty("cameras").GetArrayLength());

            // ── R9 S8: the three axes the rail already filtered on are shelves too ──
            static (string Key, string Label, int Count)[] Heads(JsonElement body) =>
                body.GetProperty("groups").EnumerateArray()
                    .Select(g => (g.GetProperty("key").GetString()!, g.GetProperty("label").GetString()!, g.GetProperty("totalItems").GetInt32())).ToArray();

            // People: AFFIRMED tags only, so Grandma's shelf holds the manual tag and not the suggestion.
            var people = PhotosControllerHarness.Body(await PhotosControllerHarness.Build(fixture, db).BrowseGroups(groupBy: "people"));
            Assert.Equal(new[] { (grandma.Id.ToString(), "Grandma", 1) }, Heads(people));
            Assert.Equal(new[] { ids["Beach/2.jpg"] }, Ids(people.GetProperty("groups")[0].GetProperty("items")));

            // Kind: two shelves, photos first, and the ARCHIVE shelf's row never counts.
            var kinds = PhotosControllerHarness.Body(await PhotosControllerHarness.Build(fixture, db).BrowseGroups(groupBy: "kind"));
            Assert.Equal(new[] { ("photo", "Photos", 3), ("video", "Videos", 1) }, Heads(kinds));
            Assert.Equal(new[] { ids["Home/3.mp4"] }, Ids(kinds.GetProperty("groups")[1].GetProperty("items")));

            // Camera: one shelf per model, biggest first; a photograph with no camera gets no shelf.
            var cameras = PhotosControllerHarness.Body(await PhotosControllerHarness.Build(fixture, db).BrowseGroups(groupBy: "camera"));
            Assert.Equal(new[] { ("Canon EOS", "Canon EOS", 1), ("iPhone 6", "iPhone 6", 1) }, Heads(cameras));
            Assert.Equal(new[] { ids["Beach/1.jpg"] }, Ids(cameras.GetProperty("groups")[0].GetProperty("items")));

            // …and the rail's filter rides all three, exactly as it rides year/month/album/folder.
            var filteredPeople = PhotosControllerHarness.Body(await PhotosControllerHarness.Build(fixture, db)
                .BrowseGroups(groupBy: "people", filter: new MovieTheater.Web.PhotoBrowseFilterQuery { camera = new[] { "Canon EOS" } }));
            Assert.Empty(Heads(filteredPeople));
            var filteredCameras = PhotosControllerHarness.Body(await PhotosControllerHarness.Build(fixture, db)
                .BrowseGroups(groupBy: "camera", filter: new MovieTheater.Web.PhotoBrowseFilterQuery { person = new[] { grandma.Id } }));
            Assert.Equal(new[] { ("iPhone 6", "iPhone 6", 1) }, Heads(filteredCameras));
            var filteredKinds = PhotosControllerHarness.Body(await PhotosControllerHarness.Build(fixture, db)
                .BrowseGroups(groupBy: "kind", filter: new MovieTheater.Web.PhotoBrowseFilterQuery { q = "Home" }));
            Assert.Equal(new[] { ("photo", "Photos", 1), ("video", "Videos", 1) }, Heads(filteredKinds));
        }

        /// <summary>
        /// R9 S8's one "clarify": `month` is YEAR-AND-MONTH ("December 2011", key `2011-12`), NOT a
        /// calendar month gathered across years — so it is KEPT. The across-years reading has its own
        /// endpoint (`/API/Photos/OnThisDay`), which exists precisely because the browse narrows by
        /// month only within a year.
        /// </summary>
        [Fact]
        public async Task Month_groups_are_a_month_OF_A_YEAR_never_a_calendar_month_across_years()
        {
            using var db = fixture.NewDb();
            Seed(db, "A/1.jpg", new DateTime(2011, 12, 25, 9, 0, 0));
            Seed(db, "A/2.jpg", new DateTime(2011, 12, 26, 9, 0, 0));
            Seed(db, "B/3.jpg", new DateTime(2014, 12, 25, 9, 0, 0));
            Seed(db, "B/4.jpg", new DateTime(2014, 3, 12, 10, 0, 0));

            var body = PhotosControllerHarness.Body(await PhotosControllerHarness.Build(fixture, db).BrowseGroups(groupBy: "month"));
            var heads = body.GetProperty("groups").EnumerateArray()
                .Select(g => (g.GetProperty("key").GetString(), g.GetProperty("label").GetString(), g.GetProperty("totalItems").GetInt32())).ToArray();
            // Two Decembers, in two different years — never one "December" of three.
            Assert.Equal(new[]
            {
                ("2014-12", "December 2014", 1),
                ("2014-03", "March 2014", 1),
                ("2011-12", "December 2011", 2),
            }, heads);
        }

        [Fact]
        public async Task Browse_pages_the_dated_timeline_newest_first_with_the_same_exclusions()
        {
            using var db = fixture.NewDb();
            Seed(db, "A/1.jpg", new DateTime(2011, 7, 4, 10, 0, 0));
            Seed(db, "A/2.jpg", new DateTime(2011, 12, 25, 9, 0, 0));
            Seed(db, "B/3.jpg", new DateTime(2014, 3, 12, 10, 0, 0));
            Seed(db, "Screenshots/h.jpg", new DateTime(2015, 8, 1, 10, 0, 0), hidden: true);
            Seed(db, "Art/brom.jpg", new DateTime(2016, 9, 1, 10, 0, 0), shelf: PhotoShelf.Archive);
            Seed(db, "Scans/4.jpg", null);
            var ids = db.PhotoAssets.OrderBy(a => a.Path).ToDictionary(a => a.Path, a => a.Id);

            var member = PhotosControllerHarness.Body(await PhotosControllerHarness.Build(fixture, db).Browse());
            Assert.Equal(3, member.GetProperty("total").GetInt32());
            Assert.Equal(new[] { ids["B/3.jpg"], ids["A/2.jpg"], ids["A/1.jpg"] }, Ids(member.GetProperty("items")));

            // Windows of 2 then 1 are exactly the whole order; total is only counted on the first page.
            var w1 = PhotosControllerHarness.Body(await PhotosControllerHarness.Build(fixture, db).Browse(skip: 0, top: 2));
            var w2 = PhotosControllerHarness.Body(await PhotosControllerHarness.Build(fixture, db).Browse(skip: 2, top: 2));
            Assert.Equal(new[] { ids["B/3.jpg"], ids["A/2.jpg"] }, Ids(w1.GetProperty("items")));
            Assert.Equal(new[] { ids["A/1.jpg"] }, Ids(w2.GetProperty("items")));
            Assert.Equal(-1, w2.GetProperty("total").GetInt32());

            var year = PhotosControllerHarness.Body(await PhotosControllerHarness.Build(fixture, db).Browse(year: 2011));
            Assert.Equal(2, year.GetProperty("total").GetInt32());
            var month = PhotosControllerHarness.Body(await PhotosControllerHarness.Build(fixture, db).Browse(year: 2011, month: 12));
            Assert.Equal(new[] { ids["A/2.jpg"] }, Ids(month.GetProperty("items")));

            // An admin asking for hidden sees the screenshot; the archive shelf never appears.
            var admin = PhotosControllerHarness.Body(await PhotosControllerHarness.Build(fixture, db, admin: true).Browse(includeHidden: true));
            Assert.Equal(4, admin.GetProperty("total").GetInt32());
            Assert.DoesNotContain(ids["Art/brom.jpg"], Ids(admin.GetProperty("items")));
        }

        [Fact]
        public async Task Groups_by_year_month_and_folder_agree_with_the_timeline()
        {
            using var db = fixture.NewDb();
            Seed(db, "A/1.jpg", new DateTime(2011, 7, 4, 10, 0, 0));
            Seed(db, "A/2.jpg", new DateTime(2011, 12, 25, 9, 0, 0));
            Seed(db, "B/3.jpg", new DateTime(2014, 3, 12, 10, 0, 0));
            Seed(db, "B/sub/4.jpg", new DateTime(2014, 3, 13, 10, 0, 0));
            Seed(db, "Screenshots/h.jpg", new DateTime(2015, 8, 1, 10, 0, 0), hidden: true);
            var ids = db.PhotoAssets.OrderBy(a => a.Path).ToDictionary(a => a.Path, a => a.Id);
            var c = PhotosControllerHarness.Build(fixture, db);

            var years = PhotosControllerHarness.Body(await c.BrowseGroups(groupBy: "year"));
            Assert.Equal(2, years.GetProperty("totalGroups").GetInt32());
            var yg = years.GetProperty("groups").EnumerateArray().ToList();
            Assert.Equal("2014", yg[0].GetProperty("key").GetString());
            Assert.Equal(2, yg[0].GetProperty("totalItems").GetInt32());
            Assert.Equal(new[] { ids["B/sub/4.jpg"], ids["B/3.jpg"] }, Ids(yg[0].GetProperty("items")));
            Assert.Equal(2, yg[1].GetProperty("totalItems").GetInt32());

            // The rail beside it counts the same years the same way.
            var rail = PhotosControllerHarness.Body(await c.TimelineYears());
            Assert.Equal(yg.Select(g => g.GetProperty("totalItems").GetInt32()), rail.GetProperty("years").EnumerateArray().Select(y => y.GetProperty("count").GetInt32()));

            var months = PhotosControllerHarness.Body(await c.BrowseGroups(groupBy: "month", groupsTop: 1, perGroupTop: 1));
            Assert.Equal(3, months.GetProperty("totalGroups").GetInt32());
            var mg = months.GetProperty("groups").EnumerateArray().Single();
            Assert.Equal("2014-03", mg.GetProperty("key").GetString());
            Assert.Equal("March 2014", mg.GetProperty("label").GetString());
            Assert.Equal(1, mg.GetProperty("items").GetArrayLength());
            var more = PhotosControllerHarness.Body(await c.BrowseGroups(groupBy: "month", singleGroupKey: "2014-03", perGroupSkip: 1, perGroupTop: 5));
            Assert.Equal(new[] { ids["B/3.jpg"] }, Ids(more.GetProperty("groups").EnumerateArray().Single().GetProperty("items")));

            var folders = PhotosControllerHarness.Body(await c.BrowseGroups(groupBy: "folder"));
            var fg = folders.GetProperty("groups").EnumerateArray().ToList();
            Assert.Equal(new[] { "A", "B" }, fg.Select(g => g.GetProperty("key").GetString())); // the hidden Screenshots folder is not a member's group
            Assert.Equal(2, fg[1].GetProperty("totalItems").GetInt32());
            Assert.Equal(new[] { ids["B/3.jpg"], ids["B/sub/4.jpg"] }, Ids(fg[1].GetProperty("items")));
        }

        /// <summary>
        /// R9 S7: `/API/Photos/OnThisDay` is the one query the Explore tab could not compose out of what
        /// existed — the browse narrows by year, and by month WITHIN a year, never by a day across years.
        /// It must reach every year, ignore the same photographs the rest of the section ignores, and cap
        /// what it returns.
        /// </summary>
        [Fact]
        public async Task On_this_day_reaches_every_year_honours_the_timeline_exclusions_and_caps_its_take()
        {
            using var db = fixture.NewDb();
            Seed(db, "A/2011.jpg", new DateTime(2011, 8, 27, 10, 0, 0));
            Seed(db, "A/2019.jpg", new DateTime(2019, 8, 27, 18, 0, 0));
            Seed(db, "A/other-day.jpg", new DateTime(2016, 8, 26, 10, 0, 0));
            Seed(db, "A/other-month.jpg", new DateTime(2016, 7, 27, 10, 0, 0));
            Seed(db, "A/hidden.jpg", new DateTime(2013, 8, 27, 10, 0, 0), hidden: true);
            Seed(db, "Art/gallery.jpg", new DateTime(2012, 8, 27, 10, 0, 0), shelf: PhotoShelf.Archive);
            var ids = db.PhotoAssets.ToDictionary(a => a.Path, a => a.Id);

            var c = PhotosControllerHarness.Build(fixture, db);
            var body = PhotosControllerHarness.Body(await c.OnThisDay(month: 8, day: 27));
            // Newest first, across the years; the other day, the other month, the hidden photograph and
            // the gallery shelf are all out.
            Assert.Equal(new[] { ids["A/2019.jpg"], ids["A/2011.jpg"] }, Ids(body.GetProperty("items")));
            Assert.Equal(8, body.GetProperty("month").GetInt32());
            Assert.Equal(27, body.GetProperty("day").GetInt32());
            Assert.Equal(new[] { 2019, 2011 }, body.GetProperty("years").EnumerateArray().Select(y => y.GetInt32()).ToArray());

            // The take is clamped: a caller cannot ask for the whole album through this route.
            var capped = PhotosControllerHarness.Body(await c.OnThisDay(month: 8, day: 27, take: 1));
            Assert.Equal(1, capped.GetProperty("items").GetArrayLength());
            var overAsked = PhotosControllerHarness.Body(await c.OnThisDay(month: 8, day: 27, take: 100000));
            Assert.Equal(2, overAsked.GetProperty("items").GetArrayLength());

            // A nonsense date falls back to today rather than answering for month 0.
            var today = DateTime.Now;
            var fallback = PhotosControllerHarness.Body(await c.OnThisDay(month: 99, day: 0));
            Assert.Equal(today.Month, fallback.GetProperty("month").GetInt32());
            Assert.Equal(today.Day, fallback.GetProperty("day").GetInt32());
        }
    }
}
