using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using MovieTheater.ArcadeGateway;

namespace MovieTheater.Tests
{
    /// <summary>
    /// Exercises the gateway's durable save store (docs/arcade-saves-plan.md): harvest a session's files
    /// out of the /saves mount → seed a slot back → fresh-clear, plus idempotency and the retention guard
    /// (prune oldest UNNAMED auto-states only; never SRAM, never a labeled snapshot). Pure file I/O — no
    /// emulator or 7-Zip needed.
    /// </summary>
    public class SaveStoreTests : IDisposable
    {
        private readonly string root;
        private readonly string mount;
        private readonly string store;

        public SaveStoreTests()
        {
            root = Path.Combine(Path.GetTempPath(), "savestore-test-" + Guid.NewGuid().ToString("N")[..8]);
            mount = Path.Combine(root, "saves");
            store = Path.Combine(root, "savestore");
            Directory.CreateDirectory(mount);
            Directory.CreateDirectory(store);
        }

        public void Dispose() { try { Directory.Delete(root, true); } catch { /* best effort */ } }

        // Debounce 0 by default: these tests write mount files and harvest in the same instant, which the
        // write-stability guard would otherwise (correctly) skip.
        private SaveStore NewStore(int maxStates = 20, long maxBytes = 100L * 1024 * 1024 * 1024, int debounceMs = 0) =>
            new(new SaveStoreOptions
                {
                    StoreDir = store, SavesMountDir = mount, MaxStatesPerGame = maxStates,
                    MaxBytes = maxBytes, HarvestDebounceMs = debounceMs,
                },
                NullLogger.Instance);

        private void WriteMountSave(string sessionId, string ext, byte[] bytes) =>
            File.WriteAllBytes(Path.Combine(mount, sessionId + ext), bytes);

        private static byte[] Bytes(int n, int seed) { var b = new byte[n]; new Random(seed).NextBytes(b); return b; }

        private string Blob(int uid, int gid, string name) => Path.Combine(store, uid.ToString(), gid.ToString(), name);

        [Fact]
        public async Task Harvest_copies_sram_and_state_with_metadata()
        {
            const string sid = "3f2a___Zelda";
            WriteMountSave(sid, ".srm", Bytes(2048, 1));
            WriteMountSave(sid, ".dat", Bytes(64 * 1024, 2));
            var s = NewStore();

            var meta = await s.HarvestSessionAsync(userId: 7, gameId: 42, system: "gba", sessionId: sid, isAutosave: true);

            Assert.Equal(2, meta.Count);
            Assert.True(File.Exists(Blob(7, 42, "sram.srm")));
            Assert.True(File.Exists(Blob(7, 42, "slot-000.dat")));
            Assert.True(File.Exists(Blob(7, 42, "sram.srm.json")));   // sidecar
            var list = s.ListSaves(7, 42);
            Assert.Contains(list, m => m is { Kind: "sram", SlotId: 0 });
            Assert.Contains(list, m => m is { Kind: "state", SlotId: 0, IsAutosave: true });
        }

        [Fact]
        public async Task Harvest_is_idempotent_for_identical_bytes()
        {
            const string sid = "aa___Game";
            WriteMountSave(sid, ".srm", Bytes(1024, 5));
            var s = NewStore();

            var first = await s.HarvestSessionAsync(1, 1, "snes", sid, true);
            Assert.Single(first);
            var mtime = File.GetLastWriteTimeUtc(Blob(1, 1, "sram.srm"));
            await Task.Delay(20);

            var second = await s.HarvestSessionAsync(1, 1, "snes", sid, true); // same bytes
            Assert.Empty(second);                                              // no rewrite reported
            Assert.Equal(mtime, File.GetLastWriteTimeUtc(Blob(1, 1, "sram.srm")));
        }

        [Fact]
        public async Task Seed_round_trips_into_a_new_session()
        {
            const string src = "src___G";
            var srm = Bytes(4096, 9);
            var dat = Bytes(20000, 10);
            WriteMountSave(src, ".srm", srm);
            WriteMountSave(src, ".dat", dat);
            var s = NewStore();
            await s.HarvestSessionAsync(3, 99, "n64", src, true);

            const string dstSession = "fresh___G";
            var seeded = s.SeedSession(userId: 3, gameId: 99, sessionId: dstSession, slotId: 0);

            Assert.True(seeded);
            Assert.Equal(dat, File.ReadAllBytes(Path.Combine(mount, dstSession + ".dat")));
            Assert.Equal(srm, File.ReadAllBytes(Path.Combine(mount, dstSession + ".srm")));
        }

        [Fact]
        public void Seed_returns_false_when_no_stored_save()
        {
            var s = NewStore();
            Assert.False(s.SeedSession(1, 1, "sess___G", slotId: 0));
        }

        [Fact]
        public async Task Delete_removes_the_mount_copy_so_it_cannot_resurrect()
        {
            // A REAL deterministic id — DeleteSave finds the mount counterpart by parsing candidates.
            const string sid = "sv-7-42-0-n64___Snowboard Kids (USA)";
            WriteMountSave(sid, ".srm", Bytes(2048, 1));
            WriteMountSave(sid, ".dat", Bytes(4096, 2));
            var s = NewStore();
            await s.HarvestSessionAsync(7, 42, "n64", sid, true);

            Assert.True(s.DeleteSave(7, 42, "state", 0));

            Assert.False(File.Exists(Blob(7, 42, "slot-000.dat")), "store blob deleted");
            Assert.False(File.Exists(Path.Combine(mount, sid + ".dat")), "mount .dat deleted with the Continue state");
            Assert.True(File.Exists(Path.Combine(mount, sid + ".srm")), "SRAM untouched by a state delete");

            Assert.True(s.DeleteSave(7, 42, "sram", 0));
            Assert.False(File.Exists(Path.Combine(mount, sid + ".srm")), "mount .srm deleted with the SRAM");

            // Nothing left for a sweep to resurrect.
            Assert.Empty(await s.HarvestSessionAsync(7, 42, "n64", sid, true));
        }

        [Fact]
        public async Task Delete_of_a_snapshot_slot_leaves_the_mount_alone()
        {
            const string sid = "sv-7-42-0-n64___Game";
            WriteMountSave(sid, ".dat", Bytes(4096, 3));
            var s = NewStore();
            await s.HarvestSessionAsync(7, 42, "n64", sid, true);
            var snap = await s.SnapshotCurrentAsync(7, 42, "n64", sid, "Boss fight");
            Assert.NotNull(snap);

            Assert.True(s.DeleteSave(7, 42, "state", snap!.SlotId));

            Assert.True(File.Exists(Path.Combine(mount, sid + ".dat")), "live Continue .dat survives a snapshot delete");
            Assert.True(File.Exists(Blob(7, 42, "slot-000.dat")), "Continue store blob survives too");
        }

        [Fact]
        public async Task Harvest_skips_files_still_being_written()
        {
            const string sid = "sv-1-2-0-n64___Game";
            WriteMountSave(sid, ".dat", Bytes(4096, 4));
            var s = NewStore(debounceMs: 60_000); // just-written file is inside the window

            Assert.Empty(await s.HarvestSessionAsync(1, 2, "n64", sid, true));

            // Once the writer has been quiet past the window, the same call harvests it.
            File.SetLastWriteTimeUtc(Path.Combine(mount, sid + ".dat"), DateTime.UtcNow.AddMinutes(-5));
            Assert.Single(await s.HarvestSessionAsync(1, 2, "n64", sid, true));
        }

        [Fact]
        public async Task Sweep_retries_an_unsettled_session_instead_of_marking_it_swept()
        {
            const string sid = "sv-1-2-0-n64___Game";
            WriteMountSave(sid, ".dat", Bytes(4096, 5));
            var s = NewStore(debounceMs: 60_000);

            Assert.Equal(0, await s.HarvestMountChangesAsync(_ => Task.FromResult(true)));

            // The file settles (no new writes) → the NEXT sweep must pick it up, i.e. the skip above
            // must not have advanced the per-session cursor.
            File.SetLastWriteTimeUtc(Path.Combine(mount, sid + ".dat"), DateTime.UtcNow.AddMinutes(-5));
            Assert.Equal(1, await s.HarvestMountChangesAsync(_ => Task.FromResult(true)));
        }

        [Fact]
        public async Task ClearSession_removes_stale_mount_files()
        {
            const string sid = "stale___G";
            WriteMountSave(sid, ".dat", Bytes(100, 1));
            WriteMountSave(sid, ".srm", Bytes(100, 2));
            var s = NewStore();

            s.ClearSession(sid);

            Assert.False(File.Exists(Path.Combine(mount, sid + ".dat")));
            Assert.False(File.Exists(Path.Combine(mount, sid + ".srm")));
        }

        [Fact]
        public async Task Retention_prunes_oldest_unnamed_states_but_never_sram_or_labeled()
        {
            const int uid = 5, gid = 5;
            var s = NewStore(maxStates: 2);
            // Craft the durable area directly: SRAM, a labeled snapshot, and 4 UNNAMED auto-state slots
            // with increasing timestamps. (S1 harvest only writes slot 0, so higher unnamed slots are the
            // retention target we construct here.)
            var gdir = Path.Combine(store, uid.ToString(), gid.ToString());
            Directory.CreateDirectory(gdir);
            WriteStored(gdir, "sram.srm", "sram", 0, label: null, when: T(1));
            WriteStored(gdir, "slot-000.dat", "state", 0, label: null, when: T(2));          // Continue — exempt
            WriteStored(gdir, "slot-001.dat", "state", 1, label: "Boss fight", when: T(3));   // labeled — exempt
            WriteStored(gdir, "slot-002.dat", "state", 2, label: null, when: T(4));           // unnamed (oldest)
            WriteStored(gdir, "slot-003.dat", "state", 3, label: null, when: T(5));
            WriteStored(gdir, "slot-004.dat", "state", 4, label: null, when: T(6));
            WriteStored(gdir, "slot-005.dat", "state", 5, label: null, when: T(7));           // unnamed (newest)

            // A harvest triggers PruneStates. With maxStates=2, keep the 2 newest UNNAMED (slots 5,4);
            // prune the 2 oldest unnamed (slots 2,3). SRAM, Continue(0), and the labeled slot survive.
            WriteMountSave("h___G", ".dat", Bytes(10, 1));
            await s.HarvestSessionAsync(uid, gid, "snes", "h___G", true);

            Assert.True(File.Exists(Path.Combine(gdir, "sram.srm")), "SRAM never pruned");
            Assert.True(File.Exists(Path.Combine(gdir, "slot-000.dat")), "Continue slot never pruned");
            Assert.True(File.Exists(Path.Combine(gdir, "slot-001.dat")), "labeled snapshot never pruned");
            Assert.True(File.Exists(Path.Combine(gdir, "slot-004.dat")), "newest unnamed kept");
            Assert.True(File.Exists(Path.Combine(gdir, "slot-005.dat")), "newest unnamed kept");
            Assert.False(File.Exists(Path.Combine(gdir, "slot-002.dat")), "oldest unnamed pruned");
            Assert.False(File.Exists(Path.Combine(gdir, "slot-003.dat")), "oldest unnamed pruned");
        }

        private static DateTime T(int min) => new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddMinutes(min);

        // Write a stored blob + its metadata sidecar directly (bypassing harvest) for the retention test.
        private void WriteStored(string gdir, string name, string kind, int slot, string? label, DateTime when)
        {
            var blob = Path.Combine(gdir, name);
            File.WriteAllBytes(blob, Bytes(256, slot + 1));
            var rel = Path.GetRelativePath(store, blob).Replace('\\', '/');
            var meta = new SaveMeta(5, 5, "snes", kind, slot, label, null, null, rel, 256, "sha" + slot, "online", true, when, when);
            File.WriteAllText(blob + ".json", JsonSerializer.Serialize(meta));
        }
    }
}
