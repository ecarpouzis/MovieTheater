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

        // Systems whose configured core implements retro_cheat_set for REAL, established by disassembling the
        // exported symbol in each core DLL (2026-07-09): a stub's first instruction is `ret`. That test is the
        // only honest one available — the API is mandatory, so every core exports the symbol, and a stub
        // accepts a code and silently discards it. Do not add a system on reasoning alone; run the probe.
        //
        //   REAL: mupen64plus_next (n64), pcsx_rearmed (ps1), snes9x (snes), nestopia (nes/fds),
        //         genesis_plus_gx (genesis/sms/gg/segacd), picodrive (sega32x), mgba (gb/gbc/gba).
        //   STUB: pcsx2, flycast, fbneo, stella, mednafen_pce.
        //
        // Deliberately ABSENT, each for a reason worth keeping written down:
        //   ps2 (pcsx2), dc/naomi/atomiswave (flycast), arcade/neogeo (fbneo) — CONFIRMED stubs. They read
        //     their own cheat formats (pnach) or carry internal cheat engines instead.
        //   pce (mednafen_pce) — a CONFIRMED stub, and it was wrongly allowlisted here at first on the
        //     assumption that "the mednafen cores implement it". They don't. 621 rows across 173 games were
        //     imported and offered as toggles that could never do anything before the probe caught it.
        //   gc (dolphin), psp (ppsspp) — their retro_cheat_set is REAL, but gc has no upstream cht folder and
        //     psp's is unverified end-to-end. Candidates, not entries.
        //   a2600/a7800/lynx/vb/wsc/ngpc — cht folders exist upstream; stella is a confirmed stub and the
        //     others are unprobed.
        private static readonly HashSet<string> CodeCapable = new(StringComparer.OrdinalIgnoreCase)
        {
            "nes", "fds", "snes", "genesis", "sms", "gg", "segacd", "sega32x",
            "gb", "gbc", "gba", "n64", "ps1",
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
            ["ps1"] = "Sony - PlayStation",
        };

        public static string? ChtFolder(string? system) =>
            system != null && ChtFolders.TryGetValue(system, out var f) ? f : null;

        /// <summary>Every system we can import cheat codes for (has both a core that applies them and an
        /// upstream cht folder).</summary>
        public static IEnumerable<string> CodeSystems => ChtFolders.Keys.Where(SupportsCheatCodes);

        // ── Curated core-option cheats ──────────────────────────────────────────────────────────────────
        // The system-wide "quality modifier" option cheats (DC/GC widescreen, PS2 ghosting-fix/deblur/
        // super-sample, PS1 PGXP) used to live here and be synthesized into the cheat picker. They MOVED to
        // ArcadeCoreOptionCatalog and the game modal's ⚙ Configure panel — the Cheats dropdown is now
        // codes-only. What remains here is the OptionCheat shape + the PS2 per-game widescreen/no-interlacing
        // pair, because arcade-cheats-import still writes those as per-game ArcadeCheat "option" rows (only the
        // ~150 titles the core can patch) and the config tool reads those rows to compute its effective value.
        public sealed record OptionCheat(string Key, string Value, string Name, bool DefaultOn, string? Note);

        // ── Implied companion options (master switches) ───────────────────────────────────────────────
        // Some option cheats are read by the core only behind a gate option: pcsx2_half_pixel_offset does
        // nothing unless pcsx2_enable_hw_hacks is on (the core's own description of that switch: "This will
        // disable automatic settings from the database" — i.e. the gate trades the GameDB auto-fixes for the
        // manual ones, per room). The catalog owns the mapping so every caller applies the same rule; an
        // explicitly picked value for the gate key always wins over an implied one.
        private static readonly Dictionary<string, (string Key, string Value)[]> Implied = new(StringComparer.Ordinal)
        {
            ["pcsx2_half_pixel_offset"] = new[] { ("pcsx2_enable_hw_hacks", "enabled") },
            // Same gate: every "HW Hacks >" option in the core is read only when the master switch is
            // on. Learned live 2026-07-09: a nativeScaling-only room shipped without the gate (the
            // player unticked the HPO cheat that used to carry it) and the experiment ran inert.
            ["pcsx2_native_scaling"] = new[] { ("pcsx2_enable_hw_hacks", "enabled") },
        };

        /// <summary>Companion options a picked option cheat needs to actually take effect. Callers merge
        /// these into the room's core options for keys not already set explicitly.</summary>
        public static IReadOnlyList<(string Key, string Value)> ImpliedOptionsFor(string optionKey) =>
            Implied.TryGetValue(optionKey, out var i) ? i : Array.Empty<(string, string)>();

        // ── The PS2 per-game option cheats the import writes (see docs/arcade/ps2-core-patches.tsv) ──────
        // NOTE the widescreen VALUE: libretro silently ignores an unrecognized option value, and this one is
        // NOT the usual "enabled" — the core declares "enabled (16:9)" / "(16:10)" / "(21:9)" / "(32:9)".
        public static readonly OptionCheat Ps2Widescreen = new(
            "pcsx2_widescreen_hint", "enabled (16:9)", "Widescreen (16:9)", true,
            "This game has a widescreen patch built into the emulator. Turn off for the original 4:3.");

        public static readonly OptionCheat Ps2NoInterlacing = new(
            "pcsx2_nointerlacing_hint", "enabled", "No interlacing (sharper)", false,
            "Removes the interlace shimmer. Can shake the picture in a few games.");
    }
}
