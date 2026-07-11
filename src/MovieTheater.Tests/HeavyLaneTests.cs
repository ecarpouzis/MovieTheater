using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using MovieTheater.ArcadeGateway;

namespace MovieTheater.Tests
{
    /// <summary>
    /// Heavy lane gateway pieces (docs/arcade-heavy-lane-plan.md): the descriptor registry, the
    /// one-session lock's self-healing, and the chunked/resumable/verified stager — the three
    /// behaviors whose failure modes are "lane wedged forever" or "corrupt 45 GB ROM shipped".
    /// </summary>
    public class HeavyLaneTests : IDisposable
    {
        private readonly string root;

        public HeavyLaneTests()
        {
            root = Path.Combine(Path.GetTempPath(), "heavy-test-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(root);
        }

        public void Dispose()
        {
            try { Directory.Delete(root, true); } catch { /* best effort */ }
        }

        // ── Registry ────────────────────────────────────────────────────────────────────────────

        private string AppsDir()
        {
            var d = Path.Combine(root, "apps");
            Directory.CreateDirectory(d);
            return d;
        }

        private static string DescriptorJson(string id, string title, int? gameId = null, string? source = null) =>
            JsonSerializer.Serialize(new
            {
                id,
                title,
                system = "switch",
                arcadeGameId = gameId,
                exe = @"C:\Windows\System32\notepad.exe",
                argsTemplate = source != null ? "-f -g \"{rom}\"" : "-f",
                staging = source != null ? new { source } : null,
            });

        [Fact]
        public void Registry_loads_descriptors_and_resolves_by_gameId()
        {
            var dir = AppsDir();
            File.WriteAllText(Path.Combine(dir, "a.json"), DescriptorJson("switch-a", "Game A", 42));
            File.WriteAllText(Path.Combine(dir, "bad.json"), "{ not json");
            var reg = new HeavyAppRegistry(dir, NullLogger.Instance);

            Assert.Single(reg.All()); // the bad file is skipped, not fatal
            Assert.Equal("Game A", reg.Get("switch-a")!.Title);
            Assert.Equal("switch-a", reg.GetByArcadeGameId(42)!.Id);
            Assert.Null(reg.Get("nope"));
        }

        [Fact]
        public void Registry_ignores_duplicate_ids()
        {
            var dir = AppsDir();
            File.WriteAllText(Path.Combine(dir, "a.json"), DescriptorJson("dup", "First"));
            File.WriteAllText(Path.Combine(dir, "b.json"), DescriptorJson("dup", "Second"));
            var reg = new HeavyAppRegistry(dir, NullLogger.Instance);
            Assert.Single(reg.All());
        }

        // ── Lock ────────────────────────────────────────────────────────────────────────────────

        [Fact]
        public void Lock_is_exclusive_but_reentrant_per_app()
        {
            var l = new HeavyLock();
            Assert.True(l.TryAcquire("app-a", "deck", out _));
            Assert.False(l.TryAcquire("app-b", "phone", out var holder));
            Assert.Equal("app-a", holder.AppId);
            // The SAME app retrying prepare (Moonlight relaunch after a hiccup) must not deadlock.
            Assert.True(l.TryAcquire("app-a", null, out var again));
            Assert.Equal("deck", again.ClientName); // and keeps the known client
            Assert.True(l.Release("app-a"));
            Assert.True(l.TryAcquire("app-b", null, out _));
        }

        [Fact]
        public void Lock_self_heals_when_the_attached_process_is_dead()
        {
            var l = new HeavyLock(pidGraceSeconds: 0); // grace elapsed — the crash-recovery path
            Assert.True(l.TryAcquire("app-a", null, out _));
            // A PID that can't exist: kernel PIDs are multiples of 4; pick a huge non-multiple.
            Assert.True(l.Attach("app-a", int.MaxValue - 2));
            System.Threading.Thread.Sleep(15);           // let the 0 s grace window elapse
            Assert.Null(l.Current());                    // read self-heals
            Assert.True(l.TryAcquire("app-b", null, out _)); // lane is free again
        }

        [Fact]
        public void Lock_holds_a_dead_pid_through_the_grace_window_so_finish_can_harvest()
        {
            // The normal end-of-session order is: emulator exits → status polls → the launch
            // script's finish. Without the grace, any poll in that gap reclaims the lock and the
            // releasing finish (which triggers the save harvest) finds nothing — caught live.
            var l = new HeavyLock(pidGraceSeconds: 300);
            Assert.True(l.TryAcquire("app-a", null, out _));
            l.SetUser("app-a", 7);
            Assert.True(l.Attach("app-a", int.MaxValue - 2)); // already-dead pid
            Assert.NotNull(l.Current());                      // a status poll does NOT strip it
            Assert.Equal(7, l.Current()!.UserId);             // the owner survives for the harvest
            Assert.True(l.Release("app-a"));                  // the finish still releases
            Assert.Null(l.Current());
        }

        [Fact]
        public void Lock_holds_while_the_attached_process_lives()
        {
            var l = new HeavyLock();
            Assert.True(l.TryAcquire("app-a", null, out _));
            Assert.True(l.Attach("app-a", Environment.ProcessId)); // definitely alive
            Assert.NotNull(l.Current());
            Assert.False(l.TryAcquire("app-b", null, out _));
        }

        // ── Stager ──────────────────────────────────────────────────────────────────────────────

        private (HeavyStager stager, HeavyApp app, string source) NewStage(int sourceBytes, long chunk, long cap = 1L << 40)
        {
            var srcDir = Path.Combine(root, "library");
            Directory.CreateDirectory(srcDir);
            var source = Path.Combine(srcDir, "Game A [0100][v0].xci");
            var rnd = new Random(1234);
            var data = new byte[sourceBytes];
            rnd.NextBytes(data);
            File.WriteAllBytes(source, data);

            var cache = Path.Combine(root, "cache");
            var stager = new HeavyStager(cache, cap, chunk, NullLogger.Instance);
            var app = new HeavyApp
            {
                Id = "switch-game-a",
                Title = "Game A",
                Exe = @"C:\Windows\System32\notepad.exe",
                ArgsTemplate = "-g \"{rom}\"",
                Staging = new HeavyStaging { Source = source },
            };
            return (stager, app, source);
        }

        private static string StateOf(object progress) =>
            (string)progress.GetType().GetProperty("state")!.GetValue(progress)!;

        [Fact]
        public void Stager_copies_in_bounded_chunks_then_verifies_then_done()
        {
            // 20 000 bytes at the 4 096 floor = 5 copy + 5 verify chunks — bounded per call.
            var (stager, app, source) = NewStage(sourceBytes: 20_000, chunk: 4_096);
            Assert.False(stager.IsStaged(app));

            var states = new List<string>();
            for (int i = 0; i < 20 && !stager.IsStaged(app); i++)
                states.Add(StateOf(stager.Advance(app)));

            Assert.True(stager.IsStaged(app));
            Assert.Contains("verify", states);            // a verify pass actually ran
            Assert.True(states.Count >= 9);               // it took many bounded calls, not one big one
            var target = stager.TargetPathFor(app);
            Assert.Equal(File.ReadAllBytes(source), File.ReadAllBytes(target));
            Assert.False(File.Exists(target + ".partial"));
            // Idempotent once done.
            Assert.Equal("done", StateOf(stager.Advance(app)));
        }

        [Fact]
        public void Stager_resumes_across_a_restart_mid_copy()
        {
            var (stager, app, source) = NewStage(sourceBytes: 20_000, chunk: 4_096);
            stager.Advance(app); // one chunk (4 096 of 20 000)
            stager.Advance(app); // two

            // "Gateway restart": a NEW stager over the same cache dir picks up the persisted state.
            var resumed = new HeavyStager(Path.Combine(root, "cache"), 1L << 40, 4_096, NullLogger.Instance);
            for (int i = 0; i < 20 && !resumed.IsStaged(app); i++) resumed.Advance(app);

            Assert.True(resumed.IsStaged(app));
            Assert.Equal(File.ReadAllBytes(source), File.ReadAllBytes(resumed.TargetPathFor(app)));
        }

        [Fact]
        public void Stager_adopts_a_hand_staged_file_without_recopying()
        {
            var (stager, app, source) = NewStage(sourceBytes: 5_000, chunk: 1_000_000);
            // Simulate the pre-existing hand-staged copy at the exact target path.
            var target = stager.TargetPathFor(app);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(source, target);

            Assert.True(stager.IsStaged(app));            // no Advance call needed
            Assert.Equal("done", StateOf(stager.Progress(app)));
        }

        [Fact]
        public void Stager_refuses_over_cap_instead_of_evicting()
        {
            var (stager, app, _) = NewStage(sourceBytes: 20_000, chunk: 4_096, cap: 1_000);
            var result = stager.Advance(app);
            Assert.Equal("error", StateOf(result));
            var error = (string?)result.GetType().GetProperty("error")?.GetValue(result);
            Assert.Contains("never deletes", error);
        }

        [Fact]
        public void Stager_detects_a_corrupted_partial_and_restarts_clean()
        {
            var (stager, app, _) = NewStage(sourceBytes: 20_000, chunk: 4_096);
            stager.Advance(app); // chunk 1
            stager.Advance(app); // chunk 2

            // Corrupt the already-copied region behind the stager's back (torn write / disk fault).
            var partial = stager.TargetPathFor(app) + ".partial";
            using (var f = new FileStream(partial, FileMode.Open, FileAccess.Write, FileShare.None))
            { f.Position = 100; f.WriteByte(0xFF); }

            var states = new List<string>();
            for (int i = 0; i < 40 && !stager.IsStaged(app); i++)
                states.Add(StateOf(stager.Advance(app)));

            // The verify pass must have caught the corruption (a restart happened), and the final
            // staged file must still be byte-perfect.
            Assert.True(stager.IsStaged(app));
            Assert.Contains(states, s => s == "copy");    // the reset-to-copy after the mismatch
            Assert.Equal(File.ReadAllBytes(app.Staging!.Source), File.ReadAllBytes(stager.TargetPathFor(app)));
        }

        // ── Apollo app compile ──────────────────────────────────────────────────────────────────

        [Fact]
        public void Compile_wraps_in_the_launch_contract_when_configured()
        {
            var app = new HeavyApp { Id = "ps3-trash-panic", Title = "Trash Panic (PS3)", Exe = @"E:\RPCS3\rpcs3.exe", WorkingDir = @"E:\RPCS3" };
            var opt = new HeavyOptions { LaunchScript = @"D:\ArcadeStorage\heavy\heavy-launch.ps1" };
            var o = ApolloAdmin.Compile(app, null, opt);

            Assert.Contains("heavy-launch.ps1", (string)o["cmd"]!);
            Assert.Contains("-AppId \"ps3-trash-panic\"", (string)o["cmd"]!);
            Assert.Contains("-Finish", (string)o["prep-cmd"]![0]!["undo"]!);
            Assert.Equal(@"E:\RPCS3", (string)o["working-dir"]!);
        }

        [Fact]
        public void Art_shortcut_matches_the_artemis_trampoline_format()
        {
            // Format from Artemis' own ShortcutTrampoline/ShortcutHelper (moonlight-noir): line-based
            // [key] value; host_uuid + host_name + an app identifier are what make it launchable.
            var art = ApolloAdmin.BuildArtShortcut("HOST-UUID-1", "Ziggy", "APP-UUID-2", "Kirby and the Forgotten Land (Switch)");
            var lines = art.Split('\n');
            Assert.Contains("[host_uuid] HOST-UUID-1", lines);
            Assert.Contains("[host_name] Ziggy", lines);
            Assert.Contains("[app_uuid] APP-UUID-2", lines);
            Assert.Contains("[app_name] Kirby and the Forgotten Land (Switch)", lines);
            // Every non-empty, non-comment line must be [key] value — the trampoline throws otherwise.
            Assert.All(lines.Where(l => l.Length > 0 && !l.StartsWith('#')), l => Assert.StartsWith("[", l));
        }

        [Fact]
        public void Compile_falls_back_to_the_raw_command_without_a_script()
        {
            var app = new HeavyApp { Id = "x", Title = "X", Exe = @"C:\emu\emu.exe", ArgsTemplate = "-f" };
            var o = ApolloAdmin.Compile(app, null, new HeavyOptions());
            Assert.Equal("\"C:\\emu\\emu.exe\" -f", (string)o["cmd"]!);
            Assert.Null(o["prep-cmd"]);
        }
    }
}
