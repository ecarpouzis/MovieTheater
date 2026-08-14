using System;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using MovieTheater.Db;
using Xunit;

namespace MovieTheater.Tests
{
    /// <summary>
    /// The year index behind the timeline's scrubber rail (<c>/API/Photos/TimelineYears</c>).
    ///
    /// <para>The one property worth defending: the counts obey the SAME exclusions as the timeline
    /// itself. A rail that promises photographs a jump then cannot show — hidden rows for a member,
    /// gallery art, another shelf entirely — is the "reads as data loss" failure the timeline's own
    /// doc comment warns about, measured instead of asserted by hand.</para>
    /// </summary>
    public class PhotoTimelineYearsTests : IDisposable
    {
        private readonly PhotoIngestFixture fixture = new PhotoIngestFixture();

        public void Dispose() => fixture.Dispose();

        private static void Seed(MovieDb db, string path, DateTime? takenAt,
            bool hidden = false, PhotoShelf shelf = PhotoShelf.Timeline)
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

        private static (int year, int count)[] Years(JsonElement body) =>
            body.GetProperty("years").EnumerateArray()
                .Select(y => (y.GetProperty("year").GetInt32(), y.GetProperty("count").GetInt32()))
                .ToArray();

        [Fact]
        public async Task Counts_years_newest_first_and_the_undated_shelf_separately()
        {
            using var db = fixture.NewDb();
            Seed(db, "A/1.jpg", new DateTime(2011, 7, 4, 10, 0, 0));
            Seed(db, "A/2.jpg", new DateTime(2011, 12, 25, 9, 0, 0));
            Seed(db, "B/3.jpg", new DateTime(2014, 3, 12, 10, 0, 0));
            Seed(db, "Scans/4.jpg", null);
            Seed(db, "Scans/5.jpg", null);

            var body = PhotosControllerHarness.Body(
                await PhotosControllerHarness.Build(fixture, db).TimelineYears());

            Assert.Equal(new[] { (2014, 1), (2011, 2) }, Years(body));
            Assert.Equal(2, body.GetProperty("undated").GetInt32());
        }

        [Fact]
        public async Task Excludes_what_the_timeline_excludes()
        {
            using var db = fixture.NewDb();
            Seed(db, "A/1.jpg", new DateTime(2011, 7, 4, 10, 0, 0));
            // Hidden from a member; the gallery shelf never counts, for anyone.
            Seed(db, "Screenshots/h.jpg", new DateTime(2011, 8, 1, 10, 0, 0), hidden: true);
            Seed(db, "Art/brom.jpg", new DateTime(2011, 9, 1, 10, 0, 0), shelf: PhotoShelf.Archive);

            var member = PhotosControllerHarness.Body(
                await PhotosControllerHarness.Build(fixture, db).TimelineYears());
            Assert.Equal(new[] { (2011, 1) }, Years(member));

            // An admin asking for hidden sees it counted — the rail must agree with the page beside it
            // in BOTH configurations.
            var admin = PhotosControllerHarness.Body(
                await PhotosControllerHarness.Build(fixture, db, admin: true).TimelineYears(includeHidden: true));
            Assert.Equal(new[] { (2011, 2) }, Years(admin));
        }
    }
}
