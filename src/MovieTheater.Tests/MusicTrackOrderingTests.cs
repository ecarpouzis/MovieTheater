using System;
using System.IO;
using System.Linq;
using Microsoft.EntityFrameworkCore;
using MovieTheater.Db;
using MovieTheater.Music;
using Xunit;

namespace MovieTheater.Tests
{
    /// <summary>
    /// The tracklist order (<see cref="MusicTrackOrdering.InTrackOrder"/>).
    /// </summary>
    /// <remarks>
    /// Written against a real (SQLite) database rather than a LINQ-to-objects list, because the whole
    /// bug WAS a database behaviour: <c>ORDER BY</c> puts NULLs first, and the two columns a
    /// tracklist sorts on are both nullable and both routinely null. An in-memory list sorts them
    /// the same way, so this would pass over a broken query if it ran off one.
    ///
    /// <para>The fixture is Dan Le Sac vs Scroobius Pip's <i>Angles</i>, verbatim from the live
    /// catalog: three files loose in the album folder (no disc tag) and eleven under <c>Disc 1</c>
    /// (disc tag 1). That is not a curiosity — 100 of the library's 2,921 albums are tagged unevenly
    /// like this — and it made the album open on track 2.</para>
    /// </remarks>
    public class MusicTrackOrderingTests : IDisposable
    {
        private readonly string workDir;
        private readonly DbContextOptions<MovieDb> options;

        public MusicTrackOrderingTests()
        {
            workDir = Path.Combine(Path.GetTempPath(), "music-order-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(workDir);
            options = new DbContextOptionsBuilder<MovieDb>()
                .UseSqlite("Data Source=" + Path.Combine(workDir, "music.db") + ";Pooling=False")
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

        /// <summary>Inserts in a deliberately unhelpful order so a passing result can't be the
        /// insertion order leaking through.</summary>
        private int SeedAngles()
        {
            using var db = NewDb();
            var artist = new MusicArtist { Name = "Dan Le Sac vs Scroobius Pip", SortName = "Dan Le Sac vs Scroobius Pip", FolderName = "Dan Le Sac vs Scroobius Pip (2008)" };
            db.MusicArtists.Add(artist);
            db.SaveChanges();
            var album = new MusicAlbum { ArtistId = artist.Id, Title = "Angles", Year = 2008, FolderPath = "x" };
            db.MusicAlbums.Add(album);
            db.SaveChanges();

            void Track(int? disc, int? no, string file) => db.MusicTracks.Add(new MusicTrack
            {
                ArtistId = artist.Id,
                AlbumId = album.Id,
                RelativePath = (disc == null ? "" : "Disc 1/") + file,
                FileName = file,
                Extension = ".flac",
                Title = file,
                DiscNo = disc,
                TrackNo = no,
            });

            // The three untagged strays first, exactly as their ids fell in the live catalog.
            Track(null, 2, "02 - Development.flac");
            Track(null, 9, "09 - First Time We Met Musik.flac");
            Track(null, 13, "13 - Waiting for the Beat to Kick In.flac");
            Track(1, 1, "01 - The Beat That My Heart Skipped.flac");
            Track(1, 11, "11 - Thou Shalt Always Kill.flac");
            Track(1, 3, "03 - Look for the Woman.flac");
            Track(1, 15, "15 - Thou Shalt Always Kill (de la Edit).flac");
            Track(1, 10, "10 - Back from Hell.flac");
            db.SaveChanges();
            return album.Id;
        }

        [Fact]
        public void An_untagged_disc_is_disc_one_not_disc_zero()
        {
            var albumId = SeedAngles();
            using var db = NewDb();
            var order = db.MusicTracks.Where(t => t.AlbumId == albumId).InTrackOrder()
                .Select(t => t.TrackNo).ToList();

            // Raw ORDER BY DiscNo would give 2, 9, 13, 1, 3, 10, 11, 15 — the album opening on
            // track 2 and track 1 arriving fourth, which is precisely the reported bug.
            Assert.Equal(new int?[] { 1, 2, 3, 9, 10, 11, 13, 15 }, order);
        }

        [Fact]
        public void A_track_with_no_number_goes_last_rather_than_first()
        {
            var albumId = SeedAngles();
            using (var db = NewDb())
            {
                var any = db.MusicTracks.First(t => t.AlbumId == albumId);
                db.MusicTracks.Add(new MusicTrack
                {
                    ArtistId = any.ArtistId, AlbumId = albumId,
                    RelativePath = "zz.flac", FileName = "Hidden Track.flac", Extension = ".flac",
                    Title = "Hidden Track", DiscNo = 1, TrackNo = null,
                });
                db.SaveChanges();
            }

            using var read = NewDb();
            var titles = read.MusicTracks.Where(t => t.AlbumId == albumId).InTrackOrder()
                .Select(t => t.FileName).ToList();
            Assert.Equal("Hidden Track.flac", titles[^1]);
        }

        [Fact]
        public void Real_discs_still_group_and_stay_in_order()
        {
            var albumId = SeedAngles();
            using (var db = NewDb())
            {
                var any = db.MusicTracks.First(t => t.AlbumId == albumId);
                db.MusicTracks.Add(new MusicTrack
                {
                    ArtistId = any.ArtistId, AlbumId = albumId,
                    RelativePath = "Disc 2/01.flac", FileName = "01 - Disc two opener.flac", Extension = ".flac",
                    Title = "Disc two opener", DiscNo = 2, TrackNo = 1,
                });
                db.SaveChanges();
            }

            using var read = NewDb();
            var rows = read.MusicTracks.Where(t => t.AlbumId == albumId).InTrackOrder()
                .Select(t => new { t.DiscNo, t.TrackNo }).ToList();
            // Folding null into disc 1 must not fold disc 2 into it as well: track 1 of disc 2 comes
            // after track 15 of disc 1, not before track 2.
            Assert.Equal(2, rows[^1].DiscNo);
            Assert.Equal(1, rows[^1].TrackNo);
        }

        [Fact]
        public void Two_files_claiming_the_same_number_are_ordered_by_name_not_by_luck()
        {
            // Angles really does have two track 09s (one loose, one on the disc). Without a tiebreak
            // the pair could swap between two fetches of the same page.
            var albumId = SeedAngles();
            using var db = NewDb();
            var nines = db.MusicTracks.Where(t => t.AlbumId == albumId && t.TrackNo == 9)
                .InTrackOrder().Select(t => t.FileName).ToList();
            db.MusicTracks.Add(new MusicTrack
            {
                ArtistId = db.MusicTracks.First().ArtistId, AlbumId = albumId,
                RelativePath = "Disc 1/09b.flac", FileName = "09 - Magician's Assistant.flac",
                Extension = ".flac", Title = "Magician's Assistant", DiscNo = 1, TrackNo = 9,
            });
            db.SaveChanges();

            var both = db.MusicTracks.Where(t => t.AlbumId == albumId && t.TrackNo == 9)
                .InTrackOrder().Select(t => t.FileName).ToList();
            Assert.Single(nines);
            Assert.Equal(new[] { "09 - First Time We Met Musik.flac", "09 - Magician's Assistant.flac" }, both);
        }
    }
}
