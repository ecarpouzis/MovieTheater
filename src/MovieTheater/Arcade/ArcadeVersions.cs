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
        public static string Label(ArcadeGame g) => LabelCore(g, includeDisc: true);

        private static string LabelCore(ArcadeGame g, bool includeDisc)
        {
            var parts = new List<string>();
            if (!string.IsNullOrEmpty(g.Region) && !g.Region.Equals("Unknown", StringComparison.OrdinalIgnoreCase))
                parts.Add(g.Region!);

            var rev = Revision(g.CloudRetroGameKey);
            if (rev != null) parts.Add(rev);

            var edition = Edition(g.CloudRetroGameKey);
            if (edition != null) parts.Add(edition);

            if (includeDisc)
            {
                var disc = DiscNumber(g.CloudRetroGameKey);
                if (disc > 0) parts.Add($"Disc {disc}");
            }

            if (!string.IsNullOrEmpty(g.Variant) && !g.Variant.Equals("Release", StringComparison.OrdinalIgnoreCase))
            {
                var lang = TranslationLang(g.CloudRetroGameKey);
                parts.Add(lang != null ? $"{g.Variant} ({lang})" : g.Variant!);
            }

            return parts.Count > 0 ? string.Join(" · ", parts) : "Standard";
        }

        /// <summary>A launchable version of a game. DiscCount &gt; 1 = a multi-disc set that plays via an
        /// .m3u with in-game disc swapping (patch 0005); the Id is the disc-1 anchor row.</summary>
        public record VersionEntry(int Id, string Label, string? Region, string? Variant, int? Year, byte MaxPlayers, int DiscCount);

        /// <summary>Turn a game's rows into launchable versions, collapsing a multi-disc set (same region/rev,
        /// different disc numbers) into ONE entry (DiscCount = number of discs). preferRegion floats to the
        /// top so an explicit region filter opens the card on that region.</summary>
        public static List<VersionEntry> Build(IEnumerable<ArcadeGame> rows, string? preferRegion)
        {
            var built = new List<(ArcadeGame anchor, VersionEntry e)>();
            foreach (var grp in rows.GroupBy(VersionKey))
            {
                var ordered = grp.OrderBy(g => DiscNumber(g.CloudRetroGameKey)).ThenBy(g => g.Id).ToList();
                var anchor = ordered[0];
                int discs = ordered.Count(g => DiscNumber(g.CloudRetroGameKey) > 0);
                int discCount = discs > 1 ? discs : (DiscNumber(anchor.CloudRetroGameKey) > 0 ? 1 : 0);
                var label = LabelCore(anchor, includeDisc: discs <= 1); // multi-disc → drop the "Disc N"
                built.Add((anchor, new VersionEntry(anchor.Id, label, anchor.Region, anchor.Variant, anchor.Year,
                    ordered.Max(g => g.MaxPlayers), discCount)));
            }
            return built
                .OrderBy(x => preferRegion != null && !string.Equals(x.anchor.Region, preferRegion, StringComparison.OrdinalIgnoreCase) ? 1 : 0)
                .ThenBy(x => Rank(x.anchor))
                .Select(x => x.e).ToList();
        }

        // Version identity EXCLUDING disc number, so all discs of one release collapse together.
        private static string VersionKey(ArcadeGame g) =>
            $"{(g.Region ?? "").ToLowerInvariant()}|{(g.Variant ?? "").ToLowerInvariant()}|{RevValue(g.CloudRetroGameKey)}|{(Edition(g.CloudRetroGameKey) ?? "").ToLowerInvariant()}";

        /// <summary>The CloudRetro launch key of the .m3u playlist for a multi-disc game — the ROM name with
        /// the disc tag stripped (so the core loads "&lt;name&gt;.m3u" instead of a single disc). Covers both
        /// the ordinary parenthesized "(Disc N)" tag and a free-text trailing "- Disc N" suffix (e.g. the R:
        /// collection's "Baldur's Gate - Disc 1/2/3", which carries no region/parens at all).</summary>
        public static string M3uKey(string cloudRetroGameKey) =>
            Regex.Replace(cloudRetroGameKey, @"\s*\(Dis[ck]\s*\d+\)|\s*-\s*Dis[ck]\s*\d+\s*$", "", RegexOptions.IgnoreCase).Trim();

        /// <summary>For a chosen version anchor, how many discs its version has and the .m3u launch key
        /// (null when it's single-disc). Multi-disc = &gt;1 disc row sharing the anchor's version identity.</summary>
        public static (int DiscCount, string? M3uKey) MultiDisc(ArcadeGame anchor, IEnumerable<ArcadeGame> gameRows)
        {
            var k = VersionKey(anchor);
            int discs = gameRows.Count(g => VersionKey(g) == k && DiscNumber(g.CloudRetroGameKey) > 0);
            return discs > 1 ? (discs, M3uKey(anchor.CloudRetroGameKey)) : (discs, null);
        }

        /// <summary>Enumerate a game's multi-disc versions — each as (disc-1 anchor, ordered disc rows).
        /// Single-disc versions are skipped. The romcache export uses this to emit .m3u manifest entries.</summary>
        public static IEnumerable<(ArcadeGame Anchor, List<ArcadeGame> Discs)> MultiDiscGroups(IEnumerable<ArcadeGame> gameRows)
        {
            foreach (var grp in gameRows.GroupBy(VersionKey))
            {
                var discs = grp.Where(g => DiscNumber(g.CloudRetroGameKey) > 0)
                    .OrderBy(g => DiscNumber(g.CloudRetroGameKey)).ThenBy(g => g.Id).ToList();
                if (discs.Count > 1) yield return (discs[0], discs);
            }
        }

        private static int RegionRank(string? region) => (region ?? "").ToLowerInvariant() switch
        {
            "world" => 0, "usa" => 1, "europe" => 2, "" or "unknown" => 3, "other" => 4, "asia" => 5, "japan" => 6, _ => 3,
        };

        public static int DiscNumber(string? key)
        {
            if (key == null) return 0;
            // Ordinary "(Disc N)" tag, or a free-text trailing "- Disc N" suffix with no parens at all
            // (e.g. "Baldur's Gate - Disc 1/2/3" — an older-style release the R: collection carries as-is).
            var m = Regex.Match(key, @"\(Dis[ck]\s*(\d+)|-\s*Dis[ck]\s*(\d+)\s*$", RegexOptions.IgnoreCase);
            if (!m.Success) return 0;
            var g = m.Groups[1].Success ? m.Groups[1] : m.Groups[2];
            return int.Parse(g.Value);
        }

        // The TOSEC "bare" version token that sits after the title and BEFORE the first (/[ — e.g.
        // "Sonic Adventure v1.005 (1999)(Sega)(US)..." → "v1.005". (No-Intro/GoodTools put revisions
        // inside parens, "(Rev 1)"/"(v1.5)"; TOSEC leaves this one bare, which is why CleanTitle used to
        // leak it into the Title and split "Sonic Adventure" into one card per revision.)
        public static string? BareVersion(string? key)
        {
            if (string.IsNullOrEmpty(key)) return null;
            int cut = key.IndexOfAny(new[] { '(', '[' });
            var head = cut > 0 ? key[..cut] : key;
            var m = Regex.Match(head, @"\bv\d+(?:\.\d+)+", RegexOptions.IgnoreCase);
            return m.Success ? m.Value.ToLowerInvariant() : null;
        }

        /// <summary>Remove a trailing bare version token from an already-tag-stripped title
        /// ("Sonic Adventure v1.005" → "Sonic Adventure"). The single source of truth both CleanTitle
        /// copies call, so ingest and JIT naming can't drift.</summary>
        public static string StripTrailingBareVersion(string title) =>
            Regex.Replace(title, @"\s+v\d+(?:\.\d+)+$", "", RegexOptions.IgnoreCase).Trim();

        private static string? Revision(string? key)
        {
            if (key == null) return null;
            var rev = Regex.Match(key, @"\(Rev\s*([0-9A-Z]+)\)", RegexOptions.IgnoreCase);
            if (rev.Success) return "Rev " + rev.Groups[1].Value.ToUpperInvariant();
            var ver = Regex.Match(key, @"\((v\s*\d+\.\d+)\)", RegexOptions.IgnoreCase);
            if (ver.Success) return ver.Groups[1].Value.Replace(" ", "");
            var prg = Regex.Match(key, @"\(PRG\s*(\d+)\)", RegexOptions.IgnoreCase);
            if (prg.Success) return "PRG" + prg.Groups[1].Value;
            var bare = BareVersion(key);   // TOSEC bare token, so collapsed revisions stay distinguishable
            if (bare != null) return bare;
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
            var bare = BareVersion(key);   // e.g. "v1.005" → 1*1000+5; higher revision sorts first (Rank negates)
            if (bare != null)
            {
                var bm = Regex.Match(bare, @"v(\d+)\.(\d+)", RegexOptions.IgnoreCase);
                if (bm.Success) return int.Parse(bm.Groups[1].Value) * 1000 + int.Parse(bm.Groups[2].Value);
            }
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
