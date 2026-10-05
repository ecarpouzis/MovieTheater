using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml;

namespace MovieTheater.Arcade
{
    /// <summary>
    /// A cabinet's player-1 control panel in the compact form the touch pad reads (ArcadeGame.Controls →
    /// the room descriptor's <c>controls</c> → src/ui/src/Pages/Arcade/touch/touchPresets.ts
    /// <c>parseControlProfile</c>): <c>kind/buttons[/sf]</c>, e.g. <c>joy4/1</c> (Pac-Man),
    /// <c>joy8/6/sf</c> (Street Fighter II), <c>twin/0</c> (Robotron), <c>trackball/1</c> (Centipede).
    ///
    /// <para>Read from MAME's <c>-listxml</c> <c>&lt;input&gt;/&lt;control&gt;</c> elements. The two formats
    /// must stay in step — the UI test and <c>ArcadeControlProfileTests</c> pin the same examples.</para>
    ///
    /// <para><b>Kinds</b> (most specific wins when a panel has several controls — Centipede is a trackball
    /// AND a fire button on a joystick-less panel; Off Road is a dial AND a pedal):
    /// gun (lightgun) &gt; trackball (trackball, mouse) &gt; spinner (dial, paddle, positional — MAME calls
    /// steering wheels paddles) &gt; twin (doublejoy, triplejoy) &gt; joy4 (a joy with 4/2/vertical-2/half-4
    /// ways — one direction at a time) / joy8 &gt; stick (analog/49-way) &gt; buttons (only_buttons, or a
    /// lone pedal) &gt; other (mahjong, hanafuda, gambling, keyboard, keypad panels the touch pad can't
    /// model).</para>
    ///
    /// <para><b>sf</b> = FBNeo lays this game out Street-Fighter style (punches over kicks: Y X L / B A R).
    /// FBNeo decides that per driver (bStreetFighterLayout in its libretro retro_input.cpp: CPS2 with 5+
    /// buttons, or a driver with 3x-punch/3x-kick macros — the Capcom CPS1/2/3 fighters); the listxml can't
    /// see macros, so the rule here is "a Capcom CPS1/2/3 board with 6 buttons, or CPS2 with 5+".</para>
    /// </summary>
    public static class ArcadeControlProfile
    {
        /// <summary>One listxml machine. <see cref="Profile"/> is null when the machine has no &lt;input&gt;. The
        /// rest feeds the MAME ingest's selection (arcade-mame-ingest): what the set is, whether it works, and
        /// which CHDs it needs (<see cref="Disks"/>: name + the parent disk it MERGES with, if any).</summary>
        public sealed record Machine(
            string Name, string? CloneOf, string? SourceFile, bool IsBios, bool IsDevice, string? Profile,
            string Description = "", string? Year = null, string? Manufacturer = null, bool IsMechanical = false,
            bool Runnable = true, string? DriverStatus = null, int Displays = 0, int Players = 0,
            IReadOnlyList<(string Name, string? Merge)>? Disks = null, IReadOnlySet<string>? ControlTypes = null,
            string? RomOf = null);

        /// <summary>Stream a listxml (hundreds of MB for a full set) without loading it whole.</summary>
        public static IEnumerable<Machine> Read(string path)
        {
            var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, IgnoreWhitespace = true, IgnoreComments = true };
            using var stream = File.OpenRead(path);
            using var reader = XmlReader.Create(stream, settings);
            while (reader.Read())
            {
                if (reader.NodeType != XmlNodeType.Element || (reader.Name != "machine" && reader.Name != "game")) continue;
                var name = reader.GetAttribute("name");
                var cloneOf = reader.GetAttribute("cloneof");
                var source = reader.GetAttribute("sourcefile");
                var isBios = reader.GetAttribute("isbios") == "yes";
                var isDevice = reader.GetAttribute("isdevice") == "yes";
                var isMech = reader.GetAttribute("ismechanical") == "yes";
                var runnable = reader.GetAttribute("runnable") != "no";
                var romOf = reader.GetAttribute("romof");
                var controls = new List<(string Type, string? Player, int Buttons, string? Ways)>();
                var disks = new List<(string, string?)>();
                string desc = "", status = null!;
                string? year = null, maker = null;
                int displays = 0, players = 0;
                bool sawInput = false;
                if (!reader.IsEmptyElement)
                {
                    using var sub = reader.ReadSubtree();
                    sub.Read();   // onto <machine> itself
                    // ReadElementContentAsString leaves the reader ON the next node, so those branches must
                    // not Read() again — stepping only when nothing was consumed keeps every sibling visible.
                    while (!sub.EOF)
                    {
                        if (sub.NodeType == XmlNodeType.Element && sub.Depth == 1 && sub.Name is "description" or "year" or "manufacturer")
                        {
                            var which = sub.Name;
                            var text = sub.ReadElementContentAsString();
                            if (which == "description") desc = text; else if (which == "year") year = text; else maker = text;
                            continue;
                        }
                        if (sub.NodeType != XmlNodeType.Element) { sub.Read(); continue; }
                        if (sub.Name == "input")
                        {
                            sawInput = true;
                            int.TryParse(sub.GetAttribute("players"), out players);
                        }
                        else if (sub.Name == "display") displays++;
                        else if (sub.Name == "driver") status = sub.GetAttribute("status")!;
                        else if (sub.Name == "disk" && sub.GetAttribute("name") is { } dn) disks.Add((dn, sub.GetAttribute("merge")));
                        else if (sub.Name == "control")
                        {
                            int.TryParse(sub.GetAttribute("buttons"), out var b);
                            controls.Add((sub.GetAttribute("type") ?? "", sub.GetAttribute("player"), b, sub.GetAttribute("ways")));
                        }
                        sub.Read();
                    }
                }
                if (string.IsNullOrEmpty(name)) continue;
                yield return new Machine(name, cloneOf, source, isBios, isDevice, sawInput ? Classify(controls, source) : null,
                    desc, year, maker, isMech, runnable, status, displays, players, disks,
                    controls.Where(c => string.IsNullOrEmpty(c.Player) || c.Player == "1").Select(c => c.Type.ToLowerInvariant()).ToHashSet(),
                    romOf);
            }
        }

        private static readonly HashSet<string> OtherPanels = new(StringComparer.OrdinalIgnoreCase)
        { "mahjong", "hanafuda", "gambling", "keyboard", "keypad" };

        /// <summary>The profile for one machine's controls. Player-1 controls only (a control with no player attribute is player 1's).</summary>
        public static string Classify(IReadOnlyCollection<(string Type, string? Player, int Buttons, string? Ways)> all, string? sourceFile)
        {
            var p1 = all.Where(c => string.IsNullOrEmpty(c.Player) || c.Player == "1").ToList();
            var types = p1.Select(c => c.Type.ToLowerInvariant()).ToHashSet();
            int buttons = p1.Count == 0 ? 0 : Math.Min(p1.Max(c => c.Buttons), 99);

            string kind;
            if (types.Contains("lightgun")) kind = "gun";
            else if (types.Contains("trackball") || types.Contains("mouse")) kind = "trackball";
            else if (types.Contains("dial") || types.Contains("paddle") || types.Contains("positional")) kind = "spinner";
            else if (types.Contains("doublejoy") || types.Contains("triplejoy")) kind = "twin";
            else if (types.Contains("joy")) kind = IsFourWay(p1.First(c => c.Type.Equals("joy", StringComparison.OrdinalIgnoreCase)).Ways) ? "joy4" : "joy8";
            else if (types.Contains("stick")) kind = "stick";
            else if (types.Contains("only_buttons") || types.Contains("pedal")) kind = "buttons";
            else if (types.Overlaps(OtherPanels)) kind = "other";
            else kind = p1.Count == 0 ? "buttons" : "other";

            var sf = kind is "joy8" or "joy4" or "stick" && IsStreetFighterLayout(sourceFile, buttons);
            return $"{kind}/{buttons}{(sf ? "/sf" : "")}";
        }

        // MAME ways: "8", "4", "2", "vertical2", "3 (half4)", "5 (half8)", "16", "strange".
        private static bool IsFourWay(string? ways)
        {
            var w = (ways ?? "").Trim().ToLowerInvariant();
            return w == "4" || w == "2" || w == "vertical2" || w.StartsWith("3 ") || w.StartsWith("4 ");
        }

        private static bool IsStreetFighterLayout(string? sourceFile, int buttons)
        {
            var src = Path.GetFileNameWithoutExtension((sourceFile ?? "").Replace('\\', '/').Split('/').Last()).ToLowerInvariant();
            return (src is "cps1" or "cps2" or "cps3" && buttons >= 6) || (src == "cps2" && buttons >= 5);
        }
    }
}
