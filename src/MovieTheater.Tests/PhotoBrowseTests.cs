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
    }
}
