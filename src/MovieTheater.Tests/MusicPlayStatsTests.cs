using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using MovieTheater.Controllers;
using MovieTheater.Core;
using MovieTheater.Services;
using MovieTheater.Db;
using Xunit;

namespace MovieTheater.Tests
{
    /// <summary>
    /// The music vertical's first play telemetry (R9 closing pass): <c>POST /API/Music/Play</c> and
    /// the table behind "Most played".
    /// </summary>
    /// <remarks>
    /// <para>Run against the SHIPPED controller over a throwaway SQLite <see cref="MovieDb"/> —
    /// never the configured connection string, which IS the live shared production database.
    /// <c>EnsureCreated</c> builds the schema from the model, so the unique (user, track) key that
    /// makes the write an UPSERT is pinned here too; a compile cannot check it.</para>
    ///
    /// <para>What is claimed: the endpoint is behind the same password-verified gate as every other
    /// <c>/API/Music/*</c> route; a beacon cannot inflate a count by arriving twice; a report for a
    /// track that does not exist is skipped rather than 500ing a fire-and-forget sender; and the
    /// library-wide roll-up the shelf rows carry sums across listeners and reaches an artist's loose
    /// tracks.</para>
    /// </remarks>
    public class MusicPlayStatsTests : IDisposable
    {
        private readonly string workDir = Path.Combine(Path.GetTempPath(), "mt-music-plays-" + Guid.NewGuid().ToString("N"));
        private readonly DbContextOptions<MovieDb> options;

        public MusicPlayStatsTests()
        {
            Directory.CreateDirectory(workDir);
            options = new DbContextOptionsBuilder<MovieDb>().UseSqlite("Data Source=" + Path.Combine(workDir, "plays.db") + ";Pooling=False").Options;
            using var db = new MovieDb(options);
            db.Database.EnsureCreated();
            Seed(db);
        }

        public void Dispose()
        {
            // Pooling=False so the temp file unlocks when the context closes. The fixtures used to call the PROCESS-GLOBAL SqliteConnection.ClearAllPools() here, which reached into every OTHER test class running in parallel and closed its pooled connections mid-test
            // an occasional, unreproducible failure somewhere else in the suite.
            try { Directory.Delete(workDir, recursive: true); } catch (IOException) { }
            GC.SuppressFinalize(this);
        }

        private static void Seed(MovieDb db)
        {
            db.Users.AddRange(new User { UserID = 1, Username = "eric" }, new User { UserID = 2, Username = "someone-else" });
            db.MusicArtists.Add(new MusicArtist { Id = 1, Name = "Air", SortName = "Air", FolderName = "Air (1998-2004)" });
            db.MusicAlbums.Add(new MusicAlbum { Id = 11, ArtistId = 1, Title = "Moon Safari", Year = 1998, FolderPath = "Air/a" });
            db.MusicTracks.AddRange(
                new MusicTrack { Id = 101, ArtistId = 1, AlbumId = 11, Title = "La Femme", FileName = "1.flac", Extension = ".flac", RelativePath = "Air/a/1.flac" },
                new MusicTrack { Id = 102, ArtistId = 1, AlbumId = 11, Title = "Sexy Boy", FileName = "2.flac", Extension = ".flac", RelativePath = "Air/a/2.flac" },
                // A loose track: it belongs to no album, so only the ARTIST roll-up can see it.
                new MusicTrack { Id = 103, ArtistId = 1, AlbumId = null, Title = "Stray", FileName = "stray.flac", Extension = ".flac", RelativePath = "Air/stray.flac" });
            db.SaveChanges();
        }

        private MusicController Build(MovieDb db, int userId, string body)
        {
            // Empty configuration on purpose: a test must never read appsettings, whose connection
            // string is the live shared database. Nothing in this path needs the gateway anyway.
            var controller = new MusicController(db, new MovieTheaterConfiguration(new ConfigurationBuilder().Build()));
            var http = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, userId.ToString()) }, "test")),
            };
            http.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
            controller.ControllerContext = new ControllerContext { HttpContext = http };
            return controller;
        }

        private static (int Counted, int Skipped) Result(IActionResult r)
        {
            var v = Assert.IsType<OkObjectResult>(r).Value!;
            return ((int)v.GetType().GetProperty("counted")!.GetValue(v)!,
                    (int)v.GetType().GetProperty("skipped")!.GetValue(v)!);
        }

        private static string Beacon(params (int TrackId, string StartedAt)[] plays) =>
            JsonSerializer.Serialize(new { plays = plays.Select(p => new { trackId = p.TrackId, startedAt = p.StartedAt }) });

        /// <summary>
        /// The minute every stamp below is measured from — two hours ago, not a date in a literal.
        /// </summary>
        /// <remarks>
        /// The endpoint TRUSTS a stamp only inside <c>now-1d … now+5min</c> and clamps anything else
        /// to <c>now</c> (the stamp keys idempotency and nothing else, so a wild one must not be able
        /// to make the next genuine play look like a duplicate). A hard-coded calendar date therefore
        /// has a shelf life: once it ages past that window every stamp in the file collapses onto the
        /// same "now" minute, and tests whose whole subject is "these are DIFFERENT minutes" quietly
        /// stop testing it — the twice-arriving beacon starts failing, and its siblings keep passing
        /// for the wrong reason. Anchoring to UtcNow keeps the window satisfied forever; flooring to
        /// the minute keeps the arithmetic exact, and computing it ONCE keeps a minute that rolls over
        /// mid-test from moving the stamps underneath the assertions.
        /// </remarks>
        private static readonly DateTime Anchor = FloorToMinute(DateTime.UtcNow.AddHours(-2));

        private static DateTime FloorToMinute(DateTime t) =>
            new(t.Year, t.Month, t.Day, t.Hour, t.Minute, 0, DateTimeKind.Utc);

        /// <summary>The wire form of "<paramref name="minute"/> minutes past the anchor".</summary>
        private static string Stamp(int minute, int second = 0) =>
            Anchor.AddMinutes(minute).AddSeconds(second)
                .ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

        /// <summary>What the endpoint should store for <see cref="Stamp"/> — floored to the minute.</summary>
        private static DateTime Minute(int minute) => Anchor.AddMinutes(minute);

        [Fact]
        public void The_endpoint_is_behind_the_same_password_gate_as_every_other_music_route()
        {
            // Read off the REAL class: the whole controller carries StreamingUser (amr=pwd), and the
            // play sink must not be the one action that opts out.
            var attr = Assert.Single(typeof(MusicController).GetCustomAttributes(typeof(AuthorizeAttribute), inherit: true).Cast<AuthorizeAttribute>());
            Assert.Equal("StreamingUser", attr.Policy);
            var action = typeof(MusicController).GetMethod(nameof(MusicController.Play))!;
            Assert.Empty(action.GetCustomAttributes(typeof(AllowAnonymousAttribute), inherit: true));
        }

        [Fact]
        public async Task A_beacon_that_arrives_twice_counts_once()
        {
            var body = Beacon((101, Stamp(0, second: 7)));

            using (var db = new MovieDb(options))
                Assert.Equal((1, 0), Result(await Build(db, 1, body).Play()));

            // The retry, the pagehide flush racing the in-flight send, the second tab: same track,
            // same started-at MINUTE. The row remembers the minute it last counted, so this is a
            // no-op — which is the whole reason the beacon can be fire-and-forget.
            using (var db = new MovieDb(options))
                Assert.Equal((0, 1), Result(await Build(db, 1, body).Play()));

            // …and a stamp inside the same minute is the same play, even at a different second.
            using (var db = new MovieDb(options))
                Assert.Equal((0, 1), Result(await Build(db, 1, Beacon((101, Stamp(0, second: 59)))).Play()));

            using (var db = new MovieDb(options))
            {
                var row = Assert.Single(db.MusicPlayStats.Where(p => p.UserId == 1 && p.MusicTrackId == 101));
                Assert.Equal(1, row.PlayCount);
                Assert.Equal(Minute(0), row.LastStartedUtc);
            }

            // Putting the record on again later IS a second play: a different minute.
            using (var db = new MovieDb(options))
                Assert.Equal((1, 0), Result(await Build(db, 1, Beacon((101, Stamp(4)))).Play()));
            using (var db = new MovieDb(options))
                Assert.Equal(2, db.MusicPlayStats.Single(p => p.UserId == 1 && p.MusicTrackId == 101).PlayCount);
        }

        [Fact]
        public async Task One_row_per_listener_per_track_and_a_junk_report_is_skipped_not_fatal()
        {
            using (var db = new MovieDb(options))
                Assert.Equal((2, 0), Result(await Build(db, 1, Beacon((101, Stamp(0)), (102, Stamp(4)))).Play()));
            // Another listener's play of the same track is their OWN row, not an increment of mine.
            using (var db = new MovieDb(options))
                Assert.Equal((1, 0), Result(await Build(db, 2, Beacon((101, Stamp(0)))).Play()));
            using (var db = new MovieDb(options))
            {
                Assert.Equal(2, db.MusicPlayStats.Count(p => p.MusicTrackId == 101));
                Assert.All(db.MusicPlayStats.ToList(), p => Assert.Equal(1, p.PlayCount));
            }

            // A track id that is not in the library, and a body that is not JSON at all: a
            // fire-and-forget sender gets an answer, never a 500 it cannot see anyway.
            using (var db = new MovieDb(options))
                Assert.Equal((0, 1), Result(await Build(db, 1, Beacon((999, Stamp(60)))).Play()));
            using (var db = new MovieDb(options))
                Assert.Equal((0, 0), Result(await Build(db, 1, "not json").Play()));
            using (var db = new MovieDb(options))
                Assert.Equal((0, 0), Result(await Build(db, 1, "").Play()));
        }

        [Fact]
        public void The_parser_floors_to_the_minute_clamps_a_wild_stamp_and_caps_the_batch()
        {
            var now = new DateTime(2026, 8, 27, 12, 30, 0, DateTimeKind.Utc);

            // The floor IS the idempotency key.
            var one = Assert.Single(MusicController.ParsePlayReports("""{"plays":[{"trackId":5,"startedAt":"2026-08-27T12:00:41Z"}]}""", now));
            Assert.Equal(5, one.TrackId);
            Assert.Equal(new DateTime(2026, 8, 27, 12, 0, 0, DateTimeKind.Utc), one.StartedUtc);

            // A bare object is one play; epoch millis are accepted too (a client that sends Date.now()).
            Assert.Single(MusicController.ParsePlayReports("""{"trackId":5}""", now));
            Assert.Equal(new DateTime(2026, 8, 27, 12, 0, 0, DateTimeKind.Utc),
                Assert.Single(MusicController.ParsePlayReports($$"""{"trackId":5,"startedAt":{{new DateTimeOffset(new DateTime(2026, 8, 27, 12, 0, 30, DateTimeKind.Utc)).ToUnixTimeMilliseconds()}}}""", now)).StartedUtc);

            // A missing or wild stamp falls back to now rather than DROPPING the report — the stamp
            // only keys idempotency, and a wild one would make the next genuine play look like a
            // duplicate.
            Assert.Equal(now, Assert.Single(MusicController.ParsePlayReports("""{"trackId":5}""", now)).StartedUtc);
            Assert.Equal(now, Assert.Single(MusicController.ParsePlayReports("""{"trackId":5,"startedAt":"2099-01-01T00:00:00Z"}""", now)).StartedUtc);
            Assert.Equal(now, Assert.Single(MusicController.ParsePlayReports("""{"trackId":5,"startedAt":"1999-01-01T00:00:00Z"}""", now)).StartedUtc);

            // Nothing usable is nothing recorded — never an exception a beacon would never see.
            Assert.Empty(MusicController.ParsePlayReports("not json", now));
            Assert.Empty(MusicController.ParsePlayReports(null, now));
            Assert.Empty(MusicController.ParsePlayReports("""{"plays":[{"trackId":0},{"trackId":-3},{"nope":1}]}""", now));

            // Bounded write: a runaway client cannot turn one POST into an unbounded job.
            var many = string.Join(",", Enumerable.Range(1, 500).Select(i => $$"""{"trackId":{{i}}}"""));
            Assert.Equal(50, MusicController.ParsePlayReports($$"""{"plays":[{{many}}]}""", now).Count);
        }

        [Fact]
        public async Task The_shelf_rows_carry_the_library_wide_roll_up()
        {
            // Two listeners, three tracks — one of them the artist's LOOSE track, which belongs to no
            // album and would be invisible if the artist roll-up went through albums.
            using (var db = new MovieDb(options))
                await Build(db, 1, Beacon((101, Stamp(0)), (102, Stamp(4)), (103, Stamp(8)))).Play();
            using (var db = new MovieDb(options))
                await Build(db, 2, Beacon((101, Stamp(20)))).Play();

            using (var db = new MovieDb(options))
            {
                var albums = Assert.IsType<OkObjectResult>(await Build(db, 1, "").Albums()).Value!;
                var items = (System.Collections.IEnumerable)albums.GetType().GetProperty("items")!.GetValue(albums)!;
                var album = items.Cast<object>().Single();
                // 2 plays of track 101 (two listeners) + 1 of track 102 = 3 for the album. The loose
                // track is NOT on it.
                Assert.Equal(3, (int)album.GetType().GetProperty("playCount")!.GetValue(album)!);
                Assert.NotNull(album.GetType().GetProperty("lastPlayedUtc")!.GetValue(album));
            }

            using (var db = new MovieDb(options))
            {
                var artists = (System.Collections.IEnumerable)Assert.IsType<OkObjectResult>(await Build(db, 1, "").Artists()).Value!;
                var artist = artists.Cast<object>().Single();
                // …and the artist's total DOES include it: 3 album plays + 1 loose = 4.
                Assert.Equal(4, (int)artist.GetType().GetProperty("playCount")!.GetValue(artist)!);
            }
        }
    }
}
