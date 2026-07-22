using System;
using System.Collections.Generic;
using System.Linq;

namespace MovieTheater.Arcade
{
    /// <summary>
    /// The curated, per-system list of libretro core options the per-game <b>config tool</b> exposes
    /// (the game modal's ⚙ Configure panel → <see cref="Db.ArcadeGameProfile"/>). This is the successor to
    /// the "quality modifier" cheats that used to live in <see cref="ArcadeCheatCatalog"/>: those toggles
    /// (DC/GC widescreen, PS2 ghosting-fix / deblur / super-sample, PS1 PGXP, …) are emulator options, not
    /// memory-poke cheats, so they moved here and the Cheats dropdown is now codes-only.
    ///
    /// <para>The catalog is <b>load-bearing as a validation allowlist</b>, not just UI sugar. libretro
    /// SILENTLY ignores an unknown option key AND an unknown value token (docs/arcade-cheats.md, and the
    /// five quality verdicts in docs/arcade-quality-plan.md) — a typo looks applied and does nothing. So
    /// the config-tool PUT accepts a (key, value) only if it appears here for that system, and the value
    /// tokens stored below are the core's EXACT tokens (extracted from each core's own
    /// <c>libretro_core_options.h</c> / DLL on Ziggy — never trust docs), e.g. <c>dolphin_efb_scale</c>
    /// takes <c>"1"</c>…<c>"6"</c> (not <c>"2x Native"</c>), <c>pcsx2_widescreen_hint</c>'s on-token is
    /// <c>"enabled (16:9)"</c> (not <c>"enabled"</c>), and PPSSPP's MSAA key is genuinely misspelled
    /// <c>ppsspp_mulitsample_level</c>.</para>
    ///
    /// <para>Flycast's prefix is <c>reicast_</c>, NOT <c>flycast_</c> — the core exposes zero real
    /// <c>flycast_*</c> keys.</para>
    ///
    /// <para>Delivery is per-room at Start: the controller reads the saved profile and injects these keys
    /// into the room's <c>CoreOptions</c> (the patch-0027 path), so a change takes effect on the next room
    /// with no worker-manifest regen. Renderer (GL/Vulkan) is handled separately via
    /// <see cref="Db.ArcadeGameProfile.HwContext"/>, not as a catalog option.</para>
    /// </summary>
    public static class ArcadeCoreOptionCatalog
    {
        /// <summary>A single allowed value of an option: the core's exact <paramref name="Token"/> plus a
        /// friendly <paramref name="Label"/> for the UI. For a free integer range, leave Values empty and
        /// mark <see cref="CoreOption.IsRange"/>.</summary>
        public sealed record OptionValue(string Token, string Label);

        /// <summary>Broad grouping the config UI lays options out by.</summary>
        public static class Category
        {
            public const string Video = "video";           // resolution / scaling / aspect / widescreen / filtering / AA
            public const string Performance = "performance"; // frameskip / cpu / speed / threads
            public const string Hack = "hack";             // rendering / compat hacks (edge-case, can glitch)
            public const string System = "system";         // region / broadcast / bootmode
            public const string Audio = "audio";
        }

        /// <summary>One configurable core option.</summary>
        public sealed record CoreOption(
            string Key,
            string Label,
            string Category,
            IReadOnlyList<OptionValue> Values,
            string Default,
            string? Note = null,
            bool IsRange = false,
            int RangeMin = 0,
            int RangeMax = 0)
        {
            public bool IsValidToken(string? token) =>
                token != null && (IsRange
                    ? int.TryParse(token, out var n) && n >= RangeMin && n <= RangeMax
                    : Values.Any(v => string.Equals(v.Token, token, StringComparison.Ordinal)));
        }

        private static OptionValue V(string token, string label) => new(token, label);
        private static OptionValue OnOff(bool on) => on ? V("enabled", "On") : V("disabled", "Off");
        private static readonly IReadOnlyList<OptionValue> EnabledDisabled = new[] { OnOff(false), OnOff(true) };

        // ── Per-system catalog ────────────────────────────────────────────────────────────────────────
        // The entries below are the "quality modifier" toggles relocated from ArcadeCheatCatalog.SystemOptions
        // (plus the PS2 widescreen/no-interlacing pair) — same keys/values/notes, so behaviour is unchanged;
        // they just live in the config tool now instead of the cheat dropdown. Their value tokens are the
        // ones already proven in the codebase. The broader per-core option set (internal resolution,
        // anisotropic/texture filtering, region/broadcast, frameskip, …) is layered in from the on-disk
        // core-option extraction — see AddExtracted() and docs/arcade-per-game-config.md. Do NOT hand-guess
        // resolution/enum tokens here: libretro silently ignores a wrong token, so an unverified value would
        // be a toggle that does nothing.
        private static readonly Dictionary<string, List<CoreOption>> ByCore = new(StringComparer.OrdinalIgnoreCase)
        {
            // Dreamcast / NAOMI / Atomiswave — flycast (reicast_ prefix).
            ["flycast"] = new()
            {
                new CoreOption("reicast_widescreen_cheats", "Widescreen (16:9)", Category.Video,
                    EnabledDisabled, "disabled",
                    "Only affects games flycast ships a widescreen cheat for; harmless otherwise."),
            },

            // GameCube — dolphin.
            ["dolphin"] = new()
            {
                new CoreOption("dolphin_widescreen_hack", "Widescreen hack (16:9)", Category.Hack,
                    EnabledDisabled, "disabled",
                    "Stretches the view to 16:9. May show graphical glitches at the screen edges."),
            },

            // PlayStation 2 — LRPS2 / pcsx2.
            ["pcsx2"] = new()
            {
                // Per-game widescreen patch: default-on lives in the ArcadeCheat rows (only the ~150 titles the
                // core can patch), so the config tool's effective value is computed per game — see the
                // controller's ResolveGameCoreOptions. Token is the core's EXACT enum, not "enabled".
                new CoreOption("pcsx2_widescreen_hint", "Widescreen (16:9)", Category.Video,
                    new[] { OnOff(false), V("enabled (16:9)","On (16:9)"), V("enabled (16:10)","On (16:10)"),
                            V("enabled (21:9)","On (21:9)"), V("enabled (32:9)","On (32:9)") },
                    "disabled", "Only does something on games the emulator ships a widescreen patch for."),
                new CoreOption("pcsx2_nointerlacing_hint", "No interlacing (sharper)", Category.Video,
                    EnabledDisabled, "disabled",
                    "Removes the interlace shimmer. Can shake the picture in a few games."),
                new CoreOption("pcsx2_half_pixel_offset", "Fix ghosting / double image", Category.Hack,
                    new[] { V("Native","Off (Native)"), V("Align to Native","Align to Native") },
                    "Native",
                    "For games that smear or ghost when upscaled. Replaces this game's automatic per-game fixes — set back to Off if anything looks worse."),
                new CoreOption("pcsx2_pgs_deblur", "Sharper picture (deblur)", Category.Hack,
                    EnabledDisabled, "disabled",
                    "Sharpens games that blur their final image with extra blit passes. Experimental — turn off if anything renders wrong."),
                new CoreOption("pcsx2_pgs_ss_tex", "Super-sample textures", Category.Hack,
                    EnabledDisabled, "disabled",
                    "Feeds higher-resolution textures back into rendering. Highly experimental — may glitch."),
            },

            // PlayStation 1 — Beetle PSX HW (the enhanced-res / PGXP path). pcsx_rearmed (PS1's other,
            // pre-Vulkan core) has no hand entries; its options come from the extraction.
            ["beetle_psx_hw"] = new()
            {
                new CoreOption("beetle_psx_hw_pgxp_mode", "Stable 3D geometry (PGXP)", Category.Hack,
                    new[] { OnOff(false), V("memory only","On (safe)"), V("memory + CPU","On (aggressive)") },
                    "disabled",
                    "Fixes PS1 3D wobble/warping. Most 3D games look better; the aggressive mode breaks some games. No effect on 2D games."),
            },
        };

        // Which core a system maps to for the config module. Multi-core systems (ps1: Beetle + pcsx_rearmed)
        // are resolved by the selected RENDER PROFILE's OptionCore in the controller; this map is the fallback
        // (a system's primary/default core) for non-hw-toggle systems and for gating the Configure button.
        private static readonly Dictionary<string, string> SystemDefaultCore = new(StringComparer.OrdinalIgnoreCase)
        {
            ["dc"] = "flycast", ["naomi"] = "flycast", ["atomiswave"] = "flycast",
            ["gc"] = "dolphin", ["wii"] = "dolphin",
            ["ps2"] = "pcsx2", ["psp"] = "ppsspp", ["n64"] = "mupen64plus_next",
            ["ps1"] = "beetle_psx_hw",
            ["nes"] = "nestopia", ["fds"] = "nestopia", ["snes"] = "snes9x",
            ["genesis"] = "genesis_plus_gx", ["sms"] = "genesis_plus_gx", ["gg"] = "genesis_plus_gx", ["segacd"] = "genesis_plus_gx",
            ["sega32x"] = "picodrive", ["gb"] = "mgba", ["gbc"] = "mgba", ["gba"] = "mgba",
            ["arcade"] = "fbneo", ["neogeo"] = "fbneo", ["saturn"] = "kronos", ["dos"] = "dosbox_pure",
        };

        /// <summary>The core a system's config maps to by default (null if the system isn't mapped).</summary>
        public static string? CoreForSystem(string? system) =>
            system != null && SystemDefaultCore.TryGetValue(system, out var c) ? c : null;

        // Keys already defined by hand above (the relocated cheat toggles), so the extraction pass never
        // duplicates or overwrites them — their labels/notes are hand-tuned for players.
        private static bool Has(string core, string key) =>
            ByCore.TryGetValue(core, out var list) && list.Any(o => o.Key == key);

        /// <summary>Add an extracted option to a CORE unless the key is already hand-defined for it. Called
        /// once at startup from the embedded core-option catalog so the tool exposes every option each core
        /// supports without hand-maintaining the bulk. Tokens here are the core's exact tokens.</summary>
        public static void AddExtracted(string core, CoreOption option)
        {
            if (Has(core, option.Key)) return;
            if (!ByCore.TryGetValue(core, out var list)) ByCore[core] = list = new List<CoreOption>();
            if (list.Any(o => o.Key == option.Key)) return;
            list.Add(option);
        }

        // On first use, fold in the extracted per-core option set (the full list of what each core supports)
        // from the committed, embedded core-options-catalog.json. Hand-defined keys above always win. The
        // load is best-effort: a missing/malformed resource (e.g. in the unit-test assembly, which links this
        // file without the resource) leaves the hand-tuned catalog intact rather than throwing at startup.
        static ArcadeCoreOptionCatalog()
        {
            try { LoadExtracted(); }
            catch { /* never let catalog init throw — the hand-defined entries still work */ }
        }

        private static void LoadExtracted()
        {
            var asm = typeof(ArcadeCoreOptionCatalog).Assembly;
            var name = asm.GetManifestResourceNames()
                .FirstOrDefault(n => n.EndsWith("core-options-catalog.json", StringComparison.OrdinalIgnoreCase));
            if (name == null) return;
            using var stream = asm.GetManifestResourceStream(name);
            if (stream == null) return;

            using var doc = System.Text.Json.JsonDocument.Parse(stream);
            // Extraction shape: { "cores": { "flycast": { "options": [ {..} ] }, ... } }. Only quality-relevant
            // options are folded in (the module isn't a raw dump); renderer-selecting keys are excluded (the
            // render-profile selector owns them). A core with no entry here simply shows the hand-tuned set.
            if (!doc.RootElement.TryGetProperty("cores", out var cores)
                || cores.ValueKind != System.Text.Json.JsonValueKind.Object) return;

            foreach (var core in cores.EnumerateObject())
            {
                if (!core.Value.TryGetProperty("options", out var opts) || opts.ValueKind != System.Text.Json.JsonValueKind.Array)
                    continue;
                foreach (var o in opts.EnumerateArray())
                {
                    // Only quality-relevant options, and never a renderer-selecting key (profile owns those).
                    if (o.TryGetProperty("qualityRelevant", out var qr) && qr.ValueKind == System.Text.Json.JsonValueKind.False)
                        continue;
                    var opt = ParseExtracted(o);
                    if (opt != null && !RendererSelectingKeys.Contains(opt.Key)) AddExtracted(core.Name, opt);
                }
            }
        }

        // Renderer-selecting keys are driven by the render-profile selector, never shown as plain options.
        private static readonly HashSet<string> RendererSelectingKeys = new(StringComparer.Ordinal)
        {
            "mupen64plus-rdp-plugin", "mupen64plus-rsp-plugin", "pcsx2_renderer", "beetle_psx_hw_renderer",
        };

        // Per-option JSON shape: { "key","label","category","note","default",
        //                          "values":[{"token","label"}], "isRange","rangeMin","rangeMax" }
        private static CoreOption? ParseExtracted(System.Text.Json.JsonElement o)
        {
            if (!o.TryGetProperty("key", out var keyEl)) return null;
            var key = keyEl.GetString();
            if (string.IsNullOrWhiteSpace(key)) return null;

            string Str(string p, string fallback = "") =>
                o.TryGetProperty(p, out var e) && e.ValueKind == System.Text.Json.JsonValueKind.String ? e.GetString()! : fallback;
            bool Bool(string p) => o.TryGetProperty(p, out var e) && e.ValueKind == System.Text.Json.JsonValueKind.True;
            int Int(string p) => o.TryGetProperty(p, out var e) && e.TryGetInt32(out var n) ? n : 0;

            var values = new List<OptionValue>();
            if (o.TryGetProperty("values", out var vals) && vals.ValueKind == System.Text.Json.JsonValueKind.Array)
                foreach (var v in vals.EnumerateArray())
                {
                    var token = v.TryGetProperty("token", out var t) ? t.GetString() : null;
                    if (token == null) continue;
                    var label = v.TryGetProperty("label", out var l) && l.ValueKind == System.Text.Json.JsonValueKind.String
                        ? l.GetString()! : token;
                    values.Add(new OptionValue(token, label));
                }

            var category = Str("category", Category.Video);
            var isRange = Bool("isRange");
            if (!isRange && values.Count == 0) return null; // an enum option with no tokens is unusable

            // The extraction names the description "desc"; accept "note" too. Blank → no note.
            var note = Str("note", Str("desc", ""));
            return new CoreOption(key!, Str("label", key!), category, values,
                Str("default", values.Count > 0 ? values[0].Token : ""),
                string.IsNullOrWhiteSpace(note) ? null : note,
                isRange, Int("rangeMin"), Int("rangeMax"));
        }

        /// <summary>The options a CORE offers (empty if none catalogued).</summary>
        public static IReadOnlyList<CoreOption> ForCore(string? core) =>
            core != null && ByCore.TryGetValue(core, out var o) ? o : Array.Empty<CoreOption>();

        /// <summary>Look up one option on a CORE.</summary>
        public static CoreOption? Find(string? core, string key) =>
            ForCore(core).FirstOrDefault(o => string.Equals(o.Key, key, StringComparison.Ordinal));

        /// <summary>The options a SYSTEM shows by default (its default core's options) — the convenience for
        /// non-hw-toggle systems and tests. Multi-core systems resolve the exact core by render profile.</summary>
        public static IReadOnlyList<CoreOption> For(string? system) => ForCore(CoreForSystem(system));

        /// <summary>Whether any of the system's config surfaces has options (gates the Configure button; the
        /// controller ORs this with the hw-toggle renderer choice).</summary>
        public static bool HasAnything(string? system) => For(system).Count > 0;
    }
}
