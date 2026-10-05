using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace MovieTheater.Arcade
{
    /// <summary>
    /// Which MAME machines belong on the site's arcade: the playable VIDEO games, minus fruit machines,
    /// slots, gambling/medal/redemption machines, computers, consoles, calculators, instruments, and
    /// anything that needs a panel a pad can't stand in for.
    ///
    /// <para>Inputs: the MAME <c>-listxml</c> (via <see cref="ArcadeControlProfile.Read"/>) plus the
    /// progettoSNAPS "folders" INIs shipped in the MAME EXTRAs pack — <c>Machine Type.ini</c> (what the cabinet
    /// IS: 16k of 0.288's machines are fruit machines), <c>category.ini</c> (genre / Casino / Gambling / …),
    /// <c>Game Or No Game.ini</c>, and <c>mess.ini</c> (computers, consoles, handhelds — the old MESS half,
    /// which the site's console systems already cover properly).</para>
    ///
    /// <para>Rules apply in a fixed order and the FIRST failing rule is the reason reported, so the ingest
    /// report reads as a funnel. Measured on 0.289 (2026-10-05): 50,368 machines → 9,6xx playable sets
    /// (≈3,400 parent games). Every list below is a judgement call made by reading what the bucket
    /// actually contains; the ingest's TSV report lists every machine with its reason, for review.</para>
    /// </summary>
    public static class ArcadeMameSelection
    {
        /// <summary>The progettoSNAPS folder INIs: machine → section.</summary>
        public sealed record Folders(
            IReadOnlyDictionary<string, string> MachineType,
            IReadOnlyDictionary<string, string> Category,
            IReadOnlyDictionary<string, string> GameOrNoGame,
            IReadOnlySet<string> Mess);

        public static Folders LoadFolders(string dir)
        {
            Dictionary<string, string> Opt(string file) =>
                File.Exists(Path.Combine(dir, file)) ? ReadIni(Path.Combine(dir, file)) : new(StringComparer.OrdinalIgnoreCase);
            var type = Opt("Machine Type.ini");
            var cat = Opt("category.ini");
            if (type.Count == 0 || cat.Count == 0)
                throw new FileNotFoundException($"'Machine Type.ini' and 'category.ini' are required in {dir} — they are what tells a fruit machine from a video game.");
            return new Folders(type, cat, Opt("Game Or No Game.ini"), Opt("mess.ini").Keys.ToHashSet(StringComparer.OrdinalIgnoreCase));
        }

        /// <summary>An INI of <c>[Section]</c> headers each followed by one machine name per line. FOLDER_SETTINGS
        /// and key=value lines are skipped; ROOT_FOLDER is a real section (mess.ini lists everything there).</summary>
        public static Dictionary<string, string> ReadIni(string path)
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string? section = null;
            foreach (var raw in File.ReadLines(path, Encoding.Latin1))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith(';')) continue;
                if (line.StartsWith('[') && line.EndsWith(']')) { section = line[1..^1]; continue; }
                if (section is null or "FOLDER_SETTINGS" || line.Contains('=')) continue;
                map[line] = section;
            }
            return map;
        }

        // Machine Type.ini sections that are never a video game for a pad, matched as prefixes.
        private static readonly string[] ExcludedTypes =
        {
            "Fruit Machine", "Slot Machine", "IGT ", "Pinball", "Computer", "Calculator", "Musical Instrument", "PDA",
            "Chess board", "Console", "Handheld game", "Medal game", "Redemption game", "Dart game", "Motherboard",
            "Other", "Tabletop game", "Printer", "Pachinko", "Pachislot", "Crane", "Photo", "Telephone", "Watch",
            // Barcrest MPU4 video boards are pub fruit/quiz machines that pay out (The Crystal Maze et al.).
            "Barcrest",
        };

        // category.ini top-level (or top/sub) buckets that are gambling, prize, physical or non-game machines.
        // "Arcade / Pinball" is deliberately absent: those are VIDEO pinball (Alien Crush, Gals Pinball).
        private static readonly string[] ExcludedCategories =
        {
            "Casino", "Gambling", "Medal Game", "Electromechanical", "Redemption Game", "Slot Machine", "Fruit Machine",
            "Computer", "Calculator", "Music Player", "Musical Instrument", "Printer", "Medical Equipment",
            "Game Console", "Handheld", "Board Game", "Utilities", "System", "TTL", "Watch", "Telephone",
            "Digital Camera", "Computer Graphic Workstation",
            "Whac-A-Mole",                       // a physical hammer/stomp panel
            "Music Game",                        // turntables, dance pads, guitars, drums — nothing a pad stands in for
            "Arcade / Fortune Teller", "Arcade / Physical Ability", "Arcade / Skill Drop",
            "Arcade / Strength Tester", "Arcade / Unknown",
        };

        // Control panels a gamepad or the touch pad can't model. A machine is dropped when its player-1 panel
        // includes one of these (a mahjong game also has a joystick for menus — it is still a mahjong game).
        private static readonly HashSet<string> UnplayablePanels = new(StringComparer.OrdinalIgnoreCase)
        { "mahjong", "hanafuda", "gambling", "keyboard" };

        /// <summary>Why <paramref name="m"/> is NOT on the site, or null when it is a playable video game.</summary>
        public static string? ExcludeReason(ArcadeControlProfile.Machine m, Folders f)
        {
            if (m.IsBios) return "bios";
            if (m.IsDevice) return "device";
            if (m.IsMechanical) return "mechanical";
            if (!m.Runnable) return "not runnable";
            if (f.Mess.Contains(m.Name)) return "computer/console (mess.ini)";
            if (f.GameOrNoGame.TryGetValue(m.Name, out var gng) && gng == "No Game") return "not a game";
            if (f.MachineType.TryGetValue(m.Name, out var type) && ExcludedTypes.Any(t => type.StartsWith(t, StringComparison.OrdinalIgnoreCase)))
                return $"machine type: {type}";
            if (f.Category.TryGetValue(m.Name, out var cat) && ExcludedCategories.Any(c => cat.StartsWith(c, StringComparison.OrdinalIgnoreCase)))
                return $"category: {cat}";
            if (m.Displays == 0) return "no screen";
            if (m.ControlTypes is { } ct && ct.Overlaps(UnplayablePanels)) return $"panel: {string.Join('+', ct.Order())}";
            if (m.DriverStatus == "preliminary") return "does not work (preliminary driver)";
            return null;
        }

        /// <summary>The description a machine's card is titled from: its PARENT's, so every clone of a game
        /// shares one card title (the same rule FBNeo's cards use). The caller cleans it.</summary>
        public static string TitleSourceFor(ArcadeControlProfile.Machine m, IReadOnlyDictionary<string, ArcadeControlProfile.Machine> all)
        {
            var desc = m.CloneOf != null && all.TryGetValue(m.CloneOf, out var parent) ? parent.Description : m.Description;
            return string.IsNullOrWhiteSpace(desc) ? m.Name : desc;
        }

        /// <summary>A MAME year ("1991", "199?", "19??") as a number when it is exact.</summary>
        public static int? YearOf(string? year) =>
            year is { Length: 4 } && int.TryParse(year, out var y) && y > 1900 ? y : null;

        /// <summary>
        /// Where a machine's CHDs are, as ONE directory the ROM cache copies to <c>&lt;rompath&gt;/&lt;name&gt;/</c>
        /// (MAME looks for a game's disks in a folder named after it). A merged CHD set keeps a clone's disk under
        /// the PARENT's folder when the disk is shared (<c>merge=</c>), so: the machine's own folder when every
        /// disk is there, else the parent's when every disk is there. Null + a reason when the disks aren't all
        /// in one place on disk (missing, or split across two folders — the cache stages one directory).
        /// </summary>
        public static (string? Dir, string? Missing) ChdDirFor(ArcadeControlProfile.Machine m, string? chdRoot)
        {
            if (m.Disks is not { Count: > 0 }) return (null, null);
            if (string.IsNullOrEmpty(chdRoot)) return (null, "needs a CHD (no --chds root given)");
            bool AllIn(string dir) => m.Disks.All(d =>
                File.Exists(Path.Combine(dir, d.Name + ".chd")) || (d.Merge != null && File.Exists(Path.Combine(dir, d.Merge + ".chd"))));
            var own = Path.Combine(chdRoot, m.Name);
            if (Directory.Exists(own) && AllIn(own)) return (own, null);
            if (m.CloneOf != null)
            {
                var parent = Path.Combine(chdRoot, m.CloneOf);
                if (Directory.Exists(parent) && AllIn(parent)) return (parent, null);
            }
            return (null, $"CHD missing: {string.Join(", ", m.Disks.Select(d => d.Name))}");
        }
    }
}
