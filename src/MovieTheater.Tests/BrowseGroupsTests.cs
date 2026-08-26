using System;
using System.Collections.Generic;
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
    /// The grouped browse core (Web.BrowseGroups) on a throwaway SQLite catalog: heads per group mode,
    /// the franchise rule (only the NEWEST insight's tags count), letters over the heads, and bands
    /// whose windows never dupe or skip because every order ends in the SimpleTitle/Kind/Id tiebreak.
    /// </summary>
    public class BrowseGroupsTests : IDisposable
    {
        private readonly string workDir = Path.Combine(Path.GetTempPath(), "mt-browse-groups-" + Guid.NewGuid().ToString("N"));
        private readonly DbContextOptions<MovieDb> options;

        public BrowseGroupsTests()
        {
            Directory.CreateDirectory(workDir);
            options = new DbContextOptionsBuilder<MovieDb>().UseSqlite("Data Source=" + Path.Combine(workDir, "groups.db")).Options;
            using var db = new MovieDb(options);
            db.Database.EnsureCreated();
            Seed(db);
        }

        public void Dispose()
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { Directory.Delete(workDir, recursive: true); } catch (IOException) { }
            GC.SuppressFinalize(this);
        }

        private static void Seed(MovieDb db)
        {
            var action = new Genre { Id = 1, Name = "Action" };
            var drama = new Genre { Id = 2, Name = "Drama" };
            var comedy = new Genre { Id = 3, Name = "Comedy" };
            db.Genres.AddRange(action, drama, comedy);

            // Movies: ids 1..6. Alpha order by SimpleTitle: Alpha(1) Bravo(2) Charlie(3) Delta(4) Echo(5) 9 Lives(6→"#")
            db.Movies.AddRange(
                new Movie { id = 1, Title = "Alpha", SimpleTitle = "Alpha", ReleaseDate = new DateTime(1994, 5, 1), imdbRating = 8.1m, UploadedDate = new DateTime(2026, 1, 1) },
                new Movie { id = 2, Title = "Bravo", SimpleTitle = "Bravo", ImdbReleaseDate = new DateTime(1999, 5, 1), ImdbRatingScraped = 6.5m, UploadedDate = new DateTime(2026, 1, 3) },
                new Movie { id = 3, Title = "Charlie", SimpleTitle = "Charlie", ReleaseDate = new DateTime(2004, 5, 1), UploadedDate = new DateTime(2026, 1, 2) },
                new Movie { id = 4, Title = "Delta", SimpleTitle = "Delta", ReleaseDate = new DateTime(2015, 5, 1), imdbRating = 9.0m },
                new Movie { id = 5, Title = "Echo", SimpleTitle = "Echo" /* undated */, imdbRating = 7.0m },
                new Movie { id = 6, Title = "9 Lives", SimpleTitle = "9 Lives", ReleaseDate = new DateTime(1996, 1, 1) });
            db.MovieGenres.AddRange(
                new MovieGenre { MovieID = 1, GenreId = 1 }, new MovieGenre { MovieID = 1, GenreId = 2 },
                new MovieGenre { MovieID = 2, GenreId = 1 },
                new MovieGenre { MovieID = 3, GenreId = 2 },
                new MovieGenre { MovieID = 4, GenreId = 1 },
                new MovieGenre { MovieID = 5, GenreId = 3 });

            // Series: ids 100, 101. One dated by StartYear, one by ReleaseDate.
            db.Series.AddRange(
                new Series { Id = 100, Title = "Foxtrot", SimpleTitle = "Foxtrot", StartYear = 1998, imdbRating = 8.8m },
                new Series { Id = 101, Title = "Golf", SimpleTitle = "Golf", ReleaseDate = new DateTime(2019, 1, 1) });
            db.SeriesGenres.AddRange(new SeriesGenre { SeriesId = 100, GenreId = 2 }, new SeriesGenre { SeriesId = 101, GenreId = 1 });

            // Insights: movie 1 has two generations — the OLD one says "mcu", the NEW one says "studio-ghibli".
            // Movies 2, 4 + series 100 carry "mcu" on their newest insight; movie 3 carries "lonely" alone.
            TitleInsight Ins(int id, InsightSubjectKind kind, int subject, DateTime when, params string[] franchises)
            {
                var ti = new TitleInsight { Id = id, SubjectKind = kind, SubjectId = subject, ModelId = "test", GeneratedUtc = when, SpecVersion = 1, Recognized = true };
                foreach (var f in franchises) ti.Tags.Add(new TitleTag { Category = TagCategory.Franchise, Value = f, Weight = 5 });
                return ti;
            }
            db.TitleInsights.AddRange(
                Ins(1, InsightSubjectKind.Movie, 1, new DateTime(2026, 1, 1), "mcu"),
                Ins(2, InsightSubjectKind.Movie, 1, new DateTime(2026, 6, 1), "studio-ghibli"),
                Ins(3, InsightSubjectKind.Movie, 2, new DateTime(2026, 6, 1), "mcu"),
                Ins(4, InsightSubjectKind.Movie, 4, new DateTime(2026, 6, 1), "mcu", "studio-ghibli"),
                Ins(5, InsightSubjectKind.Series, 100, new DateTime(2026, 6, 1), "mcu"),
                Ins(6, InsightSubjectKind.Movie, 3, new DateTime(2026, 6, 1), "lonely"));
            db.SaveChanges();
        }

        private static readonly List<BrowseGroups.MiscLight> Misc = new()
        {
            new BrowseGroups.MiscLight(900, "Hotel", "Hotel", 1995),
            new BrowseGroups.MiscLight(901, null, "India", null),
        };

        [Fact]
        public async Task GenreHeads_MergeMoviesAndSeries_AZ()
        {
            using var db = new MovieDb(options);
            var heads = await BrowseGroups.HeadsAsync(db, db.Movies, db.Series, Misc, "genre");
            Assert.Equal(new[] { "Action", "Comedy", "Drama" }, heads.Select(h => h.Key));
            Assert.Equal(4, heads[0].Count); // movies 1, 2, 4 + series 101
            Assert.Equal(1, heads[1].Count);
            Assert.Equal(3, heads[2].Count); // movies 1, 3 + series 100
        }

        [Fact]
        public async Task DecadeHeads_UseReleaseThenImdbThenStartYear_NewestFirst_MiscJoins_UndatedDropped()
        {
            using var db = new MovieDb(options);
            var heads = await BrowseGroups.HeadsAsync(db, db.Movies, db.Series, Misc, "decade");
            Assert.Equal(new[] { "2010", "2000", "1990" }, heads.Select(h => h.Key));
            Assert.Equal("2010s", heads[0].Label);
            Assert.Equal(2, heads[0].Count); // movie 4 (2015), series 101 (2019)
            Assert.Equal(1, heads[1].Count); // movie 3
            Assert.Equal(5, heads[2].Count); // movies 1, 2, 6 + series 100 (StartYear 1998) + misc 900 (1995); Echo and misc 901 are undated
        }

        [Fact]
        public async Task FranchiseHeads_CountOnlyTheNewestInsight_AndDropSingletons()
        {
            using var db = new MovieDb(options);
            var heads = await BrowseGroups.HeadsAsync(db, db.Movies, db.Series, Misc, "franchise");
            // mcu: movies 2, 4 + series 100 (movie 1's mcu tag is on a SUPERSEDED insight); studio-ghibli: movies 1, 4; lonely: one member → dropped
            Assert.Equal(new[] { "mcu", "studio-ghibli" }, heads.Select(h => h.Key));
            Assert.Equal("MCU", heads[0].Label);
            Assert.Equal("Studio Ghibli", heads[1].Label);
            Assert.Equal(3, heads[0].Count);
            Assert.Equal(2, heads[1].Count);

            var band = await BrowseGroups.BandAsync(db, db.Movies, db.Series, Misc, "franchise", new[] { "mcu", "studio-ghibli" }, "alpha", 0, 48, 0);
            Assert.Equal(new[] { ("movie", 2), ("movie", 4), ("series", 100) }, band.Members["mcu"].Select(m => (m.Kind, m.Id)));
            Assert.Equal(new[] { ("movie", 1), ("movie", 4) }, band.Members["studio-ghibli"].Select(m => (m.Kind, m.Id)));
        }

        [Fact]
        public async Task LetterHeads_BucketEverythingInScope_HashFirst_AndTheRailFollowsTheHeads()
        {
            using var db = new MovieDb(options);
            var heads = await BrowseGroups.HeadsAsync(db, db.Movies, db.Series, Misc, "letter");
            Assert.Equal(new[] { "#", "A", "B", "C", "D", "E", "F", "G", "H", "I" }, heads.Select(h => h.Key));
            Assert.Equal(1, heads[0].Count); // "9 Lives"
            var rail = BrowseGroups.GroupLetters(heads, "letter");
            Assert.Equal(("#", 0), rail[0]);
            Assert.Equal(("D", 4), rail[4]);

            var genreRail = BrowseGroups.GroupLetters(await BrowseGroups.HeadsAsync(db, db.Movies, db.Series, Misc, "genre"), "genre");
            Assert.Equal(new[] { ("A", 0), ("C", 1), ("D", 2) }, genreRail);
            Assert.Empty(BrowseGroups.GroupLetters(new List<BrowseGroups.Head> { new("1990", "1990s", 1) }, "decade"));
        }

        [Fact]
        public async Task Bands_AreWindowedWithAStableTiebreak_AndHonourTheSort()
        {
            using var db = new MovieDb(options);
            var all = await BrowseGroups.BandAsync(db, db.Movies, db.Series, Misc, "genre", new[] { "Action" }, "alpha", 0, 48, 0);
            Assert.Equal(new[] { ("movie", 1), ("movie", 2), ("movie", 4), ("series", 101) }, all.Members["Action"].Select(m => (m.Kind, m.Id)));

            // Windows of 3 then the rest: together they are exactly the full order, no dupes, no skips.
            var w1 = await BrowseGroups.BandAsync(db, db.Movies, db.Series, Misc, "genre", new[] { "Action" }, "alpha", 0, 3, 0);
            var w2 = await BrowseGroups.BandAsync(db, db.Movies, db.Series, Misc, "genre", new[] { "Action" }, "alpha", 0, 3, 3);
            Assert.Equal(all.Members["Action"], w1.Members["Action"].Concat(w2.Members["Action"]));

            // IMDb desc, unscored last, alpha inside ties: Delta 9.0, Alpha 8.1, Bravo 6.5, Golf (unscored)
            var imdb = await BrowseGroups.BandAsync(db, db.Movies, db.Series, Misc, "genre", new[] { "Action" }, "imdb", 0, 48, 0);
            Assert.Equal(new[] { 4, 1, 2, 101 }, imdb.Members["Action"].Select(m => m.Id));

            // Recently added desc: Bravo (Jan 3), Charlie is Drama; Alpha (Jan 1); undated last
            var added = await BrowseGroups.BandAsync(db, db.Movies, db.Series, Misc, "genre", new[] { "Action" }, "added", 0, 48, 0);
            Assert.Equal(new[] { 2, 1, 4, 101 }, added.Members["Action"].Select(m => m.Id));

            // Random is a seeded order: the same seed gives the same band; a different seed a permutation.
            var r1 = await BrowseGroups.BandAsync(db, db.Movies, db.Series, Misc, "genre", new[] { "Action" }, "random", 7, 48, 0);
            var r2 = await BrowseGroups.BandAsync(db, db.Movies, db.Series, Misc, "genre", new[] { "Action" }, "random", 7, 48, 0);
            Assert.Equal(r1.Members["Action"], r2.Members["Action"]);
            Assert.Equal(4, r1.Members["Action"].Count);
        }

        [Fact]
        public async Task DecadeAndLetterBands_IncludeMisc_InTheSameOrder()
        {
            using var db = new MovieDb(options);
            var nineties = await BrowseGroups.BandAsync(db, db.Movies, db.Series, Misc, "decade", new[] { "1990" }, "alpha", 0, 48, 0);
            // 9 Lives (#… "9 Lives" sorts first), Alpha, Bravo, Foxtrot, Hotel(misc)
            Assert.Equal(new[] { ("movie", 6), ("movie", 1), ("movie", 2), ("series", 100), ("misc", 900) }, nineties.Members["1990"].Select(m => (m.Kind, m.Id)));

            var h = await BrowseGroups.BandAsync(db, db.Movies, db.Series, Misc, "letter", new[] { "H", "#" }, "alpha", 0, 48, 0);
            Assert.Equal(new[] { ("misc", 900) }, h.Members["H"].Select(m => (m.Kind, m.Id)));
            Assert.Equal(new[] { ("movie", 6) }, h.Members["#"].Select(m => (m.Kind, m.Id)));
        }

        [Fact]
        public void Caps_AndLabels()
        {
            Assert.Equal("genre", BrowseGroups.NormalizeGroupBy(null));
            Assert.Equal("decade", BrowseGroups.NormalizeGroupBy(" Decade "));
            Assert.Equal(20, BrowseGroups.CapGroupsTop("genre", 0));
            Assert.Equal(50, BrowseGroups.CapGroupsTop("genre", 999));
            Assert.Equal(12, BrowseGroups.CapGroupsTop("decade", 999));
            Assert.Equal(12, BrowseGroups.CapGroupsTop("letter", 0));
            Assert.Equal(48, BrowseGroups.CapPerGroupTop(0));
            Assert.Equal(500, BrowseGroups.CapPerGroupTop(5000));
            Assert.Equal("Star Wars", BrowseGroups.FranchiseLabel("star-wars"));
            Assert.Equal("A Nightmare On Elm Street", BrowseGroups.FranchiseLabel("a nightmare on elm street")); // the tags in the live library are spaced phrases
            Assert.Equal("DCU", BrowseGroups.FranchiseLabel("dcu"));
            Assert.Equal("Bond", BrowseGroups.FranchiseLabel("bond"));
            Assert.Equal("1980s", BrowseGroups.DecadeLabel(BrowseGroups.DecadeKey(1987)));
        }
    }
}
