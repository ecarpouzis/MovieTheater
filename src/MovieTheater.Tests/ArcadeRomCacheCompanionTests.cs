using System;
using System.IO;
using System.Linq;
using MovieTheater.ArcadeGateway;
using Xunit;

namespace MovieTheater.Tests
{
    /// <summary>
    /// The <c>save:</c> companion destination — how PSP downloadable content reaches the emulator.
    ///
    /// <para>PSP DLC is the one shape the ROM mount cannot express: a game looks for it at
    /// <c>ms0:/PSP/GAME/&lt;TITLEID&gt;/</c> and nowhere else, and <c>ms0:</c> is each worker's
    /// <c>&lt;ConfDir&gt;/libretro/legacy_save</c>, not the shared read-only ROM mount. Before this existed the
    /// only way to deliver it was a hand copy onto every worker — which is how the 3DS texture packs are
    /// installed, and why they disappear whenever a ConfDir is rebuilt.</para>
    /// </summary>
    public class ArcadeRomCacheCompanionTests
    {
        private static RomCache.ManifestGame Game(string? dest) => new(
            GameId: 1, GameKey: "Ape Quest (USA)", System: "psp", Folder: "psp",
            Archive: @"R:\Roms\Games\Sony PSP\Ape Quest (USA).iso",
            Exts: new[] { ".iso" }, Discs: null, Deps: null,
            CompanionPath: @"R:\Roms\Games\Sony PSP\_dlc\NPUG80061", CompanionDest: dest);

        [Fact]
        public void SaveSpec_IsParsedAndNormalized()
        {
            Assert.Equal("PSP/GAME/NPUG80061", RomCache.SaveRootRelPath(Game("save:PSP/GAME/NPUG80061")));
            // Backslashes and stray separators are the shapes a hand-typed DB value actually arrives in.
            Assert.Equal("PSP/GAME/NPUG80061", RomCache.SaveRootRelPath(Game(@"save:\PSP\GAME\NPUG80061\")));
            Assert.Equal("PSP/GAME/NPUG80061", RomCache.SaveRootRelPath(Game("SAVE: PSP/GAME/NPUG80061 ")));
        }

        [Fact]
        public void NonSaveSpec_FallsBackToTheRomMount()
        {
            // Null is the ordinary case: every existing companion keeps going to the ROM mount.
            Assert.Null(RomCache.SaveRootRelPath(Game(null)));
            Assert.Null(RomCache.SaveRootRelPath(Game("")));
            // An unrecognized root must NOT be guessed at — it falls back rather than inventing a location.
            Assert.Null(RomCache.SaveRootRelPath(Game("system:dc/whatever")));
            Assert.Null(RomCache.SaveRootRelPath(Game(@"D:\somewhere\absolute")));
            // "save:" with nothing after it would resolve to the save ROOT itself — refuse it.
            Assert.Null(RomCache.SaveRootRelPath(Game("save:")));
            Assert.Null(RomCache.SaveRootRelPath(Game("save:///")));
        }

        [Fact]
        public async Task DlcIsInstalledIntoEveryWorkerSaveRoot_AndIsIdempotent()
        {
            var tmp = Path.Combine(Path.GetTempPath(), "mt-romcache-" + Guid.NewGuid().ToString("N"));
            try
            {
                var src = Path.Combine(tmp, "src", "NPUG80061");
                Directory.CreateDirectory(src);
                File.WriteAllText(Path.Combine(src, "PARAM.PBP"), "pbp");
                File.WriteAllText(Path.Combine(src, "key1.edat"), "k");

                var roms = Path.Combine(tmp, "roms");
                var w1 = Path.Combine(tmp, "worker-gl", "libretro", "legacy_save");
                var w2 = Path.Combine(tmp, "worker-gl-2", "libretro", "legacy_save");
                Directory.CreateDirectory(roms);
                Directory.CreateDirectory(w1);
                Directory.CreateDirectory(w2);

                var cache = NewCache(roms, new[] { w1, w2 });
                var g = Game("save:PSP/GAME/NPUG80061") with { CompanionPath = src };

                await InvokeStage(cache, g);

                foreach (var w in new[] { w1, w2 })
                {
                    var dest = Path.Combine(w, "PSP", "GAME", "NPUG80061");
                    Assert.True(File.Exists(Path.Combine(dest, "PARAM.PBP")), $"PARAM.PBP missing under {w}");
                    Assert.True(File.Exists(Path.Combine(dest, "key1.edat")), $"key1.edat missing under {w}");
                }

                // Nothing may land on the ROM mount for a save: companion.
                Assert.False(Directory.Exists(Path.Combine(roms, "psp", "Ape Quest (USA)")));

                // Re-running is what happens on every launch: it must not throw or duplicate.
                await InvokeStage(cache, g);
                Assert.Equal(2, Directory.GetFiles(Path.Combine(w1, "PSP", "GAME", "NPUG80061")).Length);
            }
            finally { TryDelete(tmp); }
        }

        [Fact]
        public async Task AMissingInstallIsRepaired_EvenThoughTheRomIsStillStaged()
        {
            // The regression that matters: IsPresent() only looks at the ROM mount, so a DLC install that
            // vanished from a worker's save root (rebuilt ConfDir, a worker added since the last launch)
            // is invisible to it. Without a re-ensure the game boots as the version without its content —
            // Ape Quest silently reverting to its prologue demo, which reads as a broken card, not a
            // missing file. Staging must put it back.
            var tmp = Path.Combine(Path.GetTempPath(), "mt-romcache-" + Guid.NewGuid().ToString("N"));
            try
            {
                var src = Path.Combine(tmp, "src", "NPUG80061");
                Directory.CreateDirectory(src);
                File.WriteAllText(Path.Combine(src, "PARAM.PBP"), "pbp");

                var roms = Path.Combine(tmp, "roms");
                var w1 = Path.Combine(tmp, "worker-gl", "libretro", "legacy_save");
                var w2 = Path.Combine(tmp, "worker-gl-2", "libretro", "legacy_save");
                Directory.CreateDirectory(roms);
                Directory.CreateDirectory(w1);
                Directory.CreateDirectory(w2);

                var cache = NewCache(roms, new[] { w1, w2 });
                var g = Game("save:PSP/GAME/NPUG80061") with { CompanionPath = src };

                await InvokeStage(cache, g);
                var onW2 = Path.Combine(w2, "PSP", "GAME", "NPUG80061");
                Assert.True(File.Exists(Path.Combine(onW2, "PARAM.PBP")));

                // A ConfDir gets rebuilt / the memory stick gets cleaned.
                Directory.Delete(onW2, recursive: true);
                Assert.False(Directory.Exists(onW2));

                await InvokeStage(cache, g);
                Assert.True(File.Exists(Path.Combine(onW2, "PARAM.PBP")), "a wiped save root was not repaired");
            }
            finally { TryDelete(tmp); }
        }

        [Fact]
        public async Task RelPathEscapingTheSaveRootIsRefused()
        {
            var tmp = Path.Combine(Path.GetTempPath(), "mt-romcache-" + Guid.NewGuid().ToString("N"));
            try
            {
                var src = Path.Combine(tmp, "src", "NPUG80061");
                Directory.CreateDirectory(src);
                File.WriteAllText(Path.Combine(src, "PARAM.PBP"), "pbp");

                var roms = Path.Combine(tmp, "roms");
                var w1 = Path.Combine(tmp, "worker-gl", "libretro", "legacy_save");
                Directory.CreateDirectory(roms);
                Directory.CreateDirectory(w1);

                var cache = NewCache(roms, new[] { w1 });
                var g = Game(@"save:../../../escaped") with { CompanionPath = src };

                await InvokeStage(cache, g);

                // The manifest is generated, but it is still data — it must not be able to write anywhere
                // on the host it likes.
                Assert.False(Directory.Exists(Path.Combine(tmp, "escaped")));
                Assert.Empty(Directory.GetDirectories(w1));
            }
            finally { TryDelete(tmp); }
        }

        [Fact]
        public async Task MissingCompanionSourceThrowsRatherThanBootingWithoutIt()
        {
            var tmp = Path.Combine(Path.GetTempPath(), "mt-romcache-" + Guid.NewGuid().ToString("N"));
            try
            {
                var roms = Path.Combine(tmp, "roms");
                var w1 = Path.Combine(tmp, "worker-gl", "libretro", "legacy_save");
                Directory.CreateDirectory(roms);
                Directory.CreateDirectory(w1);

                var cache = NewCache(roms, new[] { w1 });
                var g = Game("save:PSP/GAME/NPUG80061") with { CompanionPath = Path.Combine(tmp, "does-not-exist") };

                await Assert.ThrowsAsync<FileNotFoundException>(() => InvokeStage(cache, g));
            }
            finally { TryDelete(tmp); }
        }

        [Fact]
        public async Task NoConfiguredSaveRoots_SkipsWithoutThrowing()
        {
            // A gateway that has not been told where the workers live must not take rooms down; the game
            // boots, just without its DLC, and the warning says so.
            var tmp = Path.Combine(Path.GetTempPath(), "mt-romcache-" + Guid.NewGuid().ToString("N"));
            try
            {
                var src = Path.Combine(tmp, "src", "NPUG80061");
                Directory.CreateDirectory(src);
                File.WriteAllText(Path.Combine(src, "PARAM.PBP"), "pbp");
                var roms = Path.Combine(tmp, "roms");
                Directory.CreateDirectory(roms);

                var cache = NewCache(roms, Array.Empty<string>());
                var g = Game("save:PSP/GAME/NPUG80061") with { CompanionPath = src };

                await InvokeStage(cache, g);   // must not throw
            }
            finally { TryDelete(tmp); }
        }

        private static RomCache NewCache(string romsDir, string[] saveRoots) =>
            new(new RomCacheOptions
            {
                ManifestPath = Path.Combine(romsDir, "no-such-manifest.json"),
                RomsDir = romsDir,
                WorkerSaveRoots = saveRoots,
            }, Microsoft.Extensions.Logging.Abstractions.NullLogger<RomCache>.Instance);

        // StageCompanionAsync is private by design (it is an internal step of ExtractAsync); reach it the
        // way the surrounding suite reaches other private stagers rather than widening the public surface.
        private static System.Threading.Tasks.Task InvokeStage(RomCache cache, RomCache.ManifestGame g)
        {
            var m = typeof(RomCache).GetMethod("StageCompanionAsync",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
            return (System.Threading.Tasks.Task)m.Invoke(cache, new object[] { g, System.Threading.CancellationToken.None })!;
        }

        private static void TryDelete(string dir)
        {
            try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); } catch { /* temp */ }
        }
    }
}
