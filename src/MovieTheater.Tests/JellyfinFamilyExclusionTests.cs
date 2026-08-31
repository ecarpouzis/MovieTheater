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
    /// The Phase 5 acceptance criterion, first half (docs/photos-plan.md §2.3, §5 Phase 5): <b>zero
    /// family items appear in any movie-site surface</b>.
    ///
    /// <para>That guarantee is enforced at ONE place — the item lists <see cref="JellyfinSyncService"/>
    /// obtains from Jellyfin — because every movie-side surface downstream (Movie, MiscVideo, channels
    /// and their pools, recommendations, the review queue) reads <see cref="MediaFile"/> or
    /// <see cref="Movie"/> rows, and a family video that never becomes one cannot reach any of them.
    /// So these tests prove two things: the prefix rule itself, and that the SYNC applies it.</para>
    ///
    /// <para><b>No live server is contacted.</b> Jellyfin's HTTP surface is answered by a canned
    /// handler; the database is a SQLite file. The standing prohibition on running <c>sync-jellyfin</c>
    /// in any mode is why the exclusion is exercised through the service's own method rather than the
    /// CLI, and why the fixtures below are invented paths rather than real ones.</para>
    /// </summary>
    public class JellyfinFamilyExclusionTests
    {
        // Invented paths — no real collection layout appears in code (§6).
        private const string DbRoot = @"Q:\";
        private const string JellyfinRoot = @"\\media\share\";
        private const string PhotosRootDb = @"Q:\7 - Family Album";

        private static List<JellyfinPathMapping> Mappings() => new()
        {
            new JellyfinPathMapping { DbPrefix = DbRoot, JellyfinPrefix = JellyfinRoot },
        };

        // ── The rule ────────────────────────────────────────────────────────────────────────────

        [Theory]
        // The photo root as Jellyfin would report it (UNC), and as the DB stores it (drive letter).
        [InlineData(@"\\media\share\7 - Family Album\Vacation\clip.mp4", true)]
        [InlineData(@"Q:\7 - Family Album\Vacation\clip.mp4", true)]
        // Forward slashes and mixed case — the two forms a Linux Jellyfin and a Windows one produce.
        [InlineData(@"//media/share/7 - family album/vacation/clip.mp4", true)]
        // The root folder itself.
        [InlineData(@"Q:\7 - Family Album", true)]
        // A movie, in both vocabularies. Must survive.
        [InlineData(@"\\media\share\1 - Movies\A\Alien (1979)\Alien.mkv", false)]
        [InlineData(@"Q:\1 - Movies\A\Alien (1979)\Alien.mkv", false)]
        // The prefix trap: a SIBLING whose name merely starts with the root's. A bare StartsWith would
        // swallow it, and the family exclusion would silently delete a real library from the site.
        [InlineData(@"Q:\7 - Family Album Extra\movie.mkv", false)]
        [InlineData(@"Q:\7 - Family Albums\movie.mkv", false)]
        public void Path_prefix_decides_what_is_family(string path, bool excluded)
        {
            var exclusion = JellyfinFamilyExclusion.Build(PhotosRootDb, Mappings());
            Assert.True(exclusion.Configured);
            Assert.Equal(excluded, exclusion.IsExcluded(path));
        }

        [Fact]
        public void The_root_may_be_configured_in_EITHER_form()
        {
            // A host that mounts the collection over UNC configures PhotosLibraryDir that way; the
            // exclusion must still catch the drive-letter path the DB side would produce.
            var exclusion = JellyfinFamilyExclusion.Build(JellyfinRoot + @"7 - Family Album", Mappings());
            Assert.True(exclusion.IsExcluded(@"Q:\7 - Family Album\Vacation\clip.mp4"));
            Assert.True(exclusion.IsExcluded(@"\\media\share\7 - Family Album\Vacation\clip.mp4"));
            Assert.False(exclusion.IsExcluded(@"Q:\1 - Movies\A\Alien (1979)\Alien.mkv"));
        }

        [Fact]
        public void It_works_with_NO_library_id_configured()
        {
            // §2.3's stated requirement: the exclusion ships BEFORE the family Jellyfin library exists,
            // so it cannot depend on that library's id.
            var exclusion = JellyfinFamilyExclusion.Build(PhotosRootDb, Mappings(), libraryLocations: null);
            Assert.True(exclusion.Configured);
            Assert.True(exclusion.IsExcluded(@"Q:\7 - Family Album\clip.mp4"));
        }

        [Fact]
        public void A_configured_library_location_WIDENS_the_exclusion()
        {
            // A family library whose folders sit outside the configured photo root is still excluded
            // once its own locations are known.
            var exclusion = JellyfinFamilyExclusion.Build(PhotosRootDb, Mappings(),
                new[] { @"\\media\share\9 - Camcorder Tapes" });
            Assert.True(exclusion.IsExcluded(@"Q:\9 - Camcorder Tapes\1998\tape.avi"));
            Assert.True(exclusion.IsExcluded(@"Q:\7 - Family Album\clip.mp4"));
        }

        [Fact]
        public void Nothing_configured_excludes_nothing()
        {
            var exclusion = JellyfinFamilyExclusion.Build(null, Mappings());
            Assert.False(exclusion.Configured);
            Assert.False(exclusion.IsExcluded(@"Q:\7 - Family Album\clip.mp4"));
        }

        [Fact]
        public void A_bare_drive_root_is_REFUSED_rather_than_obeyed()
        {
            // Misconfiguring the photo root as a drive would exclude the entire library and empty the
            // movie site. Silently obeying that is a worse failure than not excluding at all.
            var exclusion = JellyfinFamilyExclusion.Build(@"Q:\", Mappings());
            Assert.False(exclusion.Configured);
            Assert.False(exclusion.IsExcluded(@"Q:\1 - Movies\A\Alien (1979)\Alien.mkv"));
        }

        [Fact]
        public void An_item_with_no_path_is_not_treated_as_family()
        {
            // "Unknown" must not read as "family": the sync already reports pathless items, and
            // silently dropping them would hide real titles.
            var exclusion = JellyfinFamilyExclusion.Build(PhotosRootDb, Mappings());
            Assert.False(exclusion.IsExcluded((string?)null));
            Assert.False(exclusion.IsExcluded(""));
        }

        // ── The sync applies it ─────────────────────────────────────────────────────────────────

        [Fact]
        public async Task The_sync_skips_a_family_item_and_still_matches_the_movie()
        {
            // Two items on the server: a movie the DB tracks, and a family video sitting at a path the
            // DB ALSO tracks (a MediaFile deliberately planted under the photo root, i.e. the exact
            // leak this exists to prevent). The movie must link; the family path must be left entirely
            // alone — no id stamped, and reported as neither matched nor untracked.
            using var fixture = new SyncFixture();
            fixture.SeedMovie(1, "A Movie", DbRoot + @"1 - Movies\A\A Movie (1999)\movie.mkv");
            fixture.SeedMovie(2, "Home Video", PhotosRootDb + @"\Vacation\clip.mp4");

            fixture.Items.Add(Item("movie-item", JellyfinRoot + @"1 - Movies\A\A Movie (1999)\movie.mkv", 100));
            fixture.Items.Add(Item("family-item", JellyfinRoot + @"7 - Family Album\Vacation\clip.mp4", 200));

            var report = await fixture.RunAsync();

            Assert.Null(report.Aborted);
            Assert.Equal(1, report.FamilyItemsExcluded);
            Assert.NotEmpty(report.FamilyExclusionPrefixes);

            using var db = fixture.NewDb();
            var rows = await db.MediaFiles.ToListAsync();
            var movieRow = rows.Single(f => f.Path.EndsWith("movie.mkv", StringComparison.OrdinalIgnoreCase));
            Assert.Equal("movie-item", movieRow.JellyfinItemId);

            // The family path never became a linked, streamable row.
            Assert.DoesNotContain(rows, f => f.JellyfinItemId == "family-item");
            var familyRow = rows.SingleOrDefault(f => f.Path.EndsWith("clip.mp4", StringComparison.OrdinalIgnoreCase));
            Assert.NotNull(familyRow);
            Assert.Null(familyRow!.JellyfinItemId);

            // And it is not offered anywhere in the report either — not as an untracked item a human
            // might be tempted to ingest, and not as an untranslatable path.
            Assert.DoesNotContain(report.Untracked, line => line.Contains("Family Album", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(report.Untranslatable, line => line.Contains("Family Album", StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public async Task A_family_item_can_never_be_repointed_onto_a_movie_row_by_fingerprint()
        {
            // The move-detection pass matches an unmatched DB row to an untracked item by (name, size).
            // A family video that happens to share both with a movie whose file went missing would be
            // silently adopted — the subtlest possible leak, and the one a path-only guard at the
            // matching step would miss. The exclusion removes the item before that pass ever sees it.
            using var fixture = new SyncFixture();
            fixture.SeedMovie(1, "A Movie", DbRoot + @"1 - Movies\A\A Movie (1999)\clip.mp4", sizeBytes: 4242);
            fixture.Items.Add(Item("family-item", JellyfinRoot + @"7 - Family Album\Vacation\clip.mp4", 4242));

            var report = await fixture.RunAsync();

            Assert.Empty(report.Repointed);
            using var db = fixture.NewDb();
            var row = await db.MediaFiles.SingleAsync();
            Assert.Null(row.JellyfinItemId);
            Assert.NotNull(row.MissingSinceUtc);   // correctly missing, rather than wrongly rescued
        }

        [Fact]
        public async Task With_no_photo_root_configured_the_sync_behaves_exactly_as_before()
        {
            // The exclusion must be inert on a host that has no photo collection — otherwise shipping
            // it would change the movie sync for every deployment that never wanted it.
            using var fixture = new SyncFixture(photosRoot: null);
            fixture.SeedMovie(1, "A Movie", DbRoot + @"1 - Movies\A\A Movie (1999)\movie.mkv");
            fixture.Items.Add(Item("movie-item", JellyfinRoot + @"1 - Movies\A\A Movie (1999)\movie.mkv", 100));

            var report = await fixture.RunAsync();

            Assert.Equal(0, report.FamilyItemsExcluded);
            Assert.Empty(report.FamilyExclusionPrefixes);
            Assert.Equal(1, report.MoviesMatched);
        }

        // ── The per-movie re-link (§2.3, the reserved-folder-name trap) ─────────────────────────

        /// <summary>
        /// The re-link's SPECIAL FEATURES sweep goes through the family filter too.
        ///
        /// <para>Special features arrive by item ID from their own Jellyfin call, not from the shelf
        /// listing the rest of the method filters — and the branch that consumes them WRITES a
        /// <c>MediaFile</c> row. §2.3's named trap is exactly this shape: a family folder whose name
        /// collides with a reserved one ("Extras", "Featurettes") is what Jellyfin hands back as a
        /// special feature. One un-filtered list is all it takes for a home video to land in the movie
        /// grid, a channel pool and a recommendation, which is the whole failure the exclusion exists to
        /// make impossible.</para>
        /// </summary>
        [Fact]
        public async Task The_relinks_special_features_sweep_is_family_filtered()
        {
            using var fixture = new SyncFixture();
            var shelfPath = DbRoot + @"1 - Movies\A";
            fixture.SeedMovie(1, "A Movie", shelfPath + @"\A Movie (1999)\old.mkv");

            // The replaced rip, under the shelf — this is what the probe is looking for.
            var newFile = Item("new-item", JellyfinRoot + @"1 - Movies\A\A Movie (1999)\new.mkv", 500);
            fixture.ShelfItems.Add(newFile);
            fixture.ById[newFile.Id] = newFile;

            // Jellyfin ALSO reports two "special features" hanging off it: a genuine featurette, and a
            // family video from a folder whose name collided with a reserved one.
            var featurette = Item("extra-item",
                JellyfinRoot + @"1 - Movies\A\A Movie (1999)\Featurettes\making-of.mkv", 60);
            var familyExtra = Item("family-extra",
                JellyfinRoot + @"7 - Family Album\Featurettes\birthday.mp4", 70);
            fixture.SpecialFeatures[newFile.Id] = new List<JellyfinItem> { featurette, familyExtra };
            fixture.ById[featurette.Id] = featurette;
            fixture.ById[familyExtra.Id] = familyExtra;

            var result = await fixture.RelinkAsync(1, shelfItemId: "shelf-folder");
            Assert.True(result.Done, result.Message);

            using var db = fixture.NewDb();
            var paths = await db.MediaFiles.Select(f => f.Path).ToListAsync();
            // The real featurette landed…
            Assert.Contains(paths, p => p.EndsWith("making-of.mkv", StringComparison.OrdinalIgnoreCase));
            // …and the family video did not, in any form.
            Assert.DoesNotContain(paths, p => p.Contains("Family Album", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(paths, p => p.EndsWith("birthday.mp4", StringComparison.OrdinalIgnoreCase));
        }

        // ── Blast-radius guards ─────────────────────────────────────────────────────────────────

        /// <summary>
        /// An exclusion that would swallow the library ABORTS the sync instead of writing.
        ///
        /// <para><see cref="JellyfinFamilyExclusion.IsMeaningfulRoot"/> refuses the specific volume-root
        /// shape at build time, which is the cause everyone thought of. This refuses the OUTCOME: a root
        /// that is a real folder but happens to sit above most of the library, a Jellyfin-reported
        /// library location that is broader than expected, or any shape nobody predicted. Without it the
        /// run completes, stamps the whole <c>MediaFile</c> table missing, and logs a clean sync — the
        /// site loses its watch buttons and nothing says why.</para>
        /// </summary>
        [Fact]
        public async Task An_exclusion_that_would_swallow_the_library_ABORTS_before_writing()
        {
            // The photo root is misconfigured one level too high, so it covers the movies too. It is a
            // meaningful FOLDER, so the build-time guard cannot see anything wrong with it.
            using var fixture = new SyncFixture(photosRoot: DbRoot + @"1 - Movies");
            for (var i = 1; i <= 40; i++)
            {
                fixture.SeedMovie(i, $"Movie {i}", DbRoot + $@"1 - Movies\A\Movie {i}\movie.mkv");
                fixture.Items.Add(Item($"item-{i}", JellyfinRoot + $@"1 - Movies\A\Movie {i}\movie.mkv", 100 + i));
            }

            var report = await fixture.RunAsync();

            Assert.NotNull(report.Aborted);
            Assert.Contains("Family exclusion", report.Aborted!, StringComparison.Ordinal);
            // Nothing was written: every row still has its (absent) id and no missing stamp.
            using var db = fixture.NewDb();
            Assert.Equal(0, await db.MediaFiles.CountAsync(f => f.MissingSinceUtc != null));
        }

        /// <summary>
        /// A run that would stamp most of the library missing ABORTS instead of writing.
        ///
        /// <para>An unmounted share, a changed path mapping, or a Jellyfin that answered with a partial
        /// library all produce the same thing: nearly every row unmatched. All three look like a
        /// successful sync in the log, and all three take the watch button off most of the site in one
        /// pass. The operator re-runs once the cause is fixed and nothing has to be undone.</para>
        /// </summary>
        [Fact]
        public async Task A_run_that_would_stamp_the_whole_library_missing_ABORTS_before_writing()
        {
            using var fixture = new SyncFixture();
            for (var i = 1; i <= 40; i++)
                fixture.SeedMovie(i, $"Movie {i}", DbRoot + $@"1 - Movies\A\Movie {i}\movie.mkv");
            // Jellyfin answers with ONE item — the shape an unmounted share or a half-finished scan has.
            fixture.Items.Add(Item("item-1", JellyfinRoot + @"1 - Movies\A\Movie 1\movie.mkv", 101));

            var report = await fixture.RunAsync();

            Assert.NotNull(report.Aborted);
            Assert.Contains("missing", report.Aborted!, StringComparison.OrdinalIgnoreCase);
            using var db = fixture.NewDb();
            Assert.Equal(0, await db.MediaFiles.CountAsync(f => f.MissingSinceUtc != null));
        }

        [Fact]
        public async Task An_ordinary_gap_is_still_stamped_missing()
        {
            // The guard must not be a way to stop the sync doing its job: a few genuinely absent files
            // are what MissingSinceUtc is FOR, and a run that refused those would be worse than no guard.
            using var fixture = new SyncFixture();
            for (var i = 1; i <= 40; i++)
            {
                fixture.SeedMovie(i, $"Movie {i}", DbRoot + $@"1 - Movies\A\Movie {i}\movie.mkv");
                if (i > 2) fixture.Items.Add(Item($"item-{i}", JellyfinRoot + $@"1 - Movies\A\Movie {i}\movie.mkv", 100 + i));
            }

            var report = await fixture.RunAsync();

            Assert.Null(report.Aborted);
            using var db = fixture.NewDb();
            Assert.Equal(2, await db.MediaFiles.CountAsync(f => f.MissingSinceUtc != null));
        }

        private static JellyfinItem Item(string id, string path, long size) => new()
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

        // ── Harness ─────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// A whole <see cref="JellyfinSyncService"/> over a SQLite file and a canned Jellyfin. The
        /// server is answered by <see cref="CannedJellyfinHandler"/> — the real
        /// <see cref="JellyfinApi"/> parsing runs, so the paths under test travel the same route they
        /// do in production. <b>Nothing here can reach a network</b>: the handler never delegates.
        /// </summary>
        private sealed class SyncFixture : IDisposable
        {
            private readonly string workDir;
            private readonly DbContextOptions<MovieDb> dbOptions;
            private readonly MovieTheaterConfiguration config;

            public readonly List<JellyfinItem> Items = new();

            /// <summary>What a ParentId-scoped shelf query answers — the re-link probe's own listing.</summary>
            public readonly List<JellyfinItem> ShelfItems = new();

            /// <summary>What <c>/SpecialFeatures</c> answers per item id. The re-link consumes this by ID
            /// rather than from the shelf listing, which is precisely why it needs its own family filter.</summary>
            public readonly Dictionary<string, List<JellyfinItem>> SpecialFeatures = new();

            /// <summary>What an <c>ids=</c> lookup can enrich, by id.</summary>
            public readonly Dictionary<string, JellyfinItem> ById = new();

            public SyncFixture(string? photosRoot = PhotosRootDb)
            {
                workDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "jf-family-tests", Guid.NewGuid().ToString("N"));
                System.IO.Directory.CreateDirectory(workDir);
                dbOptions = new DbContextOptionsBuilder<MovieDb>()
                    .UseSqlite("Data Source=" + System.IO.Path.Combine(workDir, "sync.db") + ";Pooling=False")
                    .Options;
                using var db = new MovieDb(dbOptions);
                db.Database.EnsureCreated();

                config = new MovieTheaterConfiguration(new ConfigurationBuilder().Build())
                {
                    JellyfinBaseUrl = "http://jellyfin.invalid",
                    JellyfinApiKey = "test",
                    JellyfinPathMappings = Mappings(),
                    PhotosLibraryDir = photosRoot,
                };
            }

            public MovieDb NewDb() => new MovieDb(dbOptions);

            public void SeedMovie(int id, string title, string filePath, long? sizeBytes = null)
            {
                using var db = NewDb();
                var playable = new Playable { Kind = PlayableKind.Movie };
                db.Playables.Add(playable);
                db.SaveChanges();
                db.Movies.Add(new Movie { id = id, Title = title, FilePath = filePath, PlayableId = playable.Id });
                db.MediaFiles.Add(new MediaFile
                {
                    PlayableId = playable.Id,
                    Path = filePath,
                    Role = MovieFileRole.Primary,
                    SizeBytes = sizeBytes,
                });
                db.SaveChanges();
            }

            public Task<JellyfinSyncReport> RunAsync() => Service().RunAsync(dryRun: false);

            public Task<MovieRelinkResult> RelinkAsync(int movieId, string? shelfItemId = null) =>
                Service().TryRelinkMovieFilesAsync(movieId, shelfItemId);

            private JellyfinSyncService Service()
            {
                var handler = new CannedJellyfinHandler(Items, ShelfItems, SpecialFeatures, ById);
                var httpClient = new HttpClient(handler) { BaseAddress = new Uri(config.JellyfinBaseUrl!) };
                var api = new JellyfinApi(httpClient, new SingleClientFactory(httpClient),
                    Options.Create(new JellyfinApiOptions { BaseUrl = config.JellyfinBaseUrl, ApiKey = config.JellyfinApiKey }));
                return new JellyfinSyncService(new Factory(dbOptions), api, config,
                    NullLogger<JellyfinSyncService>.Instance);
            }

            public void Dispose()
            {
                // Pooling=False so the temp file unlocks when the context closes. The fixtures used to call the PROCESS-GLOBAL SqliteConnection.ClearAllPools() here, which reached into every OTHER test class running in parallel and closed its pooled connections mid-test
                // an occasional, unreproducible failure somewhere else in the suite.
                try { System.IO.Directory.Delete(workDir, recursive: true); }
                catch (System.IO.IOException) { }
                catch (UnauthorizedAccessException) { }
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

        /// <summary>Answers the handful of Jellyfin routes the sync calls, from an in-memory list.
        /// Anything unrecognized returns an empty result rather than throwing, so a route added to the
        /// sync later shows up as "matched nothing" instead of a confusing transport error.</summary>
        private sealed class CannedJellyfinHandler : HttpMessageHandler
        {
            private readonly List<JellyfinItem> items;
            private readonly List<JellyfinItem> shelfItems;
            private readonly Dictionary<string, List<JellyfinItem>> specialFeatures;
            private readonly Dictionary<string, JellyfinItem> byId;

            public CannedJellyfinHandler(List<JellyfinItem> items, List<JellyfinItem> shelfItems,
                Dictionary<string, List<JellyfinItem>> specialFeatures, Dictionary<string, JellyfinItem> byId)
            {
                this.items = items;
                this.shelfItems = shelfItems;
                this.specialFeatures = specialFeatures;
                this.byId = byId;
            }

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var path = request.RequestUri!.AbsolutePath;
                var query = request.RequestUri.Query;

                string body;
                if (path.Equals("/System/Info", StringComparison.OrdinalIgnoreCase))
                    body = "{\"ServerName\":\"canned\",\"Version\":\"10.0.0\"}";
                else if (path.EndsWith("/SpecialFeatures", StringComparison.OrdinalIgnoreCase))
                    body = SpecialFeaturesBody(path);
                else if (path.Equals("/Items", StringComparison.OrdinalIgnoreCase))
                    body = ItemsBody(query);
                else if (path.Equals("/Users", StringComparison.OrdinalIgnoreCase))
                    body = "[{\"Id\":\"user\"}]";
                else
                    body = "{\"Items\":[],\"TotalRecordCount\":0}";

                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(body, Encoding.UTF8, "application/json"),
                });
            }

            /// <summary>/Users/{user}/Items/{itemId}/SpecialFeatures — a bare ARRAY, as Jellyfin sends it.</summary>
            private string SpecialFeaturesBody(string path)
            {
                var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
                var itemId = parts.Length >= 2 ? parts[^2] : "";
                var list = specialFeatures.TryGetValue(itemId, out var found) ? found : new List<JellyfinItem>();
                return System.Text.Json.JsonSerializer.Serialize(list);
            }

            private string ItemsBody(string query)
            {
                // StartIndex paging: answer the whole set on the first page and nothing after, which is
                // what the real server does for a set this size.
                if (query.Contains("StartIndex=", StringComparison.OrdinalIgnoreCase)
                    && !query.Contains("StartIndex=0", StringComparison.OrdinalIgnoreCase))
                    return "{\"Items\":[],\"TotalRecordCount\":" + items.Count + "}";
                // An ids= lookup: the alternate-version rescue and the re-link's detail enrichment.
                // Answers only what it was asked for, from the id map a test populates.
                if (query.Contains("ids=", StringComparison.OrdinalIgnoreCase))
                {
                    var wanted = Wanted(query, "ids=");
                    var found = wanted.Where(byId.ContainsKey).Select(id => byId[id]).ToList();
                    return System.Text.Json.JsonSerializer.Serialize(new { Items = found, TotalRecordCount = found.Count });
                }
                // A ParentId-scoped shelf listing — the re-link probe's own enumeration.
                if (query.Contains("ParentId=", StringComparison.OrdinalIgnoreCase))
                    return System.Text.Json.JsonSerializer.Serialize(
                        new { Items = shelfItems, TotalRecordCount = shelfItems.Count });

                var payload = System.Text.Json.JsonSerializer.Serialize(new { Items = items, TotalRecordCount = items.Count });
                return payload;
            }

            private static List<string> Wanted(string query, string key)
            {
                var at = query.IndexOf(key, StringComparison.OrdinalIgnoreCase);
                if (at < 0) return new List<string>();
                var rest = query.Substring(at + key.Length);
                var end = rest.IndexOf('&');
                if (end >= 0) rest = rest.Substring(0, end);
                return Uri.UnescapeDataString(rest)
                    .Split(',', StringSplitOptions.RemoveEmptyEntries)
                    .Select(s => s.Trim())
                    .ToList();
            }
        }
    }
}
