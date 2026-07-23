using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace MovieTheater.Services.LaunchBox
{
    /// <summary>One LaunchBox game as the name-correction pass sees it: its canonical display
    /// <see cref="Name"/>, the <see cref="LaunchBoxMetadata.NormalizeTitle"/> comparison <see cref="Key"/>,
    /// its loose title <see cref="Tokens"/> (for fuzzy overlap) and whether it actually carries a community
    /// rating (a rename onto an unrated game fixes matchability + box art but yields no stars).</summary>
    public sealed class LbGame
    {
        public string Name { get; init; } = "";
        public string Key { get; init; } = "";
        public HashSet<string> Tokens { get; init; } = new(StringComparer.Ordinal);
        public int Votes { get; init; }
        public bool Rated { get; init; }
    }

    /// <summary>The single best LaunchBox candidate for a card title that did NOT match exactly, with the
    /// metrics the caller gates on. <see cref="Second"/>/<see cref="SecondScore"/> back the "clear winner"
    /// rule — a rename only fires when the best beats the runner-up by a margin.</summary>
    public readonly record struct FuzzyHit(LbGame Best, double Score, double CharSim, double F1,
                                           LbGame? Second, double SecondScore);

    /// <summary>
    /// A name-retaining view of the LaunchBox dump for the <c>arcade-launchbox-rename</c> pass. Unlike
    /// <see cref="LaunchBoxMetadata.BuildIndex"/> — which keeps only <c>(system,key) → rating</c> and so
    /// can tell you a card matched but never what to rename it TO — this keeps every game's canonical
    /// <see cref="LbGame.Name"/>, and unlike the rating index it is NOT gated on votes (a name is a name,
    /// even for an unrated game). <see cref="Exact"/> answers "is this normalized key a real LaunchBox
    /// title (primary or safe alias)?"; <see cref="BestFuzzy"/> finds the closest primary name for a
    /// misspelled / mangled title, with a cheap token pre-filter so the O(n²) edit distance only runs on
    /// the handful of candidates that share tokens.
    /// </summary>
    public sealed class LaunchBoxNameIndex
    {
        /// <summary>(system, normalized key) → canonical game. Holds primary names plus the safe subset of
        /// <c>&lt;GameAlternateName&gt;</c> aliases (a primary always beats an alias; an alias claimed by
        /// two games is dropped) — same rules as the rating index, so a romaji alias key still resolves.</summary>
        public Dictionary<(string System, string Key), LbGame> Exact { get; } = new();

        /// <summary>system → primary games only, the pool <see cref="BestFuzzy"/> scans. Aliases are
        /// deliberately excluded here: they strengthen exact lookup but would add noise to fuzzy scoring.</summary>
        public Dictionary<string, List<LbGame>> BySystem { get; } = new();

        public LbGame? ExactLookup(string system, string key)
            => Exact.TryGetValue((system, key), out var g) ? g : null;

        /// <summary>Closest primary name in <paramref name="system"/> to the query, or null when nothing
        /// shares even one meaningful token. The caller decides acceptance from the returned metrics; this
        /// only ranks. Score = ½·token-F1 + ½·character-similarity, so a match must agree on BOTH the set of
        /// words AND their spelling — a pure token coincidence with a wildly different string, or vice-versa,
        /// scores low. Edit distance is computed ONLY for candidates whose token-F1 clears a cheap floor.</summary>
        public FuzzyHit? BestFuzzy(string system, HashSet<string> queryTokens, string queryKey)
        {
            if (queryTokens.Count == 0 || !BySystem.TryGetValue(system, out var pool)) return null;

            // A differing sequel/volume number means a DIFFERENT game — "Sakura Taisen 2" is not "Sakura
            // Taisen", "Street Fighter Alpha" is not "Alpha 2". Token/char similarity alone can't see that
            // (one added digit barely moves either), so require the numeric tokens to match exactly. Roman
            // numerals are already folded to digits in Tokenize, so "Cosmic Fantasy II" ⇄ "…2" still passes.
            var qNums = queryTokens.Where(t => t.All(char.IsDigit)).ToHashSet(StringComparer.Ordinal);
            // A trial/promo marker (Taikenban = trial, Hibaihin = not-for-sale, demo/sample) means a distinct
            // release from the retail game — don't let "… Taikenban" silently fold into the full title.
            var qMarks = queryTokens.Where(DemoMarkers.Contains).ToHashSet(StringComparer.Ordinal);

            LbGame? best = null, second = null;
            double bestScore = -1, secondScore = -1, bestSim = 0, bestF1 = 0;

            foreach (var c in pool)
            {
                if (c.Key == queryKey) continue; // identical key can't be a *correction*
                var cNums = c.Tokens.Where(t => t.All(char.IsDigit));
                if (!qNums.SetEquals(cNums)) continue; // sequel/volume number must agree
                if (!qMarks.SetEquals(c.Tokens.Where(DemoMarkers.Contains))) continue; // trial/promo must agree
                int inter = 0;
                foreach (var t in queryTokens) if (c.Tokens.Contains(t)) inter++;
                if (inter == 0) continue;

                double prec = (double)inter / queryTokens.Count;
                double rec = (double)inter / c.Tokens.Count;
                double f1 = prec + rec == 0 ? 0 : 2 * prec * rec / (prec + rec);
                if (f1 < 0.4) continue; // pre-filter: skip edit distance for weak token overlap

                double sim = 1.0 - (double)Levenshtein(queryKey, c.Key) / Math.Max(1, Math.Max(queryKey.Length, c.Key.Length));
                double score = 0.5 * f1 + 0.5 * sim;

                if (score > bestScore)
                {
                    second = best; secondScore = bestScore;
                    best = c; bestScore = score; bestSim = sim; bestF1 = f1;
                }
                else if (score > secondScore)
                {
                    second = c; secondScore = score;
                }
            }

            return best == null ? null
                : new FuzzyHit(best, bestScore, bestSim, bestF1, second, secondScore < 0 ? 0 : secondScore);
        }

        // ── shared helpers ────────────────────────────────────────────────────────────────────────────

        private static readonly HashSet<string> Articles = new(StringComparer.Ordinal) { "the", "a", "an" };
        // Trial/promo/demo markers (English + romaji) that distinguish a release from the retail game.
        private static readonly HashSet<string> DemoMarkers = new(StringComparer.Ordinal)
        {
            "demo", "trial", "sample", "preview", "taikenban", "taikenhan", "hibaihin", "otameshi",
        };
        private static readonly Dictionary<string, string> Roman = new(StringComparer.Ordinal)
        {
            ["ii"] = "2", ["iii"] = "3", ["iv"] = "4", ["vi"] = "6", ["vii"] = "7", ["viii"] = "8", ["ix"] = "9",
            ["xi"] = "11", ["xii"] = "12", ["xiii"] = "13", ["xiv"] = "14", ["xv"] = "15", ["xvi"] = "16",
        };

        /// <summary>Loose title tokens: lowercase alphanumerics split on punctuation, "&amp;"→"and", articles
        /// dropped, multi-char roman numerals folded to arabic — the token counterpart of
        /// <see cref="LaunchBoxMetadata.NormalizeTitle"/>, so "Streets of Rage II" and "Streets of Rage 2"
        /// share tokens. Tags in ()/[] are already gone from LaunchBox names; card titles are stripped upstream.</summary>
        public static HashSet<string> Tokenize(string? s)
        {
            var set = new HashSet<string>(StringComparer.Ordinal);
            if (string.IsNullOrEmpty(s)) return set;
            var sb = new StringBuilder();
            void Flush()
            {
                if (sb.Length == 0) return;
                var t = sb.ToString(); sb.Clear();
                if (Roman.TryGetValue(t, out var r)) t = r;
                if (!Articles.Contains(t)) set.Add(t);
            }
            foreach (var ch in s.ToLowerInvariant().Replace("&", " and "))
            {
                if (char.IsLetterOrDigit(ch)) sb.Append(ch);
                else Flush();
            }
            Flush();
            return set;
        }

        /// <summary>Classic iterative-DP Levenshtein on two strings (two-row variant).</summary>
        public static int Levenshtein(string a, string b)
        {
            if (a == b) return 0;
            if (a.Length == 0) return b.Length;
            if (b.Length == 0) return a.Length;

            var prev = new int[b.Length + 1];
            var cur = new int[b.Length + 1];
            for (int j = 0; j <= b.Length; j++) prev[j] = j;
            for (int i = 1; i <= a.Length; i++)
            {
                cur[0] = i;
                for (int j = 1; j <= b.Length; j++)
                {
                    int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                    cur[j] = Math.Min(Math.Min(prev[j] + 1, cur[j - 1] + 1), prev[j - 1] + cost);
                }
                (prev, cur) = (cur, prev);
            }
            return prev[b.Length];
        }
    }
}
