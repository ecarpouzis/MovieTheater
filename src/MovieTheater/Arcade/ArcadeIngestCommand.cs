using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using CliFx;
using CliFx.Attributes;
using CliFx.Infrastructure;
using Microsoft.EntityFrameworkCore;
using MovieTheater.Console;
using MovieTheater.Db;
using MovieTheater.Services;

namespace MovieTheater.Arcade
{
    /// <summary>
    /// Ingests a ROM directory tree into the <c>ArcadeGame</c> catalog (arcade-plan.md §5). The ROM
    /// root holds one subfolder per system (matching each CloudRetro core's <c>folder</c> key); files
    /// are classified by that folder + their extension, and each becomes an <c>ArcadeGame</c> row whose
    /// <c>CloudRetroGameKey</c> is the filename sans extension — exactly how CloudRetro's own library
    /// scan derives the launch name, so the catalog and the emulator agree on what to start.
    ///
    /// <para><b>Bulk-job rules.</b> Dry-run-first: prints <c>{inserted, updated, skipped, remaining,
    /// nextCursor}</c> and writes nothing unless <c>--apply</c>. Bounded + resumable: processes at most
    /// <c>--limit</c> files per run, ordered by relative path; the caller loops passing
    /// <c>--after &lt;nextCursor&gt;</c> until <c>remaining</c> is 0. Idempotent: upsert keyed on the
    /// (System, RomPath) unique constraint, so re-runs never double-insert and hand-edited Titles /
    /// rating ceilings are preserved. Never deletes: <c>--reconcile</c> flags rows whose file has
    /// vanished as <c>IsEnabled=false</c> (+ a note), it does not remove them.</para>
    /// </summary>
    [Command("arcade-ingest", Description = "Scan a ROM directory into the ArcadeGame catalog (dry-run unless --apply).")]
    public class ArcadeIngestCommand : BasicDICommand, ICommand
    {
        [CommandOption("roms", 'r', Description = "ROM root directory (holds per-system subfolders: snes, n64, …).", IsRequired = true)]
        public string RomsDir { get; set; } = default!;

        [CommandOption("apply", Description = "Write changes. Omit for a dry run (default).")]
        public bool Apply { get; set; }

        [CommandOption("limit", Description = "Max ROM files to process this run (default 500).")]
        public int Limit { get; set; } = 500;

        [CommandOption("after", Description = "Resume cursor: skip files whose relative path is ≤ this (from a prior run's nextCursor).")]
        public string After { get; set; } = "";

        [CommandOption("reconcile", Description = "Also flag catalog rows whose ROM file has vanished as IsEnabled=false (never deletes).")]
        public bool Reconcile { get; set; }

        private readonly IDbContextFactory<MovieDb> dbFactory;

        public ArcadeIngestCommand(MovieTheaterConfiguration config) : base(config)
        {
            dbFactory = GetRequiredService<IDbContextFactory<MovieDb>>();
        }

        public async ValueTask ExecuteAsync(IConsole console)
        {
            var w = console.Output;
            var root = Path.GetFullPath(RomsDir);
            if (!Directory.Exists(root)) { w.WriteLine($"ROM directory not found: {root}"); return; }

            await using var db = await dbFactory.CreateDbContextAsync();

            // Whole-tree scan is a cheap directory walk; the *bounded* work (the DB upsert) is sliced by --limit.
            var all = ScanRoms(root)
                .OrderBy(f => f.RomPath, StringComparer.Ordinal)
                .ToList();
            if (all.Count == 0)
            {
                w.WriteLine($"No ROMs found under {root}. Expected per-system subfolders: {string.Join(", ", ArcadeSystems.All.Select(s => s.Code))}.");
            }

            var pending = all.Where(f => string.CompareOrdinal(f.RomPath, After) > 0).ToList();
            var batch = pending.Take(Math.Max(1, Limit)).ToList();

            // Existing catalog, keyed by the unique (System, RomPath) — one load, no per-file query.
            // Case-insensitive to match SQL Server's collation (an ordinal dictionary would let a
            // case-variant slip through and collide at INSERT), plus an intra-run guard.
            var existing = await db.ArcadeGames.ToListAsync();
            static (string, string) Key(string sys, string rom) => (sys.ToLowerInvariant(), rom.ToLowerInvariant());
            var byKey = new Dictionary<(string, string), ArcadeGame>();
            foreach (var g in existing) byKey[Key(g.System, g.RomPath)] = g;

            int inserted = 0, updated = 0, skipped = 0;
            var addedThisRun = new HashSet<(string, string)>();
            foreach (var f in batch)
            {
                var key = Key(f.System, f.RomPath);
                if (byKey.TryGetValue(key, out var row))
                {
                    // Preserve hand-edits (Title, SortTitle, RatingCeiling, BoxArt). Only heal the machine
                    // fields: re-enable a row whose file is back, and fill an empty launch key.
                    bool changed = false;
                    if (!row.IsEnabled) { if (Apply) row.IsEnabled = true; changed = true; }
                    if (string.IsNullOrEmpty(row.CloudRetroGameKey)) { if (Apply) row.CloudRetroGameKey = f.GameKey; changed = true; }
                    if (changed) updated++; else skipped++;
                }
                else if (!addedThisRun.Add(key))
                {
                    skipped++; // a case-variant of something already queued this run
                }
                else
                {
                    if (Apply)
                    {
                        db.ArcadeGames.Add(new ArcadeGame
                        {
                            Title = f.Title,
                            SortTitle = f.SortTitle,
                            System = f.System,
                            RomPath = f.RomPath,
                            CloudRetroGameKey = f.GameKey,
                            MaxPlayers = f.MaxPlayers,
                            RatingCeiling = 0,   // unrestricted default; hand-raise per-title if needed
                            IsEnabled = true,
                        });
                    }
                    inserted++;
                    w.WriteLine($"  + [{f.System}] {f.Title}");
                }
            }

            if (Apply) await db.SaveChangesAsync();

            int reconciled = 0;
            if (Reconcile)
                reconciled = await ReconcileVanishedAsync(db, root, all, w);

            var remaining = pending.Count - batch.Count;
            var nextCursor = batch.Count > 0 ? batch[^1].RomPath : After;

            w.WriteLine();
            w.WriteLine($"scanned {all.Count} ROM(s); this run: {inserted} inserted, {updated} updated, {skipped} unchanged" +
                        (Reconcile ? $", {reconciled} flagged missing" : "") + ".");
            w.WriteLine($"{{ processed: {batch.Count}, remaining: {remaining}, nextCursor: \"{nextCursor}\" }}");
            if (!Apply) w.WriteLine("DRY RUN — nothing written. Re-run with --apply.");
            else if (remaining > 0) w.WriteLine($"More to do: re-run with --after \"{nextCursor}\".");
        }

        // Flag catalog rows whose file no longer exists (bounded to the current systems' rows). Never
        // deletes — sets IsEnabled=false + a note so a vanished ROM stops appearing but the row (and any
        // hand-edits) survive for when the file returns.
        private async Task<int> ReconcileVanishedAsync(MovieDb db, string root, List<ScannedRom> present, ConsoleWriter w)
        {
            var presentKeys = present.Select(f => (f.System, f.RomPath)).ToHashSet();
            var enabled = await db.ArcadeGames.Where(g => g.IsEnabled).ToListAsync();
            int flagged = 0;
            foreach (var row in enabled)
            {
                if (presentKeys.Contains((row.System, row.RomPath))) continue;
                // Double-check on disk (the scan only covers known extensions/folders) before flagging.
                var full = Path.Combine(root, row.RomPath.Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(full)) continue;
                if (Apply)
                {
                    row.IsEnabled = false;
                    row.Notes = Append(row.Notes, $"file missing at ingest {DateTime.UtcNow:yyyy-MM-dd}");
                }
                flagged++;
                w.WriteLine($"  - [{row.System}] {row.Title} (file missing)");
            }
            if (Apply && flagged > 0) await db.SaveChangesAsync();
            return flagged;
        }

        private static IEnumerable<ScannedRom> ScanRoms(string root)
        {
            foreach (var sys in ArcadeSystems.All)
            {
                foreach (var folder in sys.Folders)
                {
                    var dir = Path.Combine(root, folder);
                    if (!Directory.Exists(dir)) continue;
                    foreach (var path in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                    {
                        var ext = Path.GetExtension(path).ToLowerInvariant();
                        if (!sys.Extensions.Contains(ext)) continue;
                        var rel = Path.GetRelativePath(root, path).Replace('\\', '/');
                        var name = Path.GetFileNameWithoutExtension(path);
                        yield return new ScannedRom(
                            System: sys.Code,
                            RomPath: rel,
                            GameKey: name,                 // verbatim: what CloudRetro's scan uses as the launch name
                            Title: CleanTitle(name),
                            SortTitle: ArticleInvert(CleanTitle(name)),
                            MaxPlayers: sys.MaxPlayers);
                    }
                }
            }
        }

        // Display title: drop the common No-Intro / GoodTools trailing tags — "(USA)", "[!]", "(Rev 1)" —
        // that clutter a filename. The launch key keeps the raw filename; only the shown Title is tidied.
        private static string CleanTitle(string name)
        {
            var t = name;
            // Strip a TRAILING run of tag groups first ("(USA)", "[Hack]", "(Rev 1)" at the very end) —
            // covers the ordinary "Title (Region)" case AND a hack's own "[Hack]" suffix without
            // touching a subtitle that appears earlier in the name.
            t = Regex.Replace(t, @"(\s*[\(\[][^\)\]]*[\)\]])+\s*$", "");
            // ROM hacks conventionally name themselves "Base Game (Region) - Hack Name" — the region
            // tag sits BEFORE the hack's own subtitle, not at the end (e.g. "Super Mario 64 (USA) -
            // BAZR"). Cutting at the first tag as below would collapse every hack of the same base game
            // to one indistinguishable title. If a leading tag is immediately followed by " - <text>",
            // keep that text (it's the real name, not metadata).
            var hackName = Regex.Match(t, @"^([^\(\[]+?)\s*[\(\[][^\)\]]*[\)\]]\s*-\s*(.+)$");
            if (hackName.Success) t = $"{hackName.Groups[1].Value.Trim()} - {hackName.Groups[2].Value.Trim()}";
            int cut = t.IndexOfAny(new[] { '(', '[' });
            if (cut > 0) t = t[..cut];
            t = t.Replace('_', ' ').Trim();
            // Peel a trailing TOSEC bare version ("Sonic Adventure v1.005" → "Sonic Adventure") so
            // revisions collapse to one card. Shared helper == the ArcadeNaming copy (no drift).
            return ArcadeVersions.StripTrailingBareVersion(t);
        }

        // Article inversion for the sort key, same spirit as SimpleTitle ("The Legend…" → "Legend…, The").
        private static string ArticleInvert(string title)
        {
            foreach (var article in new[] { "The ", "A ", "An " })
                if (title.StartsWith(article, StringComparison.OrdinalIgnoreCase))
                    return title[article.Length..].TrimEnd() + ", " + article.Trim();
            return title;
        }

        private static string Append(string? existing, string note) =>
            string.IsNullOrWhiteSpace(existing) ? note : existing + "; " + note;

        private sealed record ScannedRom(string System, string RomPath, string GameKey, string Title, string SortTitle, byte MaxPlayers);
    }

    /// <summary>
    /// The system → (folder, extensions, seats) table shared by ingest classification. Folders match the
    /// CloudRetro core <c>folder</c> keys (§9 matrix / Appendix B2); seats are the system capability
    /// (SNES multitap 5, N64 4, handhelds 1). A game's real player count may be lower, but the room simply
    /// leaves the extra seats unfilled.
    /// </summary>
    public static class ArcadeSystems
    {
        public sealed record SystemDef(string Code, string[] Folders, string[] Extensions, byte MaxPlayers);

        public static readonly SystemDef[] All =
        {
            new("nes",     new[] { "nes" },          new[] { ".nes" },                          2),
            new("snes",    new[] { "snes" },         new[] { ".sfc", ".smc" },                  5),
            new("genesis", new[] { "genesis" },      new[] { ".md", ".gen", ".smd", ".bin" },   2),
            new("gb",      new[] { "gb" },           new[] { ".gb" },                           1),
            new("gbc",     new[] { "gbc" },          new[] { ".gbc" },                          1),
            new("gba",     new[] { "gba" },          new[] { ".gba" },                          1),
            new("n64",     new[] { "n64" },          new[] { ".n64", ".z64", ".v64" },          4),
            new("ps1",     new[] { "psx", "ps1" },   new[] { ".cue", ".chd", ".pbp" },          2),
            // FBNeo loads the MAME/Neo-Geo/CPS .zip romset WHOLE (never extracted) from the core's "mame"
            // folder — so arcade materializes as a JIT-COPY into roms/mame (source ext .zip ∈ these exts).
            new("arcade",  new[] { "mame" },             new[] { ".zip" },                       4),

            // ─── Systems added 2026-07 (GPU/GL 3D + 2D breadth). Codes match the config.yaml core
            // `folder` keys and the roms/ subfolders. MaxPlayers is the system capability; a title with
            // fewer real players just leaves the extra seats unfilled (same convention as snes=5). ───
            new("ps2",        new[] { "ps2" },        new[] { ".iso", ".cso", ".chd", ".bin", ".mdf", ".zso" }, 2),  // GL; pcsx2; scph39001.bin BIOS
            // GameCube via dolphin_libretro (GL 3D, native Windows GL worker). 4 controller ports — a
            // strong fit for the shared-room model (Mario Kart Double Dash, Smash Melee, Mario Party).
            // R:\Roms\Games\Nintendo GameCube is all .gcz (Dolphin's compressed format), read directly by
            // the core → RomCache COPIES it (plain-file branch, like ps2 .cso), never a 7z extract. Other
            // Dolphin disc formats listed so a pre-staged .iso/.rvz still maps. (Wii U is Cemu, not Dolphin —
            // still not ingested.)
            new("gc",         new[] { "gc" },         new[] { ".gcz", ".iso", ".rvz", ".ciso", ".gcm" },        4),
            // Wii, same dolphin_custom_libretro core/worker as gc, own "wii" folder (config.worker-gl.yaml).
            // JIT-sourced from L:\4 - Software\Wii\Disc Games (.rvz — Dolphin's own compressed format, read
            // directly, no decompress step needed like gcz). Default per-port device is Wiimote+Nunchuk
            // (RETRO_DEVICE_WIIMOTE_NC): Nunchuk stick + C/Z ride the left stick/X/Y, and — the reason this
            // was previously excluded turned out to be stale — the core's own dolphin_ir_mode option puts
            // the IR pointer on the RIGHT STICK and swing gestures behind a modifier button, both of which
            // ride our existing RetroPad-only input frame fine. True continuous-tilt games (steering-wheel-
            // style Wiimote holds) still don't map and will feel wrong; curate/flag those as reports come in.
            // WiiWare (.zip, NAND/WAD channel installs) is NOT covered by this ingest — different loading
            // model entirely, not staged as a disc.
            new("wii",        new[] { "wii" },        new[] { ".rvz", ".iso", ".wbfs", ".ciso", ".gcm" },       4),
            new("psp",        new[] { "psp" },        new[] { ".iso", ".cso", ".pbp", ".chd" },  1),  // GL; no BIOS; ad-hoc MP unsupported
            new("dc",         new[] { "dc" },         new[] { ".chd", ".gdi", ".cdi", ".cue" },  2),  // GL; dc_boot.bin + dc_flash.bin
            new("naomi",      new[] { "naomi" },      new[] { ".zip", ".chd", ".lst", ".dat" },  2),  // flycast arcade; naomi.zip BIOS
            new("atomiswave", new[] { "atomiswave" }, new[] { ".zip", ".chd" },                  2),  // flycast arcade; awbios.zip BIOS
            new("sms",        new[] { "sms" },        new[] { ".sms" },                          2),
            new("gg",         new[] { "gg" },         new[] { ".gg" },                           1),
            new("sg1000",     new[] { "sg1000" },     new[] { ".sg" },                           2),
            new("segacd",     new[] { "segacd" },     new[] { ".cue", ".chd", ".iso" },          2),  // bios_CD_U.bin
            new("sega32x",    new[] { "sega32x" },    new[] { ".32x", ".bin" },                  2),
            new("pce",        new[] { "pce" },        new[] { ".pce", ".sgx", ".cue", ".chd" },  2),  // PCE-CD needs syscard3.pce
            new("ngpc",       new[] { "ngpc" },       new[] { ".ngp", ".ngc" },                  1),
            new("wsc",        new[] { "wsc" },        new[] { ".ws", ".wsc", ".pc2" },           1),
            new("a2600",      new[] { "a2600" },      new[] { ".a26", ".bin" },                  2),
            new("a7800",      new[] { "a7800" },      new[] { ".a78" },                          2),
            new("lynx",       new[] { "lynx" },       new[] { ".lnx" },                          1),  // lynxboot.img
            new("vb",         new[] { "vb" },         new[] { ".vb", ".vboy" },                  1),
            new("fds",        new[] { "fds" },        new[] { ".fds" },                          2),  // disksys.rom
            new("neogeo",     new[] { "neogeo" },     new[] { ".zip" },                          2),  // fbneo; neogeo.zip BIOS
        };
    }
}
