using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using MovieTheater.Db;
using MovieTheater.Requests;
using Xunit;

namespace MovieTheater.Tests
{
    /// <summary>
    /// The request queue's loose resolver (2026-10-06): the normalization it compares on, the agreement
    /// rule, the year rule, and the lookup + sweep over a small SQLite catalog.
    /// </summary>
    public class ContentRequestMatcherTests : IDisposable
    {
        private readonly string workDir = Path.Combine(Path.GetTempPath(), "mt-requests-" + Guid.NewGuid().ToString("N"));
        private readonly DbContextOptions<MovieDb> options;

        public ContentRequestMatcherTests()
        {
            Directory.CreateDirectory(workDir);
            options = new DbContextOptionsBuilder<MovieDb>().UseSqlite("Data Source=" + Path.Combine(workDir, "req.db") + ";Pooling=False").Options;
            using var db = new MovieDb(options);
            db.Database.EnsureCreated();
            Seed(db);
        }

        public void Dispose()
        {
            try { Directory.Delete(workDir, recursive: true); } catch (IOException) { }
            GC.SuppressFinalize(this);
        }

        private static void Seed(MovieDb db)
        {
            db.Users.Add(new User { UserID = 1, Username = "eric" });
            // Every movie/series we "have" carries a playable with a present file; the one with a MISSING
            // file (Heat) and the one with no file at all (Ronin, a watched-list-only row) must never match.
            void Movie(int id, string title, string simple, DateTime release, bool? present = true)
            {
                var m = new Movie { id = id, Title = title, SimpleTitle = simple, ReleaseDate = release };
                if (present != null)
                {
                    var p = new Playable { Id = 100 + id, Kind = PlayableKind.Movie };
                    db.Playables.Add(p);
                    db.MediaFiles.Add(new MediaFile { PlayableId = p.Id, Path = title + ".mkv", Role = MovieFileRole.Primary, MissingSinceUtc = present == true ? null : DateTime.UtcNow });
                    m.PlayableId = p.Id;
                }
                db.Movies.Add(m);
            }
            Movie(10, "Dune", "Dune 1", new DateTime(1984, 12, 14));
            Movie(11, "Dune: Part Two", "Dune 3", new DateTime(2024, 3, 1));
            Movie(12, "The Thing", "Thing", new DateTime(1982, 6, 25));
            Movie(13, "It", "It", new DateTime(2017, 9, 8));
            Movie(14, "Assassin's Creed", "Assassins Creed", new DateTime(2016, 12, 21));
            Movie(15, "Heat", "Heat", new DateTime(1995, 12, 15), present: false);
            Movie(16, "Ronin", "Ronin", new DateTime(1998, 9, 25), present: null);
            db.Series.Add(new Series { Id = 20, Title = "The Expanse", SimpleTitle = "Expanse", StartYear = 2015 });
            var ep = new Playable { Id = 220, Kind = PlayableKind.Episode };
            db.Playables.Add(ep);
            db.MediaFiles.Add(new MediaFile { PlayableId = 220, Path = "expanse-s01e01.mkv", Role = MovieFileRole.Primary });
            db.Episodes.Add(new Episode { Id = 200, SeriesId = 20, SeasonNumber = 1, EpisodeNumber = 1, PlayableId = 220 });
            db.Series.Add(new Series { Id = 21, Title = "Firefly", SimpleTitle = "Firefly", StartYear = 2002 });
            var artist = new MusicArtist { Id = 30, Name = "Bush", SortName = "Bush", FolderName = "Bush" };
            db.MusicArtists.Add(artist);
            db.MusicAlbums.Add(new MusicAlbum { Id = 31, ArtistId = 30, Title = "Sixteen Stone", Year = 1994, FolderPath = "Bush/Bush - Sixteen Stone (1994)" });
            db.Boardgames.Add(new Boardgame { id = 40, Name = "Catan: Seafarers", YearPublished = 1997, LastSyncedUtc = DateTime.UtcNow });
            db.ArcadeGames.Add(new ArcadeGame { Id = 50, Title = "Chrono Trigger", SortTitle = "Chrono Trigger", System = "snes", Year = 1995, RomPath = "snes/Chrono Trigger.sfc", CloudRetroGameKey = "snes/Chrono Trigger.sfc", CollapseKey = "chronotrigger" });
            db.SaveChanges();
        }

        // ── Normalization ────────────────────────────────────────────────────────────────────────

        [Theory]
        [InlineData("The Lord of the Rings", "lord of the rings")]
        [InlineData("Thé Thing!", "thing")]
        [InlineData("Dune: Part Two", "dune part two")]
        [InlineData("Tom & Jerry", "tom and jerry")]
        [InlineData("  A   Bug's Life ", "bugs life")]
        [InlineData("It", "it")]
        [InlineData("The", "the")]
        [InlineData("", "")]
        [InlineData(null, "")]
        public void Normalize_folds_case_accents_articles_and_punctuation(string? input, string expected)
        {
            Assert.Equal(expected, ContentRequestMatcher.Normalize(input));
        }

        [Fact]
        public void ProbeWords_prefers_the_longest_tokens_and_skips_one_letter_ones()
        {
            Assert.Equal(new[] { "seafarers", "catan" }, ContentRequestMatcher.ProbeWords("catan seafarers"));
            Assert.Equal(new[] { "it" }, ContentRequestMatcher.ProbeWords("it"));
            Assert.Empty(ContentRequestMatcher.ProbeWords("a"));
        }

        [Theory]
        [InlineData("dune", "dune", true)]
        [InlineData("dune", "dune part two", true)]            // whole-token containment, a 4-letter token is enough
        [InlineData("it", "it follows", false)]                // too short to be allowed to contain-match
        [InlineData("cat", "cat people", false)]               // three letters: still too short
        [InlineData("it", "it", true)]
        [InlineData("cat", "catan", false)]                    // not a whole token
        [InlineData("expanse", "the expanse", true)]
        [InlineData("sixteen stone", "sixteen stone", true)]
        [InlineData("", "dune", false)]
        public void TitlesAgree_is_equal_or_whole_token_containment(string a, string b, bool expected)
        {
            Assert.Equal(expected, ContentRequestMatcher.TitlesAgree(a, b));
        }

        [Theory]
        [InlineData(null, 1984, true)]
        [InlineData(1984, null, true)]
        [InlineData(1984, 1985, true)]
        [InlineData(1984, 1986, false)]
        public void YearsAgree_allows_one_year_of_slack(int? a, int? b, bool expected)
        {
            Assert.Equal(expected, ContentRequestMatcher.YearsAgree(a, b));
        }

        [Fact]
        public void Score_ranks_exact_over_loose_and_refuses_a_loose_title_with_the_wrong_year()
        {
            Assert.Equal(3, ContentRequestMatcher.Score("dune", 1984, "dune", 1984));
            Assert.Equal(2, ContentRequestMatcher.Score("dune", 2021, "dune", 1984));
            Assert.Equal(1, ContentRequestMatcher.Score("dune", null, "dune part two", 2024));
            Assert.Equal(0, ContentRequestMatcher.Score("dune", 1984, "dune part two", 2024));
        }

        // ── Lookup ───────────────────────────────────────────────────────────────────────────────

        [Fact]
        public async Task Find_picks_the_exact_title_with_the_agreeing_year()
        {
            using var db = new MovieDb(options);
            var m = new ContentRequestMatcher(db);
            var hit = await m.FindAsync(new ContentRequest { Section = "movies", Title = "dune", Year = 1984 }, null, CancellationToken.None);
            Assert.NotNull(hit);
            Assert.Equal("movie", hit!.Kind);
            Assert.Equal(10, hit.Id);
        }

        [Fact]
        public async Task Find_reaches_series_in_the_movies_section_and_folds_the_article()
        {
            using var db = new MovieDb(options);
            var m = new ContentRequestMatcher(db);
            var hit = await m.FindAsync(new ContentRequest { Section = "movies", Title = "Expanse" }, null, CancellationToken.None);
            Assert.Equal(("series", 20), (hit?.Kind, hit?.Id));
        }

        [Fact]
        public async Task Find_skips_the_dismissed_key_but_still_offers_another_row()
        {
            using var db = new MovieDb(options);
            var m = new ContentRequestMatcher(db);
            // "dune" with no year agrees with both Dune rows; the 1984 one is exact (score 2) and wins —
            // unless it was dismissed, in which case the loose Part Two (score 1) is the proposal.
            var first = await m.FindAsync(new ContentRequest { Section = "movies", Title = "Dune" }, null, CancellationToken.None);
            Assert.Equal(10, first!.Id);
            var second = await m.FindAsync(new ContentRequest { Section = "movies", Title = "Dune" }, "movie:10", CancellationToken.None);
            Assert.Equal(11, second!.Id);
        }

        [Fact]
        public async Task Find_matches_an_album_with_the_artist_as_detail_and_an_artist_by_name()
        {
            using var db = new MovieDb(options);
            var m = new ContentRequestMatcher(db);
            var album = await m.FindAsync(new ContentRequest { Section = "music", Title = "Sixteen Stone", Detail = "Bush" }, null, CancellationToken.None);
            Assert.Equal(("album", 31), (album?.Kind, album?.Id));
            Assert.Equal("Bush — Sixteen Stone", await m.DisplayTitleAsync(album!, CancellationToken.None));

            var artist = await m.FindAsync(new ContentRequest { Section = "music", Title = "bush" }, null, CancellationToken.None);
            Assert.Equal(("artist", 30), (artist?.Kind, artist?.Id));
        }

        [Fact]
        public async Task Find_covers_boardgames_and_arcade_and_leaves_books_alone()
        {
            using var db = new MovieDb(options);
            var m = new ContentRequestMatcher(db);
            Assert.Equal(40, (await m.FindAsync(new ContentRequest { Section = "boardgames", Title = "catan seafarers" }, null, CancellationToken.None))!.Id);
            Assert.Equal(50, (await m.FindAsync(new ContentRequest { Section = "arcade", Title = "Chrono Trigger", Detail = "SNES" }, null, CancellationToken.None))!.Id);
            Assert.Null(await m.FindAsync(new ContentRequest { Section = "books", Title = "Dune" }, null, CancellationToken.None));
            Assert.Null(await m.FindAsync(new ContentRequest { Section = "movies", Title = "Nothing Like This Exists" }, null, CancellationToken.None));
        }

        [Fact]
        public async Task Find_only_proposes_content_we_have()
        {
            using var db = new MovieDb(options);
            var m = new ContentRequestMatcher(db);
            Assert.Null(await m.FindAsync(new ContentRequest { Section = "movies", Title = "Heat" }, null, CancellationToken.None));     // file missing
            Assert.Null(await m.FindAsync(new ContentRequest { Section = "movies", Title = "Ronin" }, null, CancellationToken.None));    // no file ever
            Assert.Null(await m.FindAsync(new ContentRequest { Section = "movies", Title = "Firefly" }, null, CancellationToken.None));  // series with no episodes on disk
        }

        [Fact]
        public async Task Find_reaches_a_title_with_an_apostrophe_from_a_bare_probe()
        {
            using var db = new MovieDb(options);
            var m = new ContentRequestMatcher(db);
            Assert.Equal(new[] { "assassin%s", "creed" }, ContentRequestMatcher.ProbePatterns("Assassin's Creed"));
            Assert.Equal(new[] { "assassins", "creed" }, ContentRequestMatcher.ProbePatterns("assassins creed"));
            // Typed without the apostrophe, still found: SimpleTitle is folded, and the second probe ("creed") is whole.
            Assert.Equal(14, (await m.FindAsync(new ContentRequest { Section = "movies", Title = "Assassins Creed" }, null, CancellationToken.None))!.Id);
        }

        [Fact]
        public async Task Find_does_not_let_a_two_letter_title_contain_match()
        {
            using var db = new MovieDb(options);
            var m = new ContentRequestMatcher(db);
            // "It" exists exactly, so it matches; "It Follows" (not in the catalog) must NOT be answered by "It".
            Assert.Equal(13, (await m.FindAsync(new ContentRequest { Section = "movies", Title = "It" }, null, CancellationToken.None))!.Id);
            Assert.Null(await m.FindAsync(new ContentRequest { Section = "movies", Title = "It Follows" }, null, CancellationToken.None));
        }

        // ── Sweep ────────────────────────────────────────────────────────────────────────────────

        [Fact]
        public async Task Sweep_is_bounded_resumable_and_proposes_only_for_unproposed_open_rows()
        {
            using (var db = new MovieDb(options))
            {
                var now = DateTime.UtcNow;
                db.ContentRequests.AddRange(
                    new ContentRequest { Id = 1, CreatedUtc = now, RequestedByUserId = 1, Section = "movies", Title = "The Thing", Year = 1982, Status = "open" },
                    new ContentRequest { Id = 2, CreatedUtc = now, RequestedByUserId = 1, Section = "boardgames", Title = "Catan Seafarers", Status = "open" },
                    new ContentRequest { Id = 3, CreatedUtc = now, RequestedByUserId = 1, Section = "movies", Title = "Nothing Like This", Status = "open" },
                    new ContentRequest { Id = 4, CreatedUtc = now, RequestedByUserId = 1, Section = "movies", Title = "Dune", Status = "fulfilled" },
                    new ContentRequest { Id = 5, CreatedUtc = now, RequestedByUserId = 1, Section = "movies", Title = "Dune", Status = "open", MatchKind = "movie", MatchId = 10, MatchTitle = "Dune (1984)" });
                db.SaveChanges();
            }

            using (var db = new MovieDb(options))
            {
                var m = new ContentRequestMatcher(db);
                var first = await m.SweepAsync(0, 2, CancellationToken.None);
                Assert.Equal(2, first.Checked);
                Assert.Equal(2, first.Proposed);
                Assert.Equal(2, first.NextFrom);
                Assert.Equal(1, first.Remaining);          // row 3 is left; 4 is closed, 5 already proposed

                var second = await m.SweepAsync(first.NextFrom!.Value, 2, CancellationToken.None);
                Assert.Equal(1, second.Checked);
                Assert.Equal(0, second.Proposed);
                Assert.Null(second.NextFrom);
            }

            using (var db = new MovieDb(options))
            {
                var rows = db.ContentRequests.OrderBy(r => r.Id).ToList();
                Assert.Equal(("movie", 12, "The Thing (1982)"), (rows[0].MatchKind, rows[0].MatchId, rows[0].MatchTitle));
                Assert.Equal(("boardgame", 40), (rows[1].MatchKind, rows[1].MatchId));
                Assert.Null(rows[2].MatchKind);
                Assert.Null(rows[3].MatchKind);
                Assert.Equal("Dune (1984)", rows[4].MatchTitle); // untouched
            }
        }
    }
}
