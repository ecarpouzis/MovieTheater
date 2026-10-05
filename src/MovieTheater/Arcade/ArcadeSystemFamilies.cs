using System;
using System.Collections.Generic;
using System.Linq;

namespace MovieTheater.Arcade
{
    /// <summary>
    /// The lobby's "Arcade" is a FAMILY, not one core. A cabinet player wants every coin-op game in one
    /// place, whichever emulator runs it: FBNeo (`arcade`), libretro MAME (`mame`), and the Sega boards
    /// flycast runs (`naomi`, `atomiswave`). So the Arcade carousel tile — and a `?system=arcade` filter —
    /// covers all four. NAOMI and Atomiswave keep their own tiles as narrower views of the same games;
    /// `mame` is FOLDED: it never gets a tile, because "MAME vs FBNeo" is an implementation detail of which
    /// core plays a game, not a platform anyone browses by.
    /// </summary>
    public static class ArcadeSystemFamilies
    {
        public const string Arcade = "arcade";

        /// <summary>Every system whose games are arcade (coin-op) games.</summary>
        public static readonly IReadOnlyList<string> ArcadeMembers = new[] { "arcade", "mame", "naomi", "atomiswave" };

        /// <summary>Systems with no tile of their own; their cards count (and group) under Arcade.</summary>
        public static readonly IReadOnlySet<string> FoldedIntoArcade = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "mame" };

        public static bool IsArcade(string? system) =>
            system != null && ArcadeMembers.Contains(system, StringComparer.OrdinalIgnoreCase);

        /// <summary>The tile/group a system's cards show under (mame → arcade; everything else itself).</summary>
        public static string TileOf(string system) => FoldedIntoArcade.Contains(system) ? Arcade : system;

        /// <summary>Expand a requested system set: "arcade" means the whole family. A request naming a folded
        /// system directly (an old ?system=mame link) still works — it just isn't offered as a tile.</summary>
        public static List<string> Expand(IEnumerable<string> systems)
        {
            var set = new List<string>();
            foreach (var s in systems)
            {
                if (string.Equals(s, Arcade, StringComparison.OrdinalIgnoreCase)) set.AddRange(ArcadeMembers);
                else set.Add(s);
            }
            return set.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }
    }
}
