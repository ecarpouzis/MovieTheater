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
using MovieTheater.Console;
using MovieTheater.Db;
using MovieTheater.Services;

namespace MovieTheater.Ingest
{
    /// <summary>
    /// Fills episode→file gaps the original ingest missed — including the pre-existing series, which live
    /// wherever the conventions put them (anime under <c>1 - Movies\!Anime</c>, etc.), NOT just
    /// <c>2 - Video\Series</c>. Reads the maintained <c>data/nas-file-inventory.csv</c> snapshot (NEVER scans
    /// the NAS): finds each series' video files by title, parses season/episode from the names (SxxExx,
    /// combined, Season-folder, E-only / absolute for single-season anime), and creates the missing
    /// <see cref="MediaFile"/> rows (Primary, Label "match:&lt;strategy&gt;"). Idempotent. Dry-run by default.
    /// </summary>
    [Command("map-series-files", Description = "Map episode files from the NAS inventory CSV to episodes with gaps (covers !Anime + everywhere; no NAS scan).")]
    public class MapSeriesFilesCommand : BasicDICommand, ICommand
    {
        [CommandOption("apply", Description = "Create MediaFile rows. Omit for a dry run (default).")]
        public bool Apply { get; set; }

        [CommandOption("inventory", Description = "Path to the NAS inventory CSV.")]
        public string InventoryPath { get; set; } = Path.Combine("data", "nas-file-inventory.csv");

        [CommandOption("limit", Description = "Max series to process this run.")]
        public int? Limit { get; set; }

        private static readonly HashSet<string> VideoExt = new(StringComparer.OrdinalIgnoreCase)
        { ".mkv", ".mp4", ".avi", ".m4v", ".mov", ".wmv", ".ts", ".m2ts", ".mpg", ".mpeg", ".flv", ".webm", ".divx", ".ogm", ".rmvb" };

        private readonly IDbContextFactory<MovieDb> dbFactory;

        public MapSeriesFilesCommand(MovieTheaterConfiguration config) : base(config)
        {
            dbFactory = GetRequiredService<IDbContextFactory<MovieDb>>();
        }

        public async ValueTask ExecuteAsync(IConsole console)
        {
            var w = console.Output;
            var invPath = Path.GetFullPath(InventoryPath);
            if (!File.Exists(invPath)) { console.Error.WriteLine($"Inventory CSV not found: {invPath}"); return; }

            // Index inventory video files by every normalized path-segment (folder name), so a series can be
            // found by title wherever it lives. (One read of a local CSV — no NAS access.)
            w.WriteLine($"Reading inventory {invPath} …");
            var filesBySegment = new Dictionary<string, List<InvFile>>();
            int videoRows = 0;
            foreach (var rec in ReadCsv(invPath))
            {
                if (!rec.TryGetValue("Category", out var cat) || !string.Equals(cat, "video", StringComparison.OrdinalIgnoreCase)) continue;
                var full = rec.GetValueOrDefault("FullPath");
                var rel = rec.GetValueOrDefault("RelativePath");
                var name = rec.GetValueOrDefault("FileName");
                if (string.IsNullOrWhiteSpace(full) || string.IsNullOrWhiteSpace(rel)) continue;
                var ext = rec.GetValueOrDefault("Extension");
                if (!string.IsNullOrEmpty(ext) && !VideoExt.Contains(ext)) continue;
                videoRows++;
                var f = new InvFile { Full = full, Rel = rel, Name = name ?? Path.GetFileName(full) };
                foreach (var seg in rel.Split('\\', '/'))
                {
                    var key = NormTitle(seg);
                    if (key.Length < 2) continue;
                    if (!filesBySegment.TryGetValue(key, out var list)) filesBySegment[key] = list = new List<InvFile>();
                    list.Add(f);
                }
            }
            w.WriteLine($"Indexed {videoRows} video files.");

            List<(int Id, string Title, string Scraped, string Tt, int? Year)> targets;
            HashSet<string> mappedPaths;
            await using (var db = await dbFactory.CreateDbContextAsync())
            {
                targets = (await db.Series
                    .Where(s => db.Episodes.Any(e => e.SeriesId == s.Id)
                        && db.Episodes.Any(e => e.SeriesId == s.Id && !db.MediaFiles.Any(f => f.PlayableId == e.PlayableId)))
                    .Select(s => new { s.Id, s.Title, s.ImdbScrapedTitle, s.imdbID, s.ReleaseDate, s.ImdbReleaseDate, s.StartYear }).ToListAsync())
                    .Select(s => (s.Id, s.Title ?? "", s.ImdbScrapedTitle ?? "", s.imdbID ?? "",
                        (int?)(s.ReleaseDate != null ? s.ReleaseDate.Value.Year : (s.ImdbReleaseDate != null ? s.ImdbReleaseDate.Value.Year : s.StartYear))))
                    .OrderBy(t => t.Item2, StringComparer.OrdinalIgnoreCase).ToList();
                if (Limit.HasValue) targets = targets.Take(Limit.Value).ToList();
                mappedPaths = (await db.MediaFiles.Select(f => f.Path).ToListAsync()).Select(NormPath).ToHashSet();
            }

            w.WriteLine($"series with episode-file gaps: {targets.Count}{(Apply ? "" : " — DRY RUN")}\n");
            int noFiles = 0, totalMatched = 0, seriesTouched = 0;
            var missing = new List<(int Id, string Title, string Scraped, string Tt, int? Year)>();

            foreach (var (id, title, scraped, tt, year) in targets)
            {
                // Find the folder by the DB title, then fall back to the IMDb-scraped title (folders are often
                // named with the canonical/AKA title rather than what's in our Title column).
                List<InvFile> candidates = null;
                if (filesBySegment.TryGetValue(NormTitle(title), out var c1) && c1.Count > 0) candidates = c1;
                else if (scraped.Length > 0 && filesBySegment.TryGetValue(NormTitle(scraped), out var c2) && c2.Count > 0) candidates = c2;
                if (candidates == null) { noFiles++; missing.Add((id, title, scraped, tt, year)); continue; }
                var candFiles = candidates.GroupBy(c => NormPath(c.Full)).Select(g => g.First()).ToList();

                List<EpRow> eps;
                await using (var db = await dbFactory.CreateDbContextAsync())
                {
                    eps = await db.Episodes.Where(e => e.SeriesId == id)
                        .Select(e => new EpRow { Id = e.Id, Season = e.SeasonNumber, Ep = e.EpisodeNumber, PlayableId = e.PlayableId,
                            HasFile = e.PlayableId != null && db.MediaFiles.Any(f => f.PlayableId == e.PlayableId) })
                        .ToListAsync();
                }
                var unmappedByKey = eps.Where(e => !e.HasFile).GroupBy(e => (e.Season, e.Ep)).ToDictionary(g => g.Key, g => g.First());
                if (unmappedByKey.Count == 0) continue;
                bool singleSeason = eps.Select(e => e.Season).Distinct().Count() <= 1;

                var toAdd = new List<(int episodeId, int? playableId, string path, string strat)>();
                foreach (var c in candFiles.OrderBy(c => c.Rel, StringComparer.OrdinalIgnoreCase))
                {
                    if (mappedPaths.Contains(NormPath(c.Full))) continue;
                    var parsed = ParseSe(c.Rel, c.Name, singleSeason);
                    if (parsed == null) continue;
                    var (season, ep, strat) = parsed.Value;
                    if (!unmappedByKey.TryGetValue((season, ep), out var epRow)) continue;
                    toAdd.Add((epRow.Id, epRow.PlayableId, c.Full, strat));
                    unmappedByKey.Remove((season, ep));
                    mappedPaths.Add(NormPath(c.Full));
                }

                if (toAdd.Count > 0)
                {
                    seriesTouched++; totalMatched += toAdd.Count;
                    var sample = toAdd.GroupBy(a => a.strat).Select(g => $"{g.Key}×{g.Count()}");
                    w.WriteLine($"  S{id} \"{title}\": +{toAdd.Count} ({string.Join(", ", sample)})  [{candFiles.Count} candidate files]");
                    if (Apply)
                    {
                        await using var db = await dbFactory.CreateDbContextAsync();
                        foreach (var a in toAdd)
                        {
                            int pid;
                            if (a.playableId != null) pid = a.playableId.Value;
                            else
                            {
                                var p = new Playable { Kind = PlayableKind.Episode };
                                db.Playables.Add(p); await db.SaveChangesAsync();
                                var ep = await db.Episodes.FirstAsync(e => e.Id == a.episodeId);
                                ep.PlayableId = p.Id; await db.SaveChangesAsync();
                                pid = p.Id;
                            }
                            db.MediaFiles.Add(new MediaFile { PlayableId = pid, Path = a.path, Role = MovieFileRole.Primary, Label = "match:" + a.strat });
                        }
                        await db.SaveChangesAsync();
                    }
                }
            }

            w.WriteLine($"\n{(Apply ? "" : "DRY RUN — ")}series touched {seriesTouched}, files mapped {totalMatched}, no-files-found {noFiles}.");

            if (missing.Count > 0)
            {
                w.WriteLine($"\n── {missing.Count} series with episode gaps and NO folder found in the inventory (tried DB + IMDb title) ──");
                foreach (var m in missing.OrderBy(m => m.Title, StringComparer.OrdinalIgnoreCase))
                    w.WriteLine($"  S{m.Id}  \"{m.Title}\"  | imdb-title: \"{m.Scraped}\"  | {m.Tt}  | {m.Year}");
            }
        }

        private sealed class InvFile { public string Full; public string Rel; public string Name; }
        private sealed class EpRow { public int Id; public int Season; public int Ep; public int? PlayableId; public bool HasFile; }

        private static (int season, int ep, string strat)? ParseSe(string rel, string name, bool singleSeason)
        {
            var c = Regex.Match(name, @"(?i)S(\d{1,2})\s*E(\d{1,3})\s*[&\-+]?\s*E(\d{1,3})");
            if (c.Success) return (int.Parse(c.Groups[1].Value), int.Parse(c.Groups[2].Value), "combined");
            var se = Regex.Match(name, @"(?i)S(\d{1,2})[ ._\-]*E(\d{1,3})");
            if (se.Success) return (int.Parse(se.Groups[1].Value), int.Parse(se.Groups[2].Value), "se");
            var x = Regex.Match(name, @"(?<![\dpP])(\d{1,2})x(\d{1,3})(?![\d])");
            if (x.Success) return (int.Parse(x.Groups[1].Value), int.Parse(x.Groups[2].Value), "se");
            // "Season N" folder anywhere in the relative path + an episode number/marker in the name.
            var sf = Regex.Match(rel, @"(?i)(?:Season|Series|Book|Volume)\s*(\d{1,2})");
            var en = Regex.Match(name, @"(?i)(?<![a-z0-9])E(?:p|pisode)?\s*0*(\d{1,3})(?![\d])");
            if (sf.Success && en.Success) return (int.Parse(sf.Groups[1].Value), int.Parse(en.Groups[1].Value), "folderseason");
            // E-only marker (no season) → single-season shows are season 1 (anime "…E01…").
            if (singleSeason && en.Success) return (1, int.Parse(en.Groups[1].Value), "ep");
            // Bare absolute number for single-season shows, only when exactly one plausible number remains.
            if (singleSeason)
            {
                var nums = Regex.Matches(name, @"(?<![\d])(\d{1,3})(?![\d])").Select(m => int.Parse(m.Value))
                    .Where(n => n >= 1 && n <= 400).ToList();
                if (nums.Count == 1) return (1, nums[0], "absolute");
            }
            return null;
        }

        private static string NormPath(string p) => (p ?? "").Replace('/', '\\').TrimEnd('\\').ToLowerInvariant();

        private static string NormTitle(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return "";
            s = TitleNorm.Fold(s);   // ASCII-fold first (Æ→ae, accents) so glyph titles match plain-ASCII folders
            s = Regex.Replace(s, @"\([^)]*\)", " ");
            s = Regex.Replace(s, @"\[[^\]]*\]", " ");
            s = Regex.Replace(s, @"\b(480p|576p|720p|1080p|2160p|4k|uhd|hdr|bluray|web-?dl|webrip|x264|x265|hevc)\b", " ");
            s = s.Replace("&", "and");
            // Drop the article "the" in ANY position so "The Angry Beavers" and the on-disk ", The"
            // inversion ("Angry Beavers, The") normalize identically.
            s = Regex.Replace(s, @"\bthe\b", " ");
            var sb = new StringBuilder();
            foreach (var ch in s) if (char.IsLetterOrDigit(ch)) sb.Append(ch);
            return sb.ToString();
        }

        // Minimal RFC-4180 CSV reader yielding header-keyed rows.
        private static IEnumerable<Dictionary<string, string>> ReadCsv(string path)
        {
            using var reader = new StreamReader(path, Encoding.UTF8);
            string headerLine = ReadRecord(reader, out _);
            if (headerLine == null) yield break;
            var headers = SplitCsvLine(headerLine);
            while (true)
            {
                var line = ReadRecord(reader, out bool eof);
                if (line == null) break;
                if (line.Length == 0) { if (eof) break; else continue; }
                var fields = SplitCsvLine(line);
                var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < headers.Count && i < fields.Count; i++) dict[headers[i]] = fields[i];
                yield return dict;
                if (eof) break;
            }
        }

        // Read one logical CSV record (handles quoted fields containing newlines).
        private static string ReadRecord(StreamReader reader, out bool eof)
        {
            var sb = new StringBuilder();
            bool inQuotes = false; int ch;
            eof = false;
            while ((ch = reader.Read()) != -1)
            {
                char c = (char)ch;
                if (c == '"') { inQuotes = !inQuotes; sb.Append(c); }
                else if ((c == '\n' || c == '\r') && !inQuotes)
                {
                    if (c == '\r' && reader.Peek() == '\n') reader.Read();
                    return sb.ToString().TrimStart('﻿');
                }
                else sb.Append(c);
            }
            eof = true;
            return sb.Length == 0 ? null : sb.ToString().TrimStart('﻿');
        }

        private static List<string> SplitCsvLine(string line)
        {
            var fields = new List<string>();
            var sb = new StringBuilder();
            bool inQuotes = false;
            for (int i = 0; i < line.Length; i++)
            {
                char c = line[i];
                if (inQuotes)
                {
                    if (c == '"') { if (i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; } else inQuotes = false; }
                    else sb.Append(c);
                }
                else if (c == '"') inQuotes = true;
                else if (c == ',') { fields.Add(sb.ToString()); sb.Clear(); }
                else sb.Append(c);
            }
            fields.Add(sb.ToString());
            return fields;
        }
    }
}
