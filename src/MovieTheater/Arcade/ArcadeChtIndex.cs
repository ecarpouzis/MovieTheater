using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace MovieTheater.Arcade
{
    /// <summary>
    /// Matches one of our ROM filenames to a libretro cheat file. Free of EF/CliFx so it can be unit-tested.
    ///
    /// <para>Upstream <c>.cht</c> names are ROM names with a <b>cheat-device</b> suffix and often a broader
    /// region tag than the individual dump: our <c>Ape Escape (USA).cue</c> is upstream's
    /// <c>Ape Escape (USA, Europe) (Game Buster).cht</c>, and <c>007 - GoldenEye (USA)</c> is
    /// <c>GoldenEye 007 (USA)</c>. So an exact filename compare finds only about a quarter of them.</para>
    ///
    /// <para>The fallback is deliberately narrow, because a mismatch here is not a harmless miss — a cheat
    /// code is an address poke, and one from the PAL dump aimed at the NTSC binary corrupts state instead of
    /// failing. It requires:</para>
    /// <list type="number">
    ///   <item>the same TITLE token set, order-insensitive but with nothing added or dropped
    ///     ("007 - GoldenEye" ⇄ "GoldenEye 007"; never "Super Return of the Jedi" ⇄ "Super Star Wars -
    ///     Return of the Jedi"), and</item>
    ///   <item><b>overlapping regions</b> — not equal ones. <c>(USA)</c> ⊂ <c>(USA, Europe)</c> matches;
    ///     <c>(World)</c> expands to every region; <c>Micro Machines V3 (USA)</c> against a lone
    ///     <c>(Europe) (Xploder)</c> file does not. A tag that names no region at all (the device-only
    ///     <c>(GameShark)</c>) carries no information and is treated as a wildcard.</item>
    /// </list>
    ///
    /// <para>Measured on the materialized ROM mount: 90% matched, 0 cross-region.</para>
    /// </summary>
    public sealed class ArcadeChtIndex
    {
        private readonly Dictionary<string, string> byExact = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, List<string>> bySignature = new(StringComparer.Ordinal);
        private readonly NamingProfile profile;

        public int Count => byExact.Count;

        /// <summary>How one system's ROM filenames are shaped. Every rule here is OFF by default and turned on
        /// only for the systems that need it, because the matcher is shared: measured against the 13 systems
        /// already importing cheats, enabling the short-region-code rule globally would have silently rewritten
        /// <b>87</b> existing matches (18 of them lost outright). A naming quirk in one collection must not
        /// re-decide another collection's cheats.</summary>
        /// <param name="StripCatalogNumber">Drop a leading collection index ("0168 - Mario Kart DS"). Applied
        /// to both sides, which is safe because upstream cht names never carry one.</param>
        /// <param name="RegionsFromEveryTag">Read regions from ALL parentheticals, not just the first. Needed
        /// where the region is not the leading tag ("Alice in Wonderland (DSi Enhanced) [b] (US)") — with the
        /// first-tag rule that name reads as region-LESS, which made it match a Europe cheat file.</param>
        /// <param name="ShortRegionCodes">Accept "US"/"EU"/"JP"/"FR"… as regions. These collide with the
        /// GoodTools single letters used by other collections, hence per-system.</param>
        public sealed record NamingProfile(bool StripCatalogNumber, bool RegionsFromEveryTag, bool ShortRegionCodes)
        {
            /// <summary>No-Intro/Redump style: "Title (Region) (Tags)". What every system used before nds.</summary>
            public static readonly NamingProfile NoIntro = new(false, false, false);

            /// <summary>A numbered release-set dump ("0168 - Mario Kart DS (US)(M5)"), whose index prefix,
            /// trailing region tag and two-letter region codes all defeat the No-Intro rules.</summary>
            public static readonly NamingProfile NumberedSet = new(true, true, true);
        }

        private ArcadeChtIndex(NamingProfile profile) => this.profile = profile;

        /// <summary>The regions a No-Intro/Redump tag can name. Anything else in the parenthetical (a cheat
        /// device, a revision) is not a region and tells us nothing about which dump the codes target.</summary>
        private static readonly HashSet<string> KnownRegions = new(StringComparer.OrdinalIgnoreCase)
        { "usa", "europe", "japan", "australia", "korea", "brazil", "china", "asia", "france", "germany", "italy",
          "spain", "netherlands" };

        private static readonly string[] WorldExpansion = { "usa", "europe", "japan", "australia" };

        /// <summary>Two-letter region codes, enabled per system. Kept off by default because they overlap the
        /// GoodTools single letters other collections use for other meanings.</summary>
        private static readonly Dictionary<string, string> ShortRegions = new(StringComparer.OrdinalIgnoreCase)
        {
            ["us"] = "usa", ["u"] = "usa", ["eu"] = "europe", ["e"] = "europe", ["eur"] = "europe",
            ["jp"] = "japan", ["j"] = "japan", ["jpn"] = "japan", ["fr"] = "france", ["de"] = "germany",
            ["it"] = "italy", ["es"] = "spain", ["sp"] = "spain", ["nl"] = "netherlands",
            ["kr"] = "korea", ["ks"] = "korea", ["ko"] = "korea", ["au"] = "australia",
            ["br"] = "brazil", ["cn"] = "china", ["as"] = "asia",
        };

        /// <summary>A leading collection index: "0168 - Mario Kart DS". Requires the separator, so a title that
        /// merely STARTS with digits ("1080 Avalanche") is untouched — but note "007 - Agent Under Fire" does
        /// look like an index, which is why this rule is per-system rather than global.</summary>
        private static readonly Regex CatalogPrefix = new(@"^\s*\d{3,5}\s*-\s+", RegexOptions.Compiled);

        /// <summary>Build from full paths to <c>.cht</c> files (one system's folder).</summary>
        public static ArcadeChtIndex Build(IEnumerable<string> chtPaths, NamingProfile? profile = null)
        {
            var idx = new ArcadeChtIndex(profile ?? NamingProfile.NoIntro);
            foreach (var path in chtPaths)
            {
                var stem = Path.GetFileNameWithoutExtension(path);
                if (stem.Length == 0) continue;
                idx.byExact[stem] = path;
                var sig = TitleSignature(stem, idx.profile);
                if (sig.Length == 0) continue;
                if (!idx.bySignature.TryGetValue(sig, out var list)) idx.bySignature[sig] = list = new List<string>();
                list.Add(path);
            }
            return idx;
        }

        /// <summary>The .cht path for a ROM name (no extension), or null when nothing safe matches.</summary>
        public string? Match(string romName)
        {
            if (string.IsNullOrEmpty(romName)) return null;
            if (byExact.TryGetValue(romName, out var exact)) return exact;

            var sig = TitleSignature(romName, profile);
            if (sig.Length == 0 || !bySignature.TryGetValue(sig, out var candidates)) return null;

            var romRegions = Regions(romName, profile);
            // Prefer candidates that name a compatible region; a region-less (device-only) file is the
            // last resort, never chosen over one that actually agrees on the dump.
            var compatible = new List<string>();
            var wildcards = new List<string>();
            foreach (var c in candidates)
            {
                var cheatRegions = Regions(Path.GetFileNameWithoutExtension(c), profile);
                if (cheatRegions.Count == 0 || romRegions.Count == 0) { wildcards.Add(c); continue; }
                if (romRegions.Overlaps(cheatRegions)) compatible.Add(c);
            }

            var pool = compatible.Count > 0 ? compatible : wildcards;
            if (pool.Count == 0) return null;

            // Several files can agree on title and region. Take the LEAST DECORATED name — the fewest words
            // inside "(...)"/"[...]" beyond the title itself — then the name itself, so the choice is stable
            // rather than whatever order the directory enumerated in.
            //
            // This is a correctness rule, not tidiness. Upstream keeps ROM HACK cheat files beside the stock
            // game's, and a hack's name lives in a parenthetical that the title signature strips: "Mario Kart
            // DS (USA)" and "Mario Kart DS (USA) (CTGP Nitro (v1.0.0))" are indistinguishable to the signature
            // AND agree on region. Picking arbitrarily between them handed our stock Mario Kart DS the hack's
            // addresses. The same tiebreak also prefers a plain dump over "(Rev 1)" and over a device-suffixed
            // file, which is what we want when our own dump carries no such marker.
            return pool.OrderBy(Decoration).ThenBy(p => p, StringComparer.Ordinal).First();
        }

        /// <summary>How many words a name carries inside brackets — its "decoration". Counted by depth rather
        /// than a regex because these nest: "(CTGP Nitro (v1.0.0))" is one group, four words.</summary>
        internal static int Decoration(string path)
        {
            var name = Path.GetFileNameWithoutExtension(path);
            int depth = 0, words = 0;
            bool inWord = false;
            foreach (var ch in name)
            {
                if (ch == '(' || ch == '[') { depth++; inWord = false; continue; }
                if (ch == ')' || ch == ']') { if (depth > 0) depth--; inWord = false; continue; }
                if (depth > 0 && char.IsLetterOrDigit(ch)) { if (!inWord) { words++; inWord = true; } }
                else inWord = false;
            }
            return words;
        }

        /// <summary>Sorted distinct lowercase alphanumeric tokens of the title, with every "(...)"/"[...]"
        /// tag removed — so word order and punctuation don't matter but vocabulary does.
        ///
        /// <para>Duplicated rather than borrowed from <c>ArcadeBoxArtIndex.TokenSignature</c> on purpose: that
        /// type drags in ImageSharp via <c>ArcadeBoxArt</c>, which the test project cannot reference.</para></summary>
        internal static string TitleSignature(string s, NamingProfile? profile = null)
        {
            if ((profile ?? NamingProfile.NoIntro).StripCatalogNumber) s = CatalogPrefix.Replace(s, "");

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

        /// <summary>Regions named by the FIRST parenthetical (or by every one, per profile), "World" expanded.
        /// Empty when none is named — which the caller treats as "carries no dump information", so widening
        /// this vocabulary can only turn a blind wildcard match into a checked one.</summary>
        internal static HashSet<string> Regions(string filename, NamingProfile? profile = null)
        {
            var p = profile ?? NamingProfile.NoIntro;
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            int from = 0;
            while (true)
            {
                int open = filename.IndexOf('(', from);
                if (open < 0) break;
                int close = filename.IndexOf(')', open + 1);
                if (close <= open) break;

                foreach (var raw in filename.Substring(open + 1, close - open - 1).Split(','))
                {
                    var part = raw.Trim();
                    if (part.Equals("world", StringComparison.OrdinalIgnoreCase))
                        foreach (var r in WorldExpansion) set.Add(r);
                    else if (KnownRegions.Contains(part))
                        set.Add(part.ToLowerInvariant());
                    else if (p.ShortRegionCodes && ShortRegions.TryGetValue(part, out var mapped))
                        set.Add(mapped);
                }

                if (!p.RegionsFromEveryTag) break;
                from = close + 1;
            }
            return set;
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
    }
}
