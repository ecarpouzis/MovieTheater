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

        public static (string Region, string Variant) Parse(string? cloudRetroGameKey)
        {
            var tags = ExtractTags(cloudRetroGameKey);
            return (RegionOf(tags), VariantOf(tags, cloudRetroGameKey));
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

        private static string VariantOf(List<string> tags, string? key)
        {
            foreach (var (mk, variant) in VariantMarkers)
                if (tags.Any(t => t.StartsWith(mk, StringComparison.OrdinalIgnoreCase)))
                    return variant;
            // GoodTools single-letter bracket codes: [b]=bad dump, [h]=hack, [p]=pirate, [o]=overdump.
            if (key != null)
            {
                if (Regex.IsMatch(key, @"\[h[0-9!]*\]", RegexOptions.IgnoreCase)) return "Hack";
                if (Regex.IsMatch(key, @"\[p[0-9]*\]", RegexOptions.IgnoreCase)) return "Pirate";
                if (Regex.IsMatch(key, @"\[[bo][0-9]*\]", RegexOptions.IgnoreCase)) return "BadDump";
            }
            return Release;
        }
    }
}
