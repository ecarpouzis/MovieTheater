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

        [CommandOption("aliases", Description = "TSV of seriesId<TAB>path-substring for folders whose name doesn't match the series title (e.g. TNG → Star Trek: The Next Generation).")]
        public string AliasPath { get; set; } = Path.Combine("data", "_series_folder_aliases.tsv");

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
            var allFiles = new List<InvFile>();
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
                allFiles.Add(f);
                foreach (var seg in rel.Split('\\', '/'))
                {
                    var key = NormTitle(seg);
                    if (key.Length < 2) continue;
                    if (!filesBySegment.TryGetValue(key, out var list)) filesBySegment[key] = list = new List<InvFile>();
                    list.Add(f);
                }
            }
            w.WriteLine($"Indexed {videoRows} video files.");

            // Folder aliases: seriesId → path substrings, for series whose on-disk folder is named with an
            // abbreviation/AKA the title-segment lookup can't match (Star Trek "TNG"/"DS9"/"Voyager"). Any
            // inventory file whose path contains a substring becomes a candidate for that series.
            var seriesAliases = LoadAliases(AliasPath, w);

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
                // Candidate files. An alias makes the series EXCLUSIVE/scoped: its candidates are ONLY the
                // files matching its alias substrings, NOT the broad folder-by-title pull. This separates
                // co-located series sharing one folder tree (A Certain Magical Index vs Railgun; the Marvel
                // cartoons) so one never grabs another's files. An alias may also pin a SEASON (per-part
                // anime like "…Stardust Crusaders" → S2): those files map by (pinned season, trailing number).
                var candidates = new List<InvFile>();
                var forcedSeason = new Dictionary<string, int>();
                if (seriesAliases.TryGetValue(id, out var subs))
                {
                    // A "!substring" entry EXCLUDES files (for co-located series whose shared parent folder
                    // name pollutes a positive match — e.g. Railgun files under the "…Magical Index" tree).
                    var pos = subs.Where(a => !a.negate).ToList();
                    var neg = subs.Where(a => a.negate).ToList();
                    foreach (var f in allFiles)
                    {
                        var np = NormPath(f.Full);
                        if (neg.Any(n => np.Contains(n.sub))) continue;
                        foreach (var a in pos)
                            if (np.Contains(a.sub))
                            {
                                candidates.Add(f);
                                if (a.season.HasValue) forcedSeason[np] = a.season.Value;
                                break;
                            }
                    }
                }
                else
                {
                    if (filesBySegment.TryGetValue(NormTitle(title), out var c1)) candidates.AddRange(c1);
                    else if (scraped.Length > 0 && filesBySegment.TryGetValue(NormTitle(scraped), out var c2)) candidates.AddRange(c2);
                }
                if (candidates.Count == 0) { noFiles++; missing.Add((id, title, scraped, tt, year)); continue; }
                var candFiles = candidates.GroupBy(c => NormPath(c.Full)).Select(g => g.First()).ToList();

                List<EpRow> eps;
                await using (var db = await dbFactory.CreateDbContextAsync())
                {
                    eps = await db.Episodes.Where(e => e.SeriesId == id)
                        .Select(e => new EpRow { Id = e.Id, Season = e.SeasonNumber, Ep = e.EpisodeNumber, PlayableId = e.PlayableId, Title = e.Title,
                            HasFile = e.PlayableId != null && db.MediaFiles.Any(f => f.PlayableId == e.PlayableId) })
                        .ToListAsync();
                }
                foreach (var e in eps) e.NormTitle = NormText(e.Title);
                var gaps = eps.Where(e => !e.HasFile).ToList();
                if (gaps.Count == 0) continue;
                var unmappedByKey = gaps.GroupBy(e => (e.Season, e.Ep)).ToDictionary(g => g.Key, g => g.First());
                bool singleSeason = eps.Select(e => e.Season).Distinct().Count() <= 1;
                // Gap episodes with a distinctive (≥5-char, unique-in-series) title, for title matching.
                var titleCount = gaps.Where(e => e.NormTitle.Length >= 5).GroupBy(e => e.NormTitle).ToDictionary(g => g.Key, g => g.Count());
                var titledGaps = gaps.Where(e => e.NormTitle.Length >= 5 && titleCount[e.NormTitle] == 1).ToList();

                // Match each candidate to an episode, grouped by episode id. Prefer the EPISODE TITLE
                // (robust against the double-episode numbering offset — a double-length pilot filed
                // "1x01-02" shifts every later disk number by one vs IMDb), then fall back to parsed
                // season/episode numbers. Map an episode only when EXACTLY ONE file claims it; collisions
                // (an OVA's own 01..0N run, ambiguous absolute, dup versions) are skipped, never guessed.
                // OVAs/specials/movies are excluded outright.
                var byEp = new Dictionary<int, List<(EpRow ep, InvFile file, string strat, bool extra, bool ova)>>();
                var unmatched = new List<InvFile>();
                foreach (var c in candFiles.OrderBy(c => c.Rel, StringComparer.OrdinalIgnoreCase))
                {
                    if (mappedPaths.Contains(NormPath(c.Full))) continue;
                    // Files under a "Spinoffs" subfolder are a DIFFERENT show that happens to live in the
                    // parent series' tree (Aqua Teen's "Soul Quest Overdrive", "Carl's Pissed"). Their own
                    // S01E01… numbering would otherwise collide with the parent's real episodes — skip them.
                    if (IsSpinoffPath(c.Rel)) continue;
                    if (IsNonEpisode(c.Name)) continue;
                    bool ova = IsOva(c.Name);
                    // A file under an Extras/Featurettes/Deleted-Scenes folder — OR named like one
                    // ("… The Making Of …", "… Featurette") even when it sits beside the episodes — is BONUS
                    // content for its episode (same idea as a movie's MovieFileRole.Extra), not the episode
                    // itself. It attaches as an Extra and never competes with / blocks the real episode
                    // (Primary): e.g. "S07E20a.The Making Of Bad Jubies" must not collide with "S07E20.Bad Jubies".
                    bool extra = IsExtraPath(c.Rel) || IsExtraName(c.Name);
                    EpRow epRow = null;
                    string strat = null;
                    // A season-pinned alias (per-part anime folder) sets the season; the trailing number is
                    // the episode. Takes priority — these files carry no episode title to match on.
                    if (forcedSeason.TryGetValue(NormPath(c.Full), out var fs))
                    {
                        var en = SoloNumber(CleanForNumbers(c.Name));
                        if (en.HasValue && unmappedByKey.TryGetValue((fs, en.Value), out epRow)) strat = "partseason";
                        else { unmatched.Add(c); continue; }
                    }
                    if (epRow == null) { epRow = MatchByTitle(c.Name, titledGaps); if (epRow != null) strat = "title"; }
                    if (epRow == null)
                    {
                        var parsed = ParseSe(c.Rel, c.Name, singleSeason);
                        if (parsed != null && unmappedByKey.TryGetValue((parsed.Value.season, parsed.Value.ep), out epRow))
                            strat = parsed.Value.strat;
                    }
                    if (epRow == null) { unmatched.Add(c); continue; }
                    if (!byEp.TryGetValue(epRow.Id, out var lst)) byEp[epRow.Id] = lst = new();
                    lst.Add((epRow, c, strat, extra, ova));
                }

                // Cumulative-absolute fallback for flat-numbered MULTI-season series (X-Men "01..76" in one
                // folder; many dub anime). Map a bare absolute number to (season,ep) by walking the season
                // episode-counts — but ONLY when the series' confident title/se matches that ALSO carry a
                // solo number AGREE with that cumulative mapping (≥3 agree, 0 disagree). The agreement proves
                // the disk's flat numbering matches IMDb's ordering, so the rest can be filled confidently.
                if (!singleSeason && unmatched.Count > 0)
                {
                    var seasonsOrdered = eps.Select(e => e.Season).Distinct().OrderBy(x => x).ToList();
                    var seasonMax = eps.GroupBy(e => e.Season).ToDictionary(g => g.Key, g => g.Max(e => e.Ep));
                    var cumStart = new Dictionary<int, int>(); int acc = 0;
                    foreach (var s in seasonsOrdered) { cumStart[s] = acc; acc += seasonMax[s]; }
                    (int season, int ep)? AbsToKey(int n)
                    {
                        foreach (var s in seasonsOrdered)
                            if (n > cumStart[s] && n <= cumStart[s] + seasonMax[s]) return (s, n - cumStart[s]);
                        return null;
                    }
                    int agree = 0, disagree = 0;
                    foreach (var kv in byEp)
                        foreach (var m in kv.Value)
                        {
                            if (m.strat != "title" && m.strat != "se") continue;
                            var an = SoloNumber(CleanForNumbers(m.file.Name));
                            if (!an.HasValue) continue;
                            var ck = AbsToKey(an.Value);
                            if (ck == null) continue;
                            if (ck.Value.season == m.ep.Season && ck.Value.ep == m.ep.Ep) agree++; else disagree++;
                        }
                    if (agree >= 3 && disagree == 0)
                        foreach (var c in unmatched)
                        {
                            var an = SoloNumber(CleanForNumbers(c.Name));
                            if (!an.HasValue) continue;
                            var ck = AbsToKey(an.Value);
                            if (ck == null || !unmappedByKey.TryGetValue(ck.Value, out var epRow)) continue;
                            if (!byEp.TryGetValue(epRow.Id, out var lst)) byEp[epRow.Id] = lst = new();
                            lst.Add((epRow, c, "cumulative", IsExtraPath(c.Rel), IsOva(c.Name)));
                        }
                }

                // Per episode: map the Primary only when EXACTLY ONE non-extra file claims it (collisions
                // stay skipped); attach every extra-folder file as a Role=Extra on the same episode.
                var toAdd = new List<(int episodeId, int? playableId, string path, string strat, MovieFileRole role)>();
                int collisions = 0;
                foreach (var kv in byEp)
                {
                    var prims = kv.Value.Where(x => !x.extra).ToList();
                    // A non-OVA file wins the episode; OVA files are only considered when nothing else
                    // claims it (so an OVA-only series maps, but an OVA never displaces a real episode).
                    var nonOva = prims.Where(x => !x.ova).ToList();
                    var pick = nonOva.Count > 0 ? nonOva : prims;
                    // Disk vs IMDb numbering frequently disagrees (renumbered seasons, episode-0 pilots,
                    // shifted season boundaries): one file matched by episode TITLE and a *different* file
                    // mis-parsed onto the same episode by its (wrong) S##E## number both claim it. The title
                    // match is the reliable one — when EXACTLY ONE claimant matched by title, it wins and the
                    // number-only claimants are released (they stay unmapped rather than blocking the correct
                    // file). Two title claimants (recurring guests: "Coolio" vs "Coolio (again)") stay a
                    // collision — genuinely ambiguous, so still skipped.
                    if (pick.Count > 1)
                    {
                        var titled = pick.Where(x => x.strat == "title").ToList();
                        if (titled.Count == 1) pick = titled;
                    }
                    if (pick.Count == 1)
                    {
                        var one = pick[0];
                        toAdd.Add((one.ep.Id, one.ep.PlayableId, one.file.Full, one.strat, MovieFileRole.Primary));
                        mappedPaths.Add(NormPath(one.file.Full));
                    }
                    else if (pick.Count > 1) collisions++;   // 2+ real files claim this episode → not confident
                    foreach (var e in kv.Value.Where(x => x.extra))
                    {
                        toAdd.Add((e.ep.Id, e.ep.PlayableId, e.file.Full, "extra", MovieFileRole.Extra));
                        mappedPaths.Add(NormPath(e.file.Full));
                    }
                }

                if (toAdd.Count > 0)
                {
                    seriesTouched++;
                    int nPrim = toAdd.Count(a => a.role == MovieFileRole.Primary);
                    int nExtra = toAdd.Count(a => a.role == MovieFileRole.Extra);
                    totalMatched += nPrim;
                    var sample = toAdd.Where(a => a.role == MovieFileRole.Primary).GroupBy(a => a.strat).Select(g => $"{g.Key}×{g.Count()}");
                    var coll = collisions > 0 ? $", {collisions} skipped-collision" : "";
                    var ex = nExtra > 0 ? $", {nExtra} extra" : "";
                    w.WriteLine($"  S{id} \"{title}\": +{nPrim} ({string.Join(", ", sample)}{coll}{ex})  [{candFiles.Count} candidate files]");
                    if (Apply)
                    {
                        await using var db = await dbFactory.CreateDbContextAsync();
                        // Group by episode so the Playable is resolved/created once (Primary + its Extras share it).
                        foreach (var grp in toAdd.GroupBy(a => a.episodeId))
                        {
                            int pid;
                            var existing = grp.Select(g => g.playableId).FirstOrDefault(p => p != null);
                            if (existing != null) pid = existing.Value;
                            else
                            {
                                var p = new Playable { Kind = PlayableKind.Episode };
                                db.Playables.Add(p); await db.SaveChangesAsync();
                                var ep = await db.Episodes.FirstAsync(e => e.Id == grp.Key);
                                ep.PlayableId = p.Id; await db.SaveChangesAsync();
                                pid = p.Id;
                            }
                            foreach (var a in grp)
                                db.MediaFiles.Add(new MediaFile { PlayableId = pid, Path = a.path, Role = a.role, Label = "match:" + a.strat });
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
        private sealed class EpRow { public int Id; public int Season; public int Ep; public int? PlayableId; public bool HasFile; public string Title; public string NormTitle; }

        // Fold to a comparable alnum key for episode-title matching (strips video extension + bracket/
        // paren tags, ASCII-folds, drops non-alphanumerics). Used on both the IMDb title and the filename.
        private static string NormText(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return "";
            s = Regex.Replace(s, @"(?i)\.(mkv|mp4|avi|m4v|mov|wmv|ts|m2ts|mpg|mpeg|flv|webm|divx|ogm|rmvb)$", "");
            s = TitleNorm.Fold(s);
            s = Regex.Replace(s, @"\[[^\]]*\]", " ");
            s = Regex.Replace(s, @"\([^)]*\)", " ");
            var sb = new StringBuilder();
            foreach (var ch in s.ToLowerInvariant()) if (char.IsLetterOrDigit(ch)) sb.Append(ch);
            return sb.ToString();
        }

        // Match a filename to a gap episode by its title appearing in the name. Prefers the LONGEST
        // matching title (so "The Maquis, Part II" beats a shorter substring); a tie between two distinct
        // episodes of equal length is ambiguous → no match (stay confident).
        private static EpRow MatchByTitle(string fileName, List<EpRow> titledGaps)
        {
            var fn = NormText(fileName);
            if (fn.Length == 0) return null;
            EpRow best = null; int bestLen = 0; bool tie = false;
            foreach (var e in titledGaps)
            {
                if (e.NormTitle.Length < 5 || !fn.Contains(e.NormTitle)) continue;
                if (e.NormTitle.Length > bestLen) { best = e; bestLen = e.NormTitle.Length; tie = false; }
                else if (e.NormTitle.Length == bestLen && best != null && best.Id != e.Id) tie = true;
            }
            return tie ? null : best;
        }

        // OVAs/OVA-numbered specials, movies, openings/endings and other bonus material carry their OWN
        // 1..N numbering that collides with the main episode run — they are NOT normal episodes, so they
        // never get mapped to one. (Project rule.)
        private static bool IsNonEpisode(string name)
        {
            return Regex.IsMatch(name, @"(?i)(?<![a-z])(NCED|NCOP|NCBD|Special|Specials|Picture\s*Drama|Omake|Bonus|Menu|Preview|Trailer|Recap|Extra)(?![a-z])")
                || Regex.IsMatch(name, @"(?i)\b(the\s+movie|gekijou?ban)\b")
                // Creditless/credit openings & endings, promos and commercials ("…_OP02_…", "ED16", "PV03",
                // "CM01"): they carry their OWN 1..N numbering that collides with the main episode run, so —
                // like NCOP/NCED — they're never an episode. Digit-anchored + non-letter bounded to avoid
                // matching real words ("Top10", "shED…").
                || Regex.IsMatch(name, @"(?i)(?<![a-z0-9])(NC)?(OP|ED|PV|CM)\d{1,2}(?![a-z])");
        }

        // A file living under a "Spinoffs"/"Spin-Offs" subfolder belongs to a DIFFERENT series that's filed
        // inside the parent's tree — not an episode of the parent, so it must never be mapped to it.
        private static bool IsSpinoffPath(string rel)
        {
            return Regex.IsMatch(rel ?? "", @"(?i)[\\/]spin[ ._-]?offs?[\\/]");
        }

        // OVA/OAV/ONA: bonus for a normal series (its own 1..N run collides with the main episodes), but
        // for an OVA-ONLY series (Hellsing Ultimate) the OVAs ARE the episodes. So mark, don't exclude —
        // a non-OVA file wins the episode; an OVA maps only when it's the sole claimant (handled below).
        private static bool IsOva(string name) => Regex.IsMatch(name, @"(?i)(?<![a-z])(OVA|OAV|ONA)(?![a-z])");

        // Smallest Roman numeral parser (I..L) for season/episode tokens; 0 if not a clean numeral.
        private static int RomanToInt(string s)
        {
            s = (s ?? "").ToUpperInvariant();
            var map = new Dictionary<char, int> { ['I'] = 1, ['V'] = 5, ['X'] = 10, ['L'] = 50 };
            int total = 0, prev = 0;
            for (int i = s.Length - 1; i >= 0; i--)
            {
                if (!map.TryGetValue(s[i], out var v)) return 0;
                total += v < prev ? -v : v; prev = v;
            }
            return total >= 1 && total <= 50 ? total : 0;
        }

        // A file living under an Extras/Featurettes/Deleted-Scenes/Bonus/Specials/Behind-the-Scenes folder
        // is bonus content for its episode, mapped as MovieFileRole.Extra (just like a movie's extras) so
        // it never collides with the real episode file.
        private static bool IsExtraPath(string rel)
        {
            return Regex.IsMatch(rel ?? "", @"(?i)[\\/](Featurettes?|Deleted[ ._-]?Scenes|Extras?|Bonus(?:[ ._-]?Features?)?|Specials?|Behind[ ._-]the[ ._-]Scenes|Interviews?|Commentary)[\\/]");
        }

        // The same bonus-content signal carried in the FILENAME rather than a parent folder — a "making of",
        // featurette, behind-the-scenes or commentary filed alongside the real episodes (e.g. Adventure Time's
        // "S07E20a.The Making Of Bad Jubies.mkv"). Treated as a Role=Extra so it never blocks the episode it
        // describes (whose title it usually contains and would otherwise collide with).
        private static bool IsExtraName(string name)
        {
            return Regex.IsMatch(name ?? "", @"(?i)\b(making[ ._-]*of|featurettes?|behind[ ._-]the[ ._-]scenes|deleted[ ._-]?scenes?|audio[ ._-]commentary)\b");
        }

        private static (int season, int ep, string strat)? ParseSe(string rel, string name, bool singleSeason)
        {
            var c = Regex.Match(name, @"(?i)S(\d{1,2})\s*E(\d{1,3})\s*[&\-+]?\s*E(\d{1,3})");
            if (c.Success) return (int.Parse(c.Groups[1].Value), int.Parse(c.Groups[2].Value), "combined");
            // Separators between S## and E## include 'x' ("S04xE09") and the usual . _ - space.
            var se = Regex.Match(name, @"(?i)S(\d{1,2})[ ._\-x]*E(\d{1,3})");
            if (se.Success) return (int.Parse(se.Groups[1].Value), int.Parse(se.Groups[2].Value), "se");
            var x = Regex.Match(name, @"(?<![\dpP])(\d{1,2})x(\d{1,3})(?![\d])");
            if (x.Success) return (int.Parse(x.Groups[1].Value), int.Parse(x.Groups[2].Value), "se");
            // Roman-numeral SEASON before an episode number ("…Index II 01" = S2E1; "…III 04" = S3E4).
            var rs = Regex.Match(name, @"(?i)(?<![a-z])(II|III|IV|VIII|VII|VI|V|IX|X)[\s._]+0*(\d{1,3})(?![\d])");
            if (rs.Success) { int rn = RomanToInt(rs.Groups[1].Value); if (rn > 0) return (rn, int.Parse(rs.Groups[2].Value), "roman-season"); }
            // Everything below counts bare numbers, so first strip the noise that injects spurious ones:
            // CRC hashes [70C1405D], release-group tags [Eclipse], and resolutions (1280x720)/720p/x264.
            var clean = CleanForNumbers(name);
            // "Season N" folder anywhere in the relative path + an episode number in the name.
            var sf = Regex.Match(rel, @"(?i)(?:Season|Series|Book|Volume)\s*(\d{1,2})");
            var en = Regex.Match(name, @"(?i)(?<![a-z0-9])E(?:p|pisode)?\s*0*(\d{1,3})(?![\d])");
            if (sf.Success && en.Success) return (int.Parse(sf.Groups[1].Value), int.Parse(en.Groups[1].Value), "folderseason");
            // Season folder + a bare number (no E marker), e.g. "Season 1\05 - Title.mkv", when exactly
            // one plausible number survives cleaning. Gated downstream on (season,ep) being a real gap.
            if (sf.Success)
            {
                var fn = SoloNumber(clean);
                if (fn.HasValue) return (int.Parse(sf.Groups[1].Value), fn.Value, "folderseason-num");
            }
            // E-only marker (no season) → single-season shows are season 1 (anime "…E01…").
            if (singleSeason && en.Success) return (1, int.Parse(en.Groups[1].Value), "ep");
            // Trailing roman-numeral episode for single-season shows ("…The Dawn I/II/III" = E1/E2/E3).
            if (singleSeason)
            {
                var re = Regex.Match(clean.Trim(), @"(?i)(?<![a-z])(I{1,3}|IV|VIII|VII|VI|V|IX|X)\s*$");
                if (re.Success) { int rn = RomanToInt(re.Groups[1].Value); if (rn > 0) return (1, rn, "roman-ep"); }
            }
            // Bare absolute number for single-season shows, only when exactly one plausible number remains
            // after cleaning (recovers anime "[Group] Title - 01 (1280x720) [CRC].mkv").
            if (singleSeason)
            {
                var an = SoloNumber(clean);
                if (an.HasValue) return (1, an.Value, "absolute");
            }
            return null;
        }

        // Strip bracketed group/CRC tags, parenthised resolutions, and quality tokens that would
        // otherwise be counted as episode numbers, plus the file extension.
        private static string CleanForNumbers(string name)
        {
            var n = Regex.Replace(name, @"\[[^\]]*\]", " ");                  // [Eclipse], [70C1405D]
            n = Regex.Replace(n, @"\([^)]*\)", " ");                          // (1280x720 h264), (2009)
            n = Regex.Replace(n, @"(?i)\b\d{3,4}\s*x\s*\d{3,4}\b", " ");      // bare 1280x720
            n = Regex.Replace(n, @"(?i)\b(480p|576p|720p|1080p|2160p|4k|uhd|hdr|10\s*bit|8\s*bit|x264|x265|h\.?264|h\.?265|hevc|aac|ac3|flac|dd5\.?1|dts|bluray|web-?dl|webrip|hdtv|dvdrip|bdrip|repack|remux)\b", " ");
            n = Regex.Replace(n, @"\.[a-z0-9]{2,4}$", "", RegexOptions.IgnoreCase);   // extension
            return n;
        }

        // Exactly one plausible episode number (1..400) in the cleaned text, else null (stay confident).
        private static int? SoloNumber(string clean)
        {
            var nums = Regex.Matches(clean, @"(?<![\d])(\d{1,3})(?![\d])").Select(m => int.Parse(m.Value))
                .Where(n => n >= 1 && n <= 400).ToList();
            return nums.Count == 1 ? nums[0] : (int?)null;
        }

        private static string NormPath(string p) => (p ?? "").Replace('/', '\\').TrimEnd('\\').ToLowerInvariant();

        // seriesId<TAB>path-substring[<TAB>season] per line ('#' comments, blank lines ignored). Multiple
        // lines per series allowed. Substrings match case-insensitively against the normalized full path.
        // The optional 3rd column pins a SEASON for files under that substring (per-part anime folders),
        // mapping them by (season, trailing number) since they carry no episode title.
        private static Dictionary<int, List<(string sub, int? season, bool negate)>> LoadAliases(string path, TextWriter w)
        {
            var map = new Dictionary<int, List<(string, int?, bool)>>();
            var full = Path.GetFullPath(path);
            if (!File.Exists(full)) return map;
            int n = 0;
            foreach (var raw in File.ReadAllLines(full))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#")) continue;
                var parts = line.Split('\t');
                if (parts.Length < 2 || !int.TryParse(parts[0].Trim(), out var sid)) continue;
                var sub = parts[1].Trim().Replace('/', '\\').ToLowerInvariant();
                bool negate = sub.StartsWith("!");           // "!substring" excludes matching files
                if (negate) sub = sub.Substring(1);
                if (sub.Length == 0) continue;
                int? season = (parts.Length >= 3 && int.TryParse(parts[2].Trim(), out var sn)) ? sn : (int?)null;
                if (!map.TryGetValue(sid, out var l)) map[sid] = l = new List<(string, int?, bool)>();
                l.Add((sub, season, negate)); n++;
            }
            w.WriteLine($"Loaded {n} folder alias(es) for {map.Count} series from {full}.");
            return map;
        }

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
