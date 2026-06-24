using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using CliFx;
using CliFx.Attributes;
using CliFx.Infrastructure;
using Microsoft.EntityFrameworkCore;
using MovieTheater.Console;
using MovieTheater.Db;
using MovieTheater.Services;

namespace MovieTheater.Ingest
{
    /// <summary>
    /// Scans each series' on-disk folder (its <see cref="Series.ReviewSourcePath"/>) and writes a plain-text
    /// dump of every file — sized, and each video flagged ✓ mapped / ✗ not-captured — into
    /// <see cref="Series.FolderListing"/>, so the review tool can show the whole folder at a glance and the
    /// reviewer can spot files the mapper missed. Run from a box with NAS (L:) access; the web app can't read
    /// the NAS, so this snapshot is how the listing reaches the (prod) review UI. Dry-run by default.
    /// </summary>
    [Command("scan-series-folders", Description = "Dump each series' on-disk folder (files, sizes, mapped/unmapped) into Series.FolderListing.")]
    public class ScanSeriesFoldersCommand : BasicDICommand, ICommand
    {
        [CommandOption("apply", Description = "Write Series.FolderListing. Omit for a dry run (default).")]
        public bool Apply { get; set; }

        [CommandOption("all", Description = "Include approved series too (default: pending-review only).")]
        public bool All { get; set; }

        [CommandOption("limit", Description = "Max series to scan this run.")]
        public int? Limit { get; set; }

        [CommandOption("id", Description = "Only this one Series id (regenerate a single folder dump fast). Implies --all.")]
        public int? OnlyId { get; set; }

        private static readonly HashSet<string> VideoExt = new(StringComparer.OrdinalIgnoreCase)
        { ".mkv", ".mp4", ".avi", ".m4v", ".mov", ".wmv", ".ts", ".m2ts", ".mpg", ".mpeg", ".flv", ".webm", ".divx", ".vob", ".ogm", ".rmvb" };

        private readonly IDbContextFactory<MovieDb> dbFactory;

        public ScanSeriesFoldersCommand(MovieTheaterConfiguration config) : base(config)
        {
            dbFactory = GetRequiredService<IDbContextFactory<MovieDb>>();
        }

        public async ValueTask ExecuteAsync(IConsole console)
        {
            var w = console.Output;
            List<(int Id, string Title, string Path)> targets;
            Dictionary<int, HashSet<string>> mappedBySeries;
            Dictionary<int, List<string>> filesBySeries;
            await using (var db = await dbFactory.CreateDbContextAsync())
            {
                // A series is scannable if it has an explicit source folder OR at least one mapped episode
                // file (whose location we can derive the folder from). Many ingested series never got a
                // ReviewSourcePath, so without the file-derived fallback their card shows no folder dump.
                var sq = db.Series.Where(s =>
                    (s.ReviewSourcePath != null && s.ReviewSourcePath != "")
                    || db.Episodes.Any(e => e.SeriesId == s.Id && e.PlayableId != null
                        && db.MediaFiles.Any(f => f.PlayableId == e.PlayableId)));
                if (OnlyId != null) sq = sq.Where(s => s.Id == OnlyId.Value);
                else if (!All) sq = sq.Where(s => s.ReviewBatch != null);
                targets = (await sq.Select(s => new { s.Id, s.Title, s.ReviewSourcePath }).ToListAsync())
                    .Select(s => (s.Id, s.Title ?? "", s.ReviewSourcePath ?? "")).OrderBy(t => t.Item2, StringComparer.OrdinalIgnoreCase).ToList();
                if (Limit.HasValue) targets = targets.Take(Limit.Value).ToList();

                // Which on-disk files are already captured: the MediaFile paths behind each series' episodes.
                var ids = targets.Select(t => t.Id).ToList();
                var epPlayables = await db.Episodes
                    .Where(e => e.SeriesId != null && ids.Contains(e.SeriesId.Value) && e.PlayableId != null)
                    .Select(e => new { SeriesId = e.SeriesId!.Value, PlayableId = e.PlayableId!.Value }).ToListAsync();
                var pids = epPlayables.Select(x => x.PlayableId).Distinct().ToList();
                var pathsByPlayable = (await db.MediaFiles.Where(f => pids.Contains(f.PlayableId))
                        .Select(f => new { f.PlayableId, f.Path }).ToListAsync())
                    .GroupBy(f => f.PlayableId).ToDictionary(g => g.Key, g => g.Select(x => x.Path).ToList());
                filesBySeries = epPlayables.GroupBy(x => x.SeriesId).ToDictionary(
                    g => g.Key,
                    g => g.SelectMany(x => pathsByPlayable.TryGetValue(x.PlayableId, out var l) ? l : Enumerable.Empty<string>()).ToList());
                mappedBySeries = filesBySeries.ToDictionary(kv => kv.Key, kv => kv.Value.Select(Norm).ToHashSet());
            }

            w.WriteLine($"series to scan: {targets.Count}{(All ? " (all)" : " (pending)")}{(Apply ? "" : " — DRY RUN")}");
            int scanned = 0, missing = 0, written = 0, totalUnmapped = 0, noFolder = 0, derived = 0;
            foreach (var (id, title, path) in targets)
            {
                // Use the explicit source folder, else derive it from the mapped episode files' location.
                var root = path;
                if (string.IsNullOrWhiteSpace(root))
                {
                    root = DeriveRoot(filesBySeries.TryGetValue(id, out var fl) ? fl : null);
                    if (root == null) { noFolder++; w.WriteLine($"  - no derivable folder: S{id} \"{title}\""); continue; }
                    derived++;
                }
                if (!Directory.Exists(root)) { missing++; w.WriteLine($"  ! folder not found: S{id} \"{title}\" -> {root}"); continue; }
                var mapped = mappedBySeries.TryGetValue(id, out var ms) ? ms : new HashSet<string>();
                var (listing, unmapped) = BuildListing(root, mapped);
                totalUnmapped += unmapped;
                scanned++;
                if (unmapped > 0) w.WriteLine($"  S{id} \"{title}\": {unmapped} unmapped video file(s)");
                if (Apply)
                {
                    await using var db = await dbFactory.CreateDbContextAsync();
                    var s = await db.Series.FirstOrDefaultAsync(x => x.Id == id);
                    if (s != null) { s.FolderListing = listing; await db.SaveChangesAsync(); written++; }
                }
            }
            w.WriteLine($"\n{(Apply ? "" : "DRY RUN — ")}scanned {scanned} (folder derived for {derived}), folder-missing {missing}, no-derivable-folder {noFolder}, written {written}, total unmapped videos {totalUnmapped}.");
        }

        private static string Norm(string p) => (p ?? "").Replace('/', '\\').TrimEnd('\\').ToLowerInvariant();

        // When a series has no ReviewSourcePath, derive its folder as the longest common directory of its
        // mapped episode files. The common-prefix is inherently no shallower than where the files live, and
        // the depth guard refuses anything above a title-specific folder (drive + 3 segments), so this never
        // walks a top-level library root like "L:\2 - Video\Series".
        private static string DeriveRoot(List<string> filePaths)
        {
            if (filePaths == null) return null;
            var dirs = filePaths
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .Select(p => p.Replace('/', '\\'))
                .Select(p => { var i = p.LastIndexOf('\\'); return i > 0 ? p.Substring(0, i) : null; })
                .Where(d => d != null).Distinct().ToList();
            if (dirs.Count == 0) return null;
            var split = dirs.Select(d => d.Split('\\')).ToList();
            var min = split.Min(s => s.Length);
            var common = new List<string>();
            for (int i = 0; i < min; i++)
            {
                var seg = split[0][i];
                if (split.All(s => string.Equals(s[i], seg, StringComparison.OrdinalIgnoreCase))) common.Add(seg);
                else break;
            }
            if (common.Count < 4) return null;   // guard: never scan above a title-specific folder
            return string.Join("\\", common);
        }

        private static (string listing, int unmapped) BuildListing(string root, HashSet<string> mapped)
        {
            string[] files;
            try { files = Directory.GetFiles(root, "*", SearchOption.AllDirectories); }
            catch (Exception ex) { return ($"(could not read folder: {ex.Message})", 0); }
            Array.Sort(files, StringComparer.OrdinalIgnoreCase);

            long totalBytes = 0; int vids = 0, mappedCount = 0, unmapped = 0;
            var lines = new List<string>();
            foreach (var f in files)
            {
                long size = 0; try { size = new FileInfo(f).Length; } catch { }
                totalBytes += size;
                var rel = f.Length > root.Length ? f.Substring(root.Length).TrimStart('\\', '/') : Path.GetFileName(f);
                string flag = "    ";   // non-video (subs / nfo / artwork): listed, not flagged
                if (VideoExt.Contains(Path.GetExtension(f)))
                {
                    vids++;
                    if (mapped.Contains(Norm(f))) { mappedCount++; flag = "[OK]"; }
                    else { unmapped++; flag = "[??]"; }
                }
                lines.Add($"{flag} {rel}    {Hsize(size)}");
            }

            var sb = new StringBuilder();
            sb.AppendLine(root);
            sb.AppendLine($"{files.Length} files · {Hsize(totalBytes)} · videos {vids}  ([OK] mapped {mappedCount} / [??] NOT captured {unmapped})");
            sb.AppendLine(new string('-', 64));
            foreach (var l in lines) sb.AppendLine(l);
            return (sb.ToString(), unmapped);
        }

        private static string Hsize(long bytes)
        {
            if (bytes >= 1L << 30) return $"{bytes / (double)(1L << 30):0.00} GB";
            if (bytes >= 1L << 20) return $"{bytes / (double)(1L << 20):0.0} MB";
            return $"{bytes / 1024.0:0} KB";
        }
    }
}
