using MovieTheater.Core;
using MovieTheater.Music;
using MovieTheater.Services;
using Microsoft.Extensions.Configuration;

namespace MovieTheater.Tests
{
    /// <summary>
    /// Album-art storage rules (music-plan.md §2.5) plus the stream-URL shape the transcode lane
    /// added (§Phase 7). Everything here is pure — no database, no network.
    /// </summary>
    public class MusicArtStoreTests
    {
        private static MovieTheaterConfiguration Config(string? musicImagesDir, string? postersDir)
        {
            var values = new Dictionary<string, string?>();
            if (musicImagesDir != null) values["MusicImagesDir"] = musicImagesDir;
            if (postersDir != null) values["MoviePostersDir"] = postersDir;
            var raw = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
            return new MovieTheaterConfiguration(raw);
        }

        [Fact]
        public void FileName_UsesTheMusicBucketPrefixAndSuffix()
        {
            Assert.Equal("music_42.png", MusicArtStore.FileName(42, thumbnail: false));
            Assert.Equal("music_42_s.png", MusicArtStore.FileName(42, thumbnail: true));
        }

        [Fact]
        public void ResolveDir_PrefersMusicImagesDirButFallsBackToThePostersMount()
        {
            var withOwnDir = MusicArtStore.ResolveDir(Config(Path.GetTempPath(), "posters"));
            Assert.Equal(Path.GetFullPath(Path.GetTempPath()), withOwnDir);

            var fallback = MusicArtStore.ResolveDir(Config(null, "posters"));
            Assert.Equal(Path.GetFullPath("posters"), fallback);

            Assert.Null(MusicArtStore.ResolveDir(Config(null, null)));
        }

        [Fact]
        public void PathFor_CombinesDirectoryAndBucketedName()
        {
            var dir = Path.GetTempPath();
            var path = MusicArtStore.PathFor(Config(dir, null), 7, thumbnail: true);
            Assert.Equal(Path.Combine(Path.GetFullPath(dir), "music_7_s.png"), path);
        }

        [Fact]
        public void FindFolderImage_PrefersAConventionalStemOverTheLargestFile()
        {
            var dir = Directory.CreateTempSubdirectory("music-art-test").FullName;
            try
            {
                // A big scan next to a small, conventionally-named cover: the convention wins.
                File.WriteAllBytes(Path.Combine(dir, "scan-of-the-sleeve.jpg"), new byte[4096]);
                File.WriteAllBytes(Path.Combine(dir, "cover.jpg"), new byte[16]);
                Assert.Equal(Path.Combine(dir, "cover.jpg"), MusicArtStore.FindFolderImage(dir));
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public void FindFolderImage_FallsBackToTheLargestImageAndIgnoresNonImages()
        {
            var dir = Directory.CreateTempSubdirectory("music-art-test").FullName;
            try
            {
                File.WriteAllBytes(Path.Combine(dir, "01 - Track.mp3"), new byte[99999]);
                File.WriteAllBytes(Path.Combine(dir, "small.png"), new byte[8]);
                File.WriteAllBytes(Path.Combine(dir, "big.png"), new byte[64]);
                Assert.Equal(Path.Combine(dir, "big.png"), MusicArtStore.FindFolderImage(dir));
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public void FindFolderImage_ReturnsNullWhenTheFolderHasNoImageOrDoesNotExist()
        {
            var dir = Directory.CreateTempSubdirectory("music-art-test").FullName;
            try
            {
                File.WriteAllBytes(Path.Combine(dir, "01 - Track.mp3"), new byte[16]);
                Assert.Null(MusicArtStore.FindFolderImage(dir));
            }
            finally { Directory.Delete(dir, true); }

            Assert.Null(MusicArtStore.FindFolderImage(Path.Combine(Path.GetTempPath(), "no-such-album-folder-xyz")));
        }

        [Fact]
        public void Downscale_RejectsBytesThatArentAnImage()
        {
            Assert.Null(MusicArtStore.Downscale(new byte[] { 1, 2, 3, 4 }, 300));
        }

        [Fact]
        public void ComputeAverageColor_RejectsBytesThatArentAnImage()
        {
            Assert.Null(MusicArtStore.ComputeAverageColor(new byte[] { 1, 2, 3, 4 }));
        }

        [Fact]
        public void StreamUrl_PicksTheRouteByLaneAndKeepsOneTokenShape()
        {
            const string token = "abc.def";
            Assert.Equal("https://gw.example/s/abc.def/MusicFile",
                MusicStreamRoutes.Url("https://gw.example", token, transcode: false));
            Assert.Equal("https://gw.example/s/abc.def/MusicTranscode",
                MusicStreamRoutes.Url("https://gw.example/", token, transcode: true));
        }

        [Fact]
        public void StreamUrl_TokenIsUnchangedBetweenTheTwoLanes()
        {
            const string secret = "shhh";
            var token = MusicCapabilityToken.Mint(secret, new MusicCapabilityToken.Payload(
                7, 42, "Artist (1990)/Artist - Album (1991)/01 - Song.wma",
                DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 600));

            // Both routes carry the same capability — the gateway distinguishes them by path only.
            Assert.Contains(token, MusicStreamRoutes.Url("https://gw.example", token, transcode: false));
            Assert.Contains(token, MusicStreamRoutes.Url("https://gw.example", token, transcode: true));
            Assert.True(MusicCapabilityToken.TryValidate(secret, token, out var payload));
            Assert.Equal(42, payload!.TrackId);
        }
    }
}
