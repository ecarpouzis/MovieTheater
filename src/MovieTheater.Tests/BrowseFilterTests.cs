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
    /// R9 S2: the combinable browse filter behind the Movies/TV facet rail. Includes AND, excludes NOT,
    /// across genre / franchise / AI tags (newest insight only) / people / years / the viewer's lists /
    /// the text; and the facet counts over a scope.
    /// </summary>
    public class BrowseFilterTests : IDisposable
    {
        private readonly string workDir = Path.Combine(Path.GetTempPath(), "mt-browse-filter-" + Guid.NewGuid().ToString("N"));
        private readonly DbContextOptions<MovieDb> options;

        public BrowseFilterTests()
        {
            Directory.CreateDirectory(workDir);
            options = new DbContextOptionsBuilder<MovieDb>().UseSqlite("Data Source=" + Path.Combine(workDir, "filter.db") + ";Pooling=False").Options;
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
            var crime = new Genre { Id = 1, Name = "Crime" };
            var drama = new Genre { Id = 2, Name = "Drama" };
            var horror = new Genre { Id = 3, Name = "Horror" };
            db.Genres.AddRange(crime, drama, horror);

            db.Movies.AddRange(
                new Movie { id = 1, Title = "Heat", SimpleTitle = "Heat", ReleaseDate = new DateTime(1995, 12, 15) },
                new Movie { id = 2, Title = "Hackers", SimpleTitle = "Hackers", ReleaseDate = new DateTime(1995, 9, 15) },
                // Halloween carries its crew only in the LEGACY string column — the credit tables never
                // got it. Both people legs (`q` and `person:`) have to read it, so one fixture does.
                new Movie { id = 3, Title = "Halloween", SimpleTitle = "Halloween", Director = "John Carpenter", ReleaseDate = new DateTime(1978, 10, 25) },
                new Movie { id = 4, Title = "Heathers", SimpleTitle = "Heathers", ImdbReleaseDate = new DateTime(1988, 3, 31) },
                new Movie { id = 5, Title = "Hausu", SimpleTitle = "Hausu" /* undated */ });
            db.MovieGenres.AddRange(
                new MovieGenre { MovieID = 1, GenreId = 1 }, new MovieGenre { MovieID = 1, GenreId = 2 },
                new MovieGenre { MovieID = 2, GenreId = 1 },
                new MovieGenre { MovieID = 3, GenreId = 3 },
                new MovieGenre { MovieID = 4, GenreId = 2 },
                new MovieGenre { MovieID = 5, GenreId = 3 });

            db.Series.AddRange(
                new Series { Id = 100, Title = "Hannibal", SimpleTitle = "Hannibal", StartYear = 2013 },
                new Series { Id = 101, Title = "Happy Valley", SimpleTitle = "Happy Valley", ReleaseDate = new DateTime(2014, 1, 1) });
            db.SeriesGenres.AddRange(new SeriesGenre { SeriesId = 100, GenreId = 1 }, new SeriesGenre { SeriesId = 100, GenreId = 3 }, new SeriesGenre { SeriesId = 101, GenreId = 1 });

            var pacino = new Person { Id = 1, DisplayName = "Al Pacino" };
            var deniro = new Person { Id = 2, DisplayName = "Robert De Niro" };
            var mann = new Person { Id = 3, DisplayName = "Michael Mann" };
            db.People.AddRange(pacino, deniro, mann);
            db.MovieCredits.AddRange(
                new MovieCredit { Id = 1, MovieID = 1, PersonId = 1, Role = CreditRole.Actor, Ordering = 0 },
                new MovieCredit { Id = 2, MovieID = 1, PersonId = 2, Role = CreditRole.Actor, Ordering = 1 },
                new MovieCredit { Id = 3, MovieID = 1, PersonId = 3, Role = CreditRole.Director, Ordering = 0 });
            db.SeriesCredits.Add(new SeriesCredit { Id = 1, SeriesId = 100, PersonId = 1, Role = CreditRole.Actor, Ordering = 0 });

            TitleInsight Ins(int id, InsightSubjectKind kind, int subject, DateTime when, params (TagCategory, string)[] tags)
            {
                var ti = new TitleInsight { Id = id, SubjectKind = kind, SubjectId = subject, ModelId = "test", GeneratedUtc = when, SpecVersion = 1, Recognized = true };
                foreach (var (c, v) in tags) ti.Tags.Add(new TitleTag { Category = c, Value = v, Weight = 5 });
                return ti;
            }
            db.TitleInsights.AddRange(
                // Heat: an OLD insight said "cozy"; the newest says "tense" + heist — only the newest counts.
                Ins(1, InsightSubjectKind.Movie, 1, new DateTime(2026, 1, 1), (TagCategory.Mood, "cozy")),
                Ins(2, InsightSubjectKind.Movie, 1, new DateTime(2026, 6, 1), (TagCategory.Mood, "tense"), (TagCategory.Subgenre, "heist"), (TagCategory.Franchise, "mann-verse")),
                Ins(3, InsightSubjectKind.Movie, 2, new DateTime(2026, 6, 1), (TagCategory.Mood, "playful"), (TagCategory.Subgenre, "heist")),
                Ins(4, InsightSubjectKind.Movie, 3, new DateTime(2026, 6, 1), (TagCategory.Subgenre, "slasher"), (TagCategory.Franchise, "halloween")),
                Ins(5, InsightSubjectKind.Movie, 5, new DateTime(2026, 6, 1), (TagCategory.Franchise, "halloween")),
                Ins(6, InsightSubjectKind.Series, 100, new DateTime(2026, 6, 1), (TagCategory.Mood, "tense"), (TagCategory.Franchise, "mann-verse")));

            db.Users.AddRange(new User { UserID = 7, Username = "seven" }, new User { UserID = 8, Username = "eight" });
            db.Viewings.AddRange(
                new Viewing { ViewingID = 1, UserID = 7, MovieID = 1, ViewingType = "Seen" },
                new Viewing { ViewingID = 2, UserID = 7, MovieID = 2, ViewingType = "WantToWatch" },
                new Viewing { ViewingID = 3, UserID = 7, SeriesId = 100, ViewingType = "Seen" },
                new Viewing { ViewingID = 4, UserID = 8, MovieID = 3, ViewingType = "Seen" },
                // user 8 suggested Heathers to user 7 — a Want on 7's list, placed by 8.
                new Viewing { ViewingID = 5, UserID = 7, MovieID = 4, ViewingType = "WantToWatch", CreatedByUserId = 8 });
            db.SaveChanges();
        }

        private static BrowseFilter F(Action<BrowseFilterQuery> set)
        {
            var q = new BrowseFilterQuery();
            set(q);
            return BrowseFilter.From(q);
        }

        /// <summary>The filtered set as one comparable signature: "m:1,2|s:100".</summary>
        private static string Ex(int[] movies, int[] series) => $"m:{string.Join(",", movies)}|s:{string.Join(",", series)}";

        private async Task<string> RunAsync(BrowseFilter f, int? userId = null)
        {
            using var db = new MovieDb(options);
            var (mq, sq) = BrowseFilter.Apply(db, db.Movies, db.Series, f, userId);
            return Ex((await mq.Select(m => m.id).OrderBy(x => x).ToListAsync()).ToArray(), (await sq.Select(s => s.Id).OrderBy(x => x).ToListAsync()).ToArray());
        }

        [Fact]
        public void Parsing_is_tolerant_and_canonical()
        {
            var f = F(q => { q.genre = new[] { " Crime ", "Drama", "crime" }; q.tag = new[] { "mood:tense", "bogus:x", "mood:" }; q.mpa = "5,junk,3"; q.my = "SEEN"; q.yearMin = 0; q.yearMax = 1999; });
            Assert.Equal(new[] { "Crime", "Drama" }, f.Genres);
            Assert.Single(f.Tags);
            Assert.Equal((TagCategory.Mood, "tense"), f.Tags[0]);
            Assert.Equal(new[] { 5, 3, 6 }.OrderBy(x => x), f.Mpa.OrderBy(x => x)); // NC-17 pulls X along
            Assert.Equal(new[] { "seen" }, f.My);
            Assert.Null(f.YearMin);
            Assert.Equal(1999, f.YearMax);
            Assert.False(f.IsEmpty);
            Assert.True(f.HasFacets);
            Assert.Equal(f.Sig, F(q => { q.genre = new[] { "drama", "CRIME" }; q.tag = new[] { "mood:tense" }; q.mpa = "3,5"; q.my = "seen"; q.yearMax = 1999; }).Sig);
            Assert.True(BrowseFilter.From(null).IsEmpty);
            Assert.False(F(q => q.q = "heat").HasFacets);
        }

        [Fact]
        public async Task Includes_and_excludes_compose_across_genres_and_the_text()
        {
            Assert.Equal(Ex(new int[] { 1, 2 }, new int[] { 100, 101 }), await RunAsync(F(q => q.genre = new[] { "Crime" })));
            Assert.Equal(Ex(new int[] { 1 }, new int[] {  }), await RunAsync(F(q => q.genre = new[] { "Crime", "Drama" })));
            Assert.Equal(Ex(new int[] { 1, 2 }, new int[] { 101 }), await RunAsync(F(q => { q.genre = new[] { "Crime" }; q.exGenre = new[] { "Horror" }; })));
            Assert.Equal(Ex(new int[] { 1 }, new int[] {  }), await RunAsync(F(q => { q.q = "Hea"; q.genre = new[] { "Crime" }; })));
        }

        [Fact]
        public async Task The_text_reaches_people_not_only_titles()
        {
            // The search box offers this as "in all fields". It read the two title columns only, so a
            // search for an actor was a dead end (Eric, 2026-09-03: "when I search for Tom Hanks it
            // doesn't work"). No title here contains "Pacino"; Heat and Hannibal are his credits.
            Assert.Equal(Ex(new int[] { 1 }, new int[] { 100 }), await RunAsync(F(q => q.q = "Pacino")));
            Assert.Equal(Ex(new int[] { 1 }, new int[] {  }), await RunAsync(F(q => q.q = "Michael Mann")));
            // A title hit and a credit hit are the same row set as before, OR'd — "Hea" still finds the
            // three H-titles, and a term that is both keeps both.
            Assert.Equal(Ex(new int[] { 1, 4 }, new int[] {  }), await RunAsync(F(q => q.q = "Hea")));
            // Legacy titles whose credits never made it into MovieCredit still answer, via the string columns.
            Assert.Equal(Ex(new int[] { 3 }, new int[] {  }), await RunAsync(F(q => q.q = "Carpenter")));
            // The people leg never widens a miss into a hit.
            Assert.Equal(Ex(new int[] {  }, new int[] {  }), await RunAsync(F(q => q.q = "Nobody At All")));
        }

        [Fact]
        public async Task Tags_and_franchises_read_the_newest_insight_only()
        {
            // "cozy" was on Heat's superseded insight: nothing carries it now.
            Assert.Equal(Ex(new int[] {  }, new int[] {  }), await RunAsync(F(q => q.tag = new[] { "mood:cozy" })));
            Assert.Equal(Ex(new int[] { 1 }, new int[] { 100 }), await RunAsync(F(q => q.tag = new[] { "mood:tense" })));
            Assert.Equal(Ex(new int[] { 1, 2 }, new int[] {  }), await RunAsync(F(q => q.tag = new[] { "subgenre:heist" })));
            Assert.Equal(Ex(new int[] { 2 }, new int[] {  }), await RunAsync(F(q => { q.tag = new[] { "subgenre:heist" }; q.exTag = new[] { "mood:tense" }; })));
            Assert.Equal(Ex(new int[] { 3, 5 }, new int[] {  }), await RunAsync(F(q => q.franchise = new[] { "halloween" })));
            Assert.Equal(Ex(new int[] { 1, 2, 4 }, new int[] { 100, 101 }), await RunAsync(F(q => q.exFranchise = new[] { "halloween" })));
        }

        [Fact]
        public async Task People_years_and_the_viewers_lists()
        {
            Assert.Equal(Ex(new int[] { 1 }, new int[] { 100 }), await RunAsync(F(q => q.person = new[] { "Al Pacino" })));
            Assert.Equal(Ex(new int[] { 1 }, new int[] {  }), await RunAsync(F(q => q.person = new[] { "Pacino", "Mann" })));
            Assert.Equal(Ex(new int[] { 2, 3, 4, 5 }, new int[] { 101 }), await RunAsync(F(q => q.exPerson = new[] { "Pacino" })));
            // years: ReleaseDate, else ImdbReleaseDate; series by StartYear, else ReleaseDate; undated titles fall out of any range
            Assert.Equal(Ex(new int[] { 1, 2, 4 }, new int[] {  }), await RunAsync(F(q => { q.yearMin = 1980; q.yearMax = 1999; })));
            Assert.Equal(Ex(new int[] {  }, new int[] { 100, 101 }), await RunAsync(F(q => q.yearMin = 2010)));
            // the viewer's own lists: seen / want, per user; no user ⇒ nothing
            Assert.Equal(Ex(new int[] { 1 }, new int[] { 100 }), await RunAsync(F(q => q.my = "seen"), userId: 7));
            // 7's Want list holds their own pick (2) AND the one a friend placed (4): a suggestion is a Want.
            Assert.Equal(Ex(new int[] { 2, 4 }, new int[] {  }), await RunAsync(F(q => q.my = "want"), userId: 7));
            Assert.Equal(Ex(new int[] { 3 }, new int[] {  }), await RunAsync(F(q => q.my = "seen"), userId: 8));
            Assert.Equal(Ex(new int[] {  }, new int[] {  }), await RunAsync(F(q => q.my = "seen"), userId: null));
            // The user id passed in is the list OWNER — what `for=` resolves to — so "8 browsing 7's list"
            // is userId: 7; the placer's own Want list does not carry what they placed elsewhere.
            Assert.Equal(Ex(new int[] {  }, new int[] {  }), await RunAsync(F(q => q.my = "want"), userId: 8));
            Assert.Equal(new[] { "want" }, F(q => q.my = "Want,suggested,bogus").My);
        }

        [Fact]
        public async Task Facet_counts_describe_the_scope()
        {
            using var db = new MovieDb(options);
            var c = await BrowseFilter.CountAsync(db, db.Movies, db.Series, miscCount: 2);
            Assert.Equal(9, c.Total);
            Assert.Equal(new[] { ("Movies", 5), ("Series", 2), ("Misc", 2) }, c.Types.Select(t => (t.Value, t.Count)));
            Assert.Equal(new[] { ("Crime", 4), ("Horror", 3), ("Drama", 2) }, c.Genres.Select(g => (g.Value, g.Count)));
            // franchises of one are not offered; labels are titled
            Assert.Equal(new[] { ("halloween", "Halloween", 2), ("mann-verse", "Mann Verse", 2) }, c.Franchises.Select(f => (f.Value, f.Label, f.Count)));
            Assert.Equal(new[] { ("tense", 2), ("playful", 1) }, c.Tags["mood"].Select(t => (t.Value, t.Count)));
            Assert.Equal(new[] { ("heist", 2), ("slasher", 1) }, c.Tags["subgenre"].Select(t => (t.Value, t.Count)));
            Assert.DoesNotContain(c.Tags["mood"], t => t.Value == "cozy");
            Assert.Equal(new[] { ("1970", 1), ("1980", 1), ("1990", 2), ("2010", 2) }, c.Decades.Select(d => (d.Value, d.Count)));
            Assert.Equal("Post apocalypse", BrowseFilter.Humanize("post-apocalypse"));
        }
    }
}
