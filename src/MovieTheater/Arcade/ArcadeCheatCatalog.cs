using System;
using System.Collections.Generic;
using System.Linq;

namespace MovieTheater.Arcade
{
    /// <summary>
    /// What kinds of cheat each system can actually apply, and the small set of curated core-option cheats
    /// that aren't per-game data. See docs/arcade-cheats.md.
    ///
    /// <para>Cheats reach the emulator two ways, and the split is not cosmetic:</para>
    /// <list type="number">
    ///   <item><b>Cheat codes</b> go through the libretro cheat API (<c>retro_cheat_set</c>). Only cores that
    ///     implement it can use them — the API is exported by every core, but plenty of cores export an empty
    ///     stub, in which case the code is accepted and does nothing. <see cref="SupportsCheatCodes"/> is a
    ///     deliberate allowlist rather than a "try it and see", so a system never advertises dead cheats.</item>
    ///   <item><b>Core options</b> switch on patches the emulator already ships (PS2's widescreen table).
    ///     No code, no database — but also no effect unless that emulator has a patch for that exact game,
    ///     which is why the PS2 entries are stored per game by <c>arcade-cheats-import</c> instead of listed
    ///     here.</item>
    /// </list>
    /// </summary>
    public static class ArcadeCheatCatalog
    {
        /// <summary>Hard cap on how many cheats one room may enable. Codes are raw memory pokes; a long list of
        /// them (especially conflicting ones from the same group) reliably wedges a game, and 941 of them are on
        /// offer for Mario Kart 64 alone. Enforced server-side, not just in the picker.</summary>
        public const int MaxCheatsPerRoom = 24;

        /// <summary>Cap on rows stored per ROM at import. Upstream files reach ~1,000 entries; past a few
        /// hundred the picker is a data dump and the payload stops being free. Truncation is REPORTED by the
        /// import, never silent.</summary>
        public const int MaxCheatsPerGame = 300;

        // Systems whose configured core implements retro_cheat_set for real. Verified live for n64/ps1/snes
        // (see docs/arcade-cheats.md "verification"); the rest share the same cores (mgba covers gb/gbc/gba,
        // genesis_plus_gx covers genesis/sms/gg/segacd, nestopia covers nes/fds) or are the well-known
        // mednafen/beetle implementations.
        //
        // Deliberately ABSENT, each for a reason worth keeping written down:
        //   ps2 (pcsx2), gc (dolphin), psp (ppsspp) — their cores ignore retro_cheat_set and read their own
        //     cheat formats (pnach / Gecko-AR INIs / cwcheat db) from disk. libretro-database has no cht
        //     folder for ps2 or gc either, so there is nothing to import even if they did.
        //   dc/naomi/atomiswave (flycast), arcade/neogeo (fbneo) — both carry INTERNAL cheat engines instead.
        //   a2600/a7800/lynx/vb/wsc/ngpc — cht folders exist upstream, but these cores' cheat support is
        //     unverified here. Adding one is a single line once someone confirms a code takes effect.
        private static readonly HashSet<string> CodeCapable = new(StringComparer.OrdinalIgnoreCase)
        {
            "nes", "fds", "snes", "genesis", "sms", "gg", "segacd", "sega32x",
            "gb", "gbc", "gba", "n64", "ps1", "pce",
        };

        public static bool SupportsCheatCodes(string? system) => system != null && CodeCapable.Contains(system);

        // System code → folder name in libretro-database/cht. These match the libretro-thumbnails repo names
        // (ArcadeBoxArt.ThumbRepo) because both come from the same community DAT naming, but they are listed
        // separately: the two sets differ (cht has no ps2/gc; thumbnails have no PC Engine CD), and silently
        // borrowing the other map would make a mismatch look like a matching failure.
        private static readonly Dictionary<string, string> ChtFolders = new(StringComparer.OrdinalIgnoreCase)
        {
            ["nes"] = "Nintendo - Nintendo Entertainment System",
            ["fds"] = "Nintendo - Family Computer Disk System",
            ["snes"] = "Nintendo - Super Nintendo Entertainment System",
            ["n64"] = "Nintendo - Nintendo 64",
            ["gb"] = "Nintendo - Game Boy",
            ["gbc"] = "Nintendo - Game Boy Color",
            ["gba"] = "Nintendo - Game Boy Advance",
            ["genesis"] = "Sega - Mega Drive - Genesis",
            ["sms"] = "Sega - Master System - Mark III",
            ["gg"] = "Sega - Game Gear",
            ["segacd"] = "Sega - Mega-CD - Sega CD",
            ["sega32x"] = "Sega - 32X",
            ["pce"] = "NEC - PC Engine - TurboGrafx 16",
            ["ps1"] = "Sony - PlayStation",
        };

        public static string? ChtFolder(string? system) =>
            system != null && ChtFolders.TryGetValue(system, out var f) ? f : null;

        /// <summary>Every system we can import cheat codes for (has both a core that applies them and an
        /// upstream cht folder).</summary>
        public static IEnumerable<string> CodeSystems => ChtFolders.Keys.Where(SupportsCheatCodes);

        // ── Curated core-option cheats ──────────────────────────────────────────────────────────────────
        // The PS2 pair is NOT here: pcsx2_widescreen_hint / pcsx2_nointerlacing_hint only do something for the
        // ~150 games in the core's own compiled-in patch table, so arcade-cheats-import writes them as per-game
        // rows (docs/arcade/ps2-core-patches.tsv) and pre-selects widescreen there. Offering them on every PS2
        // game would be a toggle that silently does nothing on most of the library.
        //
        // What's left are options that apply to a whole system. Both are OFF by default and say what they do:
        // neither is a per-game patch, so neither can be honestly pre-selected.
        public sealed record OptionCheat(string Key, string Value, string Name, bool DefaultOn, string? Note);

        private static readonly Dictionary<string, OptionCheat[]> SystemOptions = new(StringComparer.OrdinalIgnoreCase)
        {
            // flycast ships a built-in widescreen cheat table keyed by Dreamcast product id. We can't read that
            // table out of the DLL (it's a binary struct array, unlike PCSX2's, whose entries we recovered from
            // its log strings), so we can't tell per game whether it will fire — hence off by default and said
            // plainly, rather than pre-selected on a library where most games would see no change.
            ["dc"] = new[]
            {
                new OptionCheat("reicast_widescreen_cheats", "enabled", "Widescreen (16:9)", false,
                    "Only affects games flycast ships a widescreen cheat for; harmless otherwise."),
            },
            // Dolphin's is a rendering hack, not a per-game patch: it widens the projection for every game and
            // can reveal un-drawn geometry at the edges. Useful, but never a default.
            ["gc"] = new[]
            {
                new OptionCheat("dolphin_widescreen_hack", "enabled", "Widescreen hack (16:9)", false,
                    "Stretches the view to 16:9. May show graphical glitches at the screen edges."),
            },
        };

        /// <summary>System-wide option cheats, offered on every game of that system.</summary>
        public static IReadOnlyList<OptionCheat> SystemOptionCheats(string? system) =>
            system != null && SystemOptions.TryGetValue(system, out var o) ? o : Array.Empty<OptionCheat>();

        // ── The PS2 per-game option cheats the import writes (see docs/arcade/ps2-core-patches.tsv) ──────
        // NOTE the widescreen VALUE: libretro silently ignores an unrecognized option value, and this one is
        // NOT the usual "enabled" — the core declares "enabled (16:9)" / "(16:10)" / "(21:9)" / "(32:9)".
        public static readonly OptionCheat Ps2Widescreen = new(
            "pcsx2_widescreen_hint", "enabled (16:9)", "Widescreen (16:9)", true,
            "This game has a widescreen patch built into the emulator. Turn off for the original 4:3.");

        public static readonly OptionCheat Ps2NoInterlacing = new(
            "pcsx2_nointerlacing_hint", "enabled", "No interlacing (sharper)", false,
            "Removes the interlace shimmer. Can shake the picture in a few games.");

        /// <summary>Whether a cheat can be offered at all for a system — used to decide if the lobby card shows
        /// a cheat control before any per-game rows are known.</summary>
        public static bool AnyCheatsPossible(string? system) =>
            SupportsCheatCodes(system) || SystemOptionCheats(system).Count > 0 ||
            string.Equals(system, "ps2", StringComparison.OrdinalIgnoreCase);
    }
}
