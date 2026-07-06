using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace MovieTheater.Arcade
{
    /// <summary>
    /// A per-system index of the real libretro-thumbnails <c>Named_Boxarts</c> filenames, so a game whose
    /// ROM name has drifted from the community DAT (word order, region/language tags, TOSEC vs Redump) can
    /// still be matched by its TITLE instead of an exact string compare.
    ///
    /// <para>The filename list is fetched once per system from GitHub (<see cref="RefreshAsync"/>) and cached
    /// to <c>{postersRoot}/arcade/_index/{system}.txt</c>. <see cref="Load"/> reads that cache and builds two
    /// lookups: by normalized title (tags stripped, alnum-only) and by a sorted token signature (catches
    /// word-order swaps like "007 - GoldenEye" ⇄ "GoldenEye 007"). Region tags on the winning entries pick
    /// the preferred regional box. No network at match time — the controller stays cheap.</para>
    /// </summary>
    public sealed class ArcadeBoxArtIndex
    {
        // normalized-title -> libretro filenames (without .png); token-signature -> filenames.
        private readonly Dictionary<string, List<string>> byNorm = new(StringComparer.Ordinal);
        private readonly Dictionary<string, List<string>> byToken = new(StringComparer.Ordinal);
        // Every entry with its token set (fuzzy scoring) and ordered loose tokens (contiguous-subsequence
        // alias proposals). Loose = '&'→"and" so "Track & Field" ⇄ "Track and Field".
        private readonly List<(string File, HashSet<string> Tokens, List<string> Ordered)> entries = new();

        public int Count { get; private set; }

        public static string IndexDir(string postersRoot) => Path.Combine(postersRoot, "arcade", "_index");
        public static string IndexPath(string postersRoot, string system) => Path.Combine(IndexDir(postersRoot), system + ".txt");

        // Memoized load for the hot path (the /ArcadeImage route, on a cold card): parse the file once and
        // reuse it, reloading only if the file's timestamp changes (a fresh index refresh).
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (long Stamp, ArcadeBoxArtIndex? Index)> Cache = new();

        public static ArcadeBoxArtIndex? LoadCached(string postersRoot, string system)
        {
            var path = IndexPath(postersRoot, system);
            long stamp = File.Exists(path) ? File.GetLastWriteTimeUtc(path).Ticks : -1;
            if (Cache.TryGetValue(path, out var hit) && hit.Stamp == stamp) return hit.Index;
            var idx = stamp < 0 ? null : Load(postersRoot, system);
            Cache[path] = (stamp, idx);
            return idx;
        }

        // Guards for the on-demand index build: one build per system at a time, and a cooldown after a
        // failure so a GitHub outage doesn't get hammered on every card view.
        private static readonly ConcurrentDictionary<string, SemaphoreSlim> BuildLocks = new();
        private static readonly ConcurrentDictionary<string, DateTime> LastFailUtc = new();

        /// <summary>Return the system's index, building + caching it to the mount on first miss (so prod
        /// self-heals the drifted systems with no manual command). One build per system; a recent failure
        /// backs off. Returns null if the system has no repo, the build fails, or the mount is read-only —
        /// the matcher then falls back to exact-name/alias candidates.</summary>
        public static async Task<ArcadeBoxArtIndex?> EnsureBuiltAsync(HttpClient http, string postersRoot, string system)
        {
            var existing = LoadCached(postersRoot, system);
            if (existing != null || !ArcadeBoxArt.HasRepo(system)) return existing;
            if (LastFailUtc.TryGetValue(system, out var t) && DateTime.UtcNow - t < TimeSpan.FromMinutes(15)) return null;

            var gate = BuildLocks.GetOrAdd(system, _ => new SemaphoreSlim(1, 1));
            await gate.WaitAsync();
            try
            {
                var again = LoadCached(postersRoot, system); // another request may have just built it
                if (again != null) return again;
                var n = await RefreshAsync(http, postersRoot, system);
                if (n > 0) return LoadCached(postersRoot, system);
                LastFailUtc[system] = DateTime.UtcNow;
                return null;
            }
            catch { LastFailUtc[system] = DateTime.UtcNow; return null; }
            finally { gate.Release(); }
        }

        /// <summary>Load a system's cached filename index; null if it hasn't been fetched yet.</summary>
        public static ArcadeBoxArtIndex? Load(string postersRoot, string system)
        {
            var path = IndexPath(postersRoot, system);
            if (!File.Exists(path)) return null;
            var idx = new ArcadeBoxArtIndex();
            foreach (var raw in File.ReadLines(path))
            {
                var name = raw.Trim();
                if (name.Length == 0) continue;
                idx.Add(name);
            }
            return idx.Count > 0 ? idx : null;
        }

        private void Add(string filename)
        {
            var norm = Normalize(filename);
            if (norm.Length == 0) return;
            Append(byNorm, norm, filename);
            Append(byToken, TokenSignature(filename), filename);
            var ordered = LooseTokens(filename);
            entries.Add((filename, new HashSet<string>(ordered, StringComparer.Ordinal), ordered));
            Count++;
        }

        /// <summary>A high-confidence alias proposal: the libretro filename whose title contains the card's
        /// FULL title as a contiguous run of tokens (so "Wave Race 64" ⊂ "Wave Race 64 - Kawasaki Jet Ski",
        /// "Donald Duck - Goin' Quackers" ⊂ "Disney's Donald Duck - Goin' Quackers") — but NOT a reordered or
        /// scattered match ("Super Return of the Jedi" ✗ "Super Star Wars - Return of the Jedi"). Requires ≥2
        /// tokens so single common words don't false-match. Prefers the closest (fewest added words) + region.
        /// Curation aid only.</summary>
        public string? ContiguousProposal(string title, IEnumerable<string?>? regionHints)
        {
            var q = LooseTokens(title);
            if (q.Count < 2) return null;
            var hints = (regionHints ?? Enumerable.Empty<string?>())
                .Where(r => !string.IsNullOrWhiteSpace(r)).Select(r => r!.ToLowerInvariant()).ToList();

            return entries
                .Where(e => ContainsRun(e.Ordered, q))
                .OrderBy(e => e.Ordered.Count).ThenBy(e => RegionScore(e.File, hints)).ThenBy(e => e.File.Length)
                .Select(e => e.File).FirstOrDefault();
        }

        private static bool ContainsRun(List<string> hay, List<string> needle)
        {
            if (needle.Count == 0 || needle.Count > hay.Count) return false;
            for (int i = 0; i + needle.Count <= hay.Count; i++)
            {
                bool ok = true;
                for (int j = 0; j < needle.Count; j++)
                    if (!string.Equals(hay[i + j], needle[j], StringComparison.Ordinal)) { ok = false; break; }
                if (ok) return true;
            }
            return false;
        }

        private static List<string> LooseTokens(string s)
        {
            var tokens = new List<string>();
            var sb = new System.Text.StringBuilder();
            foreach (var ch in StripTags(s).Replace("&", " and "))
            {
                if (char.IsLetterOrDigit(ch)) sb.Append(char.ToLowerInvariant(ch));
                else if (sb.Length > 0) { tokens.Add(sb.ToString()); sb.Clear(); }
            }
            if (sb.Length > 0) tokens.Add(sb.ToString());
            return tokens;
        }

        /// <summary>Best-guess libretro filenames for a title that DIDN'T match exactly — ranked by how much
        /// of the title's tokens the entry covers (catches "Wave Race 64" → "Wave Race 64 - Kawasaki Jet
        /// Ski", "Quake 64" → "Quake", "Elmo's Letter Adventure" → "Sesame Street - Elmo's Letter
        /// Adventure"). For the alias-report + curation only; the live matcher never guesses fuzzily.</summary>
        public List<(string File, double Coverage, int Extra)> Fuzzy(string title, IEnumerable<string?>? regionHints, int take)
        {
            var q = TokenSet(title);
            if (q.Count == 0) return new();
            var hints = (regionHints ?? Enumerable.Empty<string?>())
                .Where(r => !string.IsNullOrWhiteSpace(r)).Select(r => r!.ToLowerInvariant()).ToList();

            return entries
                .Select(e =>
                {
                    int inter = q.Count(t => e.Tokens.Contains(t));
                    double coverage = (double)inter / q.Count;              // how much of OUR title is present
                    int extra = e.Tokens.Count - inter;                     // words the entry adds
                    return (e.File, Coverage: coverage, Extra: extra, Inter: inter, Region: RegionScore(e.File, hints));
                })
                .Where(x => x.Inter > 0 && x.Coverage >= 0.5)
                .OrderByDescending(x => x.Coverage).ThenBy(x => x.Extra).ThenBy(x => x.Region).ThenBy(x => x.File.Length)
                .Take(take)
                .Select(x => (x.File, x.Coverage, x.Extra))
                .ToList();
        }

        private static HashSet<string> TokenSet(string s)
        {
            var set = new HashSet<string>(StringComparer.Ordinal);
            var sb = new System.Text.StringBuilder();
            foreach (var ch in StripTags(s))
            {
                if (char.IsLetterOrDigit(ch)) sb.Append(char.ToLowerInvariant(ch));
                else if (sb.Length > 0) { set.Add(sb.ToString()); sb.Clear(); }
            }
            if (sb.Length > 0) set.Add(sb.ToString());
            return set;
        }

        private static void Append(Dictionary<string, List<string>> map, string key, string val)
        {
            if (key.Length == 0) return;
            if (!map.TryGetValue(key, out var list)) map[key] = list = new List<string>();
            list.Add(val);
        }

        /// <summary>The best libretro filename (without .png) for a card's title, or null. Tries a
        /// tag-stripped exact-title match, then a word-order-agnostic token match; among ties, prefers the
        /// region the card actually has, then USA/World/Europe.</summary>
        public string? Match(string title, IEnumerable<string?>? regionHints)
        {
            var hints = (regionHints ?? Enumerable.Empty<string?>())
                .Where(r => !string.IsNullOrWhiteSpace(r)).Select(r => r!.ToLowerInvariant()).ToList();

            var norm = Normalize(title);
            if (norm.Length > 0 && byNorm.TryGetValue(norm, out var exact))
                return PickByRegion(exact, hints);

            var sig = TokenSignature(title);
            if (sig.Length > 0 && byToken.TryGetValue(sig, out var tok))
                return PickByRegion(tok, hints);

            return null;
        }

        private static string PickByRegion(List<string> files, List<string> hints)
            => files.OrderBy(f => RegionScore(f, hints)).ThenBy(f => f.Length).First();

        // Lower is better. A file whose region tag matches one of the card's own regions wins; otherwise
        // fall back to the usual English-first order.
        private static int RegionScore(string filename, List<string> hints)
        {
            var region = FirstParen(filename).ToLowerInvariant();
            if (hints.Count > 0 && hints.Any(h => region.Contains(h))) return 0;
            if (region.Contains("usa") || region.Contains("world")) return 1;
            if (region.Contains("europe")) return 2;
            if (region.Length == 0) return 3;
            if (region.Contains("japan")) return 5;
            return 4;
        }

        private static string FirstParen(string s)
        {
            int o = s.IndexOf('(');
            if (o < 0) return "";
            int c = s.IndexOf(')', o + 1);
            return c > o ? s.Substring(o + 1, c - o - 1) : "";
        }

        /// <summary>Title with every "(...)"/"[...]" tag removed and reduced to lowercase alphanumerics —
        /// so "Banjo-Kazooie (USA) (Rev A)" and "Banjo-Kazooie" both become "banjokazooie".</summary>
        public static string Normalize(string s)
        {
            var sb = new StringBuilder(s.Length);
            foreach (var ch in StripTags(s))
                if (char.IsLetterOrDigit(ch)) sb.Append(char.ToLowerInvariant(ch));
            return sb.ToString();
        }

        /// <summary>Sorted, distinct lowercase tokens — so "007 - GoldenEye" and "GoldenEye 007" produce the
        /// same signature ("007 goldeneye") and match despite the word-order difference.</summary>
        public static string TokenSignature(string s)
        {
            var tokens = new List<string>();
            var sb = new StringBuilder();
            foreach (var ch in StripTags(s))
            {
                if (char.IsLetterOrDigit(ch)) sb.Append(char.ToLowerInvariant(ch));
                else if (sb.Length > 0) { tokens.Add(sb.ToString()); sb.Clear(); }
            }
            if (sb.Length > 0) tokens.Add(sb.ToString());
            return string.Join(' ', tokens.Distinct().OrderBy(t => t, StringComparer.Ordinal));
        }

        private static string StripTags(string s)
        {
            var sb = new StringBuilder(s.Length);
            int depth = 0;
            foreach (var ch in s)
            {
                if (ch == '(' || ch == '[') depth++;
                else if (ch == ')' || ch == ']') { if (depth > 0) depth--; }
                else if (depth == 0) sb.Append(ch);
            }
            return sb.ToString();
        }

        /// <summary>Fetch a system's full <c>Named_Boxarts</c> filename list from GitHub and cache it to disk.
        /// One API call per system (git tree, recursive). Returns the number of filenames written, or -1 if
        /// the system has no repo. Honors a GITHUB_TOKEN env var to lift the unauthenticated rate limit.</summary>
        public static async Task<int> RefreshAsync(HttpClient http, string postersRoot, string system)
        {
            if (!ArcadeBoxArt.ThumbRepo.TryGetValue(system, out var repo)) return -1;
            var repoSlug = repo.Replace(' ', '_');
            var url = $"https://api.github.com/repos/libretro-thumbnails/{repoSlug}/git/trees/master?recursive=1";

            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.UserAgent.ParseAdd("MovieTheater-arcade-boxart/1.0");
            req.Headers.Accept.ParseAdd("application/vnd.github+json");
            var token = Environment.GetEnvironmentVariable("GITHUB_TOKEN");
            if (!string.IsNullOrWhiteSpace(token)) req.Headers.Authorization = new("Bearer", token);

            using var resp = await http.SendAsync(req);
            resp.EnsureSuccessStatusCode();
            await using var stream = await resp.Content.ReadAsStreamAsync();
            using var doc = await JsonDocument.ParseAsync(stream);

            var names = new List<string>();
            const string prefix = "Named_Boxarts/";
            if (doc.RootElement.TryGetProperty("tree", out var tree))
            {
                foreach (var node in tree.EnumerateArray())
                {
                    var path = node.TryGetProperty("path", out var p) ? p.GetString() : null;
                    if (path == null || !path.StartsWith(prefix, StringComparison.Ordinal)) continue;
                    if (!path.EndsWith(".png", StringComparison.OrdinalIgnoreCase)) continue;
                    names.Add(path.Substring(prefix.Length, path.Length - prefix.Length - 4)); // drop folder + ".png"
                }
            }

            var dir = IndexDir(postersRoot);
            Directory.CreateDirectory(dir);
            await File.WriteAllLinesAsync(IndexPath(postersRoot, system), names);
            return names.Count;
        }
    }
}
