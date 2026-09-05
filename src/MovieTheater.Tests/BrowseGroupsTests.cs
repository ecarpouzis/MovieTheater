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
            options = new DbContextOptionsBuilder<MovieDb>().UseSqlite("Data Source=" + Path.Combine(workDir, "groups.db") + ";Pooling=False").Options;
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
            var action = new Genre { Id = 1, Name = "Action" };
            var drama = new Genre { Id = 2, Name = "Drama" };
            var comedy = new Genre { Id = 3, Name = "Comedy" };
            db.Genres.AddRange(action, drama, comedy);

            // Movies: ids 1..6. Alpha order by SimpleTitle: Alpha(1) Bravo(2) Charlie(3) Delta(4) Echo(5) 9 Lives(6→"#")
            db.Movies.AddRange(
                new Movie { id = 1, Title = "Alpha", SimpleTitle = "Alpha", ReleaseDate = new DateTime(1994, 5, 1), imdbRating = 8.1m, UploadedDate = new DateTime(2026, 1, 1), MpaaRating = "R" },
                new Movie { id = 2, Title = "Bravo", SimpleTitle = "Bravo", ImdbReleaseDate = new DateTime(1999, 5, 1), ImdbRatingScraped = 6.5m, UploadedDate = new DateTime(2026, 1, 3), Rating = "PG" },
                new Movie { id = 3, Title = "Charlie", SimpleTitle = "Charlie", ReleaseDate = new DateTime(2004, 5, 1), UploadedDate = new DateTime(2026, 1, 2), MpaaRatingInferred = "R" },
                new Movie { id = 4, Title = "Delta", SimpleTitle = "Delta", ReleaseDate = new DateTime(2015, 5, 1), imdbRating = 9.0m, MpaaRating = "X" },
                new Movie { id = 5, Title = "Echo", SimpleTitle = "Echo" /* undated, unrated */, imdbRating = 7.0m },
                // A SHORT: NormalizedTitleType is a stored computed column off TitleType, so the seed sets the source.
                new Movie { id = 6, Title = "9 Lives", SimpleTitle = "9 Lives", ReleaseDate = new DateTime(1996, 1, 1), TitleType = TitleType.Short });

            // MPA lookups: the effective bucket is resolved through these, exactly as the age gate resolves it.
            db.RatingMpas.AddRange(
                new RatingMPA { RatingID = 1, MinAge = 0, MPAName = "G" },
                new RatingMPA { RatingID = 2, MinAge = 8, MPAName = "PG" },
                new RatingMPA { RatingID = 3, MinAge = 13, MPAName = "PG-13" },
                new RatingMPA { RatingID = 4, MinAge = 17, MPAName = "R" },
                new RatingMPA { RatingID = 5, MinAge = 18, MPAName = "NC-17" },
                new RatingMPA { RatingID = 6, MinAge = 18, MPAName = "X" });
            db.RatingMaps.AddRange(
                new RatingMap { RatingMapID = 1, MovieRating = "G", MPARatingID = 1 },
                new RatingMap { RatingMapID = 2, MovieRating = "PG", MPARatingID = 2 },
                new RatingMap { RatingMapID = 3, MovieRating = "PG-13", MPARatingID = 3 },
                new RatingMap { RatingMapID = 4, MovieRating = "R", MPARatingID = 4 },
                new RatingMap { RatingMapID = 5, MovieRating = "NC-17", MPARatingID = 5 },
                new RatingMap { RatingMapID = 6, MovieRating = "X", MPARatingID = 6 });

            // Credits: Kubrick directs Alpha + Bravo AND acts in Charlie (an ACTOR credit is not a director shelf).
            db.People.AddRange(
                new Person { Id = 1, DisplayName = "Stanley Kubrick" },
                new Person { Id = 2, DisplayName = "Agnès Varda" });
            db.MovieCredits.AddRange(
                new MovieCredit { Id = 1, MovieID = 1, PersonId = 1, Role = CreditRole.Director },
                new MovieCredit { Id = 2, MovieID = 2, PersonId = 1, Role = CreditRole.Director },
                new MovieCredit { Id = 3, MovieID = 3, PersonId = 1, Role = CreditRole.Actor },
                new MovieCredit { Id = 4, MovieID = 4, PersonId = 2, Role = CreditRole.Director });
            db.SeriesCredits.Add(new SeriesCredit { Id = 1, SeriesId = 100, PersonId = 2, Role = CreditRole.Director });

            // The viewer's own lists: user 42 has seen Alpha and series Foxtrot, wants Bravo, rated Alpha.
            db.Users.AddRange(new User { UserID = 42, Username = "eric" }, new User { UserID = 7, Username = "someone-else" });
            db.Viewings.AddRange(
                new Viewing { ViewingID = 1, UserID = 42, MovieID = 1, ViewingType = "Seen" },
                new Viewing { ViewingID = 2, UserID = 42, SeriesId = 100, ViewingType = "Seen" },
                new Viewing { ViewingID = 3, UserID = 42, MovieID = 2, ViewingType = "WantToWatch" },
                new Viewing { ViewingID = 4, UserID = 42, MovieID = 1, ViewingType = "Rated", ViewingData = "88" },
                new Viewing { ViewingID = 5, UserID = 7, MovieID = 4, ViewingType = "Seen" },
                // …and user 7 suggested Charlie to user 42: a Want on 42's list, placed by 7.
                new Viewing { ViewingID = 6, UserID = 42, MovieID = 3, ViewingType = "WantToWatch", CreatedByUserId = 7 });
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
            var old1 = Ins(1, InsightSubjectKind.Movie, 1, new DateTime(2026, 1, 1), "mcu");
            var new1 = Ins(2, InsightSubjectKind.Movie, 1, new DateTime(2026, 6, 1), "studio-ghibli");
            var m2 = Ins(3, InsightSubjectKind.Movie, 2, new DateTime(2026, 6, 1), "mcu");
            var m4 = Ins(4, InsightSubjectKind.Movie, 4, new DateTime(2026, 6, 1), "mcu", "studio-ghibli");
            var s100 = Ins(5, InsightSubjectKind.Series, 100, new DateTime(2026, 6, 1), "mcu");
            var m3 = Ins(6, InsightSubjectKind.Movie, 3, new DateTime(2026, 6, 1), "lonely");
            // The AI tag axes read the same "newest insight" rule: the SUPERSEDED generation says "sunny",
            // the newest says "cozy", and only the newest may reach a shelf.
            static void Tag(TitleInsight ti, TagCategory c, params string[] values)
            {
                foreach (var v in values) ti.Tags.Add(new TitleTag { Category = c, Value = v, Weight = 5 });
            }
            Tag(old1, TagCategory.Mood, "sunny");
            Tag(new1, TagCategory.Mood, "cozy");
            Tag(new1, TagCategory.Subgenre, "neo-noir");
            Tag(m2, TagCategory.Mood, "cozy");
            Tag(m4, TagCategory.Mood, "bleak");
            Tag(s100, TagCategory.Subgenre, "neo-noir");
            db.TitleInsights.AddRange(old1, new1, m2, m4, s100, m3);
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

        /// <summary>
        /// R9 S8 retired the `letter` axis (the A–Z strip is the letter axis; a shelf per letter drew the
        /// same index twice). The old `LetterHeads_…` test's assertions live on here as the rail rule:
        /// the alphabetical axes get a letter rail, the fixed-order ones deliberately get none.
        /// </summary>
        [Fact]
        public async Task LetterAxisIsGone_AndTheGroupRailOnlyExistsForTheAlphabeticalAxes()
        {
            using var db = new MovieDb(options);
            Assert.Equal("genre", BrowseGroups.NormalizeGroupBy("letter"));

            var genreRail = BrowseGroups.GroupLetters(await BrowseGroups.HeadsAsync(db, db.Movies, db.Series, Misc, "genre"), "genre");
            Assert.Equal(new[] { ("A", 0), ("C", 1), ("D", 2) }, genreRail);

            var directorRail = BrowseGroups.GroupLetters(await BrowseGroups.HeadsAsync(db, db.Movies, db.Series, Misc, "director"), "director");
            Assert.Equal(new[] { ("A", 0), ("S", 1) }, directorRail);

            foreach (var by in new[] { "decade", "type", "mpa", "my" })
                Assert.Empty(BrowseGroups.GroupLetters(new List<BrowseGroups.Head> { new("x", "Anything", 1) }, by));
        }

        [Fact]
        public async Task TypeHeads_AreTheFourScopeBuckets_InScopeOrder_AndMiscJoins()
        {
            using var db = new MovieDb(options);
            var heads = await BrowseGroups.HeadsAsync(db, db.Movies, db.Series, Misc, "type");
            Assert.Equal(new[] { "Movies", "Series", "Short", "Misc" }, heads.Select(h => h.Key));
            Assert.Equal(5, heads[0].Count);  // movies 1–5; "9 Lives" is a Short
            Assert.Equal(2, heads[1].Count);  // series 100, 101
            Assert.Equal(1, heads[2].Count);  // 9 Lives
            Assert.Equal(2, heads[3].Count);  // the two misc rows

            var band = await BrowseGroups.BandAsync(db, db.Movies, db.Series, Misc, "type", new[] { "Short", "Misc" }, "alpha", 0, 48, 0);
            Assert.Equal(new[] { ("movie", 6) }, band.Members["Short"].Select(m => (m.Kind, m.Id)));
            Assert.Equal(new[] { ("misc", 900), ("misc", 901) }, band.Members["Misc"].Select(m => (m.Kind, m.Id)));
        }

        [Fact]
        public async Task DirectorHeads_CountDirectorCreditsOnly_AcrossMoviesAndSeries()
        {
            using var db = new MovieDb(options);
            var heads = await BrowseGroups.HeadsAsync(db, db.Movies, db.Series, Misc, "director");
            Assert.Equal(new[] { "Agnès Varda", "Stanley Kubrick" }, heads.Select(h => h.Key));
            Assert.Equal(2, heads[0].Count); // movie 4 + series 100
            Assert.Equal(2, heads[1].Count); // movies 1, 2 — the ACTOR credit on movie 3 is not a director shelf

            var band = await BrowseGroups.BandAsync(db, db.Movies, db.Series, Misc, "director", new[] { "Agnès Varda" }, "alpha", 0, 48, 0);
            Assert.Equal(new[] { ("movie", 4), ("series", 100) }, band.Members["Agnès Varda"].Select(m => (m.Kind, m.Id)));
        }

        [Fact]
        public async Task MpaHeads_ResolveTheEffectiveBucket_FoldXOntoNC17_AndLeaveUnratedUngrouped()
        {
            using var db = new MovieDb(options);
            var heads = await BrowseGroups.HeadsAsync(db, db.Movies, db.Series, Misc, "mpa");
            // PG (movie 2, via the LEGACY column), R (movie 1 real + movie 3 inferred), NC-17 (movie 4's "X").
            Assert.Equal(new[] { "2", "4", "5" }, heads.Select(h => h.Key));
            Assert.Equal(new[] { "PG", "R", "NC-17" }, heads.Select(h => h.Label));
            Assert.Equal(1, heads[0].Count);
            Assert.Equal(2, heads[1].Count);
            Assert.Equal(1, heads[2].Count);

            // Echo (5), 9 Lives (6) and both series carry no resolvable rating: no shelf, exactly as an
            // undated title has no decade. Nothing is silently filed under a stop the rail cannot drill to.
            var band = await BrowseGroups.BandAsync(db, db.Movies, db.Series, Misc, "mpa", new[] { "4" }, "alpha", 0, 48, 0);
            Assert.Equal(new[] { ("movie", 1), ("movie", 3) }, band.Members["4"].Select(m => (m.Kind, m.Id)));
        }

        [Fact]
        public async Task TagAxes_ReadTheNewestInsightOnly_AndKeepTheirSingletons()
        {
            using var db = new MovieDb(options);
            var moods = await BrowseGroups.HeadsAsync(db, db.Movies, db.Series, Misc, "mood");
            // "sunny" is on movie 1's SUPERSEDED insight and must not appear; "bleak" is a singleton and STAYS
            // (unlike a franchise of one — one film really is that mood).
            Assert.Equal(new[] { "bleak", "cozy" }, moods.Select(h => h.Key));
            Assert.Equal(new[] { "Bleak", "Cozy" }, moods.Select(h => h.Label));
            Assert.Equal(2, moods[1].Count); // movies 1, 2

            var subgenres = await BrowseGroups.HeadsAsync(db, db.Movies, db.Series, Misc, "subgenre");
            Assert.Equal(new[] { "neo-noir" }, subgenres.Select(h => h.Key));
            Assert.Equal("Neo noir", subgenres[0].Label);
            var band = await BrowseGroups.BandAsync(db, db.Movies, db.Series, Misc, "subgenre", new[] { "neo-noir" }, "alpha", 0, 48, 0);
            Assert.Equal(new[] { ("movie", 1), ("series", 100) }, band.Members["neo-noir"].Select(m => (m.Kind, m.Id)));
        }

        [Fact]
        public async Task MyListsHeads_AreTheCallersOwn_InRailOrder_AndEmptyWithoutAViewer()
        {
            using var db = new MovieDb(options);
            var heads = await BrowseGroups.HeadsAsync(db, db.Movies, db.Series, Misc, "my", userId: 42);
            Assert.Equal(new[] { "seen", "want", "rated" }, heads.Select(h => h.Key));
            Assert.Equal(new[] { "Seen", "Want to watch", "Rated" }, heads.Select(h => h.Label));
            Assert.Equal(2, heads[0].Count); // movie 1 + series 100
            Assert.Equal(2, heads[1].Count); // Bravo (own) + Charlie (placed by user 7 — a suggestion is a Want)
            Assert.Equal(1, heads[2].Count);

            var band = await BrowseGroups.BandAsync(db, db.Movies, db.Series, Misc, "my", new[] { "seen" }, "alpha", 0, 48, 0, userId: 42);
            Assert.Equal(new[] { ("movie", 1), ("series", 100) }, band.Members["seen"].Select(m => (m.Kind, m.Id)));

            // Another viewer's rows are not this viewer's, and a signed-out reader has no lists at all.
            var theirs = await BrowseGroups.HeadsAsync(db, db.Movies, db.Series, Misc, "my", userId: 7);
            Assert.Equal(new[] { "seen" }, theirs.Select(h => h.Key));
            Assert.Equal(1, theirs[0].Count);
            Assert.Empty(await BrowseGroups.HeadsAsync(db, db.Movies, db.Series, Misc, "my"));

            // …and the axis says so, so the cache key can carry the user id for it and nothing else.
            Assert.True(BrowseGroups.IsUserDependent("my"));
            foreach (var by in new[] { "genre", "decade", "franchise", "type", "director", "mpa", "mood" })
                Assert.False(BrowseGroups.IsUserDependent(by));
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

        }

        [Fact]
        public void Caps_AndLabels()
        {
            Assert.Equal("genre", BrowseGroups.NormalizeGroupBy(null));
            Assert.Equal("decade", BrowseGroups.NormalizeGroupBy(" Decade "));
            foreach (var by in new[] { "type", "director", "mpa", "my", "subgenre", "mood", "era", "setting" })
                Assert.Equal(by, BrowseGroups.NormalizeGroupBy(by.ToUpperInvariant()));
            Assert.Equal(20, BrowseGroups.CapGroupsTop("genre", 0));
            Assert.Equal(50, BrowseGroups.CapGroupsTop("genre", 999));
            Assert.Equal(12, BrowseGroups.CapGroupsTop("decade", 999));
            Assert.Equal(12, BrowseGroups.CapGroupsTop("type", 0));
            Assert.Equal(12, BrowseGroups.CapGroupsTop("mpa", 999));
            Assert.Equal(50, BrowseGroups.CapGroupsTop("director", 999));
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
