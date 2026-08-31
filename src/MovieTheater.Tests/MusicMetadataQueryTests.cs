using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using MovieTheater.Db;
using MovieTheater.Music;
using Xunit;

namespace MovieTheater.Tests
{
    /// <summary>
    /// R9 S10's new model, and the READ SHAPES the Music shelf endpoints build over it, exercised
    /// against a real (throwaway SQLite) database.
    /// </summary>
    /// <remarks>
    /// <para><c>EnsureCreated</c> builds the schema straight from the model, so this is also the
    /// pin on the configuration itself: the (album, source, genre) unique key that lets the tag pass
    /// and the external passes coexist, and the (user, album) unique key that stops a double-tap
    /// minting a second opinion. Both are claims a compile cannot check.</para>
    /// <para>The controller is deliberately not compiled in — no test may reach the configured
    /// connection string, which IS the live shared database — so what is pinned here is the SHAPE
    /// each endpoint composes: the cross-source genre merge with the file's own answer first, the
    /// ratings aggregate, and the artist's best-album reading of "top rated".</para>
    /// </remarks>
    public class MusicMetadataQueryTests : IDisposable
    {
        private readonly string workDir = Path.Combine(Path.GetTempPath(), "mt-music-meta-" + Guid.NewGuid().ToString("N"));
        private readonly DbContextOptions<MovieDb> options;

        public MusicMetadataQueryTests()
        {
            Directory.CreateDirectory(workDir);
            options = new DbContextOptionsBuilder<MovieDb>().UseSqlite("Data Source=" + Path.Combine(workDir, "music.db") + ";Pooling=False").Options;
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
            var artist = new MusicArtist { Id = 1, Name = "Air", SortName = "Air", FolderName = "Air (1998-2004)" };
            var other = new MusicArtist { Id = 2, Name = "Zed", SortName = "Zed", FolderName = "Zed (2001)" };
            db.MusicArtists.AddRange(artist, other);

            db.MusicAlbums.AddRange(
                new MusicAlbum { Id = 11, ArtistId = 1, Title = "Moon Safari", Year = 1998, FolderPath = "Air/a", Popularity = 74 },
                new MusicAlbum { Id = 12, ArtistId = 1, Title = "Talkie Walkie", Year = 2004, FolderPath = "Air/b" },
                new MusicAlbum { Id = 21, ArtistId = 2, Title = "Nothing", Year = 2001, FolderPath = "Zed/a" });

            db.MusicAlbumGenres.AddRange(
                // Deliberately out of the order the endpoint must return them in.
                new MusicAlbumGenre { AlbumId = 11, Genre = "Indie Rock", Source = MusicGenreSources.MusicBrainz, Weight = 40, CreatedUtc = DateTime.UtcNow },
                new MusicAlbumGenre { AlbumId = 11, Genre = "Downtempo", Source = MusicGenreSources.Tags, Weight = 3, CreatedUtc = DateTime.UtcNow },
                new MusicAlbumGenre { AlbumId = 11, Genre = "Electronic", Source = MusicGenreSources.Tags, Weight = 9, CreatedUtc = DateTime.UtcNow },
                // Same genre from a second source, differently cased: ONE pill, not two.
                new MusicAlbumGenre { AlbumId = 11, Genre = "electronic", Source = MusicGenreSources.LastFm, Weight = 4, CreatedUtc = DateTime.UtcNow });

            db.Users.AddRange(new User { UserID = 1, Username = "eric" }, new User { UserID = 2, Username = "sam" });
            db.MusicAlbumRatings.AddRange(
                new MusicAlbumRating { UserId = 1, AlbumId = 11, Score = 90, CreatedUtc = DateTime.UtcNow, UpdatedUtc = DateTime.UtcNow },
                new MusicAlbumRating { UserId = 2, AlbumId = 11, Score = 80, CreatedUtc = DateTime.UtcNow, UpdatedUtc = DateTime.UtcNow },
                new MusicAlbumRating { UserId = 1, AlbumId = 21, Score = 0, CreatedUtc = DateTime.UtcNow, UpdatedUtc = DateTime.UtcNow });
            db.SaveChanges();
        }

        /// <summary>The shelf's genre list: the file's own answer first, then by how strongly the
        /// source asserts it, de-duplicated case-insensitively.</summary>
        private static List<string> MergeGenres(IEnumerable<MusicAlbumGenre> rows) => rows
            .OrderBy(r => r.Source == MusicGenreSources.Tags ? 0 : 1)
            .ThenByDescending(r => r.Weight)
            .Select(r => r.Genre)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        [Fact]
        public async Task An_albums_genres_merge_across_sources_with_the_files_own_answer_first()
        {
            using var db = new MovieDb(options);
            var rows = await db.MusicAlbumGenres.AsNoTracking().Where(g => g.AlbumId == 11).ToListAsync();
            // Both passes are right about something — the files say what THIS rip was labelled, the
            // web says what the world calls it — so the shelf lists the union rather than picking.
            Assert.Equal(new[] { "Electronic", "Downtempo", "Indie Rock" }, MergeGenres(rows));
        }

        [Fact]
        public async Task Source_is_part_of_the_key_so_the_passes_coexist_and_replacing_one_leaves_the_other()
        {
            using var db = new MovieDb(options);
            // Same album, same genre, different source: a legal pair. The unique index is on the
            // TRIPLE, which is the whole reason three passes can write to one table.
            db.MusicAlbumGenres.Add(new MusicAlbumGenre { AlbumId = 11, Genre = "Downtempo", Source = MusicGenreSources.LastFm, Weight = 1, CreatedUtc = DateTime.UtcNow });
            await db.SaveChangesAsync();

            // A pass re-running replaces only ITS OWN rows for the album.
            var mine = await db.MusicAlbumGenres.Where(g => g.AlbumId == 11 && g.Source == MusicGenreSources.Tags).ToListAsync();
            db.MusicAlbumGenres.RemoveRange(mine);
            db.MusicAlbumGenres.Add(new MusicAlbumGenre { AlbumId = 11, Genre = "Ambient", Source = MusicGenreSources.Tags, Weight = 7, CreatedUtc = DateTime.UtcNow });
            await db.SaveChangesAsync();

            var after = await db.MusicAlbumGenres.AsNoTracking().Where(g => g.AlbumId == 11).ToListAsync();
            Assert.Equal(new[] { "Ambient" }, after.Where(g => g.Source == MusicGenreSources.Tags).Select(g => g.Genre));
            Assert.Contains(after, g => g.Source == MusicGenreSources.MusicBrainz && g.Genre == "Indie Rock");
            Assert.Contains(after, g => g.Source == MusicGenreSources.LastFm && g.Genre == "electronic");
        }

        [Fact]
        public async Task One_listener_gets_one_row_per_album()
        {
            using var db = new MovieDb(options);
            db.MusicAlbumRatings.Add(new MusicAlbumRating { UserId = 1, AlbumId = 11, Score = 55, CreatedUtc = DateTime.UtcNow, UpdatedUtc = DateTime.UtcNow });
            await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        }

        [Fact]
        public async Task The_house_average_and_the_blend_are_read_in_one_grouped_pass()
        {
            using var db = new MovieDb(options);
            var aggregates = await db.MusicAlbumRatings.AsNoTracking()
                .GroupBy(r => r.AlbumId)
                .Select(g => new { AlbumId = g.Key, Average = (double?)g.Average(r => (double)r.Score), Count = g.Count() })
                .ToListAsync();
            var moon = aggregates.Single(a => a.AlbumId == 11);
            Assert.Equal(85, moon.Average);
            Assert.Equal(2, moon.Count);

            // 0 is a REAL score — the album everybody hated has an opinion attached and must not
            // read as unrated.
            var nothing = aggregates.Single(a => a.AlbumId == 21);
            Assert.Equal(0, nothing.Average);
            Assert.Equal(1, nothing.Count);
            Assert.NotNull(MusicPopularity.Blend(nothing.Average, nothing.Count, null));

            // An album nobody has rated and nobody has heard of has NO opinion attached at all.
            Assert.DoesNotContain(aggregates, a => a.AlbumId == 12);
            Assert.Null(MusicPopularity.Blend(null, 0, null));
        }

        [Fact]
        public async Task An_artists_top_rating_is_the_best_of_their_albums_and_null_when_none_scores()
        {
            using var db = new MovieDb(options);
            var scores = (await db.MusicAlbumRatings.AsNoTracking()
                    .GroupBy(r => r.AlbumId)
                    .Select(g => new { AlbumId = g.Key, Average = (double?)g.Average(r => (double)r.Score), Count = g.Count() })
                    .ToListAsync())
                .ToDictionary(a => a.AlbumId, a => a);

            var top = (await db.MusicAlbums.AsNoTracking().Select(a => new { a.Id, a.ArtistId, a.Popularity }).ToListAsync())
                .GroupBy(a => a.ArtistId)
                .ToDictionary(g => g.Key, g => g
                    .Select(a => scores.TryGetValue(a.Id, out var s)
                        ? MusicPopularity.Blend(s.Average, s.Count, a.Popularity)
                        : MusicPopularity.Blend(null, 0, a.Popularity))
                    .Where(v => v != null)
                    .DefaultIfEmpty(null)
                    .Max());

            // Air's best is Moon Safari (85 from two listeners, shrunk toward its 74 popularity);
            // Talkie Walkie has nothing at all and cannot lower it.
            Assert.NotNull(top[1]);
            Assert.InRange(top[1]!.Value, 74, 85);
            // Zed's only record scored 0 from one listener — a real score, and the artist's best.
            Assert.NotNull(top[2]);
            Assert.InRange(top[2]!.Value, 0, 40);
        }
    }
}
