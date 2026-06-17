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
    /// The movie counterpart of <see cref="MapSeriesFilesCommand"/>: finds on-disk files for approved movies
    /// that have no <see cref="MediaFile"/> (no "watch" button) using the maintained
    /// <c>data/nas-file-inventory.csv</c> snapshot (NEVER scans the NAS). Year-gated passes, all keyed on a
    /// file's IMMEDIATE PARENT folder so a film nested in a franchise collection keys on its own folder
    /// (avoids the "Breakin'" -> "Breakin' 2" collision):
    ///   • PASS 1 (exact): folder name normalizes exactly to the movie Title + carries its year (±1).
    ///   • PASS 2 (token): for franchise/collection folders named "N - Title (Year)" with on-disk typos or
    ///     extra subtitle words ("2 - Star Trek II - The Wrath of Khan (1982)"), match when the year matches
    ///     AND the movie's significant tokens are (almost) a subset of the folder's. Distinct, year-stamped
    ///     titles only — Eric's call that this is safe.
    /// ASCII-folds via <see cref="TitleNorm"/> (AE-ligature -> "ae", accents stripped) so "AEon Flux" matches
    /// the on-disk "Aeon Flux (2005)". NEVER matches by SimpleTitle (munged for franchise sorting). Pass-2 rows
    /// get Label "match:movie-fuzzy" so review can eyeball them. Idempotent. Dry-run by default.
    /// </summary>
    [Command("map-movie-files", Description = "Map on-disk files to approved movies that have none (inventory CSV; no NAS scan). Year-gated exact + token passes.")]
    public class MapMovieFilesCommand : BasicDICommand, ICommand
    {
        [CommandOption("apply", Description = "Create Playable/MediaFile rows. Omit for a dry run (default).")]
        public bool Apply { get; set; }

        [CommandOption("inventory", Description = "Path to the NAS inventory CSV.")]
        public string InventoryPath { get; set; } = Path.Combine("data", "nas-file-inventory.csv");

        [CommandOption("id", Description = "Only this movie id (for spot-fixes).")]
        public int? OnlyId { get; set; }

        [CommandOption("fuzzy", Description = "Enable PASS 2 token matching for franchise/collection folders (default true).")]
        public bool Fuzzy { get; set; } = true;

        [CommandOption("min-tokens", Description = "Min significant tokens for a fuzzy (PASS 2) match. Lower = matches shorter titles, riskier.")]
        public int MinFuzzyTokens { get; set; } = 3;

        [CommandOption("aliases", Description = "TSV of 'movieId<TAB>folderPathSubstring' forced matches for alt/foreign titles.")]
        public string AliasPath { get; set; } = Path.Combine("data", "_movie_file_aliases.tsv");

        private static readonly HashSet<string> VideoExt = new(StringComparer.OrdinalIgnoreCase)
        { ".mkv", ".mp4", ".avi", ".m4v", ".mov", ".wmv", ".ts", ".m2ts", ".mpg", ".mpeg", ".flv", ".webm", ".divx", ".ogm", ".rmvb" };
        private static readonly HashSet<string> Stop = new(StringComparer.Ordinal)
        { "the", "a", "an", "of", "and", "part", "pt", "vol", "volume", "in" };

        private readonly IDbContextFactory<MovieDb> dbFactory;

        public MapMovieFilesCommand(MovieTheaterConfiguration config) : base(config)
        {
            dbFactory = GetRequiredService<IDbContextFactory<MovieDb>>();
        }

        public async ValueTask ExecuteAsync(IConsole console)
        {
            var w = console.Output;
            var invPath = Path.GetFullPath(InventoryPath);
            if (!File.Exists(invPath)) { console.Error.WriteLine($"Inventory CSV not found: {invPath}"); return; }

            // Group inventory video files by their immediate parent directory; record each folder's name
            // tokens + years once. (One local CSV read; no NAS access.)
            w.WriteLine($"Reading inventory {invPath} …");
            var folders = new Dictionary<string, FolderGroup>(StringComparer.OrdinalIgnoreCase);
            int videoRows = 0;
            foreach (var rec in ReadCsv(invPath))
            {
                if (!rec.TryGetValue("Category", out var cat) || !string.Equals(cat, "video", StringComparison.OrdinalIgnoreCase)) continue;
                var full = rec.GetValueOrDefault("FullPath");
                var name = rec.GetValueOrDefault("FileName");
                var parent = rec.GetValueOrDefault("ParentFolder");
                if (string.IsNullOrWhiteSpace(full) || string.IsNullOrWhiteSpace(parent)) continue;
                var ext = rec.GetValueOrDefault("Extension");
                if (!string.IsNullOrEmpty(ext) && !VideoExt.Contains(ext)) continue;
                long.TryParse(rec.GetValueOrDefault("SizeBytes"), out var size);
                videoRows++;
                var dirKey = NormPath(Path.GetDirectoryName(full) ?? full);
                if (!folders.TryGetValue(dirKey, out var fg))
                    folders[dirKey] = fg = new FolderGroup { Norm = NormFolder(parent), Tokens = Tokens(parent, true), Years = YearsIn(parent) };
                fg.Files.Add(new InvFile { Full = full, Name = name ?? Path.GetFileName(full), Size = size });
            }
            var byNorm = new Dictionary<string, List<FolderGroup>>();
            foreach (var fg in folders.Values)
            {
                if (fg.Norm.Length < 2) continue;
                if (!byNorm.TryGetValue(fg.Norm, out var l)) byNorm[fg.Norm] = l = new List<FolderGroup>();
                l.Add(fg);
            }
            var allFiles = folders.Values.SelectMany(fg => fg.Files).ToList();
            w.WriteLine($"Indexed {videoRows} video files into {folders.Count} folders.");

            // Optional explicit aliases (the long-tail resolver): movieId -> a substring of the file's full
            // path. Lets alt/foreign-title films the automatic passes can't reach be pinned by hand/masterlist.
            var aliases = new Dictionary<int, string>();
            var aliasPath = Path.GetFullPath(AliasPath);
            if (File.Exists(aliasPath))
                foreach (var raw in File.ReadAllLines(aliasPath))
                {
                    var line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith("#")) continue;
                    var parts = line.Split('|');   // '|' is illegal in Windows paths, so it's an unambiguous delimiter
                    if (parts.Length >= 2 && int.TryParse(parts[0].Trim(), out var mid)) aliases[mid] = parts[1].Trim();
                }
            if (aliases.Count > 0) w.WriteLine($"Loaded {aliases.Count} aliases from {aliasPath}.");

            List<(int Id, string Title, int? Year, string Tt, int? PlayableId)> targets;
            HashSet<string> mappedPaths;
            Dictionary<string, int> reclaimable;   // normPath -> the episode MediaFile.Id to delete on reclaim
            await using (var db = await dbFactory.CreateDbContextAsync())
            {
                var q = db.Movies.Where(m => m.TitleType == TitleType.Movie && m.ReviewBatch == null
                        && !db.MediaFiles.Any(f => f.PlayableId == m.PlayableId));
                if (OnlyId.HasValue) q = db.Movies.Where(m => m.id == OnlyId.Value);
                targets = (await q.Select(m => new { m.id, m.Title, m.ReleaseDate, m.ImdbReleaseDate, m.imdbID, m.PlayableId }).ToListAsync())
                    .Select(m => (m.id, m.Title ?? "", (int?)(m.ReleaseDate != null ? m.ReleaseDate.Value.Year : (m.ImdbReleaseDate != null ? m.ImdbReleaseDate.Value.Year : (int?)null)), m.imdbID ?? "", m.PlayableId))
                    .OrderBy(t => t.Item2, StringComparer.OrdinalIgnoreCase).ToList();

                // A file attached to an EPISODE via a low-confidence position guess (absolute/combined) is
                // RECLAIMABLE: a confident movie title+year match should win it back. This is the fix for movies
                // mis-filed inside a series folder (e.g. the 2005 film under "…\Aeon Flux (1991)\Aeon Flux (2005)"
                // that the absolute-numbering guess grabbed as S02E02). Confident episode strategies (se,
                // folderseason, …) are NOT reclaimable.
                var risky = await db.MediaFiles
                    .Where(f => (f.Label == "match:absolute" || f.Label == "match:combined")
                        && db.Episodes.Any(e => e.PlayableId == f.PlayableId))
                    .Select(f => new { f.Id, f.Path }).ToListAsync();
                reclaimable = new Dictionary<string, int>();
                foreach (var r in risky) reclaimable[NormPath(r.Path)] = r.Id;

                // Exclusion set = every mapped path EXCEPT the reclaimable ones (those stay matchable).
                mappedPaths = (await db.MediaFiles.Select(f => f.Path).ToListAsync())
                    .Select(NormPath).Where(p => !reclaimable.ContainsKey(p)).ToHashSet();
            }

            w.WriteLine($"fileless approved movies: {targets.Count}{(Apply ? "" : " — DRY RUN")}\n");
            int exact = 0, fuzzy = 0, alias = 0, mappedFiles = 0, reclaims = 0;
            var unmatched = new List<(int Id, string Title, int? Year, string Tt)>();

            foreach (var (id, title, year, tt, playableId) in targets)
            {
                bool yrOk(FolderGroup g) => !year.HasValue || g.Years.Any(y => Math.Abs(y - year.Value) <= 1);
                var titleNorm = NormTitle(title);
                var titleToks = Tokens(title, false);

                List<InvFile> pool = null; string strat = null;

                // PASS 0 — explicit alias: pin this movie to files whose full path contains the substring.
                // The long-tail resolver for alt/foreign titles the automatic passes can't reach.
                if (aliases.TryGetValue(id, out var sub) && sub.Length > 0)
                {
                    var subN = NormPath(sub);
                    pool = Usable(allFiles.Where(f => NormPath(f.Full).Contains(subN)), mappedPaths);
                    if (pool.Count > 0) strat = "movie-alias";
                }

                if (strat == null)
                {
                    FolderGroup hit = null;
                    if (byNorm.TryGetValue(titleNorm, out var exactCands))
                        hit = exactCands.FirstOrDefault(g => yrOk(g) && Usable(g.Files, mappedPaths).Count > 0);
                    if (hit != null) strat = "movie-title-year";

                    // PASS 2 — token overlap for franchise/collection folders (year-gated). Folder may carry
                    // extra words (the "N -" ordinal, a franchise prefix); year does the disambiguation.
                    if (hit == null && Fuzzy && year.HasValue && titleToks.Count >= MinFuzzyTokens)
                    {
                        FolderGroup best = null; double bestScore = 0;
                        foreach (var g in folders.Values)
                        {
                            if (!yrOk(g) || g.Tokens.Count == 0) continue;
                            if (Usable(g.Files, mappedPaths).Count == 0) continue;
                            int inter = titleToks.Count(t => g.Tokens.Contains(t));
                            double score = (double)inter / Math.Min(titleToks.Count, g.Tokens.Count);
                            if (score > bestScore) { bestScore = score; best = g; }
                        }
                        if (best != null && bestScore >= 0.70) { hit = best; strat = "movie-fuzzy"; }
                    }
                    if (hit != null) pool = Usable(hit.Files, mappedPaths);
                }

                if (pool == null || pool.Count == 0) { unmatched.Add((id, title, year, tt)); continue; }
                var files = SelectFeatureAndParts(pool);
                if (files.Count == 0) { unmatched.Add((id, title, year, tt)); continue; }
                if (strat == "movie-fuzzy") fuzzy++; else if (strat == "movie-alias") alias++; else exact++;
                mappedFiles += files.Count;
                bool reclaimed = files.Any(f => reclaimable.ContainsKey(NormPath(f.file.Full)));
                if (reclaimed) reclaims++;
                var partNote = files.Count > 1 ? $" (+{files.Count - 1} part)" : "";
                var flag = (strat == "movie-fuzzy" ? "  ~fuzzy" : strat == "movie-alias" ? "  [alias]" : "")
                         + (reclaimed ? "  [reclaimed-from-series-episode]" : "");
                w.WriteLine($"  M{id} \"{title}\" ({year}) -> {Path.GetFileName(files[0].file.Full)}{partNote}{flag}");
                if (Apply)
                {
                    await using var db = await dbFactory.CreateDbContextAsync();
                    int pid;
                    var m = await db.Movies.FirstAsync(x => x.id == id);
                    if (m.PlayableId != null) pid = m.PlayableId.Value;
                    else
                    {
                        var p = new Playable { Kind = PlayableKind.Movie };
                        db.Playables.Add(p); await db.SaveChangesAsync();
                        m.PlayableId = p.Id; await db.SaveChangesAsync();
                        pid = p.Id;
                    }
                    foreach (var (file, role, part) in files)
                    {
                        if (reclaimable.TryGetValue(NormPath(file.Full), out var oldId))
                        {
                            var old = await db.MediaFiles.FirstOrDefaultAsync(x => x.Id == oldId);
                            if (old != null) db.MediaFiles.Remove(old);   // free the file from the wrong episode
                        }
                        db.MediaFiles.Add(new MediaFile { PlayableId = pid, Path = file.Full, Role = role, PartNumber = part, Label = "match:" + strat });
                        mappedPaths.Add(NormPath(file.Full));
                    }
                    await db.SaveChangesAsync();
                }
            }

            w.WriteLine($"\n{(Apply ? "" : "DRY RUN — ")}mapped {exact + fuzzy + alias} movies ({exact} exact, {fuzzy} fuzzy, {alias} alias; {reclaims} reclaimed from mis-mapped series episodes), {mappedFiles} files; unmatched {unmatched.Count}.");
            if (unmatched.Count > 0)
            {
                w.WriteLine($"\n── {unmatched.Count} movies with no confident on-disk match (not owned = wishlist, or filed under a name too different → map by hand) ──");
                foreach (var u in unmatched.OrderBy(u => u.Title, StringComparer.OrdinalIgnoreCase))
                    w.WriteLine($"  M{u.Id}  \"{u.Title}\" ({u.Year})  {u.Tt}");
            }
        }

        private static List<InvFile> Usable(IEnumerable<InvFile> files, HashSet<string> mapped) =>
            files.Where(c => !Episodic(c.Name) && !Junk(c.Name) && !mapped.Contains(NormPath(c.Full))).ToList();

        private static List<(InvFile file, MovieFileRole role, int? part)> SelectFeatureAndParts(List<InvFile> pool)
        {
            if (pool.Count == 0) return new();
            var withPart = pool.Select(c => (file: c, part: PartNumber(c.Name))).ToList();
            var numbered = withPart.Where(x => x.part != null).OrderBy(x => x.part).ToList();
            if (numbered.Count >= 2 && numbered.Count == pool.Count)
            {
                var res = new List<(InvFile, MovieFileRole, int?)> { (numbered[0].file, MovieFileRole.Primary, null) };
                foreach (var x in numbered.Skip(1)) res.Add((x.file, MovieFileRole.Part, x.part));
                return res;
            }
            var byTrailing = TrailingNumberParts(pool);
            if (byTrailing != null) return byTrailing;
            var feature = pool.OrderByDescending(c => c.Size).First();
            return new List<(InvFile, MovieFileRole, int?)> { (feature, MovieFileRole.Primary, null) };
        }

        // Split parts that lack a cd/disc/part token but share a prefix and end in distinct small numbers
        // ("Millenium Mambo 1.mkv" / "Millenium Mambo 2.mkv"). 2-4 files, numbers 1..N only — conservative.
        private static List<(InvFile, MovieFileRole, int?)> TrailingNumberParts(List<InvFile> pool)
        {
            if (pool.Count < 2 || pool.Count > 4) return null;
            var rx = new Regex(@"^(.*?)[ ._\-]*(\d{1,2})$");
            var parsed = new List<(InvFile f, string pre, int n)>();
            foreach (var c in pool)
            {
                var m = rx.Match(Path.GetFileNameWithoutExtension(c.Name));
                if (!m.Success) return null;
                parsed.Add((c, m.Groups[1].Value.Trim().ToLowerInvariant(), int.Parse(m.Groups[2].Value)));
            }
            if (parsed.Select(p => p.pre).Distinct().Count() != 1) return null;          // must share a prefix
            if (parsed.Select(p => p.n).Distinct().Count() != parsed.Count) return null; // distinct numbers
            if (parsed.Max(p => p.n) > pool.Count) return null;                          // 1..N, not a year
            var ordered = parsed.OrderBy(p => p.n).ToList();
            var res = new List<(InvFile, MovieFileRole, int?)> { (ordered[0].f, MovieFileRole.Primary, null) };
            foreach (var x in ordered.Skip(1)) res.Add((x.f, MovieFileRole.Part, x.n));
            return res;
        }

        private sealed class FolderGroup { public string Norm; public HashSet<string> Tokens; public List<int> Years; public List<InvFile> Files = new(); }
        private sealed class InvFile { public string Full; public string Name; public long Size; }

        private static int? PartNumber(string name)
        {
            var m = Regex.Match(name, @"(?i)\b(?:cd|disc|disk|part|pt)\s*0*(\d{1,2})\b");
            return m.Success ? int.Parse(m.Groups[1].Value) : (int?)null;
        }
        private static bool Episodic(string name) => Regex.IsMatch(name, @"(?i)S\d{1,2}\s*E\d{1,3}|\b\d{1,2}x\d{1,3}\b");
        private static bool Junk(string name) { var n = name.ToLowerInvariant(); return n.Contains("sample") || n.Contains("trailer"); }
        private static List<int> YearsIn(string s) => Regex.Matches(s, @"(?:19|20)\d\d").Select(m => int.Parse(m.Value)).ToList();
        private static string NormPath(string p) => (p ?? "").Replace('/', '\\').TrimEnd('\\').ToLowerInvariant();

        // Lowercase, ASCII-fold (shared TitleNorm), strip (year)/[bracket]/quality, &->and. Cleaned form
        // before token/alnum.
        private static string Clean(string s)
        {
            s = TitleNorm.Fold(s);
            s = Regex.Replace(s, @"\([^)]*\)", " ");
            s = Regex.Replace(s, @"\[[^\]]*\]", " ");
            s = Regex.Replace(s, @"\b(480p|576p|720p|1080p|2160p|4k|uhd|hdr|bluray|web-?dl|webrip|x264|x265|hevc|remux|dvdrip|xvid|brrip|bdrip)\b", " ");
            return s.Replace("&", "and");
        }

        private static string NormTitle(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return "";
            s = Regex.Replace(Clean(s), @"\bthe\b", " ");
            var sb = new StringBuilder();
            foreach (var ch in s) if (char.IsLetterOrDigit(ch)) sb.Append(ch);
            return sb.ToString();
        }

        // Folder form: also drop a leading ordinal prefix ("2 - ", "14a - ") the NAS uses to force order.
        private static string NormFolder(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return "";
            s = Regex.Replace(s, @"^\s*\d{1,3}[a-z]?\s*-\s*", " ");
            return NormTitle(s);
        }

        private static HashSet<string> Tokens(string s, bool isFolder)
        {
            if (string.IsNullOrWhiteSpace(s)) return new();
            s = Clean(s);
            if (isFolder) s = Regex.Replace(s, @"^\s*\d{1,3}[a-z]?\s*-\s*", " ");
            var toks = Regex.Split(s, @"[^a-z0-9]+").Where(t => t.Length > 0 && !Stop.Contains(t));
            return new HashSet<string>(toks, StringComparer.Ordinal);
        }

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
