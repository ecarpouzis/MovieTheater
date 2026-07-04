using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using MovieTheater.ArcadeGateway;

namespace MovieTheater.Tests
{
    /// <summary>
    /// Exercises the gateway's just-in-time ROM cache (docs/arcade-jit-cache.md) end-to-end with real
    /// 7-Zip against synthetic PSX-shaped archives: extract → idempotency → LRU eviction → the
    /// destructive-safety guards (never delete the source archive; pinned games survive eviction).
    /// Skips when 7-Zip isn't installed so it doesn't fail on a machine without it.
    /// </summary>
    public class RomCacheTests : IDisposable
    {
        private static readonly string SevenZip = @"C:\Program Files\7-Zip\7z.exe";
        private readonly string root;
        private readonly string archivesDir;
        private readonly string romsDir;

        public RomCacheTests()
        {
            root = Path.Combine(Path.GetTempPath(), "romcache-test-" + Guid.NewGuid().ToString("N")[..8]);
            archivesDir = Path.Combine(root, "archives");
            romsDir = Path.Combine(root, "roms");
            Directory.CreateDirectory(archivesDir);
            Directory.CreateDirectory(romsDir);
        }

        public void Dispose()
        {
            try { Directory.Delete(root, true); } catch { /* best effort */ }
        }

        private RomCache NewCache(long maxBytes) => new(new RomCacheOptions
        {
            ManifestPath = WriteManifest(),
            RomsDir = romsDir,
            SevenZipPath = SevenZip,
            MaxBytes = maxBytes,
        }, NullLogger.Instance);

        private string CueOf(string key) => Path.Combine(romsDir, "psx", key + ".cue");

        [Fact]
        public async Task Extracts_on_demand_and_is_idempotent()
        {
            if (!File.Exists(SevenZip)) return; // 7-Zip absent → skip (present in dev/CI)
            MakeArchive("Foo (USA)", 5);
            MakeArchive("Bar (USA)", 5);
            var cache = NewCache(1024L * 1024 * 1024);

            Assert.True(cache.IsManaged(1));
            Assert.False(cache.IsManaged(999));

            await cache.EnsureMaterializedAsync(1);
            Assert.True(File.Exists(CueOf("Foo (USA)")));
            Assert.True(File.Exists(Path.Combine(romsDir, "psx", "Foo (USA) (Track 1).bin")));

            var mtime = File.GetLastWriteTimeUtc(CueOf("Foo (USA)"));
            await Task.Delay(20);
            await cache.EnsureMaterializedAsync(1); // must be a no-op, not a re-extract
            Assert.Equal(mtime, File.GetLastWriteTimeUtc(CueOf("Foo (USA)")));
            Assert.True(File.Exists(Path.Combine(archivesDir, "Foo (USA).7z")));
        }

        [Fact]
        public async Task Evicts_LRU_over_cap_but_never_the_source_archive()
        {
            if (!File.Exists(SevenZip)) return; // 7-Zip absent → skip (present in dev/CI)
            MakeArchive("Foo (USA)", 5);
            MakeArchive("Bar (USA)", 5);
            var cache = NewCache(8L * 1024 * 1024); // ~1.5 games

            await cache.EnsureMaterializedAsync(1);
            await cache.EnsureMaterializedAsync(2);

            Assert.True(File.Exists(CueOf("Bar (USA)")), "most-recent game kept");
            Assert.False(File.Exists(CueOf("Foo (USA)")), "LRU game evicted under cap");
            Assert.True(File.Exists(Path.Combine(archivesDir, "Foo (USA).7z")), "source archive never deleted");
            Assert.True(File.Exists(Path.Combine(archivesDir, "Bar (USA).7z")));
        }

        [Fact]
        public async Task Pinned_game_survives_eviction()
        {
            if (!File.Exists(SevenZip)) return; // 7-Zip absent → skip (present in dev/CI)
            MakeArchive("Foo (USA)", 5);
            MakeArchive("Bar (USA)", 5);
            var cache = NewCache(8L * 1024 * 1024);

            await cache.EnsureMaterializedAsync(1);
            cache.Pin(1);                       // in use for a live session
            await cache.EnsureMaterializedAsync(2); // over cap, but game 1 is pinned
            Assert.True(File.Exists(CueOf("Foo (USA)")), "pinned game not evicted");
            cache.Unpin(1);
        }

        [Fact]
        public async Task Extracts_zipped_2D_rom_by_system_extension()
        {
            if (!File.Exists(SevenZip)) return; // 7-Zip absent → skip (present in dev/CI)
            // A SNES-shaped .zip: <key>.sfc inside <key>.zip (the R: 2D layout, verified live).
            const string key = "Super Mario World (USA)";
            var stage = Path.Combine(root, "stage2d");
            Directory.CreateDirectory(stage);
            var rom = new byte[512 * 1024]; new Random(2).NextBytes(rom);
            File.WriteAllBytes(Path.Combine(stage, key + ".sfc"), rom);
            var arc = Path.Combine(archivesDir, key + ".zip");
            RunSevenZip("a", "-tzip", arc, Path.Combine(stage, "*"));
            Directory.Delete(stage, true);

            var manifest = Path.Combine(root, "manifest2d.json");
            File.WriteAllText(manifest, JsonSerializer.Serialize(new
            {
                version = 1,
                games = new object[]
                {
                    new { gameId = 10, gameKey = key, system = "snes", folder = "snes", archive = arc, exts = new[] { ".sfc", ".smc" } },
                },
            }));
            var cache = new RomCache(new RomCacheOptions
            {
                ManifestPath = manifest, RomsDir = romsDir, SevenZipPath = SevenZip, MaxBytes = 1024L * 1024 * 1024,
            }, NullLogger.Instance);

            Assert.True(cache.IsManaged(10));
            await cache.EnsureMaterializedAsync(10);
            var sfc = Path.Combine(romsDir, "snes", key + ".sfc");
            Assert.True(File.Exists(sfc), "SNES rom extracted, found by its .sfc extension");

            var mtime = File.GetLastWriteTimeUtc(sfc);
            await Task.Delay(20);
            await cache.EnsureMaterializedAsync(10); // idempotent — no re-extract
            Assert.Equal(mtime, File.GetLastWriteTimeUtc(sfc));
            Assert.True(File.Exists(arc), "source .zip never deleted");
        }

        // A PSX-shaped archive: <key>.cue + <key> (Track 1).bin of binMb, zipped to <key>.7z.
        private void MakeArchive(string key, int binMb)
        {
            var stage = Path.Combine(root, "stage-" + key);
            Directory.CreateDirectory(stage);
            File.WriteAllText(Path.Combine(stage, key + ".cue"),
                $"FILE \"{key} (Track 1).bin\" BINARY\n  TRACK 01 MODE2/2352\n");
            var bin = new byte[binMb * 1024 * 1024];
            new Random(1).NextBytes(bin); // ~incompressible so the .7z reflects real size
            File.WriteAllBytes(Path.Combine(stage, key + " (Track 1).bin"), bin);

            var arc = Path.Combine(archivesDir, key + ".7z");
            RunSevenZip("a", "-mx=1", arc, Path.Combine(stage, "*"));
            Directory.Delete(stage, true);
        }

        private static void RunSevenZip(params string[] args)
        {
            var psi = new ProcessStartInfo(SevenZip)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi)!;
            p.StandardOutput.ReadToEnd();
            p.StandardError.ReadToEnd();
            p.WaitForExit();
        }

        // Manifest matching what arcade-romcache-export writes: gameId → source archive + extract folder.
        private string WriteManifest()
        {
            var path = Path.Combine(root, "manifest.json");
            var json = JsonSerializer.Serialize(new
            {
                version = 1,
                games = new object[]
                {
                    new { gameId = 1, gameKey = "Foo (USA)", system = "ps1", folder = "psx", archive = Path.Combine(archivesDir, "Foo (USA).7z"), exts = new[] { ".cue", ".chd" } },
                    new { gameId = 2, gameKey = "Bar (USA)", system = "ps1", folder = "psx", archive = Path.Combine(archivesDir, "Bar (USA).7z"), exts = new[] { ".cue", ".chd" } },
                },
            });
            File.WriteAllText(path, json);
            return path;
        }
    }
}
