using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MovieTheater.Db;
using MovieTheater.Services;
using MovieTheater.Services.Jellyfin;
using Xunit;

namespace MovieTheater.Tests
{
    /// <summary>
    /// Keyframe custody through the sync (2026-08-13): Jellyfin's <c>KeyframeData</c> rows
    /// cascade-delete with their items, so a rename destroys the server-side list for every file whose
    /// path died — and the banked copy in <see cref="MediaKeyframes"/>, keyed by content fingerprint,
    /// is what hands the same bytes their list back on the new item id.
    ///
    /// <para>Each case is asserted from both sides where it matters: the restore fires exactly when a
    /// re-point lands the SAME bytes on a new id, and every refusal path (no banked row, size
    /// disagreement, a stock server without the endpoint) leaves the row on the pre-custody behavior —
    /// unstamped, for the nightly re-extraction — rather than stamped over a lie.</para>
    /// </summary>
    public class JellyfinKeyframeCustodyTests
    {
        private const string DbRoot = @"Q:\";
        private const string JellyfinRoot = @"\\media\share\";
        private const string Ticks = "[0,11260000,115530000,219800000]";
        private const string Fp = "aaaa1111bbbb2222cccc3333dddd4444eeee5555ffff6666aaaa7777bbbb8888";

        // ── The id-format bridge the bank join stands on ───────────────────────────────────────────

        [Fact]
        public void DashedUpper_reshapes_our_dashless_id_into_Jellyfins_TEXT_form()
        {
            // A string reshaping, not a Guid round-trip — the sample pair is real (jellyfin.db holds
            // the left, MediaFile holds the right), so this pins the actual on-disk formats.
            Assert.Equal("DDD251B8-FE23-CDAC-497B-110EB82E25B5",
                JellyfinItemIds.DashedUpper("ddd251b8fe23cdac497b110eb82e25b5"));
            // Anything not 32 hex chars passes through untouched rather than being mangled.
            Assert.Equal("NOT-AN-ID", JellyfinItemIds.DashedUpper("not-an-id"));
        }

        // ── The restore ────────────────────────────────────────────────────────────────────────────

        [Fact]
        public async Task A_repoint_with_the_same_bytes_restores_the_banked_list_onto_the_new_item()
        {
            using var fixture = new CustodyFixture();
            fixture.SeedTrackedFile(size: 4242, itemId: "old-item", fingerprint: Fp, stamped: true);
            fixture.SeedBankedList(Fp, size: 4242);
            // Jellyfin re-created the item at the same path (what a rename round-trip, a library
            // rebuild or a cleanup-then-rescan produces): same bytes, new id.
            fixture.Items.Add(CustodyFixture.Item("new-item", JellyfinRoot + @"1 - Movies\A\A Movie (1999)\movie.mkv", 4242));

            var report = await fixture.RunAsync();

            Assert.Null(report.Aborted);
            using var db = fixture.NewDb();
            var row = await db.MediaFiles.SingleAsync();
            Assert.Equal("new-item", row.JellyfinItemId);
            // The stamp survived the re-point: the new item got the banked list, not a null.
            Assert.NotNull(row.JfKeyframesUtc);
            var import = Assert.Single(fixture.ImportCalls);
            Assert.Equal("new-item", import.ItemId);
            Assert.Contains(Ticks, import.Body);
        }

        [Fact]
        public async Task A_size_change_nulls_the_fingerprint_so_a_restore_can_never_serve_old_bytes()
        {
            using var fixture = new CustodyFixture();
            fixture.SeedTrackedFile(size: 4242, itemId: "the-item", fingerprint: Fp, stamped: true);
            fixture.SeedBankedList(Fp, size: 4242);
            // Same item id, different size: a re-rip in place.
            fixture.Items.Add(CustodyFixture.Item("the-item", JellyfinRoot + @"1 - Movies\A\A Movie (1999)\movie.mkv", 9999));

            await fixture.RunAsync();

            using var db = fixture.NewDb();
            var row = await db.MediaFiles.SingleAsync();
            // The fingerprint described bytes that no longer exist; keeping it would let a LATER
            // re-point restore the old encode's keyframes onto the new one.
            Assert.Null(row.ContentFingerprint);
            Assert.Empty(fixture.ImportCalls);
            // The in-place replacement went down the existing re-extraction lane instead.
            Assert.Contains("the-item", fixture.ExtractCalls);
        }

        [Fact]
        public async Task A_banked_row_whose_size_disagrees_is_refused()
        {
            using var fixture = new CustodyFixture();
            fixture.SeedTrackedFile(size: 4242, itemId: "old-item", fingerprint: Fp, stamped: true);
            // The bank claims these bytes are a different length than the row says — somebody is
            // wrong, and importing on a maybe is how silent freezes come back.
            fixture.SeedBankedList(Fp, size: 999);
            fixture.Items.Add(CustodyFixture.Item("new-item", JellyfinRoot + @"1 - Movies\A\A Movie (1999)\movie.mkv", 4242));

            await fixture.RunAsync();

            using var db = fixture.NewDb();
            var row = await db.MediaFiles.SingleAsync();
            Assert.Equal("new-item", row.JellyfinItemId);
            Assert.Null(row.JfKeyframesUtc);          // falls to the nightly, which re-measures
            Assert.Empty(fixture.ImportCalls);
        }

        [Fact]
        public async Task An_unbanked_fingerprint_falls_back_to_the_nightly_exactly_as_before()
        {
            using var fixture = new CustodyFixture();
            fixture.SeedTrackedFile(size: 4242, itemId: "old-item", fingerprint: Fp, stamped: true);
            // No banked list at all — the bank pass never covered this file.
            fixture.Items.Add(CustodyFixture.Item("new-item", JellyfinRoot + @"1 - Movies\A\A Movie (1999)\movie.mkv", 4242));

            var report = await fixture.RunAsync();

            Assert.Null(report.Aborted);
            using var db = fixture.NewDb();
            var row = await db.MediaFiles.SingleAsync();
            Assert.Equal("new-item", row.JellyfinItemId);
            Assert.Null(row.JfKeyframesUtc);
            Assert.Empty(fixture.ImportCalls);
        }

        [Fact]
        public async Task A_stock_server_404_degrades_to_the_pre_custody_behavior()
        {
            using var fixture = new CustodyFixture { ImportStatus = HttpStatusCode.NotFound };
            fixture.SeedTrackedFile(size: 4242, itemId: "old-item", fingerprint: Fp, stamped: true);
            fixture.SeedBankedList(Fp, size: 4242);
            fixture.Items.Add(CustodyFixture.Item("new-item", JellyfinRoot + @"1 - Movies\A\A Movie (1999)\movie.mkv", 4242));

            // A stock Jellyfin (a stock upgrade wipes the patch) has no ImportKeyframes endpoint. The
            // sync must complete, re-point the row, and leave the stamp null for the nightly.
            var report = await fixture.RunAsync();

            Assert.Null(report.Aborted);
            using var db = fixture.NewDb();
            var row = await db.MediaFiles.SingleAsync();
            Assert.Equal("new-item", row.JellyfinItemId);
            Assert.Null(row.JfKeyframesUtc);
        }

        // ── Harness ────────────────────────────────────────────────────────────────────────────────

        /// <summary>The <c>JellyfinFamilyExclusionTests.SyncFixture</c> shape, trimmed to this file's
        /// needs and extended with recorders for the two keyframe POST routes.</summary>
        private sealed class CustodyFixture : IDisposable
        {
            private readonly string workDir;
            private readonly DbContextOptions<MovieDb> dbOptions;
            private readonly MovieTheaterConfiguration config;

            public readonly List<JellyfinItem> Items = new();
            public readonly List<(string ItemId, string Body)> ImportCalls = new();
            public readonly List<string> ExtractCalls = new();
            public HttpStatusCode ImportStatus { get; init; } = HttpStatusCode.NoContent;

            public CustodyFixture()
            {
                workDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "jf-custody-tests", Guid.NewGuid().ToString("N"));
                System.IO.Directory.CreateDirectory(workDir);
                dbOptions = new DbContextOptionsBuilder<MovieDb>()
                    .UseSqlite("Data Source=" + System.IO.Path.Combine(workDir, "custody.db") + ";Pooling=False")
                    .Options;
                using var db = new MovieDb(dbOptions);
                db.Database.EnsureCreated();

                config = new MovieTheaterConfiguration(new ConfigurationBuilder().Build())
                {
                    JellyfinBaseUrl = "http://jellyfin.invalid",
                    JellyfinApiKey = "test",
                    JellyfinPathMappings = new List<JellyfinPathMapping>
                    {
                        new JellyfinPathMapping { DbPrefix = DbRoot, JellyfinPrefix = JellyfinRoot },
                    },
                };
            }

            public MovieDb NewDb() => new MovieDb(dbOptions);

            public void SeedTrackedFile(long size, string itemId, string? fingerprint, bool stamped)
            {
                using var db = NewDb();
                var playable = new Playable { Kind = PlayableKind.Movie };
                db.Playables.Add(playable);
                db.SaveChanges();
                var path = DbRoot + @"1 - Movies\A\A Movie (1999)\movie.mkv";
                db.Movies.Add(new Movie { id = 1, Title = "A Movie", FilePath = path, PlayableId = playable.Id });
                db.MediaFiles.Add(new MediaFile
                {
                    PlayableId = playable.Id,
                    Path = path,
                    Role = MovieFileRole.Primary,
                    SizeBytes = size,
                    JellyfinItemId = itemId,
                    ContentFingerprint = fingerprint,
                    JfKeyframesUtc = stamped ? DateTime.UtcNow.AddDays(-30) : null,
                    VideoCodec = "h264",
                });
                db.SaveChanges();
            }

            public void SeedBankedList(string fingerprint, long size)
            {
                using var db = NewDb();
                db.MediaKeyframes.Add(new MediaKeyframes
                {
                    Fingerprint = fingerprint,
                    TotalDurationTicks = 74722880000,
                    KeyframeTicks = Ticks,
                    SizeBytes = size,
                    SourceItemId = "old-item",
                    CapturedUtc = DateTime.UtcNow.AddDays(-1),
                });
                db.SaveChanges();
            }

            public Task<JellyfinSyncReport> RunAsync()
            {
                var handler = new Handler(this);
                var httpClient = new HttpClient(handler) { BaseAddress = new Uri(config.JellyfinBaseUrl!) };
                var api = new JellyfinApi(httpClient, new SingleClientFactory(httpClient),
                    Options.Create(new JellyfinApiOptions { BaseUrl = config.JellyfinBaseUrl, ApiKey = config.JellyfinApiKey }));
                var service = new JellyfinSyncService(new Factory(dbOptions), api, config,
                    NullLogger<JellyfinSyncService>.Instance);
                return service.RunAsync(dryRun: false);
            }

            public static JellyfinItem Item(string id, string path, long size) => new()
            {
                Id = id,
                Name = id,
                Path = path,
                MediaSources = new List<JellyfinMediaSource>
                {
                    new JellyfinMediaSource
                    {
                        Container = "mkv",
                        Size = size,
                        MediaStreams = new List<JellyfinMediaStream>
                        {
                            new JellyfinMediaStream { Type = "Video", Codec = "h264", Width = 1920, Height = 1080 },
                        },
                    },
                },
            };

            public void Dispose()
            {
                // Pooling=False so the temp file unlocks when the context closes. The fixtures used to call the PROCESS-GLOBAL SqliteConnection.ClearAllPools() here, which reached into every OTHER test class running in parallel and closed its pooled connections mid-test
                // an occasional, unreproducible failure somewhere else in the suite.
                try { System.IO.Directory.Delete(workDir, recursive: true); }
                catch (System.IO.IOException) { }
                catch (UnauthorizedAccessException) { }
            }

            private sealed class Handler : HttpMessageHandler
            {
                private readonly CustodyFixture fixture;
                public Handler(CustodyFixture fixture) => this.fixture = fixture;

                protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancel)
                {
                    var path = request.RequestUri!.AbsolutePath;
                    var query = request.RequestUri.Query;

                    if (path.EndsWith("/ImportKeyframes", StringComparison.OrdinalIgnoreCase))
                    {
                        var itemId = path.Split('/', StringSplitOptions.RemoveEmptyEntries)[^2];
                        var body = request.Content == null ? "" : await request.Content.ReadAsStringAsync(cancel);
                        fixture.ImportCalls.Add((itemId, body));
                        return new HttpResponseMessage(fixture.ImportStatus);
                    }
                    if (path.EndsWith("/ExtractKeyframes", StringComparison.OrdinalIgnoreCase))
                    {
                        fixture.ExtractCalls.Add(path.Split('/', StringSplitOptions.RemoveEmptyEntries)[^2]);
                        // Extraction "fails" so nothing re-stamps through the OTHER lane — every stamp a
                        // test observes came from the restore under test.
                        return new HttpResponseMessage(HttpStatusCode.InternalServerError);
                    }

                    string body2;
                    if (path.Equals("/System/Info", StringComparison.OrdinalIgnoreCase))
                        body2 = "{\"ServerName\":\"canned\",\"Version\":\"10.0.0\"}";
                    else if (path.Equals("/Items", StringComparison.OrdinalIgnoreCase)
                             && (!query.Contains("StartIndex=", StringComparison.OrdinalIgnoreCase)
                                 || query.Contains("StartIndex=0", StringComparison.OrdinalIgnoreCase))
                             && !query.Contains("ids=", StringComparison.OrdinalIgnoreCase)
                             && !query.Contains("ParentId=", StringComparison.OrdinalIgnoreCase))
                        body2 = System.Text.Json.JsonSerializer.Serialize(
                            new { Items = fixture.Items, TotalRecordCount = fixture.Items.Count });
                    else
                        body2 = "{\"Items\":[],\"TotalRecordCount\":0}";

                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(body2, Encoding.UTF8, "application/json"),
                    };
                }
            }

            private sealed class Factory : IDbContextFactory<MovieDb>
            {
                private readonly DbContextOptions<MovieDb> options;
                public Factory(DbContextOptions<MovieDb> options) => this.options = options;
                public MovieDb CreateDbContext() => new MovieDb(options);
            }

            private sealed class SingleClientFactory : IHttpClientFactory
            {
                private readonly HttpClient client;
                public SingleClientFactory(HttpClient client) => this.client = client;
                public HttpClient CreateClient(string name) => client;
            }
        }
    }
}
