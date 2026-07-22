using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace MovieTheater.Arcade
{
    /// <summary>
    /// Parsed FinalBurn Neo "Arcade only" ClrMamePro-XML DAT (data/arcade/fbneo-arcade.dat, from
    /// libretro-database metadat/fbneo-split — version-matched to the buildbot fbneo core). This is the
    /// authority for what the arcade actually runs: our ROM source is a full MAME romset (~36k sets) but
    /// the core is FBNeo (~6.9k arcade games), so a game is playable iff its shortname is in this DAT.
    ///
    /// <para>It provides the three things the catalog cleanup needs:
    /// <list type="bullet">
    /// <item><b>real title</b> — <c>&lt;description&gt;</c> (e.g. shortname <c>1942a</c> → "1942 (Revision A)");</item>
    /// <item><b>dedupe key</b> — the <c>cloneof</c> parent chain, so every revision/region of one game
    /// collapses under the parent's title into a single lobby card;</item>
    /// <item><b>ROM dependency closure</b> — the <c>romof</c> chain (parent + BIOS zips such as
    /// <c>neogeo.zip</c>) that must be staged alongside a game or FBNeo reports "missing romset".</item>
    /// </list></para>
    /// </summary>
    public sealed class FbneoDat
    {
        public sealed record Entry(
            string Name, string Description, string? CloneOf, string? RomOf,
            bool IsBios, int? Year, string? Manufacturer);

        private readonly Dictionary<string, Entry> byName;

        public string Version { get; }
        public int Count => byName.Count;

        private FbneoDat(Dictionary<string, Entry> entries, string version)
        {
            byName = entries;
            Version = version;
        }

        public static FbneoDat Load(string path)
        {
            var full = Path.GetFullPath(path);
            if (!File.Exists(full))
                throw new FileNotFoundException($"FBNeo DAT not found: {full}");

            var doc = XDocument.Load(full);
            // FBNeo ClrMamePro DATs carry <header><version>; a MAME `-listxml` dump carries build="0.xxx"
            // on the root <mame> instead — accept either so this one loader serves both sources.
            var version = doc.Root?.Element("header")?.Element("version")?.Value?.Trim()
                          ?? (string?)doc.Root?.Attribute("build") ?? "unknown";

            var entries = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
            // FBNeo uses <game>; MAME `-listxml` uses <machine>. Same attributes/children otherwise.
            foreach (var g in doc.Root!.Elements().Where(e => e.Name.LocalName is "game" or "machine"))
            {
                var name = (string?)g.Attribute("name");
                if (string.IsNullOrWhiteSpace(name)) continue;
                var desc = g.Element("description")?.Value?.Trim() ?? name;
                int? year = int.TryParse(g.Element("year")?.Value?.Trim(), out var y) ? y : null;
                // "Not a playable game": a BIOS set, a MAME device, or an explicitly non-runnable machine.
                bool notAGame =
                    string.Equals((string?)g.Attribute("isbios"), "yes", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals((string?)g.Attribute("isdevice"), "yes", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals((string?)g.Attribute("runnable"), "no", StringComparison.OrdinalIgnoreCase);
                entries[name] = new Entry(
                    Name: name,
                    Description: desc,
                    CloneOf: (string?)g.Attribute("cloneof"),
                    RomOf: (string?)g.Attribute("romof"),
                    IsBios: notAGame,
                    Year: year,
                    Manufacturer: g.Element("manufacturer")?.Value?.Trim());
            }
            return new FbneoDat(entries, version);
        }

        public bool TryGet(string shortName, out Entry entry) => byName.TryGetValue(shortName, out entry!);

        public bool Contains(string shortName) => byName.ContainsKey(shortName);

        /// <summary>The root of the <c>cloneof</c> chain — the parent whose title the whole family shares
        /// (returns the name itself when it is a parent or unknown to the DAT). Cycle-guarded.</summary>
        public string ParentOf(string shortName)
        {
            var cur = shortName;
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            while (byName.TryGetValue(cur, out var e) && !string.IsNullOrEmpty(e.CloneOf) && seen.Add(cur))
                cur = e.CloneOf!;
            return cur;
        }

        /// <summary>The set of romset zip <em>shortnames</em> FBNeo needs to load this game: the game itself
        /// plus its transitive <c>romof</c> chain (its split parent and any BIOS such as <c>neogeo</c>). The
        /// BIOS target may not be a <c>&lt;game&gt;</c> entry itself, but its name is still the zip to stage.
        /// Order: the game first, then its dependencies. Cycle-guarded.</summary>
        public IReadOnlyList<string> Closure(string shortName)
        {
            var result = new List<string> { shortName };
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { shortName };
            var cur = shortName;
            while (byName.TryGetValue(cur, out var e) && !string.IsNullOrEmpty(e.RomOf) && seen.Add(e.RomOf!))
            {
                result.Add(e.RomOf!);
                cur = e.RomOf!;
            }
            return result;
        }

        /// <summary>The lobby card title for a shortname: its parent's description with trailing
        /// "(Revision A)" / "(World 910522)" / "[bootleg]" qualifiers stripped ("1942a" → "1942",
        /// "sf2ce" → "Street Fighter II': Champion Edition"). Falls back to the shortname if unknown.</summary>
        public string TitleFor(string shortName)
        {
            var parent = ParentOf(shortName);
            var desc = byName.TryGetValue(parent, out var pe) ? pe.Description
                     : byName.TryGetValue(shortName, out var e) ? e.Description
                     : shortName;
            // Case-normalize lowercase subtitle segments the DAT carries ("... - the loop master"),
            // leaving proper/acronym segments alone. Harmless on already-cased MAME descriptions.
            return ArcadeNaming.NormalizeSegmentCase(CleanDescription(desc));
        }

        // Strip trailing parenthetical/bracket qualifier groups from a DAT description so a family's
        // parent title reads cleanly. Repeats to drop stacked tags ("... (World) (set 1)").
        private static readonly Regex TrailingTag = new(@"\s*[\(\[][^\(\)\[\]]*[\)\]]\s*$", RegexOptions.Compiled);

        public static string CleanDescription(string description)
        {
            var t = description?.Trim() ?? "";
            string prev;
            do { prev = t; t = TrailingTag.Replace(t, "").Trim(); } while (t != prev && t.Length > 0);
            return t.Length > 0 ? t : (description?.Trim() ?? "");
        }
    }
}
