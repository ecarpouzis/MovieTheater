using Microsoft.Extensions.Logging.Abstractions;
using MovieTheater.ArcadeGateway;

namespace MovieTheater.Tests
{
    /// <summary>
    /// HeavyVault (docs/arcade-heavy-lane-plan.md §8): per-user dir-zip saves for Moonlight-streamed
    /// titles. The behaviors under test are the ones whose failure modes destroy someone's game:
    /// deterministic zips (change detection must not false-positive), never-clobber seeding
    /// (displace, don't delete), the RPCS3 multi-dir glob, and the exact round-trip.
    /// </summary>
    public class HeavyVaultTests : IDisposable
    {
        private readonly string root;
        private readonly string store;
        private readonly string emu;

        public HeavyVaultTests()
        {
            root = Path.Combine(Path.GetTempPath(), "heavyvault-test-" + Guid.NewGuid().ToString("N")[..8]);
            store = Path.Combine(root, "store");
            emu = Path.Combine(root, "emu", "savedata");
            Directory.CreateDirectory(store);
            Directory.CreateDirectory(emu);
        }

        public void Dispose()
        {
            try { Directory.Delete(root, true); } catch { /* best effort */ }
        }

        private HeavyVault NewVault() => new(store, NullLogger.Instance);

        private HeavyApp App(string livePath, int gameId = 900) => new()
        {
            Id = "test-app",
            Title = "Test App",
            System = "ps3",
            ArcadeGameId = gameId,
            Exe = @"C:\emu\emu.exe",
            Save = new HeavySave { Kind = "dir", LivePath = livePath },
        };

        private void WriteLive(string dir, params (string rel, string content)[] files)
        {
            foreach (var (rel, content) in files)
            {
                var p = Path.Combine(emu, dir, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(p)!);
                File.WriteAllText(p, content);
            }
        }

        [Fact]
        public void Zip_is_deterministic_and_null_when_nothing_exists()
        {
            var livePath = Path.Combine(emu, "TITLE01");
            Assert.Null(HeavyVault.ZipLiveDirs(livePath)); // never played

            WriteLive("TITLE01", ("SAVE.BIN", "hello"), ("sub/META.SFO", "meta"));
            var a = HeavyVault.ZipLiveDirs(livePath);
            // Re-touch the files (new timestamps, same content) — the zip must not change.
            WriteLive("TITLE01", ("SAVE.BIN", "hello"), ("sub/META.SFO", "meta"));
            var b = HeavyVault.ZipLiveDirs(livePath);
            Assert.Equal(a, b);

            WriteLive("TITLE01", ("SAVE.BIN", "changed"));
            Assert.NotEqual(a, HeavyVault.ZipLiveDirs(livePath));
        }

        [Fact]
        public void Glob_livePath_captures_all_matching_dirs()
        {
            WriteLive("NPUA80247-AUTO-", ("SYS-DATA", "auto"));
            WriteLive("NPUA80247-GAME1", ("SYS-DATA", "manual"));
            WriteLive("NPUB99999-OTHER", ("SYS-DATA", "someone else"));
            var dirs = HeavyVault.ResolveLiveDirs(Path.Combine(emu, "NPUA80247*"));
            Assert.Equal(2, dirs.Count);
            Assert.DoesNotContain(dirs, d => d.Contains("NPUB99999"));
        }

        [Fact]
        public void Harvest_stores_once_and_skips_unchanged()
        {
            var livePath = Path.Combine(emu, "TITLE01");
            WriteLive("TITLE01", ("SAVE.BIN", "progress"));
            var vault = NewVault();

            var meta = vault.Harvest(App(livePath), userId: 7);
            Assert.NotNull(meta);
            Assert.Equal("dirzip", meta!.Kind);
            Assert.Equal(0, meta.SlotId);
            Assert.Equal("heavy", meta.Source);
            // Blob lands on the SaveStore-compatible path so My-Saves blob ops serve it.
            Assert.True(File.Exists(Path.Combine(store, "7", "900", "dirzip.zip")));

            Assert.Null(vault.Harvest(App(livePath), 7));  // unchanged → no new vault write
            WriteLive("TITLE01", ("SAVE.BIN", "more progress"));
            Assert.NotNull(vault.Harvest(App(livePath), 7)); // changed → vaulted again
        }

        [Fact]
        public void Seed_restores_the_exact_tree_and_displaces_instead_of_deleting()
        {
            var livePath = Path.Combine(emu, "NPUA80247*");
            WriteLive("NPUA80247-AUTO-", ("SAVE.BIN", "user7's game"), ("sub/META.SFO", "m"));
            var vault = NewVault();
            Assert.NotNull(vault.Harvest(App(livePath), 7));

            // Someone else's (un-vaulted) progress is now live.
            Directory.Delete(Path.Combine(emu, "NPUA80247-AUTO-"), true);
            WriteLive("NPUA80247-AUTO-", ("SAVE.BIN", "guest progress, never vaulted"));

            Assert.True(vault.Seed(App(livePath), 7));

            // User 7's tree is back, byte-exact.
            Assert.Equal("user7's game", File.ReadAllText(Path.Combine(emu, "NPUA80247-AUTO-", "SAVE.BIN")));
            Assert.Equal("m", File.ReadAllText(Path.Combine(emu, "NPUA80247-AUTO-", "sub", "META.SFO")));
            // The guest's content was DISPLACED into the store-side graveyard, not deleted.
            var displaced = Directory.EnumerateFiles(Path.Combine(store, "_displaced"), "SAVE.BIN", SearchOption.AllDirectories).ToList();
            Assert.Single(displaced);
            Assert.Equal("guest progress, never vaulted", File.ReadAllText(displaced[0]));
        }

        [Fact]
        public void Seed_without_a_vault_entry_leaves_live_content_alone()
        {
            var livePath = Path.Combine(emu, "TITLE01");
            WriteLive("TITLE01", ("SAVE.BIN", "local pre-vault save"));
            Assert.False(NewVault().Seed(App(livePath), 7));
            Assert.Equal("local pre-vault save", File.ReadAllText(Path.Combine(emu, "TITLE01", "SAVE.BIN")));
        }

        [Fact]
        public void Seed_same_content_is_a_noop_without_displacement()
        {
            var livePath = Path.Combine(emu, "TITLE01");
            WriteLive("TITLE01", ("SAVE.BIN", "progress"));
            var vault = NewVault();
            vault.Harvest(App(livePath), 7);

            Assert.True(vault.Seed(App(livePath), 7)); // relaunch by the same user
            Assert.False(Directory.Exists(Path.Combine(store, "_displaced"))); // nothing displaced
        }

        [Fact]
        public void Vault_ops_are_noops_without_gameId_or_livePath()
        {
            var vault = NewVault();
            var noSave = new HeavyApp { Id = "x", Title = "X", Exe = "x.exe", ArcadeGameId = 1 };
            Assert.Null(vault.Harvest(noSave, 7));
            Assert.False(vault.Seed(noSave, 7));
            var noGame = App(Path.Combine(emu, "TITLE01"));
            noGame.ArcadeGameId = null;
            Assert.Null(vault.Harvest(noGame, 7));
        }
    }
}
