using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using CliFx;
using CliFx.Attributes;
using CliFx.Exceptions;
using CliFx.Infrastructure;
using Microsoft.EntityFrameworkCore;
using MovieTheater.Console;
using MovieTheater.Db;
using MovieTheater.Services;

namespace MovieTheater.Arcade
{
    /// <summary>
    /// Catalogs a current MAME romset (non-merged ROMs + merged CHDs) as the site's <c>mame</c> system — the
    /// libretro MAME core, beside the FBNeo <c>arcade</c> system. Only playable video games are taken
    /// (<see cref="ArcadeMameSelection"/>: no fruit machines, slots, gambling, computers, consoles, mahjong
    /// panels, non-working drivers), and by default only game FAMILIES that no enabled arcade/naomi/atomiswave
    /// card already covers — FBNeo keeps the games it runs (saves, rewind and tuning live there).
    ///
    /// <para><b>Rows land DISABLED</b>, tagged <c>Notes = "mame-ingest:&lt;batch&gt;"</c> (the bulk-insert review
    /// marker). <c>--enable</c> is the separate, deliberate step that turns a reviewed batch on — it re-checks
    /// every row's zip and CHDs on disk first and leaves a row off when anything is missing.</para>
    ///
    /// <para>Paths: <c>--roms</c> is the folder of non-merged zips (each self-contained but for BIOS/device
    /// sets, which the WORKERS carry in <c>system/mame/bios</c> — libretro MAME adds that to its rompath);
    /// <c>--chds</c> the merged CHD root (<c>&lt;root&gt;/&lt;game&gt;/*.chd</c>). Re-running with new paths
    /// re-points existing rows (SourceArchivePath / SourceCompanionPath) and touches nothing else, so the set can
    /// be ingested where it lands and moved later.</para>
    ///
    /// <para><b>Bulk-job rules</b>: dry-run unless <c>--apply</c>; bounded by <c>--limit</c>; ordered by
    /// shortname and resumable via <c>--after &lt;shortname&gt;</c>; prints <c>{ processed, remaining, nextCursor }</c>.
    /// <c>--report</c> writes every machine's decision as TSV for review.</para>
    /// </summary>
    [Command("arcade-mame-ingest", Description = "Catalog a MAME romset's playable video games as the 'mame' system (disabled, for review); dry-run unless --apply.")]
    public class ArcadeMameIngestCommand : BasicDICommand, ICommand
    {
        public const string System = "mame";
        /// <summary>The worker ROM folder (config.worker-gl.yaml systemAliases mame → mamelr). NOT "mame": that
        /// folder (and core key) has always been FBNeo's.</summary>
        public const string Folder = "mamelr";
        public const string NotePrefix = "mame-ingest:";

        [CommandOption("xml", Description = "MAME -listxml matching the romset. Default data/arcade/mame0289.xml.")]
        public string XmlPath { get; set; } = "data/arcade/mame0289.xml";

        [CommandOption("folders", Description = "progettoSNAPS folders dir (Machine Type.ini, category.ini, Game Or No Game.ini, mess.ini).", IsRequired = true)]
        public string FoldersDir { get; set; } = default!;

        [CommandOption("roms", Description = "Folder of NON-MERGED romset zips.", IsRequired = true)]
        public string RomsDir { get; set; } = default!;

        [CommandOption("chds", Description = "Merged CHD root (<root>/<game>/*.chd). Without it, CHD games are skipped.")]
        public string? ChdsDir { get; set; }

        [CommandOption("batch", Description = "Review batch label written to Notes (default mame0289).")]
        public string Batch { get; set; } = "mame0289";

        [CommandOption("include-covered", Description = "Also take game families an enabled arcade/naomi/atomiswave card already covers.")]
        public bool IncludeCovered { get; set; }

        [CommandOption("only", Description = "Comma-separated shortnames to process (testing a few games).")]
        public string? Only { get; set; }

        [CommandOption("report", Description = "Write every machine's decision as TSV to this path.")]
        public string? ReportPath { get; set; }

        [CommandOption("enable", Description = "Instead of ingesting: ENABLE this batch's rows whose zip (and CHDs) exist. Chunked like the ingest.")]
        public bool Enable { get; set; }

        [CommandOption("apply", Description = "Write changes. Omit for a dry run (default).")]
        public bool Apply { get; set; }

        [CommandOption("limit", Description = "Max candidate machines to process this run (default 2000).")]
        public int Limit { get; set; } = 2000;

        [CommandOption("after", Description = "Resume cursor: skip shortnames ordinally ≤ this (a prior nextCursor).")]
        public string? After { get; set; }

        private readonly IDbContextFactory<MovieDb> dbFactory;

        public ArcadeMameIngestCommand(MovieTheaterConfiguration config) : base(config)
        {
            dbFactory = GetRequiredService<IDbContextFactory<MovieDb>>();
        }

        public static string RomPathFor(string shortName) => $"{Folder}/{shortName}.zip";

        public async ValueTask ExecuteAsync(IConsole console)
        {
            var w = console.Output;
            if (Enable) { await EnableAsync(w); return; }

            XmlPath = RepoDataPath.Resolve(XmlPath);
            if (!Directory.Exists(RomsDir)) throw new CommandException($"--roms not found: {RomsDir}", 1);
            var folders = ArcadeMameSelection.LoadFolders(FoldersDir);
            var all = new Dictionary<string, ArcadeControlProfile.Machine>(StringComparer.OrdinalIgnoreCase);
            foreach (var m in ArcadeControlProfile.Read(XmlPath)) all[m.Name] = m;
            w.WriteLine($"listxml: {all.Count} machines — {XmlPath}");

            await using var db = await dbFactory.CreateDbContextAsync();
            db.Database.SetCommandTimeout(180);

            // Families already on the site through another core: by shortname, folded to the family parent.
            var covered = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (!IncludeCovered)
            {
                var keys = await db.ArcadeGames.Where(g => g.IsEnabled && (g.System == "arcade" || g.System == "naomi" || g.System == "atomiswave"))
                    .Select(g => g.CloudRetroGameKey).ToListAsync();
                foreach (var k in keys) covered.Add(all.TryGetValue(k, out var km) ? km.CloneOf ?? km.Name : k);
            }
            var existing = await db.ArcadeGames.Where(g => g.System == System).ToDictionaryAsync(g => g.RomPath, StringComparer.OrdinalIgnoreCase);

            var only = Only?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var decisions = new List<(string Name, string Decision)>();
            var keep = new List<ArcadeControlProfile.Machine>();
            var funnel = new Dictionary<string, int>(StringComparer.Ordinal);
            void Drop(ArcadeControlProfile.Machine m, string why)
            {
                var bucket = why.Split(':')[0];
                funnel[bucket] = funnel.GetValueOrDefault(bucket) + 1;
                decisions.Add((m.Name, "skip: " + why));
            }

            foreach (var m in all.Values.OrderBy(m => m.Name, StringComparer.Ordinal))
            {
                if (only != null && !only.Contains(m.Name)) continue;
                if (ArcadeMameSelection.ExcludeReason(m, folders) is { } why) { Drop(m, why); continue; }
                if (covered.Contains(m.CloneOf ?? m.Name)) { Drop(m, "covered: an enabled card already plays this game"); continue; }
                keep.Add(m);
            }

            // One primary per family: the parent when it made the cut, else the first clone that did.
            var primaries = keep.GroupBy(m => m.CloneOf ?? m.Name, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.FirstOrDefault(m => m.CloneOf == null) ?? g.First()).Select(m => m.Name)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            int processed = 0, inserted = 0, repointed = 0, unchanged = 0, noZip = 0, noChd = 0;
            string? cursor = After;
            var batchNote = NotePrefix + Batch;
            var examples = new List<string>();
            foreach (var m in keep.Where(m => After == null || string.CompareOrdinal(m.Name, After) > 0))
            {
                if (processed >= Limit) break;
                processed++;
                cursor = m.Name;

                var zip = Path.Combine(RomsDir, m.Name + ".zip");
                if (!File.Exists(zip)) { noZip++; decisions.Add((m.Name, "skip: zip not in --roms")); continue; }
                var (chdDir, chdMissing) = ArcadeMameSelection.ChdDirFor(m, ChdsDir);
                if (chdMissing != null) { noChd++; decisions.Add((m.Name, "skip: " + chdMissing)); continue; }

                var romPath = RomPathFor(m.Name);
                if (existing.TryGetValue(romPath, out var row))
                {
                    // Re-point only. Titles/enrichment on an existing row may have been curated since.
                    bool changed = false;
                    if (!string.Equals(row.SourceArchivePath, zip, StringComparison.OrdinalIgnoreCase)) { if (Apply) row.SourceArchivePath = zip; changed = true; }
                    if (!string.Equals(row.SourceCompanionPath, chdDir, StringComparison.OrdinalIgnoreCase)) { if (Apply) row.SourceCompanionPath = chdDir; changed = true; }
                    if (changed) { repointed++; decisions.Add((m.Name, "re-point")); } else { unchanged++; decisions.Add((m.Name, "unchanged")); }
                    continue;
                }

                var title = ArcadeNaming.NormalizeSegmentCase(FbneoDat.CleanDescription(ArcadeMameSelection.TitleSourceFor(m, all)));
                var (region, variant) = ArcadeRomTags.Parse(m.Description);
                var g = new ArcadeGame
                {
                    System = System,
                    RomPath = romPath,
                    CloudRetroGameKey = m.Name,
                    Title = title,
                    SortTitle = ArcadeNaming.ArticleInvert(title),
                    CollapseKey = ArcadeNaming.CollapseKey(title),
                    MaxPlayers = (byte)Math.Clamp(m.Players, 1, 4),
                    Year = ArcadeMameSelection.YearOf(m.Year),
                    Region = region != ArcadeRomTags.Unknown ? region : null,
                    Variant = variant != ArcadeRomTags.Release ? variant : null,
                    Publisher = string.IsNullOrWhiteSpace(m.Manufacturer) ? null : m.Manufacturer,
                    Controls = m.Profile,
                    SourceArchivePath = zip,
                    SourceCompanionPath = chdDir,
                    IsEnabled = false,                       // the review gate: --enable turns a batch on
                    IsPrimary = primaries.Contains(m.Name),
                    Notes = batchNote,
                };
                if (Apply) db.ArcadeGames.Add(g);
                inserted++;
                decisions.Add((m.Name, "insert"));
                if (examples.Count < 12) examples.Add($"{m.Name,-12} {m.Profile,-12} {(chdDir != null ? "CHD " : "    ")}{title}");
                if (Apply && inserted % 500 == 0) { await db.SaveChangesAsync(); w.WriteLine($"  … {processed} processed, {inserted} inserted (through {cursor})"); }
            }
            if (Apply) await db.SaveChangesAsync();

            var nextCursor = cursor;
            var remaining = keep.Count(m => nextCursor == null || string.CompareOrdinal(m.Name, nextCursor) > 0);

            w.WriteLine();
            w.WriteLine($"funnel ({all.Count} machines{(only != null ? $", --only {only.Count}" : "")}):");
            foreach (var (k, n) in funnel.OrderByDescending(kv => kv.Value)) w.WriteLine($"  skipped — {k,-36} {n,6}");
            w.WriteLine($"  candidates (playable, not covered)      {keep.Count,6}   in {primaries.Count} game families");
            w.WriteLine();
            w.WriteLine($"this run: processed {processed}");
            w.WriteLine($"  {(Apply ? "inserted" : "would insert"),-14} {inserted,6}   (disabled, Notes = {batchNote})");
            w.WriteLine($"  re-pointed     {repointed,6}");
            w.WriteLine($"  unchanged      {unchanged,6}");
            w.WriteLine($"  zip missing    {noZip,6}");
            w.WriteLine($"  CHD missing    {noChd,6}");
            if (examples.Count > 0) { w.WriteLine("  e.g.:"); foreach (var s in examples) w.WriteLine("    " + s); }
            if (ReportPath != null)
            {
                await File.WriteAllLinesAsync(ReportPath, decisions.Select(d =>
                    $"{d.Name}\t{d.Decision}\t{(all.TryGetValue(d.Name, out var dm) ? dm.Description : "")}"), Encoding.UTF8);
                w.WriteLine($"  report: {decisions.Count} decisions → {ReportPath}");
            }
            w.WriteLine();
            w.WriteLine($"{{ processed: {processed}, remaining: {remaining}, nextCursor: {nextCursor ?? "null"} }}");
            if (!Apply) w.WriteLine("DRY RUN — nothing written. Re-run with --apply.");
            else if (remaining > 0) w.WriteLine($"More to do: re-run with --after {nextCursor}.");
        }

        /// <summary>The deliberate go-live step: enable a reviewed batch, row by row, only where the files are
        /// really there (a disabled row stays off with its reason printed). Ordered by Id, chunked by --limit.</summary>
        private async Task EnableAsync(TextWriter w)
        {
            var batchNote = NotePrefix + Batch;
            await using var db = await dbFactory.CreateDbContextAsync();
            db.Database.SetCommandTimeout(180);
            int afterId = int.TryParse(After, out var a) ? a : 0;
            var only = Only?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var rows = await db.ArcadeGames
                // StartsWith: other tools APPEND breadcrumbs to Notes (arcade-boxart-source), so equality would
                // silently drop rows from their own batch.
                .Where(g => g.System == System && g.Notes != null && g.Notes.StartsWith(batchNote) && !g.IsEnabled && g.Id > afterId)
                .OrderBy(g => g.Id).Take(Limit).ToListAsync();
            if (only != null) rows = rows.Where(g => only.Contains(g.CloudRetroGameKey)).ToList();
            int enabled = 0, missing = 0;
            foreach (var g in rows)
            {
                var zipOk = g.SourceArchivePath != null && File.Exists(g.SourceArchivePath);
                var chdOk = g.SourceCompanionPath == null || Directory.Exists(g.SourceCompanionPath);
                if (!zipOk || !chdOk) { missing++; w.WriteLine($"  left off: {g.CloudRetroGameKey} ({(!zipOk ? "zip" : "CHD dir")} missing)"); continue; }
                if (Apply) g.IsEnabled = true;
                enabled++;
            }
            if (Apply) await db.SaveChangesAsync();
            var next = rows.Count > 0 ? rows[^1].Id : afterId;
            var remaining = await db.ArcadeGames.CountAsync(g => g.System == System && g.Notes != null && g.Notes.StartsWith(batchNote) && !g.IsEnabled && g.Id > next);
            w.WriteLine($"{(Apply ? "enabled" : "would enable")} {enabled}, left off {missing} (batch {batchNote})");
            w.WriteLine($"{{ processed: {rows.Count}, remaining: {remaining}, nextCursor: {next} }}");
            w.WriteLine("Then run arcade-romcache-export so the gateway can stage them.");
            if (!Apply) w.WriteLine("DRY RUN — nothing written. Re-run with --apply.");
        }
    }
}
