using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using MovieTheater.Db;

namespace MovieTheater.Arcade
{
    /// <summary>
    /// Turns the many ROM rows of one game (same System+Title) into the lobby's per-game version list:
    /// a human label for each ("USA", "Europe", "USA · Rev B", "USA · GameCube Edition", "USA · Hack",
    /// "USA · Disc 1") and an ordering that puts the best English official release first (the card's
    /// default selection + box-art source). See docs/arcade-dedupe-multidisc-plan.md.
    /// </summary>
    public static class ArcadeVersions
    {
        /// <summary>Ascending sort key — the smallest is the default-selected version. Prefers official
        /// over modified, English regions, disc 1, and the highest revision.</summary>
        public static (int, int, int, int, int) Rank(ArcadeGame g) =>
        (
            string.Equals(g.Variant, "Release", StringComparison.OrdinalIgnoreCase) || g.Variant == null ? 0 : 1,
            RegionRank(g.Region),
            DiscNumber(g.CloudRetroGameKey) is int d and > 0 ? d : 0, // Disc 1 before Disc 2; non-disc = 0
            -RevValue(g.CloudRetroGameKey),                            // higher revision first
            g.Id
        );

        /// <summary>The dropdown label: region + the tags that distinguish this ROM from its siblings.</summary>
        public static string Label(ArcadeGame g)
        {
            var parts = new List<string>();
            if (!string.IsNullOrEmpty(g.Region) && !g.Region.Equals("Unknown", StringComparison.OrdinalIgnoreCase))
                parts.Add(g.Region!);

            var rev = Revision(g.CloudRetroGameKey);
            if (rev != null) parts.Add(rev);

            var edition = Edition(g.CloudRetroGameKey);
            if (edition != null) parts.Add(edition);

            var disc = DiscNumber(g.CloudRetroGameKey);
            if (disc > 0) parts.Add($"Disc {disc}");

            if (!string.IsNullOrEmpty(g.Variant) && !g.Variant.Equals("Release", StringComparison.OrdinalIgnoreCase))
            {
                var lang = TranslationLang(g.CloudRetroGameKey);
                parts.Add(lang != null ? $"{g.Variant} ({lang})" : g.Variant!);
            }

            return parts.Count > 0 ? string.Join(" · ", parts) : "Standard";
        }

        private static int RegionRank(string? region) => (region ?? "").ToLowerInvariant() switch
        {
            "world" => 0, "usa" => 1, "europe" => 2, "" or "unknown" => 3, "other" => 4, "asia" => 5, "japan" => 6, _ => 3,
        };

        public static int DiscNumber(string? key)
        {
            if (key == null) return 0;
            var m = Regex.Match(key, @"\(Dis[ck]\s*(\d+)", RegexOptions.IgnoreCase);
            return m.Success ? int.Parse(m.Groups[1].Value) : 0;
        }

        private static string? Revision(string? key)
        {
            if (key == null) return null;
            var rev = Regex.Match(key, @"\(Rev\s*([0-9A-Z]+)\)", RegexOptions.IgnoreCase);
            if (rev.Success) return "Rev " + rev.Groups[1].Value.ToUpperInvariant();
            var ver = Regex.Match(key, @"\((v\s*\d+\.\d+)\)", RegexOptions.IgnoreCase);
            if (ver.Success) return ver.Groups[1].Value.Replace(" ", "");
            var prg = Regex.Match(key, @"\(PRG\s*(\d+)\)", RegexOptions.IgnoreCase);
            if (prg.Success) return "PRG" + prg.Groups[1].Value;
            return null;
        }

        private static int RevValue(string? key)
        {
            if (key == null) return 0;
            var rev = Regex.Match(key, @"\(Rev\s*([0-9A-Z]+)\)", RegexOptions.IgnoreCase);
            if (rev.Success)
            {
                var s = rev.Groups[1].Value;
                if (int.TryParse(s, out var n)) return n;
                if (s.Length == 1 && char.IsLetter(s[0])) return char.ToUpperInvariant(s[0]) - 'A' + 1;
            }
            var ver = Regex.Match(key, @"\(v\s*(\d+)\.(\d+)\)", RegexOptions.IgnoreCase);
            if (ver.Success) return int.Parse(ver.Groups[1].Value) * 100 + int.Parse(ver.Groups[2].Value);
            var prg = Regex.Match(key, @"\(PRG\s*(\d+)\)", RegexOptions.IgnoreCase);
            if (prg.Success) return int.Parse(prg.Groups[1].Value);
            return 0;
        }

        // "(Something Edition)" → "Something Edition" (e.g. GameCube Edition, Player's Choice).
        private static string? Edition(string? key)
        {
            if (key == null) return null;
            var m = Regex.Match(key, @"\(([^()]*\bEdition)\)", RegexOptions.IgnoreCase);
            return m.Success ? m.Groups[1].Value.Trim() : null;
        }

        // Translation target language from a TOSEC "[tr xx]" tag, upper-cased ("de" → "DE").
        private static string? TranslationLang(string? key)
        {
            if (key == null) return null;
            var m = Regex.Match(key, @"\[tr\s+([a-z]{2})\]", RegexOptions.IgnoreCase);
            return m.Success ? m.Groups[1].Value.ToUpperInvariant() : null;
        }
    }
}
