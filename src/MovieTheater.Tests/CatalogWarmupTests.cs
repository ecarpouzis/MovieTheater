using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using MovieTheater.Db;
using MovieTheater.Web;
using Xunit;

namespace MovieTheater.Tests
{
    /// <summary>
    /// R9 S7 — the change-driven catalog warmer's two halves that can be judged without warming
    /// anything: the FINGERPRINT (does it move when the catalog moves, and stay still when it does
    /// not) and the GATING DECISION (first pass, change, backstop, minimum interval).
    ///
    /// Nothing here builds an index or touches a cache: a real warm reads the whole library, and the
    /// configured connection string is the live shared database.
    /// </summary>
    public class CatalogWarmupTests : IDisposable
    {
        private readonly string workDir = Path.Combine(Path.GetTempPath(), "mt-warmup-" + Guid.NewGuid().ToString("N"));
        private readonly DbContextOptions<MovieDb> options;

        public CatalogWarmupTests()
        {
            Directory.CreateDirectory(workDir);
            options = new DbContextOptionsBuilder<MovieDb>().UseSqlite("Data Source=" + Path.Combine(workDir, "warm.db")).Options;
            using var db = new MovieDb(options);
            db.Database.EnsureCreated();
            db.Movies.AddRange(
                new Movie { id = 1, Title = "Heat", SimpleTitle = "Heat", UploadedDate = new DateTime(2020, 1, 1) },
                new Movie { id = 2, Title = "Hackers", SimpleTitle = "Hackers", UploadedDate = new DateTime(2021, 6, 1) });
            db.Series.Add(new Series { Id = 100, Title = "Hannibal", SimpleTitle = "Hannibal", UploadedDate = new DateTime(2019, 5, 5) });
            // MiscVideo.PlayableId is a required FK, so a misc row needs its Playable first.
            db.Playables.Add(new Playable { Id = 500 });
            db.MiscVideos.Add(new MiscVideo { Id = 7, Title = "Reel", SimpleTitle = "Reel", PlayableId = 500 });
            db.SaveChanges();
        }

        public void Dispose()
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { Directory.Delete(workDir, recursive: true); } catch (IOException) { }
            GC.SuppressFinalize(this);
        }

        private static readonly CatalogWarmupOptions Opts = new()
        {
            CheckInterval = TimeSpan.FromMinutes(5),
            BackstopTtl = TimeSpan.FromHours(4),
            MinInterval = TimeSpan.FromMinutes(2),
        };

        // ── The fingerprint ────────────────────────────────────────────────────────────────────

        [Fact]
        public async Task Fingerprint_counts_the_catalog_and_is_stable_when_nothing_changes()
        {
            using var db = new MovieDb(options);
            var a = await CatalogFingerprint.ReadAsync(db);
            var b = await CatalogFingerprint.ReadAsync(db);
            Assert.Equal(a, b);
            Assert.Equal(2, a.Movies);
            Assert.Equal(1, a.Series);
            Assert.Equal(1, a.Misc);
            Assert.Equal(new DateTime(2021, 6, 1).Ticks, a.MovieStamp);
        }

        [Fact]
        public async Task Fingerprint_moves_when_a_title_lands_and_when_one_is_quarantined()
        {
            using var db = new MovieDb(options);
            var before = await CatalogFingerprint.ReadAsync(db);

            db.Movies.Add(new Movie { id = 3, Title = "Hausu", SimpleTitle = "Hausu", UploadedDate = new DateTime(2026, 1, 1) });
            await db.SaveChangesAsync();
            var added = await CatalogFingerprint.ReadAsync(db);
            Assert.NotEqual(before, added);
            Assert.Equal(3, added.Movies);
            Assert.Equal(new DateTime(2026, 1, 1).Ticks, added.MovieStamp);

            // A quarantined row is invisible to the browse, so it must be invisible to the fingerprint too.
            db.Movies.Single(m => m.id == 3).ReviewBatch = "batch-1";
            await db.SaveChangesAsync();
            var quarantined = await CatalogFingerprint.ReadAsync(db);
            Assert.Equal(2, quarantined.Movies);
            Assert.NotEqual(added, quarantined);
        }

        [Fact]
        public async Task Fingerprint_moves_when_an_insight_is_generated()
        {
            using var db = new MovieDb(options);
            var before = await CatalogFingerprint.ReadAsync(db);
            db.TitleInsights.Add(new TitleInsight { SubjectKind = InsightSubjectKind.Movie, SubjectId = 1, GeneratedUtc = new DateTime(2026, 2, 2), ModelId = "test" });
            await db.SaveChangesAsync();
            var after = await CatalogFingerprint.ReadAsync(db);
            Assert.NotEqual(before, after);
            Assert.Equal(1, after.Insights);
            Assert.Equal(new DateTime(2026, 2, 2).Ticks, after.InsightStamp);
        }

        // ── The gating decision ────────────────────────────────────────────────────────────────

        private static readonly CatalogFingerprint A = new(10, 2, 1, 5, 3, 100, 200, 300);
        private static readonly CatalogFingerprint B = new(11, 2, 1, 5, 3, 400, 200, 300);
        private static readonly DateTime Now = new(2026, 8, 27, 12, 0, 0, DateTimeKind.Utc);

        [Fact]
        public void First_pass_always_warms()
        {
            var d = CatalogWarmupPlan.Decide(null, A, null, Now, Opts);
            Assert.True(d.Warm);
            Assert.Equal("first pass", d.Reason);
        }

        [Fact]
        public void An_unchanged_catalog_inside_the_backstop_does_not_warm()
        {
            var d = CatalogWarmupPlan.Decide(A, A, Now.AddHours(-1), Now, Opts);
            Assert.False(d.Warm);
            Assert.Equal("unchanged", d.Reason);
        }

        [Fact]
        public void A_changed_fingerprint_warms()
        {
            var d = CatalogWarmupPlan.Decide(A, B, Now.AddHours(-1), Now, Opts);
            Assert.True(d.Warm);
            Assert.Contains("catalog changed", d.Reason);
        }

        [Fact]
        public void The_backstop_warms_an_unchanged_catalog_eventually()
        {
            var d = CatalogWarmupPlan.Decide(A, A, Now.AddHours(-5), Now, Opts);
            Assert.True(d.Warm);
            Assert.Equal("backstop TTL elapsed", d.Reason);
        }

        [Fact]
        public void The_minimum_interval_beats_a_change_so_a_burst_of_marks_cannot_thrash_the_warm()
        {
            // Viewing counts are part of the fingerprint, so ticking through the Rate page moves it
            // once per row; the floor is what keeps that from re-warming the whole index each time.
            var d = CatalogWarmupPlan.Decide(A, B, Now.AddSeconds(-30), Now, Opts);
            Assert.False(d.Warm);
            Assert.Equal("inside the minimum interval", d.Reason);
        }

        [Fact]
        public void Disabled_never_warms_even_on_the_first_pass()
        {
            var d = CatalogWarmupPlan.Decide(null, A, null, Now, new CatalogWarmupOptions { Enabled = false });
            Assert.False(d.Warm);
            Assert.Equal("disabled", d.Reason);
        }

        // ── The keys the warm writes ───────────────────────────────────────────────────────────

        [Fact]
        public void A_warmed_key_is_the_key_an_unfiltered_request_computes()
        {
            var scope = new[] { NormalizedTitleType.Movies };
            var warmed = BrowseCacheKeys.Groups(null, 100, scope, null, null, BrowseFilter.Empty.Sig, userDependent: false, groupBy: "genre");
            // The same scope read by a signed-in viewer at the same age, with no personal-list filter.
            var request = BrowseCacheKeys.Groups(42, 100, scope, null, "", BrowseFilter.Empty.Sig, userDependent: false, groupBy: "genre");
            Assert.Equal(warmed, request);

            // …but a `my=seen` scope IS the caller's, and must not collide with anyone else's.
            var mine = BrowseCacheKeys.Groups(42, 100, scope, null, "", "my=seen", userDependent: true, groupBy: "genre");
            var theirs = BrowseCacheKeys.Groups(43, 100, scope, null, "", "my=seen", userDependent: true, groupBy: "genre");
            Assert.NotEqual(mine, theirs);
            Assert.NotEqual(warmed, mine);
        }

        [Fact]
        public void Facet_keys_are_shared_across_viewers_but_split_by_age_scope_and_text()
        {
            var movies = new[] { NormalizedTitleType.Movies };
            var series = new[] { NormalizedTitleType.Series };
            Assert.Equal(BrowseCacheKeys.Facets(100, movies, null), BrowseCacheKeys.Facets(100, movies, "  "));
            Assert.NotEqual(BrowseCacheKeys.Facets(100, movies, null), BrowseCacheKeys.Facets(13, movies, null));
            Assert.NotEqual(BrowseCacheKeys.Facets(100, movies, null), BrowseCacheKeys.Facets(100, series, null));
            Assert.NotEqual(BrowseCacheKeys.Facets(100, movies, null), BrowseCacheKeys.Facets(100, movies, "heat"));
        }

        [Fact]
        public void The_default_warm_plan_is_bounded_and_never_includes_misc()
        {
            var targets = CatalogWarmupTargets.Default();
            Assert.InRange(targets.Count, 1, 32);
            Assert.All(targets, t => Assert.DoesNotContain(NormalizedTitleType.Misc, t.TypeScope));
            Assert.Equal(2, targets.Count(t => t.IsFacets));
            Assert.Contains(targets, t => t.GroupBy == "franchise");
        }

        [Fact]
        public void The_warm_plan_covers_the_new_cheap_axes_and_never_the_users_own_lists()
        {
            var targets = CatalogWarmupTargets.Default();
            var axes = targets.Where(t => !t.IsFacets).Select(t => t.GroupBy).Distinct().ToList();

            // R9 S8: the axes the Group pill offers that depend on nothing but the age gate.
            foreach (var by in new[] { "genre", "decade", "franchise", "type", "mpa", "director", "subgenre", "mood", "era", "setting" })
                Assert.Contains(by, axes);

            // `my` reads the CALLER's own lists — there is no shared entry to warm, and a warm that
            // wrote one would hand one viewer another viewer's Seen shelf.
            Assert.DoesNotContain("my", axes);
            Assert.All(targets.Where(t => !t.IsFacets), t => Assert.False(BrowseGroups.IsUserDependent(t.GroupBy!)));

            // The core three warm every scope; the rest skip the Series-only copy to keep the byte
            // budget honest (they build on first ask instead).
            var series = new[] { NormalizedTitleType.Series };
            foreach (var by in CatalogWarmupTargets.CoreAxes)
                Assert.Contains(targets, t => t.GroupBy == by && t.TypeScope.SequenceEqual(series));
            foreach (var by in CatalogWarmupTargets.WideAxes)
                Assert.DoesNotContain(targets, t => t.GroupBy == by && t.TypeScope.SequenceEqual(series));
        }
    }
}
