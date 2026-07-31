using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
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

        [CommandOption("recursive", Description = "Also descend into subdirectories (GD-ROM-style dumps: one archive per named game folder, e.g. 'trizeal/gdl-0026.chd'). A matched file one level down uses its PARENT FOLDER name as the game key/title instead of its own arbitrary filename; top-level files keep the existing behavior.")]
        public bool Recursive { get; set; }

        [CommandOption("rom-ext", Description = "Override the extracted ROM extension used for RomPath (e.g. .chd for archives that wrap a CHD rather than the system default .cue). Needed when the archive extension is a wrapper type (.7z) but the ROM inside differs from the system's first extension.")]
        public string? RomExtOverride { get; set; }

        [CommandOption("strip-numeric-prefix", Description = "Drop a leading catalog number (\"0001 - Title\") when deriving Title/SortTitle. For Advanscene-numbered sets (the L: NDS collection). OPT-IN — a global strip would eat real titles like \"1943 - The Battle of Midway\".")]
        public bool StripNumericPrefix { get; set; }

        [CommandOption("no-bad-dump-tag", Description = "Don't let a GoodTools [b]/[o] bracket set Variant=BadDump. For sets whose [b] is unreliable (the L: NDS collection tags 1,047 of 6,600, including sole-US releases that are byte-identical to the dumps already in service).")]
        public bool NoBadDumpTag { get; set; }

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
            // --rom-ext overrides the nominal extension for cases where the archive wraps a non-default ROM
            // (e.g. .7z containing a .chd — the default would be .cue, which would miss the existing rows).
            var romExt = RomExtOverride != null
                ? (RomExtOverride.StartsWith('.') ? RomExtOverride : "." + RomExtOverride)
                : sys.Extensions.Any(e => e.Equals(ext, StringComparison.OrdinalIgnoreCase)) ? ext : sys.Extensions[0];
            var search = Recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
            var all = Directory.EnumerateFiles(dir, "*" + ext, search)
                .Select(p => BuildEntry(p, dir, sys.Code, folder, romExt, sys.MaxPlayers, StripNumericPrefix))
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
                        var (region, variant) = ArcadeRomTags.Parse(e.GameKey, badDumpTag: !NoBadDumpTag);
                        db.ArcadeGames.Add(new ArcadeGame
                        {
                            Title = e.Title,
                            SortTitle = e.SortTitle,
                            CollapseKey = ArcadeNaming.CollapseKey(e.Title),
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

        private static JitEntry BuildEntry(string archivePath, string rootDir, string system, string folder, string romExt, byte maxPlayers, bool stripNumericPrefix = false)
        {
            // GD-ROM/disc-style dumps: a file one level below the archives root sits in a folder named
            // after the game, with an arbitrary internal filename (e.g. "trizeal/gdl-0026.chd" — the
            // catalog serial, not the title). The immediate parent folder is the real name there;
            // top-level files (the ordinary case) keep using their own filename.
            var parentDir = Path.GetDirectoryName(archivePath)!;
            var name = string.Equals(Path.GetFullPath(parentDir), Path.GetFullPath(rootDir), StringComparison.OrdinalIgnoreCase)
                ? Path.GetFileNameWithoutExtension(archivePath)          // "Air Combat (USA)" / "Super Mario World (USA)"
                : Path.GetFileName(parentDir);                           // "trizeal" (the containing game folder)
            // The catalog number is stripped for DISPLAY only — GameKey and RomPath keep the verbatim
            // filename, because that is what the archive contains and what CloudRetro's library scan
            // matches on. Without this the number rides into Title and thence CollapseKey, so every
            // regional dump of one game stays its own lobby card (6,600 cards instead of 4,816).
            var titleSource = stripNumericPrefix ? ArcadeNaming.StripCatalogNumber(name) : name;
            var title = ArcadeNaming.CleanTitle(titleSource);
            return new JitEntry(
                Archive: Path.GetFullPath(archivePath),
                ArchiveName: Path.GetFileName(archivePath),
                System: system,
                Folder: folder,
                GameKey: name,                                          // == the launch-ROM base name inside the archive
                RomPath: $"{folder}/{name}{romExt}",                    // nominal extracted, CloudRetro-visible path
                Title: title,
                SortTitle: ArcadeNaming.ArticleInvert(title),
                MaxPlayers: maxPlayers);
        }

        private sealed record JitEntry(
            string Archive, string ArchiveName, string System, string Folder,
            string GameKey, string RomPath, string Title, string SortTitle, byte MaxPlayers);
    }

    /// <summary>
    /// Re-points EXISTING ArcadeGame rows onto a replacement archive, in place — for when a master
    /// collection is swapped for a differently-named one (e.g. the PSX collection moving from
    /// <c>L:\4 - Software\PSX Master Collection\*.7z</c> to a converted
    /// <c>R:\Roms\Games\Sony Playstation\*.7z</c>: same games, almost entirely different filenames, so a
    /// plain re-run of <c>arcade-jit-ingest</c> against the new directory would INSERT ~1,700 duplicate
    /// rows instead of updating the ones that already carry curated Title/Region/RatingCeiling edits).
    ///
    /// <para>Takes a reconciliation CSV (arcade_id → new archive path) built out-of-band — see
    /// <c>data/rom-catalog/psx_repoint_final.py</c> for the PSX case, which reused the project's existing
    /// title-normalization tooling to match old rows to new filenames and hand-resolved the handful of
    /// ambiguous ones. Only <c>SourceArchivePath</c>, <c>RomPath</c>, and <c>CloudRetroGameKey</c> change;
    /// Title/SortTitle/Region/Variant/RatingCeiling/IsEnabled are hand-edits and are left untouched.</para>
    ///
    /// <para>A mapped row is skipped (not an error) until its target archive actually exists on disk —
    /// so this command can be re-run after each conversion chunk lands and will pick up newly-ready rows
    /// automatically, in step with a slow/partial conversion. An optional drops CSV (arcade_id, reason)
    /// disables rows that have no replacement (never deleted — reversible).</para>
    ///
    /// <para><b>Bulk-job rules</b>: dry-run unless <c>--apply</c>; bounded by <c>--limit</c>, ordered by
    /// arcade id, resumable via <c>--after</c>; idempotent (re-running a fully-applied map is a no-op).</para>
    /// </summary>
    [Command("arcade-jit-repoint", Description = "Re-point existing ArcadeGame rows onto a replacement archive per a reconciliation CSV (dry-run unless --apply).")]
    public class ArcadeJitRepointCommand : BasicDICommand, ICommand
    {
        [CommandOption("map", Description = "CSV with an arcade_id column and a new archive path column (see data/rom-catalog/psx-repoint-map-FINAL.csv).", IsRequired = true)]
        public string MapPath { get; set; } = default!;

        [CommandOption("id-column", Description = "Header name of the arcade id column. Default arcade_id.")]
        public string IdColumn { get; set; } = "arcade_id";

        [CommandOption("path-column", Description = "Header name of the new archive path column. Default new_7z_path_R.")]
        public string PathColumn { get; set; } = "new_7z_path_R";

        [CommandOption("drops", Description = "Optional CSV with arcade_id + reason columns -- rows to disable (IsEnabled=false), applied in full every run.")]
        public string? DropsPath { get; set; }

        [CommandOption("apply", Description = "Write changes. Omit for a dry run (default).")]
        public bool Apply { get; set; }

        [CommandOption("limit", Description = "Max rows to repoint this run (default 500).")]
        public int Limit { get; set; } = 500;

        [CommandOption("after", Description = "Resume cursor: skip arcade ids <= this (from a prior run's nextCursor).")]
        public int After { get; set; }

        private readonly IDbContextFactory<MovieDb> dbFactory;

        public ArcadeJitRepointCommand(MovieTheaterConfiguration config) : base(config)
        {
            dbFactory = GetRequiredService<IDbContextFactory<MovieDb>>();
        }

        public async ValueTask ExecuteAsync(IConsole console)
        {
            var w = console.Output;
            if (!File.Exists(MapPath)) { w.WriteLine($"Map CSV not found: {MapPath}"); return; }

            var map = ReadCsv(MapPath);
            var idIdx = map.header.IndexOf(IdColumn);
            var pathIdx = map.header.IndexOf(PathColumn);
            if (idIdx < 0 || pathIdx < 0)
            {
                w.WriteLine($"Map CSV missing required column(s): {IdColumn}, {PathColumn}. Header: {string.Join(", ", map.header)}");
                return;
            }
            var mapRows = map.rows
                .Where(r => int.TryParse(r[idIdx], out _))
                .Select(r => (Id: int.Parse(r[idIdx]), NewArchivePath: r[pathIdx]))
                .OrderBy(r => r.Id)
                .ToList();

            await using var db = await dbFactory.CreateDbContextAsync();

            int disabled = 0;
            if (DropsPath != null)
            {
                if (!File.Exists(DropsPath)) { w.WriteLine($"Drops CSV not found: {DropsPath}"); return; }
                var drops = ReadCsv(DropsPath);
                var dropIdIdx = drops.header.IndexOf("arcade_id");
                var dropIds = drops.rows.Where(r => int.TryParse(r[dropIdIdx], out _)).Select(r => int.Parse(r[dropIdIdx])).ToHashSet();
                var dropRows = await db.ArcadeGames.Where(g => dropIds.Contains(g.Id)).ToListAsync();
                foreach (var g in dropRows)
                    if (g.IsEnabled) { if (Apply) g.IsEnabled = false; disabled++; }
                var notFound = dropIds.Except(dropRows.Select(g => g.Id)).ToList();
                if (notFound.Count > 0) w.WriteLine($"  ! {notFound.Count} drop id(s) not found in DB: {string.Join(", ", notFound)}");
            }

            var pending = mapRows.Where(r => r.Id > After).ToList();
            var ready = pending.Where(r => File.Exists(r.NewArchivePath)).ToList();
            var notYetConverted = pending.Count - ready.Count;
            var batch = ready.Take(Math.Max(1, Limit)).ToList();

            var ids = batch.Select(r => r.Id).ToHashSet();
            var rows = await db.ArcadeGames.Where(g => ids.Contains(g.Id)).ToDictionaryAsync(g => g.Id);

            int updated = 0, unchanged = 0, missing = 0;
            var claimedRomPaths = new HashSet<(string, string)>();
            foreach (var r in batch)
            {
                if (!rows.TryGetValue(r.Id, out var g)) { missing++; w.WriteLine($"  ! arcade id {r.Id} not found in DB (stale map?)"); continue; }

                var sys = ArcadeSystems.All.FirstOrDefault(s => s.Code == g.System);
                var folder = sys?.Folders[0] ?? FolderOf(g.RomPath);
                var archiveExt = Path.GetExtension(r.NewArchivePath);
                var romExt = sys != null && sys.Extensions.Any(e => e.Equals(archiveExt, StringComparison.OrdinalIgnoreCase))
                    ? archiveExt : (sys?.Extensions.FirstOrDefault() ?? ".cue");
                var gameKey = Path.GetFileNameWithoutExtension(r.NewArchivePath);
                var newRomPath = $"{folder}/{gameKey}{romExt}";

                var romKey = (g.System.ToLowerInvariant(), newRomPath.ToLowerInvariant());
                if (!claimedRomPaths.Add(romKey))
                {
                    w.WriteLine($"  ! arcade id {r.Id}: computed RomPath '{newRomPath}' collides with another row repointed this same run -- skipped, check the map.");
                    continue;
                }

                bool changed = g.SourceArchivePath != r.NewArchivePath || g.RomPath != newRomPath || g.CloudRetroGameKey != gameKey;
                if (changed)
                {
                    if (Apply) { g.SourceArchivePath = r.NewArchivePath; g.RomPath = newRomPath; g.CloudRetroGameKey = gameKey; }
                    updated++;
                }
                else unchanged++;
            }

            if (Apply) await db.SaveChangesAsync();

            var nextCursor = batch.Count > 0 ? batch[^1].Id : After;
            var remaining = ready.Count - batch.Count;

            w.WriteLine();
            w.WriteLine($"repointed {updated} row(s), {unchanged} already correct, {missing} not found, {disabled} disabled.");
            w.WriteLine($"{{ processed: {batch.Count}, remaining: {remaining}, notYetConverted: {notYetConverted}, nextCursor: {nextCursor} }}");
            if (!Apply) w.WriteLine("DRY RUN — nothing written. Re-run with --apply.");
            else
            {
                w.WriteLine("Now regenerate the gateway manifest: arcade-romcache-export --out <path>.");
                if (remaining > 0) w.WriteLine($"More to do: re-run with --after {nextCursor}.");
                if (notYetConverted > 0) w.WriteLine($"{notYetConverted} mapped row(s) still waiting on their archive to finish converting.");
            }
        }

        private static string FolderOf(string romPath)
        {
            var slash = romPath.IndexOf('/');
            return slash > 0 ? romPath[..slash] : "";
        }

        // Minimal RFC4180 CSV reader (quoted fields, embedded commas/quotes/newlines) -- no external
        // dependency for a handful of small reconciliation files.
        private static (List<string> header, List<List<string>> rows) ReadCsv(string path)
        {
            var records = new List<List<string>>();
            var field = new System.Text.StringBuilder();
            var record = new List<string>();
            bool inQuotes = false;
            var text = File.ReadAllText(path);
            void EndField() { record.Add(field.ToString()); field.Clear(); }
            void EndRecord() { EndField(); records.Add(record); record = new List<string>(); }
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (inQuotes)
                {
                    if (c == '"' && i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i++; }
                    else if (c == '"') inQuotes = false;
                    else field.Append(c);
                }
                else
                {
                    if (c == '"') inQuotes = true;
                    else if (c == ',') EndField();
                    else if (c == '\r') { }
                    else if (c == '\n') EndRecord();
                    else field.Append(c);
                }
            }
            if (field.Length > 0 || record.Count > 0) EndRecord();
            records.RemoveAll(r => r.Count == 1 && r[0].Length == 0);
            var header = records.Count > 0 ? records[0] : new List<string>();
            return (header, records.Skip(1).ToList());
        }
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

        [CommandOption("dat", Description = "FBNeo Arcade DAT for the arcade/neogeo romof dependency closure. Default data/arcade/fbneo-arcade.dat.")]
        public string DatPath { get; set; } = "data/arcade/fbneo-arcade.dat";

        private readonly IDbContextFactory<MovieDb> dbFactory;

        public ArcadeRomCacheExportCommand(MovieTheaterConfiguration config) : base(config)
        {
            dbFactory = GetRequiredService<IDbContextFactory<MovieDb>>();
        }

        public async ValueTask ExecuteAsync(IConsole console)
        {
            var w = console.Output;

            // The FBNeo DAT drives the arcade/neogeo dependency closure (romof parent+BIOS zips). Optional:
            // without it, non-arcade systems still export fine, but arcade games get no closure and can hit
            // "missing romset" at launch — so warn loudly.
            FbneoDat? dat = null;
            try { dat = FbneoDat.Load(DatPath); w.WriteLine($"FBNeo DAT v{dat.Version} ({dat.Count} games) loaded for romof closure."); }
            catch (Exception ex) { w.WriteLine($"WARNING: no FBNeo DAT ({ex.Message}) — arcade games will get NO dependency closure (may fail with 'missing romset')."); }

            await using var db = await dbFactory.CreateDbContextAsync();

            var rows = await db.ArcadeGames
                .Where(g => g.IsEnabled && g.SourceArchivePath != null)
                .OrderBy(g => g.Id)
                .ToListAsync();

            var games = new List<ManifestGame>();
            var folded = new HashSet<int>();

            // Multi-disc: one .m3u entry per multi-disc version — the gateway extracts every disc + writes
            // the playlist. Keyed by the disc-1 anchor id (what the room launches). (docs/arcade-dedupe-multidisc-plan.md)
            foreach (var titleGroup in rows.GroupBy(g => new { g.System, g.Title }))
                foreach (var (anchor, discs) in ArcadeVersions.MultiDiscGroups(titleGroup))
                {
                    games.Add(new ManifestGame(
                        GameId: anchor.Id,
                        GameKey: ArcadeVersions.M3uKey(anchor.CloudRetroGameKey),
                        System: anchor.System,
                        Folder: FolderOf(anchor.RomPath),
                        Archive: "",
                        Exts: new[] { ".m3u" },
                        Discs: discs.Select(d => new DiscRef(d.SourceArchivePath!, Path.GetFileName(d.RomPath))).ToArray()));
                    foreach (var d in discs) folded.Add(d.Id);
                }

            // Everything not folded into an .m3u above = ordinary single-disc / non-disc JIT rows.
            foreach (var g in rows.Where(g => !folded.Contains(g.Id)))
                games.Add(new ManifestGame(
                    GameId: g.Id, GameKey: g.CloudRetroGameKey, System: g.System,
                    Folder: FolderOf(g.RomPath), Archive: g.SourceArchivePath!, Exts: ExtsFor(g.System),
                    Deps: DepsFor(g, dat), CompanionPath: g.SourceCompanionPath));

            games = games.OrderBy(g => g.GameId).ToList();

            var manifest = new Manifest(Version: 1, Games: games);
            var json = JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true });

            var outFull = Path.GetFullPath(OutPath);
            Directory.CreateDirectory(Path.GetDirectoryName(outFull)!);
            // ATOMIC publish. The gateway hot-reloads this file on mtime, so writing ~10 MB in place gave
            // it a window to read a half-written manifest — a parse failure there used to strand the
            // gateway on a stale catalog (RomCache.LoadManifest). Write a temp file and move it into
            // place: NTFS makes the replace atomic, so a reader sees only the old or the new file.
            var tmp = outFull + ".tmp";
            await File.WriteAllTextAsync(tmp, json);
            File.Move(tmp, outFull, overwrite: true);

            int withDeps = games.Count(x => x.Deps is { Length: > 0 });
            int depRefs = games.Sum(x => x.Deps?.Length ?? 0);
            w.WriteLine($"Wrote {games.Count} JIT game(s) → {outFull}");
            w.WriteLine($"  {withDeps} game(s) carry a romof dependency closure ({depRefs} dep archive reference(s)).");
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

        // The FBNeo romof dependency closure for an arcade/neogeo game, as source-archive paths the gateway
        // must stage alongside the launch ROM (the split parent + BIOS zips, e.g. neogeo.zip). Each dep is
        // the sibling of the game's own source archive in the same folder, named "&lt;shortname&gt;&lt;ext&gt;".
        // Null for non-fbneo systems or games unknown to the DAT. Paths are constructed, not existence-checked
        // (RomCache tolerates an absent dep — that's an incomplete romset, surfaced by fbneo at launch).
        private static string[]? DepsFor(ArcadeGame g, FbneoDat? dat)
        {
            if (dat == null || g.SourceArchivePath == null || !dat.Contains(g.CloudRetroGameKey)) return null;
            var closure = dat.Closure(g.CloudRetroGameKey);   // [self, dep1, dep2, ...]
            if (closure.Count <= 1) return null;
            var dir = Path.GetDirectoryName(g.SourceArchivePath)!;
            var ext = Path.GetExtension(g.SourceArchivePath);  // ".zip"
            return closure.Skip(1).Select(name => Path.Combine(dir, name + ext)).ToArray();
        }

        private sealed record Manifest(int Version, List<ManifestGame> Games);
        private sealed record ManifestGame(int GameId, string GameKey, string System, string Folder, string Archive, string[] Exts, DiscRef[]? Discs = null, string[]? Deps = null, string? CompanionPath = null);
        private sealed record DiscRef(string Archive, string File);
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

        [CommandOption("system", 's', Description = "Only process this system's rows (nds, ps1, …). Default: all systems.")]
        public string? System { get; set; }

        [CommandOption("no-bad-dump-tag", Description = "Don't let a GoodTools [b]/[o] bracket set Variant=BadDump — see arcade-jit-ingest. Pair with --system so a set whose [b] is unreliable doesn't clear the tag on collections where it is meaningful.")]
        public bool NoBadDumpTag { get; set; }

        private readonly IDbContextFactory<MovieDb> dbFactory;

        public ArcadeTagBackfillCommand(MovieTheaterConfiguration config) : base(config)
        {
            dbFactory = GetRequiredService<IDbContextFactory<MovieDb>>();
        }

        public async ValueTask ExecuteAsync(IConsole console)
        {
            var w = console.Output;
            await using var db = await dbFactory.CreateDbContextAsync();

            var scoped = db.ArcadeGames.Where(g => g.Id > After);
            if (!string.IsNullOrWhiteSpace(System)) scoped = scoped.Where(g => g.System == System);
            var batch = await scoped.OrderBy(g => g.Id).Take(Math.Max(1, Limit)).ToListAsync();

            int changed = 0;
            var regions = new Dictionary<string, int>();
            var variants = new Dictionary<string, int>();
            foreach (var g in batch)
            {
                var (region, variant) = ArcadeRomTags.Parse(g.CloudRetroGameKey, badDumpTag: !NoBadDumpTag);
                if ((Retag || string.IsNullOrEmpty(g.Region)) && g.Region != region) { if (Apply) g.Region = region; changed++; }
                if ((Retag || string.IsNullOrEmpty(g.Variant)) && g.Variant != variant) { if (Apply) g.Variant = variant; }
                regions[region] = regions.GetValueOrDefault(region) + 1;
                variants[variant] = variants.GetValueOrDefault(variant) + 1;
            }
            if (Apply) await db.SaveChangesAsync();

            var nextCursor = batch.Count > 0 ? batch[^1].Id : After;
            var remaining = await db.ArcadeGames
                .Where(g => g.Id > nextCursor)
                .Where(g => string.IsNullOrWhiteSpace(System) || g.System == System)
                .CountAsync();

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
        public static string CleanTitle(string name) => CleanTitle(name, collapseFixes: true);

        /// <summary>Drops a leading catalog/release number from an Advanscene-style set name
        /// ("0001 - Electroplankton (JP)" → "Electroplankton (JP)"). <b>Opt-in per ingest</b>, never
        /// applied globally: plenty of real titles open with a 4-digit number and a dash
        /// ("1943 - The Battle of Midway", "1944 - The Loop Master" on fbneo), and an unconditional
        /// strip would silently rename them to their subtitle. Only the display Title is derived from
        /// the stripped form — GameKey/RomPath stay verbatim so they still match the archive on disk.</summary>
        public static string StripCatalogNumber(string name) =>
            Regex.Replace(name, @"^\d{3,5}\s*-\s*", "");

        /// <summary>Core title cleaner. <paramref name="collapseFixes"/> gates the normalizations added by
        /// the cross-dump collapse fix (the free-text/glued disc peel and the article un-inversion) —
        /// renormalize passes <c>false</c> to reproduce the PREVIOUS algorithm's output, so it can tell an
        /// auto-named row from a hand-edited one before rewriting. All other callers use the default
        /// <c>true</c>.</summary>
        internal static string CleanTitle(string name, bool collapseFixes)
        {
            var t = name;
            // Strip a TRAILING run of tag groups first ("(USA)", "[Hack]", "(Rev 1)" at the very end) —
            // covers the ordinary "Title (Region)" case AND a hack's own "[Hack]" suffix without
            // touching a subtitle that appears earlier in the name. (Kept identical to the
            // ArcadeIngestCommand copy — see the gotcha note above.)
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
            // A handful of older-style releases carry their disc number as a free-text trailing suffix with
            // no parens at all ("Baldur's Gate - Disc 1"/"- Disc 2"/"- Disc 3") instead of the ordinary
            // "(Disc N)" tag the paren-stripping above already handles — without this, each disc keeps a
            // DIFFERENT Title and never collapses into one card. Mirrors ArcadeVersions.DiscNumber/M3uKey,
            // which strip the same suffix from CloudRetroGameKey for grouping/launch purposes.
            t = Regex.Replace(t, @"\s*-\s*Dis[ck]\s*\d+\s*$", "", RegexOptions.IgnoreCase);
            // Broader inline-disc peel for the CD collections whose disc token sits at the tail with only a
            // space or a glued hyphen, OR is left dangling after a trailing catalog/serial tag was stripped
            // above: "Enemy Zero Disc 3", "Elves2-Cd1", "Azel Panzer Dragoon Rpg Disc 1" (from
            // "...disc 1 (gs-9076)"). Covers Disc/Disk/CD, "Disc N of M", and either separator. GATED by
            // peelBareDisc so renormalize can reproduce the previous (pre-fix) titles for its hand-edit
            // guard. The "Disc N of M" form is an unambiguous multi-disc part and always folds; a plain
            // trailing "Disc N" folds UNLESS the word before it labels a periodical/compilation
            // ("Demo/Sampler/Action/Trial/Bonus/Special/Preview/Promo/Cover/Magazine Disc N"), whose numbers
            // enumerate SEPARATE products (the PS1 magazine demo discs) that must stay distinct cards.
            if (collapseFixes)
            {
                t = Regex.Replace(t, @"[-\s]+Dis[ck]\s*\d+\s+of\s+\d+\s*$", "", RegexOptions.IgnoreCase).Trim();
                if (!Regex.IsMatch(t, @"\b(?:demo|sampler|action|trial|preview|bonus|special|promo|cover|magazine)[-\s]+(?:cd|dis[ck])\s*\d+\s*$", RegexOptions.IgnoreCase))
                    t = Regex.Replace(t, @"[-\s]+(?:cd|dis[ck])\s*\d+\s*$", "", RegexOptions.IgnoreCase).Trim();
                // Un-invert a source that stored the SORT form as its name ("Mansion of Hidden Souls, The"
                // → "The Mansion of Hidden Souls"). Display Titles are always natural word order; the
                // ", The" inversion belongs ONLY to SortTitle (ArticleInvert). Without this, a dump named
                // "…, The" and one named "The …" are two Titles and never collapse into one card.
                var art = Regex.Match(t, @"^(.+),\s+(the|an?)$", RegexOptions.IgnoreCase);
                if (art.Success) t = $"{art.Groups[2].Value} {art.Groups[1].Value}".Trim();
            }
            // Strip a trailing TOSEC bare version token ("Sonic Adventure v1.005" → "Sonic Adventure") so
            // revisions of one game share a Title and collapse to one card (the version shows in the
            // dropdown via ArcadeVersions.Revision).
            t = ArcadeVersions.StripTrailingBareVersion(t);
            // Some source collections (the R: drive's reclassified PSX/Saturn/CD-i/Sega CD/Naomi dumps)
            // carry entirely lowercase filenames — title-case ONLY when the whole result is lowercase, so
            // an already-properly-cased name from a normal No-Intro/Redump source (the overwhelming
            // majority) is never touched.
            return IsAllLower(t) ? TitleCase(t) : t;
        }

        public static string ArticleInvert(string title)
        {
            foreach (var article in new[] { "The ", "A ", "An " })
                if (title.StartsWith(article, StringComparison.OrdinalIgnoreCase))
                    return title[article.Length..].TrimEnd() + ", " + article.Trim();
            return title;
        }

        /// <summary>The lobby's card-grouping key: the punctuation/article-folded normalization of the
        /// display Title. Reuses <see cref="MovieTheater.Services.LaunchBox.LaunchBoxMetadata.NormalizeTitle"/>
        /// (drops articles positionally, strips punctuation, folds &amp;→and) so cosmetically-different dumps
        /// of one game share a key and collapse into one card. Capped to the column width; empty for a title
        /// with no alphanumerics.</summary>
        public static string CollapseKey(string? title)
        {
            var k = Services.LaunchBox.LaunchBoxMetadata.NormalizeTitle(title);
            return k.Length <= 200 ? k : k[..200];
        }

        /// <summary>True if the string has at least one letter and none of them are uppercase — the
        /// signature of a lowercased-filename source, as opposed to a properly-cased one (which this must
        /// leave alone) or a title with no letters at all (numbers/punctuation only — nothing to case).</summary>
        internal static bool IsAllLower(string s) => s.Any(char.IsLetter) && !s.Any(char.IsUpper);

        // Short connector words that stay lowercase mid-title (never at the first or last word).
        private static readonly HashSet<string> MinorWords = new(StringComparer.OrdinalIgnoreCase)
        {
            "a", "an", "and", "as", "at", "but", "by", "for", "from", "in", "into", "nor", "of",
            "on", "or", "per", "so", "the", "to", "up", "vs", "vs.", "via", "with", "yet",
        };

        /// <summary>Best-effort title case for an all-lowercase source name: capitalizes each word's first
        /// letter (minor connector words stay lowercase unless first/last), keeps roman-numeral tokens
        /// ("ii", "iv") fully upper, and capitalizes each side of a hyphen ("geppy-x" → "Geppy-X"). Can't
        /// recover deliberately stylized casing (PaRappa, inFAMOUS) since the source has none left to
        /// recover — plain title case is the reasonable ceiling here.</summary>
        internal static string TitleCase(string s)
        {
            var words = s.Split(' ');
            for (int i = 0; i < words.Length; i++)
            {
                if (words[i].Length == 0) continue;
                // A lone "-" (title/subtitle separator, e.g. "Base Game - The Subtitle") or a colon starts
                // a new clause — the word after it is an edge too, same as the very first/last word.
                bool afterSeparator = i > 0 && (words[i - 1] == "-" || words[i - 1].EndsWith(':'));
                bool isEdge = i == 0 || i == words.Length - 1 || afterSeparator;
                words[i] = !isEdge && MinorWords.Contains(words[i])
                    ? words[i].ToLowerInvariant()
                    : CapitalizeWord(words[i]);
            }
            return string.Join(' ', words);
        }

        private static string CapitalizeWord(string word)
        {
            var segments = word.Split('-');
            for (int i = 0; i < segments.Length; i++)
            {
                var seg = segments[i];
                segments[i] = Regex.IsMatch(seg, @"^[ivxlcdm]+$", RegexOptions.IgnoreCase)
                    ? seg.ToUpperInvariant()                                    // whole-segment roman numeral
                    : seg.Length > 0 && char.IsLetter(seg[0])
                        ? char.ToUpperInvariant(seg[0]) + seg[1..].ToLowerInvariant()  // only the leading
                        : seg;                                                  // letter — "70's" stays "70's"
            }
            return string.Join('-', segments);
        }

        /// <summary>Title-case any ALL-LOWERCASE segment of a name — segments split by " - ", " / ", ": "
        /// — while leaving already-cased segments untouched. Fixes the FBNeo DAT's lowercase subtitles
        /// ("Darkstalkers - the night warriors" → "Darkstalkers - The Night Warriors", "1944 - the loop
        /// master" → "1944 - The Loop Master") WITHOUT re-casing a proper segment or an acronym ("WWF" must
        /// not become "Wwf"): a segment containing any uppercase letter is assumed intentional and kept.</summary>
        internal static string NormalizeSegmentCase(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            var parts = Regex.Split(s, @"(\s[-/]\s|:\s+)");   // keep the separators (odd indices)
            for (int i = 0; i < parts.Length; i += 2)
                if (IsAllLower(parts[i])) parts[i] = TitleCase(parts[i].Trim());
            return string.Join("", parts);
        }
    }

    /// <summary>
    /// Ingests ScummVM games from a target list generated OFFLINE by the standalone ScummVM CLI
    /// (<c>scummvm --add --recursive --path=&lt;R: root&gt; --config=&lt;out.ini&gt;</c>, run once — never
    /// a runtime dependency; the arcade itself only ever talks to the buildbot's <c>scummvm_libretro</c>
    /// core, same as every other system). Each detected target becomes an <c>ArcadeGame</c> row whose
    /// "ROM" is a tiny generated <c>&lt;target&gt;.scummvm</c> hook file (checked into
    /// <c>data/arcade/scummvm-hooks/</c>, content = the target name) — the JIT-copied primary the gateway
    /// materializes like any other. <see cref="ArcadeGame.SourceCompanionPath"/> carries the REAL game
    /// data directory on R:, staged into <c>roms/scummvm/&lt;target&gt;/</c> by the same companion
    /// mechanism that fixed Naomi's GD-ROM pairs — the deployed <c>scummvm.ini</c>'s per-target
    /// <c>path=</c> must point at exactly that materialized location (generated alongside, not here —
    /// see <c>docs/arcade-scummvm.md</c> for the deploy step).
    ///
    /// <para><b>Bulk-job rules</b> (same contract as the other ingest commands): dry-run unless
    /// <c>--apply</c>; bounded by <c>--limit</c>, ordered by target name, resumable via <c>--after</c>;
    /// idempotent upsert on the (System, RomPath) unique key; never deletes.</para>
    /// </summary>
    [Command("arcade-scummvm-ingest", Description = "Catalog ScummVM targets from a detected-games ini as JIT ArcadeGames (dry-run unless --apply).")]
    public class ArcadeScummvmIngestCommand : BasicDICommand, ICommand
    {
        [CommandOption("ini", Description = "Path to the ScummVM-generated targets ini. Default data/arcade/scummvm-detected.ini.")]
        public string IniPath { get; set; } = "data/arcade/scummvm-detected.ini";

        [CommandOption("hooks-dir", Description = "Where to write the generated .scummvm hook files. Default data/arcade/scummvm-hooks.")]
        public string HooksDir { get; set; } = "data/arcade/scummvm-hooks";

        [CommandOption("apply", Description = "Write changes. Omit for a dry run (default).")]
        public bool Apply { get; set; }

        [CommandOption("limit", Description = "Max targets to process this run (default 500).")]
        public int Limit { get; set; } = 500;

        [CommandOption("after", Description = "Resume cursor: skip targets whose name is ≤ this (from a prior run's nextCursor).")]
        public string After { get; set; } = "";

        private readonly IDbContextFactory<MovieDb> dbFactory;

        public ArcadeScummvmIngestCommand(MovieTheaterConfiguration config) : base(config)
        {
            dbFactory = GetRequiredService<IDbContextFactory<MovieDb>>();
        }

        public async ValueTask ExecuteAsync(IConsole console)
        {
            var w = console.Output;
            var iniFull = Path.GetFullPath(IniPath);
            if (!File.Exists(iniFull)) { w.WriteLine($"Targets ini not found: {iniFull}"); return; }
            var hooksFull = Path.GetFullPath(HooksDir);

            var targets = ParseTargets(iniFull)
                .OrderBy(t => t.Target, StringComparer.Ordinal)
                .ToList();
            if (targets.Count == 0) { w.WriteLine($"No targets found in {iniFull}."); return; }

            var pending = targets.Where(t => string.CompareOrdinal(t.Target, After) > 0).ToList();
            var batch = pending.Take(Math.Max(1, Limit)).ToList();

            await using var db = await dbFactory.CreateDbContextAsync();
            var existing = await db.ArcadeGames.Where(g => g.System == "scummvm").ToListAsync();
            var byRomPath = existing.ToDictionary(g => g.RomPath, StringComparer.OrdinalIgnoreCase);

            int inserted = 0, updated = 0, skipped = 0;
            if (Apply) Directory.CreateDirectory(hooksFull);
            foreach (var t in batch)
            {
                if (!Directory.Exists(t.Path))
                {
                    w.WriteLine($"  ! [{t.Target}] source directory missing, skipped: {t.Path}");
                    skipped++;
                    continue;
                }

                var romPath = $"scummvm/{t.Target}.scummvm";
                var hookFile = Path.Combine(hooksFull, $"{t.Target}.scummvm");
                var title = ArcadeNaming.CleanTitle(t.Description);

                if (byRomPath.TryGetValue(romPath, out var row))
                {
                    bool changed = false;
                    if (!row.IsEnabled) { if (Apply) row.IsEnabled = true; changed = true; }
                    if (row.SourceCompanionPath != t.Path) { if (Apply) row.SourceCompanionPath = t.Path; changed = true; }
                    if (row.SourceArchivePath != hookFile) { if (Apply) row.SourceArchivePath = hookFile; changed = true; }
                    if (changed) updated++; else skipped++;
                }
                else
                {
                    if (Apply)
                    {
                        await File.WriteAllTextAsync(hookFile, t.Target + "\n");
                        db.ArcadeGames.Add(new ArcadeGame
                        {
                            Title = title,
                            SortTitle = ArcadeNaming.ArticleInvert(title),
                            CollapseKey = ArcadeNaming.CollapseKey(title),
                            System = "scummvm",
                            RomPath = romPath,
                            CloudRetroGameKey = t.Target,
                            SourceArchivePath = hookFile,
                            SourceCompanionPath = t.Path,
                            MaxPlayers = 1,
                            RatingCeiling = 0,
                            IsEnabled = true,
                        });
                    }
                    inserted++;
                    w.WriteLine($"  + [{t.Target}] {title}");
                }
            }

            if (Apply) await db.SaveChangesAsync();

            var remaining = pending.Count - batch.Count;
            var nextCursor = batch.Count > 0 ? batch[^1].Target : After;

            w.WriteLine();
            w.WriteLine($"scanned {targets.Count} target(s); this run: {inserted} inserted, {updated} updated, {skipped} unchanged/skipped.");
            w.WriteLine($"{{ processed: {batch.Count}, remaining: {remaining}, nextCursor: \"{nextCursor}\" }}");
            if (!Apply) w.WriteLine("DRY RUN — nothing written. Re-run with --apply.");
            else if (remaining > 0) w.WriteLine($"More to do: re-run with --after \"{nextCursor}\".");
        }

        private sealed record ScummvmTarget(string Target, string GameId, string Description, string Path);

        // Minimal INI reader for exactly the shape `scummvm --add` writes: `[target]` sections, `key=value`
        // lines. Skips the leading `[scummvm]` global-settings section (no `path=`, so it's naturally
        // filtered by the `path` presence check below).
        private static IEnumerable<ScummvmTarget> ParseTargets(string iniPath)
        {
            string? target = null;
            var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var raw in File.ReadLines(iniPath))
            {
                var line = raw.Trim();
                if (line.Length == 0) continue;
                if (line.StartsWith('[') && line.EndsWith(']'))
                {
                    if (target != null && fields.TryGetValue("path", out var p))
                        yield return new ScummvmTarget(target, fields.GetValueOrDefault("gameid", target),
                            fields.GetValueOrDefault("description", target), p.TrimEnd('\\', '/'));
                    target = line[1..^1];
                    fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    continue;
                }
                var eq = line.IndexOf('=');
                if (eq > 0) fields[line[..eq]] = line[(eq + 1)..];
            }
            if (target != null && fields.TryGetValue("path", out var lastPath))
                yield return new ScummvmTarget(target, fields.GetValueOrDefault("gameid", target),
                    fields.GetValueOrDefault("description", target), lastPath.TrimEnd('\\', '/'));
        }
    }
}
