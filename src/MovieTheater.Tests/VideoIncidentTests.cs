using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Claims;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MovieTheater.Controllers;
using MovieTheater.Db;
using MovieTheater.Services;
using MovieTheater.Services.Jellyfin;
using Xunit;

namespace MovieTheater.Tests
{
    /// <summary>
    /// <c>POST /API/Stream/Incident</c> — the video players' self-report sink.
    /// </summary>
    /// <remarks>
    /// The action is exercised directly over a real (SQLite) database rather than through a host,
    /// because what is worth pinning here is what it DOES with a body: a report from a dying page is
    /// a raw beacon (no model binding, no preflight, no content negotiation), it must survive being
    /// malformed, it must not let a runaway client write unbounded rows, and it carries the table's
    /// only retention bound. None of those are ASP.NET behaviours.
    ///
    /// <para>The Jellyfin client handed to the controller is pointed at a socket that is never
    /// opened: this action must not touch it, and a test that quietly started working through a real
    /// client would be testing something else.</para>
    /// </remarks>
    public class VideoIncidentTests : IDisposable
    {
        private const int UserId = 12;
        private readonly string workDir;
        private readonly DbContextOptions<MovieDb> options;

        public VideoIncidentTests()
        {
            workDir = Path.Combine(Path.GetTempPath(), "video-incident-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(workDir);
            options = new DbContextOptionsBuilder<MovieDb>()
                .UseSqlite("Data Source=" + Path.Combine(workDir, "incidents.db") + ";Pooling=False")
                .Options;
            using var db = new MovieDb(options);
            db.Database.EnsureCreated();
        }

        public void Dispose()
        {
            // Pooling=False so the temp file unlocks when the context closes. The fixtures used to call the PROCESS-GLOBAL SqliteConnection.ClearAllPools() here, which reached into every OTHER test class running in parallel and closed its pooled connections mid-test
            // an occasional, unreproducible failure somewhere else in the suite.
            try { Directory.Delete(workDir, recursive: true); } catch (IOException) { /* the OS still has it */ }
            GC.SuppressFinalize(this);
        }

        private MovieDb NewDb() => new MovieDb(options);

        private static StreamController Build(MovieDb db, string body)
        {
            // Built from an empty in-memory source: a test must never reach the real appsettings —
            // that file's connection string is the live shared production database.
            var config = new MovieTheaterConfiguration(new ConfigurationBuilder().Build());
            var httpClient = new HttpClient { BaseAddress = new Uri("http://jellyfin.invalid") };
            var jellyfin = new JellyfinApi(httpClient, new SingleClientFactory(httpClient),
                Options.Create(new JellyfinApiOptions { BaseUrl = "http://jellyfin.invalid", ApiKey = "unused" }));

            var identity = new ClaimsIdentity(new[]
            {
                new Claim(ClaimTypes.NameIdentifier, UserId.ToString()),
                new Claim(ClaimTypes.Name, "viewer"),
                // Streaming is a password-verified surface, and so is its incident sink — the report
                // comes from a session that was already allowed to play video.
                new Claim("amr", "pwd"),
            }, "TestScheme");

            var http = new DefaultHttpContext { User = new ClaimsPrincipal(identity) };
            http.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
            http.Request.ContentType = "text/plain";

            return new StreamController(db, jellyfin, config, NullLogger<StreamController>.Instance,
                new MovieTheater.Streaming.TranscodeSessionRegistry())
            {
                ControllerContext = new ControllerContext { HttpContext = http },
            };
        }

        private async Task<IActionResult> PostAsync(string body)
        {
            using var db = NewDb();
            return await Build(db, body).Incident();
        }

        [Fact]
        public async Task Records_the_report_with_the_identity_the_player_sent()
        {
            var result = await PostAsync("""
                {"kind":"stall","summary":"frozen 14s while playing","player":"watch",
                 "movieId":9754,"playableId":331,"positionSeconds":4212.5,
                 "userAgent":"Mozilla/5.0 (Windows NT 10.0)","state":{"rung":"direct"},
                 "events":[{"event":"waiting"},{"event":"stall"}]}
                """);

            Assert.IsType<OkObjectResult>(result);
            using var db = NewDb();
            var row = Assert.Single(db.VideoPlaybackIncidents);
            Assert.Equal("stall", row.Kind);
            Assert.Equal("frozen 14s while playing", row.Summary);
            Assert.Equal("watch", row.Player);
            Assert.Equal(9754, row.MovieId);
            Assert.Equal(331, row.PlayableId);
            Assert.Equal(4212.5, row.PositionSeconds);
            Assert.Equal(UserId, row.UserId);
            // The ids that don't apply stay null rather than becoming 0 — a report about a movie must
            // never read as a report about series 0.
            Assert.Null(row.SeriesId);
            Assert.Null(row.MiscVideoId);
            Assert.Null(row.ChannelId);
            // The whole body is kept: the parsed columns are for querying, the payload is the evidence.
            Assert.Contains("\"events\"", row.Payload);
        }

        [Fact]
        public async Task A_tv_report_is_identified_by_its_channel()
        {
            await PostAsync("""{"kind":"fatal","player":"tv","channelId":12,"playableId":8801}""");

            using var db = NewDb();
            var row = Assert.Single(db.VideoPlaybackIncidents);
            Assert.Equal("tv", row.Player);
            Assert.Equal(12, row.ChannelId);
            Assert.Equal(8801, row.PlayableId);
            Assert.Null(row.MovieId);
        }

        [Fact]
        public async Task Keeps_a_report_it_cannot_parse_because_it_is_still_evidence()
        {
            // A page being frozen or unloaded can hand over a truncated beacon. Dropping it would
            // discard the one signal that says something fired at all — which is the entire reason
            // this table exists.
            await PostAsync("{\"kind\":\"stall\",\"events\":[{\"event\":\"wait");

            using var db = NewDb();
            var row = Assert.Single(db.VideoPlaybackIncidents);
            Assert.Equal("unparseable", row.Kind);
            Assert.Contains("stall", row.Payload);
        }

        [Fact]
        public async Task Refuses_an_empty_body()
        {
            var result = await PostAsync("   ");
            Assert.IsType<BadRequestObjectResult>(result);
            using var db = NewDb();
            Assert.Empty(db.VideoPlaybackIncidents);
        }

        [Fact]
        public async Task Caps_the_payload_so_a_runaway_client_cannot_write_unbounded_rows()
        {
            var giant = new string('x', 300 * 1024);
            await PostAsync(giant); // not JSON either — it lands as unparseable, still capped

            using var db = NewDb();
            var row = Assert.Single(db.VideoPlaybackIncidents);
            Assert.Equal(256 * 1024, row.Payload.Length);
        }

        [Fact]
        public async Task Truncates_fields_to_the_columns_they_have_to_fit()
        {
            var longKind = new string('k', 100);
            var longSummary = new string('s', 900);
            var longAgent = new string('u', 900);
            await PostAsync($$"""
                {"kind":"{{longKind}}","summary":"{{longSummary}}","userAgent":"{{longAgent}}","player":"watchtvwatchtv"}
                """);

            using var db = NewDb();
            var row = Assert.Single(db.VideoPlaybackIncidents);
            Assert.Equal(40, row.Kind.Length);
            Assert.Equal(400, row.Summary!.Length);
            Assert.Equal(400, row.UserAgent!.Length);
            Assert.Equal(10, row.Player!.Length);
        }

        [Fact]
        public async Task Prunes_expired_rows_on_insert_and_leaves_the_rest_alone()
        {
            // The bound rides the only path that GROWS the table — no timer, no background service,
            // and nothing to run when nobody is failing.
            using (var seed = NewDb())
            {
                for (var i = 0; i < 3; i++)
                    seed.VideoPlaybackIncidents.Add(Row(DateTime.UtcNow.AddDays(-200)));
                seed.VideoPlaybackIncidents.Add(Row(DateTime.UtcNow.AddDays(-179)));
                seed.VideoPlaybackIncidents.Add(Row(DateTime.UtcNow.AddDays(-1)));
                await seed.SaveChangesAsync();
            }

            await PostAsync("""{"kind":"stall"}""");

            using var db = NewDb();
            // The three expired rows are gone; the 179-day-old one is inside the window and stays,
            // as does yesterday's and the one just filed.
            Assert.Equal(3, await db.VideoPlaybackIncidents.CountAsync());
            Assert.False(await db.VideoPlaybackIncidents.AnyAsync(i => i.CreatedUtc < DateTime.UtcNow.AddDays(-180)));
        }

        [Fact]
        public async Task Sweeps_at_most_one_capped_batch_per_insert()
        {
            // One unlucky report must not pay for a year of backlog in a single request. Incidents
            // arrive faster than they expire whenever it matters, so a small batch per insert holds
            // the bound without ever becoming the unbounded delete the cap exists to prevent.
            using (var seed = NewDb())
            {
                for (var i = 0; i < 60; i++)
                    seed.VideoPlaybackIncidents.Add(Row(DateTime.UtcNow.AddDays(-300)));
                await seed.SaveChangesAsync();
            }

            var body = await PostAsync("""{"kind":"stall"}""");
            var ok = Assert.IsType<OkObjectResult>(body);
            Assert.Equal(50, ok.Value!.GetType().GetProperty("pruned")!.GetValue(ok.Value));

            using var db = NewDb();
            Assert.Equal(11, await db.VideoPlaybackIncidents.CountAsync()); // 60 − 50 swept + the new one
        }

        private static VideoPlaybackIncident Row(DateTime createdUtc) => new()
        {
            CreatedUtc = createdUtc,
            Kind = "stall",
            Payload = "{}",
        };

        private sealed class SingleClientFactory : IHttpClientFactory
        {
            private readonly HttpClient client;
            public SingleClientFactory(HttpClient client) => this.client = client;
            public HttpClient CreateClient(string name) => client;
        }
    }
}
