using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

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

        public int Count => byExact.Count;

        /// <summary>The regions a No-Intro/Redump tag can name. Anything else in the parenthetical (a cheat
        /// device, a revision) is not a region and tells us nothing about which dump the codes target.</summary>
        private static readonly HashSet<string> KnownRegions = new(StringComparer.OrdinalIgnoreCase)
        { "usa", "europe", "japan", "australia", "korea", "brazil", "china", "asia", "france", "germany", "italy", "spain" };

        private static readonly string[] WorldExpansion = { "usa", "europe", "japan", "australia" };

        /// <summary>Build from full paths to <c>.cht</c> files (one system's folder).</summary>
        public static ArcadeChtIndex Build(IEnumerable<string> chtPaths)
        {
            var idx = new ArcadeChtIndex();
            foreach (var path in chtPaths)
            {
                var stem = Path.GetFileNameWithoutExtension(path);
                if (stem.Length == 0) continue;
                idx.byExact[stem] = path;
                var sig = TitleSignature(stem);
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

            var sig = TitleSignature(romName);
            if (sig.Length == 0 || !bySignature.TryGetValue(sig, out var candidates)) return null;

            var romRegions = Regions(romName);
            // Prefer a candidate that names a compatible region; a region-less (device-only) file is the
            // last resort, never chosen over one that actually agrees on the dump.
            string? wildcard = null;
            foreach (var c in candidates)
            {
                var cheatRegions = Regions(Path.GetFileNameWithoutExtension(c));
                if (cheatRegions.Count == 0) { wildcard ??= c; continue; }
                if (romRegions.Count == 0) { wildcard ??= c; continue; }
                if (romRegions.Overlaps(cheatRegions)) return c;
            }
            return wildcard;
        }

        /// <summary>Sorted distinct lowercase alphanumeric tokens of the title, with every "(...)"/"[...]"
        /// tag removed — so word order and punctuation don't matter but vocabulary does.
        ///
        /// <para>Duplicated rather than borrowed from <c>ArcadeBoxArtIndex.TokenSignature</c> on purpose: that
        /// type drags in ImageSharp via <c>ArcadeBoxArt</c>, which the test project cannot reference.</para></summary>
        internal static string TitleSignature(string s)
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

        /// <summary>Regions named by the FIRST parenthetical, "World" expanded. Empty when it names none.</summary>
        internal static HashSet<string> Regions(string filename)
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int open = filename.IndexOf('(');
            if (open < 0) return set;
            int close = filename.IndexOf(')', open + 1);
            if (close <= open) return set;

            foreach (var raw in filename.Substring(open + 1, close - open - 1).Split(','))
            {
                var part = raw.Trim();
                if (part.Equals("world", StringComparison.OrdinalIgnoreCase))
                    foreach (var r in WorldExpansion) set.Add(r);
                else if (KnownRegions.Contains(part))
                    set.Add(part.ToLowerInvariant());
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
