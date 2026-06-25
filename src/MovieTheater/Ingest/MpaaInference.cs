using System;
using System.Collections.Generic;
using System.Linq;

namespace MovieTheater.Ingest
{
    /// <summary>
    /// Derives a ROUGH MPAA-equivalent certificate for a title that never carried a real one, from
    /// signals we already hold in the DB: IMDb genres, the model-inferred TitleInsight Intensity
    /// (0–100), and the discovery <em>tags</em> (Mood / Tone / Subgenre / ContentDescriptor).
    ///
    /// <para><b>How it scores.</b> Rather than a short hard-coded rule list, every meaningful tag
    /// value carries a signed weight in a lexicon and nudges a 0–100 maturity score up or down
    /// ("bleak"/"gritty"/"slasher" push up; "wholesome"/"feel-good"/"romcom" pull down), scaled by
    /// the model's confidence in that tag. Genres shape the baseline. The score then buckets to
    /// G/PG/PG-13/R.</para>
    ///
    /// <para><b>Stance (project decision):</b> permissive by default — an unremarkable title lands
    /// low so kids/teens can see it — but with a hard <em>trauma guard</em>: the strongest content
    /// signals (gore, body-horror, explicit, slasher, the Adult genre) floor the result at R no
    /// matter how low the rest scores, so nothing genuinely disturbing reaches a child account.
    /// Output is never above R; we don't guess NC-17/X.</para>
    ///
    /// <para>The estimate is always stored in <c>MpaaRatingInferred</c> with a provenance string —
    /// never in the real <c>MpaaRating</c> column — so a guess stays distinguishable from a scraped
    /// certificate and the backfill is re-runnable.</para>
    /// </summary>
    public static class MpaaInference
    {
        // Canonical certificate text per bucket; all map back through RatingMap (G→1 … R→4), so the
        // effective-rating resolver re-derives the same bucket the gate compares against.
        public const string G = "G";
        public const string PG = "PG";
        public const string PG13 = "PG-13";
        public const string R = "R";

        // ── genre groups (matched case-insensitively against IMDb Genre.Name) ──
        private static readonly HashSet<string> Family = Set("Family");
        private static readonly HashSet<string> Animation = Set("Animation");
        private static readonly HashSet<string> Mild = Set(
            "Documentary", "News", "Talk-Show", "Game-Show", "Reality-TV",
            "Music", "Musical", "Biography", "History", "Sport");
        private static readonly HashSet<string> Edgy = Set("Thriller", "Crime", "War", "Mystery", "Film-Noir");

        /// <summary>Tag values (any category) that floor the result at R — the trauma guard. Their
        /// mere presence is decisive, regardless of confidence weight.</summary>
        private static readonly HashSet<string> ForceR = Set(
            "gore", "body-horror", "explicit", "slasher", "splatter", "torture-porn", "snuff", "erotic");

        /// <summary>Words/phrases in the free-text Vibe / WhyInteresting prose that floor the result
        /// at R (the trauma guard, prose edition). Matched on word boundaries after normalizing
        /// hyphens to spaces, so "child-abuse" → "child abuse".</summary>
        private static readonly string[] ProseForceR =
        {
            "torture", "child abuse", "rape", "incest", "mutilation", "dismember", "snuff",
            "bestiality", "pedophile", "necrophilia", "graphic sex", "unsimulated",
        };

        /// <summary>Signed maturity weights for words/phrases in the Vibe / WhyInteresting prose.
        /// Positive = more mature. Complements the structured tags with what the model wrote in prose.</summary>
        private static readonly Dictionary<string, int> ProseWeights = new(StringComparer.OrdinalIgnoreCase)
        {
            // up
            ["brutal"] = 14, ["savage"] = 12, ["graphic"] = 12, ["gruesome"] = 14, ["bloody"] = 10,
            ["bloodbath"] = 14, ["massacre"] = 12, ["slaughter"] = 12, ["nightmare"] = 7, ["horrifying"] = 12,
            ["shocker"] = 9, ["shocking"] = 8, ["depraved"] = 16, ["obscene"] = 13, ["filthy"] = 9,
            ["perverse"] = 12, ["sleazy"] = 11, ["exploitation"] = 12, ["nihilistic"] = 9, ["harrowing"] = 12,
            ["grim"] = 5, ["disturbing"] = 13, ["violent"] = 11, ["violence"] = 11, ["sexual"] = 12,
            ["nudity"] = 11, ["addiction"] = 7, ["suicide"] = 9, ["abuse"] = 9, ["trauma"] = 7,
            ["visceral"] = 8, ["murder"] = 4, ["serial killer"] = 10, ["drug"] = 7, ["war crime"] = 9,
            // down
            ["charming"] = -10, ["whimsical"] = -10, ["wholesome"] = -14, ["heartwarming"] = -12,
            ["family friendly"] = -16, ["gentle"] = -8, ["sweet"] = -7, ["cozy"] = -12, ["delightful"] = -8,
            ["feel good"] = -12, ["playful"] = -8, ["cheerful"] = -8, ["lighthearted"] = -10,
            ["uplifting"] = -10, ["innocent"] = -8, ["fairy tale"] = -6, ["kid friendly"] = -14,
            ["for all ages"] = -14,
        };

        /// <summary>
        /// Signed maturity weights per tag value (applied to whatever category it appears in; a value
        /// seen in two categories counts once, at its strongest weight). Positive = more mature.
        /// Grounded in the live Mood/Tone/Subgenre/ContentDescriptor vocabularies.
        /// </summary>
        private static readonly Dictionary<string, int> TagWeights = new(StringComparer.OrdinalIgnoreCase)
        {
            // ── content descriptors ──
            ["violence"] = 16, ["disturbing"] = 18, ["raunchy"] = 14, ["gross-out"] = 6, ["jump-scares"] = 7,
            ["nudity"] = 16, ["sexual"] = 16, ["drug-use"] = 12,
            ["feel-good"] = -14,
            // ── mood ──
            ["bleak"] = 13, ["gritty"] = 12, ["dread"] = 14, ["unsettling"] = 13, ["tense"] = 8,
            ["melancholic"] = 4, ["operatic"] = 2,
            ["playful"] = -12, ["wholesome"] = -16, ["uplifting"] = -12, ["whimsical"] = -10,
            ["cozy"] = -14, ["heartwarming"] = -14, ["intimate"] = -2, ["dreamlike"] = -2,
            // ── tone ──
            ["self-serious"] = 3,
            // ── subgenre ──
            ["horror"] = 16, ["gothic horror"] = 16, ["horror comedy"] = 6, ["psychological thriller"] = 13,
            ["survival thriller"] = 13, ["creature feature"] = 9, ["kaiju"] = 4, ["vampire"] = 9,
            ["supernatural"] = 6, ["dark fantasy"] = 11, ["dark comedy"] = 7, ["crime saga"] = 12,
            ["noir"] = 7, ["neo-noir"] = 9, ["action thriller"] = 9, ["political thriller"] = 7,
            ["sci-fi thriller"] = 8, ["spy"] = 5, ["disaster"] = 6, ["spaghetti western"] = 6,
            ["cyberpunk"] = 3, ["mystery"] = 3, ["heist"] = 4, ["satire"] = 2, ["b movie"] = 2,
            ["romcom"] = -12, ["romance"] = -4, ["fantasy comedy"] = -9, ["musical"] = -8,
            ["comedy"] = -7, ["buddy cop"] = -4, ["mockumentary"] = -3, ["fantasy adventure"] = -4,
            ["family adventure"] = -12,
        };

        // Score thresholds → bucket. Tuned to the catalog calibration (real G≈22, PG≈24, PG-13≈33,
        // R≈41 avg Intensity) shifted permissive: only a clearly elevated score reaches R.
        private const int GMax = 18;
        private const int PgMax = 33;
        private const int Pg13Max = 48;
        private const int ForceRFloor = 55;

        /// <summary>A discovery tag plus the model's confidence weight in it (0–100).</summary>
        public readonly struct WeightedTag
        {
            public WeightedTag(string value, int weight) { Value = value; Weight = weight; }
            public string Value { get; }
            public int Weight { get; }
        }

        /// <summary>
        /// Infer a rough MPAA certificate. Returns the certificate text (G/PG/PG-13/R) and a
        /// provenance string describing what drove it.
        /// </summary>
        public static (string rating, string source) Infer(
            IEnumerable<string>? genres, int? intensity, IEnumerable<WeightedTag>? tags, string? prose = null)
        {
            var g = (genres ?? Enumerable.Empty<string>())
                .Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim())
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            // Dedupe tag values across categories, keeping the strongest confidence weight per value.
            var tagWeight = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var t in tags ?? Enumerable.Empty<WeightedTag>())
            {
                if (string.IsNullOrWhiteSpace(t.Value)) continue;
                var key = t.Value.Trim();
                var weight = Math.Clamp(t.Weight, 0, 100);
                if (!tagWeight.TryGetValue(key, out var prev) || weight > prev) tagWeight[key] = weight;
            }

            // Normalize the prose to " word word " so single words and phrases both match on boundaries.
            string proseNorm = NormalizeProse(prose);
            bool hasProse = proseNorm.Length > 2;

            // Zero-signal titles (no genre, no Intensity, no tags, no prose) get a middle, teen-safe
            // default rather than a permissive G we can't justify — and are flagged for a human.
            if (g.Count == 0 && intensity is null && tagWeight.Count == 0 && !hasProse)
                return (PG13, "default:no-signal");

            bool forced = tagWeight.Keys.Any(k => ForceR.Contains(k))
                       || g.Contains("Adult")
                       || (hasProse && ProseForceR.Any(p => Mentions(proseNorm, p)));

            double score = intensity ?? 25;   // permissive mild baseline when no Intensity available

            // ── genre shaping ──
            if (g.Contains("Horror")) score = Math.Max(score, 40);
            if (g.Overlaps(Edgy)) score += 5;
            if (g.Overlaps(Family)) score -= 18;
            if (g.Overlaps(Animation)) score -= 8;
            if (g.Overlaps(Mild)) score -= 6;

            // ── weighted tag lexicon (confidence-scaled, floored at 0.6 so weak tags still count) ──
            foreach (var (value, weight) in tagWeight)
            {
                if (!TagWeights.TryGetValue(value, out var delta)) continue;
                double scale = Math.Max(0.6, weight / 100.0);
                score += delta * scale;
            }

            // ── free-text prose scan (capped so a keyword-dense blurb can't dominate) ──
            double proseDelta = 0;
            if (hasProse)
                foreach (var (word, delta) in ProseWeights)
                    if (Mentions(proseNorm, word)) proseDelta += delta;
            score += Math.Clamp(proseDelta, -22, 32);

            if (forced) score = Math.Max(score, ForceRFloor);
            score = Math.Clamp(score, 0, 100);

            string rating =
                score < GMax ? G :
                score < PgMax ? PG :
                score < Pg13Max ? PG13 : R;

            // ── provenance ──
            var parts = new List<string>();
            if (forced) parts.Add("trauma-guard");
            if (intensity.HasValue) parts.Add("intensity");
            if (tagWeight.Count > 0) parts.Add("tags");
            if (hasProse && proseDelta != 0) parts.Add("vibe");
            if (g.Count > 0) parts.Add("genre");
            bool ai = intensity.HasValue || tagWeight.Count > 0 || (hasProse && proseDelta != 0);
            string src = (ai ? "ai:" : "genre:") + string.Join("+", parts);

            return (rating, src);
        }

        // Lowercase, fold every non-letter (hyphens, punctuation) to a space, pad with spaces — so a
        // keyword match is a whole-token / phrase match, not an accidental substring.
        private static string NormalizeProse(string? s)
        {
            if (string.IsNullOrWhiteSpace(s)) return "";
            var chars = s.ToLowerInvariant().Select(c => char.IsLetter(c) ? c : ' ').ToArray();
            return " " + string.Join(" ", new string(chars).Split(' ', StringSplitOptions.RemoveEmptyEntries)) + " ";
        }

        private static bool Mentions(string normPadded, string keyword) =>
            normPadded.Contains(" " + keyword + " ");

        private static HashSet<string> Set(params string[] names) => new(names, StringComparer.OrdinalIgnoreCase);
    }
}
