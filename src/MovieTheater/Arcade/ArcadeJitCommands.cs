using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
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
    /// Ingests a *master archive collection* into the <c>ArcadeGame</c> catalog as just-in-time (JIT)
    /// titles (docs/arcade-jit-cache.md). Unlike <see cref="ArcadeIngestCommand"/> — which catalogs ROM
    /// files that already live under the workers' ROM mount — this points at a directory of compressed
    /// disc images on the library drive (e.g. <c>L:\4 - Software\PSX Master Collection</c>, one
    /// <c>.7z</c> per game). No ROM is copied here: each row records the source archive in
    /// <c>SourceArchivePath</c>, and the ArcadeGateway extracts it into the ROM mount on demand at play
    /// time (then LRU-evicts it). The row is browsable immediately even though its <c>RomPath</c> is not
    /// yet materialized on disk.
    ///
    /// <para>For a Redump-style set the disc's <c>.cue</c> inside the archive shares the archive's base
    /// name, so <c>CloudRetroGameKey</c> = archive name sans <c>.7z</c> and the expected extracted
    /// <c>RomPath</c> = <c>&lt;folder&gt;/&lt;name&gt;.cue</c> — exactly what CloudRetro's library scan
    /// will expose once the gateway extracts it.</para>
    ///
    /// <para><b>Bulk-job rules</b> (same contract as arcade-ingest): dry-run unless <c>--apply</c>;
    /// bounded by <c>--limit</c>, ordered by archive name, resumable via <c>--after</c>; idempotent
    /// upsert on the (System, RomPath) unique key; never deletes.</para>
    /// </summary>
    [Command("arcade-jit-ingest", Description = "Catalog a master archive collection (.7z discs) as JIT ArcadeGames (dry-run unless --apply).")]
    public class ArcadeJitIngestCommand : BasicDICommand, ICommand
    {
        [CommandOption("archives", 'a', Description = "Directory of source archives (e.g. the PSX master collection of .7z discs).", IsRequired = true)]
        public string ArchivesDir { get; set; } = default!;

        [CommandOption("system", 's', Description = "System code for these archives (ps1, snes, …). Default ps1.")]
        public string System { get; set; } = "ps1";

        [CommandOption("ext", Description = "Archive extension to match. Default .7z.")]
        public string ArchiveExt { get; set; } = ".7z";

        [CommandOption("apply", Description = "Write changes. Omit for a dry run (default).")]
        public bool Apply { get; set; }

        [CommandOption("limit", Description = "Max archives to process this run (default 500).")]
        public int Limit { get; set; } = 500;

        [CommandOption("after", Description = "Resume cursor: skip archives whose name is ≤ this (from a prior run's nextCursor).")]
        public string After { get; set; } = "";

        private readonly IDbContextFactory<MovieDb> dbFactory;

        public ArcadeJitIngestCommand(MovieTheaterConfiguration config) : base(config)
        {
            dbFactory = GetRequiredService<IDbContextFactory<MovieDb>>();
        }

        public async ValueTask ExecuteAsync(IConsole console)
        {
            var w = console.Output;
            var dir = Path.GetFullPath(ArchivesDir);
            if (!Directory.Exists(dir)) { w.WriteLine($"Archive directory not found: {dir}"); return; }

            var sys = ArcadeSystems.All.FirstOrDefault(s => s.Code == System);
            if (sys == null) { w.WriteLine($"Unknown system '{System}'. Known: {string.Join(", ", ArcadeSystems.All.Select(s => s.Code))}."); return; }
            var folder = sys.Folders[0]; // the CloudRetro core folder these extract into (ps1 → psx)
            var ext = ArchiveExt.StartsWith('.') ? ArchiveExt : "." + ArchiveExt;

            await using var db = await dbFactory.CreateDbContextAsync();

            // When the source file IS the ROM the core loads (a bare .z64, a MAME .zip), keep that real
            // extension so the RomPath matches on disk and any pre-staged copy (no duplicate rows); when the
            // source is an archive that unpacks to a different ROM, use the system's nominal extension.
            var romExt = sys.Extensions.Any(e => e.Equals(ext, StringComparison.OrdinalIgnoreCase)) ? ext : sys.Extensions[0];
            var all = Directory.EnumerateFiles(dir, "*" + ext, SearchOption.TopDirectoryOnly)
                .Select(p => BuildEntry(p, sys.Code, folder, romExt, sys.MaxPlayers))
                .OrderBy(e => e.ArchiveName, StringComparer.Ordinal)
                .ToList();
            if (all.Count == 0)
                w.WriteLine($"No '{ext}' archives found under {dir}.");

            var pending = all.Where(e => string.CompareOrdinal(e.ArchiveName, After) > 0).ToList();
            var batch = pending.Take(Math.Max(1, Limit)).ToList();

            var existing = await db.ArcadeGames.ToListAsync();
            // Key case-insensitively to match SQL Server's default collation on the (System, RomPath)
            // unique index — otherwise a curated ROM and its No-Intro twin that differ only in case
            // (e.g. "…the Hedgehog…" vs "…The Hedgehog…") slip past an ordinal dictionary and collide at
            // INSERT. Tolerate any pre-existing case-variant rows too.
            static (string, string) Key(string sys, string rom) => (sys.ToLowerInvariant(), rom.ToLowerInvariant());
            var byKey = new Dictionary<(string, string), ArcadeGame>();
            foreach (var g in existing) byKey[Key(g.System, g.RomPath)] = g;

            int inserted = 0, updated = 0, skipped = 0;
            var addedThisRun = new HashSet<(string, string)>();
            foreach (var e in batch)
            {
                var key = Key(e.System, e.RomPath);
                if (byKey.TryGetValue(key, out var row))
                {
                    // Preserve hand-edits; only heal machine fields: re-enable, fill launch key, and keep
                    // the source-archive pointer in sync if the collection moved.
                    bool changed = false;
                    if (!row.IsEnabled) { if (Apply) row.IsEnabled = true; changed = true; }
                    if (string.IsNullOrEmpty(row.CloudRetroGameKey)) { if (Apply) row.CloudRetroGameKey = e.GameKey; changed = true; }
                    if (row.SourceArchivePath != e.Archive) { if (Apply) row.SourceArchivePath = e.Archive; changed = true; }
                    if (changed) updated++; else skipped++;
                }
                else if (!addedThisRun.Add(key))
                {
                    skipped++; // a case-variant of something already queued this run — don't double-insert
                }
                else
                {
                    if (Apply)
                    {
                        var (region, variant) = ArcadeRomTags.Parse(e.GameKey);
                        db.ArcadeGames.Add(new ArcadeGame
                        {
                            Title = e.Title,
                            SortTitle = e.SortTitle,
                            System = e.System,
                            RomPath = e.RomPath,
                            CloudRetroGameKey = e.GameKey,
                            SourceArchivePath = e.Archive,
                            MaxPlayers = e.MaxPlayers,
                            RatingCeiling = 0,   // unrestricted default; hand-raise mature titles as needed
                            Region = region,
                            Variant = variant,
                            IsEnabled = true,
                        });
                    }
                    inserted++;
                    w.WriteLine($"  + [{e.System}] {e.Title}");
                }
            }

            if (Apply) await db.SaveChangesAsync();

            var remaining = pending.Count - batch.Count;
            var nextCursor = batch.Count > 0 ? batch[^1].ArchiveName : After;

            w.WriteLine();
            w.WriteLine($"scanned {all.Count} archive(s); this run: {inserted} inserted, {updated} updated, {skipped} unchanged.");
            w.WriteLine($"{{ processed: {batch.Count}, remaining: {remaining}, nextCursor: \"{nextCursor}\" }}");
            if (!Apply) w.WriteLine("DRY RUN — nothing written. Re-run with --apply.");
            else
            {
                w.WriteLine("Now regenerate the gateway manifest: arcade-romcache-export --out <path>.");
                if (remaining > 0) w.WriteLine($"More to do: re-run with --after \"{nextCursor}\".");
            }
        }

        private static JitEntry BuildEntry(string archivePath, string system, string folder, string romExt, byte maxPlayers)
        {
            var name = Path.GetFileNameWithoutExtension(archivePath);   // "Air Combat (USA)" / "Super Mario World (USA)"
            return new JitEntry(
                Archive: Path.GetFullPath(archivePath),
                ArchiveName: Path.GetFileName(archivePath),
                System: system,
                Folder: folder,
                GameKey: name,                                          // == the launch-ROM base name inside the archive
                RomPath: $"{folder}/{name}{romExt}",                    // nominal extracted, CloudRetro-visible path
                Title: ArcadeNaming.CleanTitle(name),
                SortTitle: ArcadeNaming.ArticleInvert(ArcadeNaming.CleanTitle(name)),
                MaxPlayers: maxPlayers);
        }

        private sealed record JitEntry(
            string Archive, string ArchiveName, string System, string Folder,
            string GameKey, string RomPath, string Title, string SortTitle, byte MaxPlayers);
    }

    /// <summary>
    /// Exports the JIT catalog to the manifest the ArcadeGateway reads to materialize ROMs on demand
    /// (docs/arcade-jit-cache.md). The gateway holds no DB by design, so this is the single hand-off:
    /// every enabled <c>ArcadeGame</c> with a <c>SourceArchivePath</c> becomes an entry mapping the
    /// row's id (which the capability token carries) to its source archive + expected extract location.
    /// Re-run whenever the JIT catalog changes.
    /// </summary>
    [Command("arcade-romcache-export", Description = "Write the gateway's JIT ROM-cache manifest (gameId → source archive).")]
    public class ArcadeRomCacheExportCommand : BasicDICommand, ICommand
    {
        [CommandOption("out", 'o', Description = "Manifest output path (e.g. docker/arcade/arcade-romcache.json).", IsRequired = true)]
        public string OutPath { get; set; } = default!;

        private readonly IDbContextFactory<MovieDb> dbFactory;

        public ArcadeRomCacheExportCommand(MovieTheaterConfiguration config) : base(config)
        {
            dbFactory = GetRequiredService<IDbContextFactory<MovieDb>>();
        }

        public async ValueTask ExecuteAsync(IConsole console)
        {
            var w = console.Output;
            await using var db = await dbFactory.CreateDbContextAsync();

            var rows = await db.ArcadeGames
                .Where(g => g.IsEnabled && g.SourceArchivePath != null)
                .OrderBy(g => g.Id)
                .Select(g => new { g.Id, g.System, g.RomPath, g.CloudRetroGameKey, g.SourceArchivePath })
                .ToListAsync();

            var games = rows.Select(g => new ManifestGame(
                GameId: g.Id,
                GameKey: g.CloudRetroGameKey,
                System: g.System,
                Folder: FolderOf(g.RomPath),
                Archive: g.SourceArchivePath!,
                Exts: ExtsFor(g.System))).ToList();

            var manifest = new Manifest(Version: 1, Games: games);
            var json = JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true });

            var outFull = Path.GetFullPath(OutPath);
            Directory.CreateDirectory(Path.GetDirectoryName(outFull)!);
            await File.WriteAllTextAsync(outFull, json);

            w.WriteLine($"Wrote {games.Count} JIT game(s) → {outFull}");
        }

        // The extract folder = the first path segment of the expected RomPath ("psx/Foo.cue" → "psx").
        private static string FolderOf(string romPath)
        {
            var slash = romPath.IndexOf('/');
            return slash > 0 ? romPath[..slash] : "";
        }

        // The system's candidate ROM extensions, so the gateway can find the extracted launch ROM
        // regardless of which one an archive holds (SNES .sfc vs .smc, Genesis .md/.gen/.smd/.bin).
        private static string[] ExtsFor(string system) =>
            ArcadeSystems.All.FirstOrDefault(s => s.Code == system)?.Extensions ?? new[] { ".cue" };

        private sealed record Manifest(int Version, List<ManifestGame> Games);
        private sealed record ManifestGame(int GameId, string GameKey, string System, string Folder, string Archive, string[] Exts);
    }

    /// <summary>
    /// Backfills <c>ArcadeGame.Region</c> + <c>ArcadeGame.Variant</c> for the existing catalog by parsing
    /// each game's ROM filename (<c>CloudRetroGameKey</c>) with <see cref="ArcadeRomTags"/> — the data the
    /// arcade-lobby Region + mods filters run on. Bulk-job rules: dry-run unless <c>--apply</c>; bounded by
    /// <c>--limit</c>, resumable via <c>--after &lt;id&gt;</c>; fills only empty fields unless <c>--retag</c>.
    /// </summary>
    [Command("arcade-tag-backfill", Description = "Parse Region + Variant from each game's ROM filename (dry-run unless --apply).")]
    public class ArcadeTagBackfillCommand : BasicDICommand, ICommand
    {
        [CommandOption("apply", Description = "Write changes. Omit for a dry run (default).")]
        public bool Apply { get; set; }

        [CommandOption("limit", Description = "Max games to process this run (default 20000).")]
        public int Limit { get; set; } = 20000;

        [CommandOption("after", Description = "Resume cursor: skip games whose Id ≤ this (from a prior nextCursor).")]
        public int After { get; set; }

        [CommandOption("retag", Description = "Overwrite existing Region/Variant (default: fill only empty ones).")]
        public bool Retag { get; set; }

        private readonly IDbContextFactory<MovieDb> dbFactory;

        public ArcadeTagBackfillCommand(MovieTheaterConfiguration config) : base(config)
        {
            dbFactory = GetRequiredService<IDbContextFactory<MovieDb>>();
        }

        public async ValueTask ExecuteAsync(IConsole console)
        {
            var w = console.Output;
            await using var db = await dbFactory.CreateDbContextAsync();

            var batch = await db.ArcadeGames.Where(g => g.Id > After)
                .OrderBy(g => g.Id).Take(Math.Max(1, Limit)).ToListAsync();

            int changed = 0;
            var regions = new Dictionary<string, int>();
            var variants = new Dictionary<string, int>();
            foreach (var g in batch)
            {
                var (region, variant) = ArcadeRomTags.Parse(g.CloudRetroGameKey);
                if ((Retag || string.IsNullOrEmpty(g.Region)) && g.Region != region) { if (Apply) g.Region = region; changed++; }
                if ((Retag || string.IsNullOrEmpty(g.Variant)) && g.Variant != variant) { if (Apply) g.Variant = variant; }
                regions[region] = regions.GetValueOrDefault(region) + 1;
                variants[variant] = variants.GetValueOrDefault(variant) + 1;
            }
            if (Apply) await db.SaveChangesAsync();

            var nextCursor = batch.Count > 0 ? batch[^1].Id : After;
            var remaining = await db.ArcadeGames.CountAsync(g => g.Id > nextCursor);

            w.WriteLine("regions: " + string.Join(", ", regions.OrderByDescending(k => k.Value).Select(k => $"{k.Key}:{k.Value}")));
            w.WriteLine("variants: " + string.Join(", ", variants.OrderByDescending(k => k.Value).Select(k => $"{k.Key}:{k.Value}")));
            w.WriteLine($"{{ processed: {batch.Count}, changed: {changed}, remaining: {remaining}, nextCursor: {nextCursor} }}");
            if (!Apply) w.WriteLine("DRY RUN — nothing written. Re-run with --apply.");
            else if (remaining > 0) w.WriteLine($"More to do: re-run with --after {nextCursor}.");
        }
    }

    /// <summary>Shared title tidying for the arcade catalog (drop No-Intro/GoodTools tags; article-invert
    /// the sort key). Kept here so both ingest commands agree on naming.</summary>
    internal static class ArcadeNaming
    {
        public static string CleanTitle(string name)
        {
            var t = name;
            int cut = t.IndexOfAny(new[] { '(', '[' });
            if (cut > 0) t = t[..cut];
            return t.Replace('_', ' ').Trim();
        }

        public static string ArticleInvert(string title)
        {
            foreach (var article in new[] { "The ", "A ", "An " })
                if (title.StartsWith(article, StringComparison.OrdinalIgnoreCase))
                    return title[article.Length..].TrimEnd() + ", " + article.Trim();
            return title;
        }
    }
}
