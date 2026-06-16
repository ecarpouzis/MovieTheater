using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using CliFx;
using CliFx.Attributes;
using CliFx.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using MovieTheater.Console;
using MovieTheater.Db;
using MovieTheater.Services;

namespace MovieTheater.Ingest
{
    /// <summary>
    /// Phase 5 — maps each tagged, movie-shaped title's on-disk video files (found under its
    /// <see cref="Movie.ReviewSourcePath"/> in the NAS inventory) to <see cref="MediaFile"/> rows under
    /// the movie's <see cref="Playable"/>, classified Primary / Part / Variant / Extra. Series
    /// (TvSeries/TvMiniSeries) are skipped — their episode files are the episode pass. Ambiguous folders
    /// (more than one feature-sized candidate) are left unmapped and reported for on-site review.
    /// Idempotent by (PlayableId, Path). Run AFTER the enrichment scrape so TitleType is final.
    /// </summary>
    [Command("map-library-files", Description = "Map tagged movies' video files to MediaFile rows (Primary/Part/Variant/Extra).")]
    public class MapLibraryFilesCommand : BasicDICommand, ICommand
    {
        [CommandOption("inventory", Description = "NAS file inventory CSV (default: data/nas-file-inventory.csv).")]
        public string InventoryPath { get; set; } = Path.Combine("data", "nas-file-inventory.csv");

        [CommandOption("dry-run", Description = "Report the mapping without writing MediaFile rows.")]
        public bool DryRun { get; set; }

        private static readonly Regex ExtraDir = new(@"(?i)[\\/](extras?|featurettes?|behind[ ._-]the[ ._-]scenes|bonus|deleted[ ._-]scenes?|interviews?|making[ ._-]of|trailers?)([\\/]|$)", RegexOptions.Compiled);
        private static readonly Regex VariantRe = new(@"(?i)\b(director'?s? ?cut|extended|theatrical|unrated|uncut|remastered|alternate|special[ ._-]edition|redux|final[ ._-]cut|imax)\b", RegexOptions.Compiled);
        private static readonly Regex PartRe = new(@"(?i)\b(?:cd|disc|disk|part|pt)[ ._-]*([0-9]+)\b", RegexOptions.Compiled);
        private static readonly Regex SampleRe = new(@"(?i)\bsample\b", RegexOptions.Compiled);
        private const long MinFeatureBytes = 200L * 1024 * 1024;

        private readonly IDbContextFactory<MovieDb> dbFactory;
        private readonly ILogger<MapLibraryFilesCommand> logger;

        public MapLibraryFilesCommand(MovieTheaterConfiguration config) : base(config)
        {
            dbFactory = GetRequiredService<IDbContextFactory<MovieDb>>();
            logger = GetRequiredService<ILogger<MapLibraryFilesCommand>>();
        }

        private static string Norm(string p) => Path.TrimEndingDirectorySeparator((p ?? "").Trim().Replace('/', '\\')).ToLowerInvariant();

        private sealed class Vid { public string FullPath; public long Size; public string Name; }
        private sealed class Mov { public int Id; public int PlayableId; public string Title; public string Path; public string Key; }

        public async ValueTask ExecuteAsync(IConsole console)
        {
            var invPath = Path.GetFullPath(InventoryPath);
            if (!File.Exists(invPath)) { console.Error.WriteLine($"Inventory not found: {invPath}"); return; }

            var videos = new List<Vid>();
            foreach (var rec in ReadCsv(invPath))
            {
                if (!rec.TryGetValue("Category", out var cat) || !string.Equals(cat, "video", StringComparison.OrdinalIgnoreCase)) continue;
                if (!rec.TryGetValue("FullPath", out var fp) || string.IsNullOrWhiteSpace(fp)) continue;
                long.TryParse(rec.TryGetValue("SizeBytes", out var sb) ? sb : "0", out var size);
                videos.Add(new Vid { FullPath = fp.Trim(), Size = size, Name = rec.TryGetValue("FileName", out var n) ? n.Trim() : "" });
            }
            console.Output.WriteLine($"Inventory video files: {videos.Count}");

            await using var db = await dbFactory.CreateDbContextAsync();
            var movies = (await db.Movies
                .Where(m => m.ReviewBatch != null && m.PlayableId != null
                            && m.TitleType != TitleType.TvSeries && m.TitleType != TitleType.TvMiniSeries
                            && m.ReviewSourcePath != null)
                .Select(m => new { m.id, PlayableId = m.PlayableId!.Value, m.Title, m.ReviewSourcePath })
                .ToListAsync())
                .Select(m => new Mov { Id = m.id, PlayableId = m.PlayableId, Title = m.Title, Path = m.ReviewSourcePath, Key = Norm(m.ReviewSourcePath) })
                .ToList();
            console.Output.WriteLine($"Tagged movie-shaped titles: {movies.Count}");

            // Assign each video to the LONGEST tagged title path that prefixes it (so a film inside a
            // collection folder claims its own files, not the parent).
            var byKeyLen = movies.OrderByDescending(m => m.Key.Length).ToList();
            var filesByMovie = new Dictionary<int, List<Vid>>();
            foreach (var v in videos)
            {
                var nfp = Norm(v.FullPath);
                foreach (var m in byKeyLen)
                {
                    if (nfp == m.Key || nfp.StartsWith(m.Key + "\\", StringComparison.Ordinal))
                    {
                        if (!filesByMovie.TryGetValue(m.Id, out var l)) filesByMovie[m.Id] = l = new List<Vid>();
                        l.Add(v);
                        break;
                    }
                }
            }

            var existing = DryRun
                ? new HashSet<string>()
                : (await db.MediaFiles.Select(f => new { f.PlayableId, f.Path }).ToListAsync())
                    .Select(f => f.PlayableId + "|" + Norm(f.Path)).ToHashSet();

            int titlesMapped = 0, noFiles = 0, ambiguous = 0, written = 0;
            var roleCounts = new Dictionary<MovieFileRole, int>();
            var review = new List<string>();

            foreach (var m in movies)
            {
                var files = filesByMovie.TryGetValue(m.Id, out var l) ? l : new List<Vid>();
                var (rows, note) = Classify(m.Path, files);
                if (note == "NO_VIDEO") { noFiles++; review.Add($"NO_VIDEO  {m.Title}  →  {m.Path}"); continue; }
                if (note.StartsWith("MULTI_PRIMARY")) { ambiguous++; review.Add($"{note}  {m.Title}  →  {m.Path}"); continue; }

                titlesMapped++;
                string primaryPath = null;
                foreach (var r in rows)
                {
                    roleCounts[r.Role] = roleCounts.GetValueOrDefault(r.Role) + 1;
                    if (r.Role == MovieFileRole.Primary) primaryPath = r.Path;
                    if (DryRun) continue;
                    if (!existing.Add(m.PlayableId + "|" + Norm(r.Path))) continue;
                    db.MediaFiles.Add(new MediaFile
                    {
                        PlayableId = m.PlayableId, Path = r.Path, Role = r.Role,
                        PartNumber = r.PartNumber, Label = r.Label, SizeBytes = r.Size,
                    });
                    written++;
                }
                if (!DryRun && primaryPath != null)
                {
                    var mv = await db.Movies.FirstOrDefaultAsync(x => x.id == m.Id);
                    if (mv != null && string.IsNullOrEmpty(mv.FilePath)) mv.FilePath = primaryPath;
                }
            }

            if (!DryRun) await db.SaveChangesAsync();

            console.Output.WriteLine($"\n{(DryRun ? "DRY RUN — " : "")}titles mapped: {titlesMapped}; no-video: {noFiles}; ambiguous (left for review): {ambiguous}");
            foreach (var kv in roleCounts.OrderByDescending(k => k.Value)) console.Output.WriteLine($"    {kv.Key}: {kv.Value}");
            if (!DryRun) console.Output.WriteLine($"MediaFile rows written: {written}");
            console.Output.WriteLine($"\nreview ({review.Count}):");
            foreach (var r in review.Take(40)) console.Output.WriteLine("  " + r);
            logger.LogInformation("map-library-files mapped {Mapped} titles, wrote {Written} MediaFiles ({DryRun})", titlesMapped, written, DryRun);
        }

        private sealed class Row { public MovieFileRole Role; public int? PartNumber; public string Label; public long Size; public string Path; }

        private static (List<Row> rows, string note) Classify(string titlePath, List<Vid> files)
        {
            var key = Norm(titlePath);
            var rows = new List<Row>();
            var real = files.Where(f => !SampleRe.IsMatch(f.Name)).ToList();
            if (real.Count == 0) return (rows, "NO_VIDEO");

            var primaries = new List<Vid>();
            foreach (var f in real)
            {
                var rel = Norm(f.FullPath).StartsWith(key) ? f.FullPath.Substring(Math.Min(titlePath.Length, f.FullPath.Length)) : f.Name;
                var em = ExtraDir.Match(rel);
                if (em.Success) { rows.Add(new Row { Role = MovieFileRole.Extra, Label = em.Groups[1].Value, Size = f.Size, Path = f.FullPath }); continue; }
                var pm = PartRe.Match(f.Name);
                if (pm.Success) { rows.Add(new Row { Role = MovieFileRole.Part, PartNumber = int.TryParse(pm.Groups[1].Value, out var pn) ? pn : (int?)null, Size = f.Size, Path = f.FullPath }); continue; }
                var vm = VariantRe.Match(f.Name);
                if (vm.Success) { rows.Add(new Row { Role = MovieFileRole.Variant, Label = vm.Groups[1].Value, Size = f.Size, Path = f.FullPath }); continue; }
                primaries.Add(f);
            }

            primaries.Sort((a, b) => b.Size.CompareTo(a.Size));
            var feature = primaries.Where(p => p.Size >= MinFeatureBytes).ToList();
            if (feature.Count == 0) feature = primaries;
            if (feature.Count >= 1)
            {
                rows.Insert(0, new Row { Role = MovieFileRole.Primary, Size = feature[0].Size, Path = feature[0].FullPath });
                if (feature.Count > 1) return (rows, $"MULTI_PRIMARY({feature.Count})");
            }
            return (rows, "");
        }

        // ── minimal RFC-4180 CSV reader → per-row dictionaries keyed by the header ──
        private static IEnumerable<Dictionary<string, string>> ReadCsv(string path)
        {
            var records = ParseRecords(path);
            if (records.Count == 0) yield break;
            var header = records[0];
            for (int i = 1; i < records.Count; i++)
            {
                var f = records[i];
                if (f.Count == 1 && f[0].Length == 0) continue;
                var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                for (int c = 0; c < header.Count && c < f.Count; c++) d[header[c]] = f[c];
                yield return d;
            }
        }

        private static List<List<string>> ParseRecords(string path)
        {
            var text = File.ReadAllText(path, Encoding.UTF8).TrimStart('﻿');
            var records = new List<List<string>>();
            var field = new StringBuilder();
            var record = new List<string>();
            bool inQuotes = false;
            for (int i = 0; i < text.Length; i++)
            {
                char ch = text[i];
                if (inQuotes)
                {
                    if (ch == '"') { if (i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i++; } else inQuotes = false; }
                    else field.Append(ch);
                }
                else
                {
                    switch (ch)
                    {
                        case '"': inQuotes = true; break;
                        case ',': record.Add(field.ToString()); field.Clear(); break;
                        case '\r': break;
                        case '\n': record.Add(field.ToString()); field.Clear(); records.Add(record); record = new List<string>(); break;
                        default: field.Append(ch); break;
                    }
                }
            }
            if (field.Length > 0 || record.Count > 0) { record.Add(field.ToString()); records.Add(record); }
            return records;
        }
    }
}
