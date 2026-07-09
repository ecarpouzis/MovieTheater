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
            if (string.IsNullOrWhiteSpace(Cht) && string.IsNullOrWhiteSpace(Ps2Patches))
            {
                w.WriteLine("Nothing to do: pass --cht <dir> and/or --ps2-patches <file>.");
                return;
            }

            if (!string.IsNullOrWhiteSpace(Ps2Patches))
                await ImportPs2PatchesAsync(console);

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

        // ── 2. Community cheat codes from libretro-database/cht ──────────────────────────────────────────
        private async Task ImportCheatCodesAsync(IConsole console)
        {
            var w = console.Output;
            var root = Path.GetFullPath(Cht);
            if (!Directory.Exists(root)) { w.WriteLine($"cht folder not found: {root}"); return; }

            var systems = string.IsNullOrWhiteSpace(System)
                ? ArcadeCheatCatalog.CodeSystems.ToList()
                : new List<string> { System.Trim().ToLowerInvariant() };

            await using var db = await dbFactory.CreateDbContextAsync();

            int totalGames = 0, matched = 0, cheatRows = 0, skippedExisting = 0, truncated = 0, noCodeSkipped = 0;
            int lastId = AfterId;
            int budget = Limit;

            foreach (var sys in systems)
            {
                if (budget <= 0) break;
                if (!ArcadeCheatCatalog.SupportsCheatCodes(sys))
                {
                    w.WriteLine($"  [{sys}] skipped — its core does not apply libretro cheat codes.");
                    continue;
                }
                var folder = ArcadeCheatCatalog.ChtFolder(sys);
                var dir = folder == null ? null : Path.Combine(root, folder);
                if (dir == null || !Directory.Exists(dir)) { w.WriteLine($"  [{sys}] no cht folder ({folder})."); continue; }

                // Exact ROM name first, then same-title + overlapping-region. Nothing looser: a code from the
                // wrong dump pokes wrong addresses rather than failing cleanly. See ArcadeChtIndex.
                var files = Directory.EnumerateFiles(dir, "*.cht", SearchOption.TopDirectoryOnly).ToList();
                var index = ArcadeChtIndex.Build(files);
                w.WriteLine($"  [{sys}] {index.Count} cht files indexed.");

                var games = await db.ArcadeGames
                    .Where(g => g.System == sys && g.IsEnabled && g.Id > AfterId)
                    .OrderBy(g => g.Id).Take(budget).ToListAsync();

                foreach (var g in games)
                {
                    totalGames++; budget--; lastId = Math.Max(lastId, g.Id);

                    var chtPath = index.Match(g.CloudRetroGameKey);
                    if (chtPath == null) continue;

                    var had = await db.ArcadeCheats.AnyAsync(c => c.ArcadeGameId == g.Id && c.Source == "libretro-cht");
                    if (had && !Overwrite) { skippedExisting++; continue; }

                    var parsed = ArcadeChtFile.Parse(await File.ReadAllTextAsync(chtPath), out int withoutCode);
                    noCodeSkipped += withoutCode;
                    if (parsed.Count == 0) continue;

                    matched++;
                    if (parsed.Count > ArcadeCheatCatalog.MaxCheatsPerGame)
                    {
                        w.WriteLine($"  TRUNCATE [{sys}] {g.Title}: {parsed.Count} cheats → keeping first {ArcadeCheatCatalog.MaxCheatsPerGame}.");
                        parsed = parsed.Take(ArcadeCheatCatalog.MaxCheatsPerGame).ToList();
                        truncated++;
                    }

                    w.WriteLine($"  [{sys}] {g.Title} → {parsed.Count} cheats ({Path.GetFileName(chtPath)})");
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
                    if (Apply && cheatRows % 2000 == 0) await db.SaveChangesAsync();
                }
            }

            if (Apply) await db.SaveChangesAsync();

            int remaining = 0;
            foreach (var sys in systems.Where(ArcadeCheatCatalog.SupportsCheatCodes))
                remaining += await db.ArcadeGames.CountAsync(g => g.System == sys && g.IsEnabled && g.Id > lastId);

            w.WriteLine();
            w.WriteLine($"{(Apply ? "APPLIED" : "DRY RUN")} cheat-codes: processed={totalGames} matched={matched} rows={cheatRows} " +
                        $"skipped-existing={skippedExisting} truncated-games={truncated} entries-without-code={noCodeSkipped} " +
                        $"remaining={remaining} nextCursor={lastId}");
            if (remaining > 0) w.WriteLine($"Re-run with --after-id {lastId} to continue.");
        }

        private static string Trunc(string s, int n) => s.Length <= n ? s : s[..n];
    }
}
