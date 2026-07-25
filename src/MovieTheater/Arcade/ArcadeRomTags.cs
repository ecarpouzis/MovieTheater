using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace MovieTheater.Arcade
{
    /// <summary>
    /// Parses the No-Intro / GoodTools tags embedded in a ROM's filename — which we keep verbatim as
    /// <see cref="MovieTheater.Db.ArcadeGame.CloudRetroGameKey"/> — into a normalized <b>Region</b> and
    /// <b>Variant</b> for the arcade lobby filters.
    ///
    /// <para><b>Region</b>: where it released, bucketed to a small filterable set — USA, Europe, Japan,
    /// World, Asia, Other, or Unknown (no region tag). Multi-region names resolve by priority
    /// (World &gt; USA &gt; Europe &gt; Japan), so "(USA, Europe)" → USA.</para>
    ///
    /// <para><b>Variant</b>: whether it's an official <see cref="Release"/> or an unofficial / modified
    /// dump — Hack, Beta, Proto, Demo, Unlicensed, Pirate, BadDump. Official niceties (Rev N, Nintendo
    /// Power "(NP)", language lists like "(En,Fr,De)", year tags) stay <see cref="Release"/>. A game is
    /// "modded" (the lobby's mod filter) exactly when Variant is not Release.</para>
    /// </summary>
    public static class ArcadeRomTags
    {
        public const string Release = "Release";
        public const string Unknown = "Unknown";

        /// <param name="badDumpTag">Whether a GoodTools <c>[b]</c>/<c>[o]</c> bracket marks the dump as
        /// <c>BadDump</c>. Defaults to true (the GoodTools meaning). Set false for a collection whose
        /// <c>[b]</c> is unreliable — the L: Advanscene NDS set carries it on 1,047 of 6,600 files,
        /// including sole-US releases of major games, and sampled <c>[b]</c> ROMs are byte-identical to
        /// the dumps already running on the arcade. Mistagging them BadDump would drop them out of the
        /// "official releases only" filter and demote them in <c>ArcadeVersions.Rank</c>, so a card whose
        /// only US dump is <c>[b]</c> would open on a foreign-language version instead.</param>
        public static (string Region, string Variant) Parse(string? cloudRetroGameKey, bool badDumpTag = true)
        {
            var tags = ExtractTags(cloudRetroGameKey);
            return (RegionOf(tags), VariantOf(tags, cloudRetroGameKey, badDumpTag));
        }

        // Pull the comma-split contents of every (...) and [...] group, e.g.
        // "Zelda (USA, Europe) (Rev 1) [b]" → ["USA","Europe","Rev 1","b"].
        private static List<string> ExtractTags(string? key)
        {
            var list = new List<string>();
            if (string.IsNullOrEmpty(key)) return list;
            foreach (Match m in Regex.Matches(key, @"[\(\[]([^\)\]]*)[\)\]]"))
                foreach (var part in m.Groups[1].Value.Split(','))
                {
                    var t = part.Trim();
                    if (t.Length > 0) list.Add(t);
                }
            return list;
        }

        // Region tokens → normalized bucket. PAL European countries fold into Europe; East-Asian into Asia.
        private static readonly Dictionary<string, string> RegionMap = new(StringComparer.OrdinalIgnoreCase)
        {
            ["USA"] = "USA", ["US"] = "USA", ["Canada"] = "USA",
            ["Europe"] = "Europe", ["EU"] = "Europe", ["UK"] = "Europe", ["England"] = "Europe",
            ["Germany"] = "Europe", ["France"] = "Europe", ["Spain"] = "Europe", ["Italy"] = "Europe",
            ["Netherlands"] = "Europe", ["Sweden"] = "Europe", ["Finland"] = "Europe", ["Denmark"] = "Europe",
            ["Norway"] = "Europe", ["Poland"] = "Europe", ["Russia"] = "Europe", ["Greece"] = "Europe",
            ["Portugal"] = "Europe", ["Belgium"] = "Europe", ["Austria"] = "Europe", ["Ireland"] = "Europe",
            ["Australia"] = "Europe", ["New Zealand"] = "Europe",
            ["Japan"] = "Japan", ["JP"] = "Japan",
            ["World"] = "World",
            ["Korea"] = "Asia", ["China"] = "Asia", ["Taiwan"] = "Asia", ["Hong Kong"] = "Asia", ["Asia"] = "Asia",
            // Advanscene two-letter codes (the L: NDS set): KS = Korea, AU = Australia. The other
            // two-letter codes it uses (DE/FR/IT/ES/NL/NO/DK) are LANGUAGE SKUs of a European release,
            // not separate regions — deliberately left unmapped so they stay Unknown rather than
            // masquerading as the English (EU) dump. They still fold into the parent card via
            // CollapseKey, appearing as version-dropdown entries.
            ["KS"] = "Asia", ["AU"] = "Europe",
            ["Brazil"] = "Other", ["Mexico"] = "Other",
        };

        private static readonly string[] RegionPriority = { "World", "USA", "Europe", "Japan", "Asia", "Other" };

        private static string RegionOf(List<string> tags)
        {
            var hits = tags.Where(t => RegionMap.ContainsKey(t)).Select(t => RegionMap[t]).ToHashSet();
            if (hits.Count == 0) return Unknown;
            foreach (var r in RegionPriority)
                if (hits.Contains(r)) return r;
            return Unknown;
        }

        // Variant keywords (a tag equal to, or starting with, one of these). Order = display priority.
        private static readonly (string Key, string Variant)[] VariantMarkers =
        {
            ("Hack", "Hack"), ("Pirate", "Pirate"),
            ("Prototype", "Proto"), ("Proto", "Proto"),
            ("Beta", "Beta"),
            ("Demo", "Demo"), ("Sample", "Demo"), ("Kiosk", "Demo"),
            ("Unl", "Unlicensed"), ("Unlicensed", "Unlicensed"), ("Aftermarket", "Unlicensed"),
            ("Homebrew", "Unlicensed"), ("Program", "Unlicensed"),
        };

        private static string VariantOf(List<string> tags, string? key, bool badDumpTag = true)
        {
            foreach (var (mk, variant) in VariantMarkers)
                if (tags.Any(t => t.StartsWith(mk, StringComparison.OrdinalIgnoreCase)))
                    return variant;
            // GoodTools / TOSEC single-letter bracket codes. A trained/translated/fixed/cracked/hacked dump
            // is a modification, so it buckets to Hack — that's what distinguishes it from the clean release
            // of the same game on the card (and what the "official releases only" filter hides). [b]/[o] are
            // bad dumps; [p] pirate. Order matters: [tr]/[t+-] (translation) before [t#] (trainer).
            if (key != null)
            {
                if (Regex.IsMatch(key, @"\[h[0-9!]*\]", RegexOptions.IgnoreCase)) return "Hack";
                if (Regex.IsMatch(key, @"\[tr[\s\]_]", RegexOptions.IgnoreCase)) return "Hack";  // [tr en]/[tr de] translation
                if (Regex.IsMatch(key, @"\[T[+\-]")) return "Hack";                              // [T+Eng]/[T-Eng] GoodTools translation
                if (Regex.IsMatch(key, @"\[t[0-9]*\]", RegexOptions.IgnoreCase)) return "Hack";  // [t]/[t2] trainer
                if (Regex.IsMatch(key, @"\[f[0-9]*\]", RegexOptions.IgnoreCase)) return "Hack";  // [f]/[f1] fixed
                if (Regex.IsMatch(key, @"\[cr[\s\]]", RegexOptions.IgnoreCase)) return "Hack";   // [cr] cracked
                if (Regex.IsMatch(key, @"\[p[0-9]*\]", RegexOptions.IgnoreCase)) return "Pirate";
                if (badDumpTag && Regex.IsMatch(key, @"\[[bo][0-9]*\]", RegexOptions.IgnoreCase)) return "BadDump";
            }
            return Release;
        }
    }
}
