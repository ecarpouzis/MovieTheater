using System;
using System.Collections.Generic;
using System.Linq;

namespace MovieTheater.Arcade
{
    /// <summary>
    /// The launch-path half of the cross-core hygiene the config module already has: <b>a room is handed
    /// only the option keys its booting core can read.</b>
    ///
    /// <para>An <see cref="Db.ArcadeGameProfile"/> is one flat blob per title, and on a multi-core system
    /// it legitimately holds BOTH cores' keys — Last Impact carries <c>parallel-n64-*</c> for its pinned
    /// Glide64 profile AND the <c>mupen64plus-*</c> twins so the fix survives a forced mupen launch. That
    /// is correct storage; delivering the whole blob to every room is not: the 2026-08-02 worker-log sweep
    /// (docs/arcade/opt-reconcile-evidence-2026-08-02.md) showed every such room booting with the OTHER
    /// core's keys in its option set, dead on arrival (`[opt] DEAD keys`), which makes a room's real
    /// configuration unreadable and buries genuine reconcile signal in known noise.</para>
    ///
    /// <para>The drop test is deliberately narrow — a key is withheld only when the catalog KNOWS it
    /// belongs to a different core reachable from this system's profiles AND the booting core's catalog
    /// does not claim it. Everything else passes: hand-entered Advanced keys (unknown everywhere),
    /// renderer-selecting keys (in no catalog by design — and the temporary-DB-row diagnostic mechanism
    /// the Phase 3 boot tests used rides on them passing), and every key of the booting core itself,
    /// including profile-delivered ones like <c>parallel-n64-gliden64-*</c> that only exist on hand-only
    /// cores. Single-core systems reduce to a no-op.</para>
    /// </summary>
    public static class ArcadeRoomOptionDelivery
    {
        /// <summary>Filter a room's merged option set down to what the booting core can read.
        /// <paramref name="bootingCore"/> is the render profile's OptionCore (null → the system's default
        /// core). Returns the filtered set plus the dropped keys for the caller to log.</summary>
        public static (Dictionary<string, string> Options, List<string> Dropped) FilterForBootingCore(
            string? system, string? bootingCore, IReadOnlyDictionary<string, string> options)
        {
            var core = bootingCore ?? ArcadeCoreOptionCatalog.CoreForSystem(system);
            var otherCores = ArcadeRendererProfiles.For(system)
                .Select(p => p.OptionCore)
                .Append(ArcadeCoreOptionCatalog.CoreForSystem(system))
                .Where(c => c != null && !string.Equals(c, core, StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            var filtered = new Dictionary<string, string>(StringComparer.Ordinal);
            var dropped = new List<string>();
            foreach (var (key, value) in options)
            {
                var foreign = ArcadeCoreOptionCatalog.Find(core, key) == null
                              && otherCores.Any(c => ArcadeCoreOptionCatalog.Find(c, key) != null);
                if (foreign) dropped.Add(key);
                else filtered[key] = value;
            }
            return (filtered, dropped);
        }
    }
}
