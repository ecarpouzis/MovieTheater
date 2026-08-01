using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
    /// Imports cheats into <see cref="ArcadeCheat"/> from two sources (see docs/arcade-cheats.md):
    ///
    /// <list type="number">
    ///   <item><b>Cheat codes</b> — the community libretro cheat database, a local clone of
    ///     <c>libretro/libretro-database</c> (<c>--cht &lt;dir&gt;</c> points at its <c>cht</c> folder). Matched to
    ///     ROMs per system by exact filename first, then by the same title/token matcher the box-art pipeline
    ///     uses.</item>
    ///   <item><b>PS2 core-option patches</b> (<c>--ps2-patches</c>) — the widescreen / no-interlacing table
    ///     extracted from the LRPS2 core itself (docs/arcade/ps2-core-patches.tsv). These become per-game
    ///     option rows, so the lobby only offers widescreen on the ~150 games the emulator can actually patch.</item>
    /// </list>
    ///
    /// <para><b>Bulk-job shape</b> (global rule): bounded work per invocation (<c>--limit</c>, cursor
    /// <c>--after-id</c>), prints <c>{processed, remaining, nextCursor}</c> so the caller can drive it to
    /// completion, and is idempotent — a game that already has rows from this source is skipped unless
    /// <c>--overwrite</c>. Dry-run by default; the only delete is scoped to one game's rows from the SAME
    /// source, so a code re-import can never drop the curated option rows (or vice versa).</para>
    /// </summary>
    [Command("arcade-cheats-import", Description = "Import libretro cheat codes + PS2 core patches into ArcadeCheat (dry-run unless --apply).")]
    public class ArcadeCheatsImportCommand : BasicDICommand, ICommand
    {
        [CommandOption("cht", Description = "Path to libretro-database's 'cht' folder (imports cheat codes).")]
        public string Cht { get; set; } = "";

        [CommandOption("ps2-patches", Description = "Path to the extracted PS2 core patch table TSV (imports widescreen/no-interlace option cheats).")]
        public string Ps2Patches { get; set; } = "";

        [CommandOption("dolphin-ini", Description = "Path to Dolphin's Sys/GameSettings folder (imports GameCube/Wii Gecko + ActionReplay cheats).")]
        public string DolphinIni { get; set; } = "";

        [CommandOption("dolphin-tool", Description = "Path to DolphinTool.exe — reads each disc's game id, which is what selects its cheats.")]
        public string DolphinTool { get; set; } = "";

        [CommandOption("roms-dir", Description = "ROM mount root, used to locate a disc when its SourceArchivePath is unset.")]
        public string RomsDir { get; set; } = "";

        [CommandOption("system", Description = "Only this system code (e.g. n64). Default: every supported system.")]
        public string System { get; set; } = "";

        [CommandOption("apply", Description = "Write changes. Omit for a dry run (default).")]
        public bool Apply { get; set; }

        [CommandOption("overwrite", Description = "Re-import games that already have rows from this source (default: skip them).")]
        public bool Overwrite { get; set; }

        [CommandOption("limit", Description = "Max ROMs to process this call (default 500). Bounded on purpose — loop with --after-id.")]
        public int Limit { get; set; } = 500;

        [CommandOption("after-id", Description = "Resume cursor: process only ArcadeGame.Id greater than this.")]
        public int AfterId { get; set; }

        private readonly IDbContextFactory<MovieDb> dbFactory;

        public ArcadeCheatsImportCommand(MovieTheaterConfiguration config) : base(config)
        {
            dbFactory = GetRequiredService<IDbContextFactory<MovieDb>>();
        }

        public async ValueTask ExecuteAsync(IConsole console)
        {
            var w = console.Output;
            if (string.IsNullOrWhiteSpace(Cht) && string.IsNullOrWhiteSpace(Ps2Patches) && string.IsNullOrWhiteSpace(DolphinIni))
            {
                w.WriteLine("Nothing to do: pass --cht <dir>, --ps2-patches <file> and/or --dolphin-ini <dir>.");
                return;
            }

            if (!string.IsNullOrWhiteSpace(Ps2Patches))
                await ImportPs2PatchesAsync(console);

            if (!string.IsNullOrWhiteSpace(DolphinIni))
                await ImportDolphinIniCheatsAsync(console);

            if (!string.IsNullOrWhiteSpace(Cht))
                await ImportCheatCodesAsync(console);
        }

        // ── 1. PS2 widescreen / no-interlacing, gated by the core's own patch table ──────────────────────
        //
        // The TSV is (kind, title, region) as the core logs it. Match on normalized title AND region, because
        // the patches are per-dump: "Ace Combat Zero (NTSC-U)" is patched, the PAL disc separately so. A game
        // whose region we can't line up is SKIPPED, never guessed — a widescreen toggle that silently does
        // nothing is exactly what we're trying to avoid.
        private async Task ImportPs2PatchesAsync(IConsole console)
        {
            var w = console.Output;
            var path = Path.GetFullPath(Ps2Patches);
            if (!File.Exists(path)) { w.WriteLine($"PS2 patch table not found: {path}"); return; }

            // (kind, normalized title, region) → the option cheat to write.
            var wanted = new Dictionary<(string Kind, string Norm, string Region), bool>();
            foreach (var line in await File.ReadAllLinesAsync(path))
            {
                if (line.Length == 0 || line[0] == '#' || line.StartsWith("kind\t", StringComparison.Ordinal)) continue;
                var parts = line.Split('\t');
                if (parts.Length < 3) continue;
                var region = MapPs2Region(parts[2]);
                if (region == null) continue;
                wanted[(parts[0], ArcadeBoxArtIndex.Normalize(parts[1]), region)] = true;
            }
            w.WriteLine($"PS2 patch table: {wanted.Count} (kind, title, region) entries.");

            await using var db = await dbFactory.CreateDbContextAsync();
            var games = await db.ArcadeGames
                .Where(g => g.System == "ps2" && g.IsEnabled && g.Id > AfterId)
                .OrderBy(g => g.Id).Take(Limit).ToListAsync();
            var remaining = await db.ArcadeGames.CountAsync(g => g.System == "ps2" && g.IsEnabled && g.Id > AfterId) - games.Count;

            int wsHits = 0, niHits = 0, wrote = 0, skippedExisting = 0;
            foreach (var g in games)
            {
                var norm = ArcadeBoxArtIndex.Normalize(g.Title);
                var region = g.Region ?? "Unknown";
                var rows = new List<ArcadeCheat>();
                if (wanted.ContainsKey(("widescreen", norm, region)))
                {
                    rows.Add(OptionRow(g.Id, -2, ArcadeCheatCatalog.Ps2Widescreen));
                    wsHits++;
                }
                if (wanted.ContainsKey(("nointerlacing", norm, region)))
                {
                    rows.Add(OptionRow(g.Id, -1, ArcadeCheatCatalog.Ps2NoInterlacing));
                    niHits++;
                }
                if (rows.Count == 0) continue;

                var had = await db.ArcadeCheats.AnyAsync(c => c.ArcadeGameId == g.Id && c.Source == "pcsx2-gamedb");
                if (had && !Overwrite) { skippedExisting++; continue; }

                w.WriteLine($"  [ps2] {g.Title} ({region}) → {string.Join(", ", rows.Select(r => r.Name))}");
                if (Apply)
                {
                    if (had)
                        db.ArcadeCheats.RemoveRange(await db.ArcadeCheats
                            .Where(c => c.ArcadeGameId == g.Id && c.Source == "pcsx2-gamedb").ToListAsync());
                    db.ArcadeCheats.AddRange(rows);
                }
                wrote += rows.Count;
            }
            if (Apply) await db.SaveChangesAsync();

            var nextCursor = games.Count > 0 ? games[^1].Id : AfterId;
            w.WriteLine($"{(Apply ? "APPLIED" : "DRY RUN")} ps2-patches: processed={games.Count} widescreen={wsHits} nointerlace={niHits} " +
                        $"rows={wrote} skipped-existing={skippedExisting} remaining={remaining} nextCursor={nextCursor}");
        }

        private static ArcadeCheat OptionRow(int gameId, int ordinal, ArcadeCheatCatalog.OptionCheat o) => new()
        {
            ArcadeGameId = gameId, Kind = "option", Ordinal = ordinal, Name = o.Name,
            OptionKey = o.Key, OptionValue = o.Value, DefaultOn = o.DefaultOn, Note = o.Note, Source = "pcsx2-gamedb",
        };

        // The core logs NTSC-U / NTSC-J / PAL / PAL-M / NTSC; our rows carry the ingest's Region vocabulary.
        // "NTSC" alone is ambiguous (could be U or J) → null = skip, rather than mis-assign a patch.
        private static string? MapPs2Region(string coreRegion) => coreRegion.Trim().ToUpperInvariant() switch
        {
            "NTSC-U" => "USA",
            "NTSC-J" => "Japan",
            "PAL" or "PAL-M" => "Europe",
            _ => null,
        };

        // ── 2. GameCube / Wii cheats from Dolphin's own Sys/GameSettings INIs ────────────────────────────
        //
        // These do not come from the community cheat database — upstream has no cht folder for either system,
        // and Dolphin's retro_cheat_set could not use one anyway: it only ENABLES a code it already loaded from
        // its own INIs, matched by re-serializing it and comparing strings (DolphinGameIni explains the format).
        //
        // The match key is the disc's GAME ID read out of the image, not its filename. That costs a DolphinTool
        // invocation per ROM (~0.1 s local, ~0.3 s over the NAS) and buys the one thing filename matching can
        // never give: it is impossible to hand a game another region's codes, because the region IS the id.
        private async Task ImportDolphinIniCheatsAsync(IConsole console)
        {
            var w = console.Output;
            var iniDir = Path.GetFullPath(DolphinIni);
            if (!Directory.Exists(iniDir)) { w.WriteLine($"Dolphin GameSettings folder not found: {iniDir}"); return; }
            if (string.IsNullOrWhiteSpace(DolphinTool) || !File.Exists(DolphinTool))
            { w.WriteLine($"--dolphin-tool is required and must exist (got: '{DolphinTool}'). Without it we cannot read a disc's game id, and guessing one would hand a game another game's memory pokes."); return; }

            var systems = string.IsNullOrWhiteSpace(System)
                ? ArcadeCheatCatalog.DolphinIniSystems.ToList()
                : new List<string> { System.Trim().ToLowerInvariant() };
            systems = systems.Where(s => ArcadeCheatCatalog.UsesDolphinIniCheats(s)).ToList();
            if (systems.Count == 0) { w.WriteLine("No Dolphin-INI systems selected (gc, wii)."); return; }

            await using var db = await dbFactory.CreateDbContextAsync();
            var games = await db.ArcadeGames
                .Where(g => systems.Contains(g.System) && g.IsEnabled && g.Id > AfterId)
                .OrderBy(g => g.Id).Take(Limit).ToListAsync();

            int matched = 0, rows = 0, skippedExisting = 0, noImage = 0, noId = 0, noCodes = 0,
                truncated = 0, unreproducible = 0, tooLong = 0;
            int lastId = AfterId;

            foreach (var g in games)
            {
                lastId = Math.Max(lastId, g.Id);

                var had = await db.ArcadeCheats.AnyAsync(c => c.ArcadeGameId == g.Id && c.Source == DolphinSource);
                if (had && !Overwrite) { skippedExisting++; continue; }

                // The JIT/materialized copy under the ROM mount and the source archive are the same disc; take
                // whichever exists. A game we cannot open is SKIPPED and counted, never guessed at by title.
                var image = ResolveDiscPath(g);
                if (image == null) { noImage++; continue; }

                var header = await DolphinDiscId.ReadAsync(DolphinTool, image);
                if (header == null) { noId++; continue; }

                var texts = DolphinGameIni.IniChain(header.GameId, header.Revision)
                    .Select(f => Path.Combine(iniDir, f))
                    .Where(File.Exists)
                    .Select(File.ReadAllText)
                    .ToList();
                if (texts.Count == 0) { noCodes++; continue; }

                var cheats = DolphinGameIni.Parse(texts, out int skippedEncrypted);
                unreproducible += skippedEncrypted;

                // Gecko codes are whole PowerPC subroutines and a few run past the nvarchar(4000) column
                // (the "Activate AX Mode" style patches). Drop them WHOLE, exactly as ArcadeChtFile does:
                // half a code is not a weaker cheat, it is a poke at the wrong addresses. Reported, not silent.
                int beforeLength = cheats.Count;
                cheats = cheats.Where(c => c.Code.Length is > 0 and <= ArcadeChtFile.MaxCodeLength).ToList();
                tooLong += beforeLength - cheats.Count;

                if (cheats.Count == 0) { noCodes++; continue; }

                matched++;
                if (cheats.Count > ArcadeCheatCatalog.MaxCheatsPerGame)
                {
                    w.WriteLine($"  TRUNCATE [{g.System}] {g.Title}: {cheats.Count} cheats → keeping first {ArcadeCheatCatalog.MaxCheatsPerGame}.");
                    cheats = cheats.Take(ArcadeCheatCatalog.MaxCheatsPerGame).ToList();
                    truncated++;
                }

                w.WriteLine($"  [{g.System}] {g.Title} [{header.GameId}r{header.Revision}] → {cheats.Count} cheats");
                if (Apply)
                {
                    if (had)
                        db.ArcadeCheats.RemoveRange(await db.ArcadeCheats
                            .Where(c => c.ArcadeGameId == g.Id && c.Source == DolphinSource).ToListAsync());
                    for (int i = 0; i < cheats.Count; i++)
                        db.ArcadeCheats.Add(new ArcadeCheat
                        {
                            ArcadeGameId = g.Id, Kind = "code", Ordinal = i,
                            Name = Trunc(cheats[i].Name, 200), Code = cheats[i].Code, Source = DolphinSource,
                        });
                }
                rows += cheats.Count;
            }

            if (Apply) await db.SaveChangesAsync();

            var remaining = await db.ArcadeGames
                .CountAsync(g => systems.Contains(g.System) && g.IsEnabled && g.Id > lastId);

            w.WriteLine();
            w.WriteLine($"{(Apply ? "APPLIED" : "DRY RUN")} dolphin-ini: processed={games.Count} matched={matched} rows={rows} " +
                        $"skipped-existing={skippedExisting} no-image={noImage} no-game-id={noId} no-codes={noCodes} " +
                        $"truncated-games={truncated} encrypted-AR-dropped={unreproducible} over-length-dropped={tooLong} " +
                        $"remaining={remaining} nextCursor={lastId}");
            if (remaining > 0) w.WriteLine($"Re-run with --after-id {lastId} to continue.");
        }

        /// <summary>Provenance for the Dolphin-INI rows. Kept distinct from "libretro-cht" so a re-import of
        /// either source only ever deletes its own rows.</summary>
        private const string DolphinSource = "dolphin-ini";

        /// <summary>Where this game's disc image actually is: the archive/original if recorded, else under the
        /// ROM mount. Returns null rather than a path that isn't there.</summary>
        private string? ResolveDiscPath(ArcadeGame g)
        {
            if (!string.IsNullOrWhiteSpace(g.SourceArchivePath) && File.Exists(g.SourceArchivePath))
                return g.SourceArchivePath;
            if (!string.IsNullOrWhiteSpace(RomsDir) && !string.IsNullOrWhiteSpace(g.RomPath))
            {
                var p = Path.Combine(RomsDir, g.RomPath.Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(p)) return p;
            }
            return null;
        }

        // ── 3. Community cheat codes from libretro-database/cht ──────────────────────────────────────────
        private async Task ImportCheatCodesAsync(IConsole console)
        {
            var w = console.Output;
            var root = Path.GetFullPath(Cht);
            if (!Directory.Exists(root)) { w.WriteLine($"cht folder not found: {root}"); return; }

            var systems = string.IsNullOrWhiteSpace(System)
                ? ArcadeCheatCatalog.CodeSystems.ToList()
                : new List<string> { System.Trim().ToLowerInvariant() };

            systems = systems.Where(s =>
            {
                if (!ArcadeCheatCatalog.SupportsCheatCodes(s))
                { w.WriteLine($"  [{s}] skipped — its core does not apply libretro cheat codes."); return false; }
                var folder = ArcadeCheatCatalog.ChtFolder(s);
                if (folder == null || !Directory.Exists(Path.Combine(root, folder)))
                { w.WriteLine($"  [{s}] no cht folder ({folder})."); return false; }
                return true;
            }).ToList();
            if (systems.Count == 0) { w.WriteLine("No importable systems."); return; }

            await using var db = await dbFactory.CreateDbContextAsync();

            int totalGames = 0, matched = 0, cheatRows = 0, skippedExisting = 0, truncated = 0, noCodeSkipped = 0;
            int lastId = AfterId;

            // ONE id-ordered page across every code system. The cursor MUST be global: an earlier version
            // looped system-by-system with a shared `lastId`, and since each system's ids occupy their own
            // range (nes ~1-4k, ps1 ~58k), the first system to advance the cursor past a later system's range
            // silently excluded that whole system from every subsequent call. It reported remaining=0 having
            // processed 2,845 of 12,202 games.
            var games = await db.ArcadeGames
                .Where(g => systems.Contains(g.System) && g.IsEnabled && g.Id > AfterId)
                .OrderBy(g => g.Id).Take(Limit).ToListAsync();

            // Filename indexes are built once per system, on first use — a system that isn't in this page
            // never pays for its index (snes alone is 2,773 files).
            var indexes = new Dictionary<string, ArcadeChtIndex>(StringComparer.OrdinalIgnoreCase);
            ArcadeChtIndex IndexFor(string sys)
            {
                if (indexes.TryGetValue(sys, out var hit)) return hit;
                var dir = Path.Combine(root, ArcadeCheatCatalog.ChtFolder(sys)!);
                // The naming profile is per system on purpose — see ArcadeCheatCatalog.NamingProfileFor.
                var idx = ArcadeChtIndex.Build(Directory.EnumerateFiles(dir, "*.cht", SearchOption.TopDirectoryOnly),
                                               ArcadeCheatCatalog.NamingProfileFor(sys));
                w.WriteLine($"  [{sys}] {idx.Count} cht files indexed.");
                return indexes[sys] = idx;
            }

            foreach (var g in games)
            {
                totalGames++;
                lastId = Math.Max(lastId, g.Id);

                // Exact ROM name first, then same-title + overlapping-region. Nothing looser: a code from the
                // wrong dump pokes wrong addresses rather than failing cleanly. See ArcadeChtIndex.
                var chtPath = IndexFor(g.System).Match(g.CloudRetroGameKey);
                if (chtPath == null) continue;

                var had = await db.ArcadeCheats.AnyAsync(c => c.ArcadeGameId == g.Id && c.Source == "libretro-cht");
                if (had && !Overwrite) { skippedExisting++; continue; }

                var parsed = ArcadeChtFile.Parse(await File.ReadAllTextAsync(chtPath), out int withoutCode);
                noCodeSkipped += withoutCode;
                if (parsed.Count == 0) continue;

                matched++;
                if (parsed.Count > ArcadeCheatCatalog.MaxCheatsPerGame)
                {
                    w.WriteLine($"  TRUNCATE [{g.System}] {g.Title}: {parsed.Count} cheats → keeping first {ArcadeCheatCatalog.MaxCheatsPerGame}.");
                    parsed = parsed.Take(ArcadeCheatCatalog.MaxCheatsPerGame).ToList();
                    truncated++;
                }

                w.WriteLine($"  [{g.System}] {g.Title} → {parsed.Count} cheats ({Path.GetFileName(chtPath)})");
                if (Apply)
                {
                    if (had)
                        db.ArcadeCheats.RemoveRange(await db.ArcadeCheats
                            .Where(c => c.ArcadeGameId == g.Id && c.Source == "libretro-cht").ToListAsync());
                    foreach (var e in parsed)
                        db.ArcadeCheats.Add(new ArcadeCheat
                        {
                            ArcadeGameId = g.Id, Kind = "code", Ordinal = e.Ordinal,
                            Name = Trunc(e.Name, 200), Code = e.Code, Source = "libretro-cht",
                        });
                }
                cheatRows += parsed.Count;
            }

            if (Apply) await db.SaveChangesAsync();

            var remaining = await db.ArcadeGames
                .CountAsync(g => systems.Contains(g.System) && g.IsEnabled && g.Id > lastId);

            w.WriteLine();
            w.WriteLine($"{(Apply ? "APPLIED" : "DRY RUN")} cheat-codes: processed={totalGames} matched={matched} rows={cheatRows} " +
                        $"skipped-existing={skippedExisting} truncated-games={truncated} entries-without-code={noCodeSkipped} " +
                        $"remaining={remaining} nextCursor={lastId}");
            if (remaining > 0) w.WriteLine($"Re-run with --after-id {lastId} to continue.");
        }

        private static string Trunc(string s, int n) => s.Length <= n ? s : s[..n];
    }
}
