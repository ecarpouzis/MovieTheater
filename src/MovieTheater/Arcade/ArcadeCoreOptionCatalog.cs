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
            public const string Input = "input";           // cursor / stick feel — how the pad drives the game
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

            // Nintendo DS — melonDS DS (melonds_ prefix). ONLY the internal-resolution lever is exposed:
            // render_mode is deliberately NOT here (opengl is load-bearing — the software renderer can't
            // upscale AND the stylus/touch path is wired for the GL frame; changing it would break both).
            // Default 4 matches config.worker-gl.yaml. Integer range 1..8 (the core's own bounds); a bad
            // token silently no-ops in libretro, so the range guard is the validation.
            ["melondsds"] = new()
            {
                new CoreOption("melonds_opengl_resolution", "Internal resolution (upscale)", Category.Video,
                    Array.Empty<OptionValue>(), "4",
                    "1 = native (256x192/screen), up to 8x. Higher = sharper 3D, bigger stream. Default 4x.",
                    IsRange: true, RangeMin: 1, RangeMax: 8),
            },

            // Nintendo 3DS — citra (citra_ prefix). Same policy: expose only the resolution factor;
            // citra_graphics_api stays pinned to OpenGL (touch needs the GL MouseTracker) and is NOT here.
            // Tokens are citra's EXACT value list ("1x (Native)", "2x", …) — an enum, not a plain int.
            // Default "2x" matches config.worker-gl.yaml.
            ["citra"] = new()
            {
                new CoreOption("citra_resolution_factor", "Internal resolution (upscale)", Category.Video,
                    new[]
                    {
                        V("1x (Native)", "1x (Native)"), V("2x", "2x"), V("3x", "3x"), V("4x", "4x"),
                        V("5x", "5x"), V("6x", "6x"), V("7x", "7x"), V("8x", "8x"), V("9x", "9x"),
                        V("10x", "10x"),
                    }, "2x",
                    "Renders the 3D at N× the 3DS's native resolution. Higher = sharper, bigger stream. Default 2x."),
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

            // N64 alternate core — parallel_n64 (the compatibility profile; picked via the render-profile
            // selector, core-key "parallel_n64"). Its renderer (gfxplugin/rspplugin) is owned by the profile
            // and excluded below; these are the player-facing levers. Tokens verified from
            // parallel_n64_libretro.dll (2026-07-23). The startup extraction folds in the full option set on
            // top of these hand-tuned few (and the unit-test assembly, which has no embedded json, sees only
            // these — so every preset key/token for this core MUST appear here).
            ["parallel_n64"] = new()
            {
                // Full token list verified against libretro_core_options.h 2026-07-29 (10 values; the four
                // above 1920x1440 were absent here). This is the SHARPNESS lever on the GL plugins: they do
                // not supersample, so at 640x480 the pipeline's scale:2 is a pure 2x BLUR up to the
                // 1280x960 delivery. Raising this renders at delivery size instead of upscaling a small
                // frame. Vulkan/paraLLEl-RDP ignores it and uses parallel-rdp-upscaling instead.
                new CoreOption("parallel-n64-screensize", "Internal resolution", Category.Video,
                    new[] { V("320x240","1x (native, fastest)"), V("640x480","2x"), V("960x720","3x"),
                            V("1280x960","4x"), V("1440x1080","4.5x"), V("1600x1200","5x"),
                            V("1920x1440","6x"), V("2240x1680","7x"), V("2880x2160","9x"),
                            V("5760x4320","18x (absurd, will not stream)") },
                    "640x480",
                    "How large the game renders internally before the stream scales it. Higher is sharper but heavier."),
                // dynamic_recompiler_ari64 added 2026-07-29 — a 4th token this core really exposes
                // (verified in libretro_core_options.h) that was missing here, so it was unreachable.
                new CoreOption("parallel-n64-cpucore", "CPU core", Category.Performance,
                    new[] { V("pure_interpreter","Pure interpreter (most accurate)"),
                            V("cached_interpreter","Cached interpreter (balanced)"),
                            V("dynamic_recompiler","Dynamic recompiler (fastest)"),
                            V("dynamic_recompiler_ari64","Dynamic recompiler — ari64 (alternate JIT)") },
                    "cached_interpreter",
                    "Accuracy vs speed of the emulated CPU. Cached interpreter is the safe default for romhacks."),
                new CoreOption("parallel-n64-filtering", "Texture filtering", Category.Video,
                    new[] { V("automatic","Automatic"), V("N64 3-point","N64 3-point (authentic)"),
                            V("nearest","Nearest (crisp)"), V("bilinear","Bilinear (smooth)") },
                    "automatic",
                    "How textures are smoothed. 3-point matches real N64 hardware."),
                // paraLLEl-RDP (Vulkan renderer) supersampling — the fidelity lever on the Vulkan profile,
                // like mupen's parallel-rdp-upscaling. Inert on the GLideN64 (GL) profile. Tokens verified.
                new CoreOption("parallel-n64-parallel-rdp-upscaling", "Supersampling (Vulkan)", Category.Video,
                    new[] { V("1x","Off (native)"), V("2x","2x"), V("4x","4x"), V("8x","8x (sharpest)") },
                    "8x",
                    "Renders at a multiple of native then downsamples — anti-aliasing on the paraLLEl-RDP (Vulkan) renderer."),
                // ── ADDED 2026-07-29. Tokens + DEFAULTS read from the CORE SOURCE
                // (parallel-n64 libretro/libretro_core_options.h), not guessed and not scraped from the
                // DLL string table — a string-table read of screensize above gave a WRONG list (it
                // surfaced a legacy 320x200/400x256/800x600 set and appeared to cap at 960x720, when the
                // real list runs to 5760x4320). Read the source, or the option silently does nothing.
                //
                // THE SPEED/ACCURACY DIAL FOR THE GL PLUGINS, and it defaults to the most expensive
                // setting. Nothing in our stack ever set it, so every GLideN64/Glide64/rice room has been
                // paying veryhigh. Suspected cause of the heavy frame-rate collapse measured on the
                // Glide64 path (video/s falling to 9.6 with ticks/s pinned at 60, i.e. the emulator idle
                // while frames stop arriving). Inert on the paraLLEl-RDP (Vulkan) profile.
                new CoreOption("parallel-n64-gfxplugin-accuracy", "Graphics accuracy (GL plugins)", Category.Performance,
                    new[] { V("low","Low (fastest)"), V("medium","Medium"), V("high","High"),
                            V("veryhigh","Very high (core default, slowest)") },
                    "veryhigh",
                    "Accuracy vs speed of the OpenGL graphics plugins (GLideN64 / Glide64 / rice). Lower it if the game chugs; it does nothing on the Vulkan renderer."),
                // The romhack compatibility switch. Real on OUR build only: upstream declares this option
                // and assigns it, but NOTHING READS IT (dead code) — our patched core wires it into
                // pi_controller.c. SM64: Last Impact needs it or the emulated CPU dies ~11 s into boot.
                // Core default is True (permissive), matching upstream's own advertised default.
                new CoreOption("parallel-n64-allow-unaligned-dma", "Allow unaligned DMA (romhacks)", Category.System,
                    new[] { V("True","Allow unaligned (romhack compatible)"), V("False","Force alignment (hardware accurate)") },
                    "True",
                    "Honours odd PI cart addresses instead of force-aligning them. Required by some romhacks; hardware-accurate alignment breaks them."),
            },

            // ── ScummVM. Point-and-click games driven by a GAMEPAD, so the only settings worth a
            // player's time are how the cursor feels — there is no resolution/upscale lever at all
            // (software 2D renderer; sharpness is the config-level `scale`, not per-room deliverable).
            // Tokens + defaults verified against the DEPLOYED scummvm_libretro.dll's own option table
            // (660e13b0-2026.2.1git), not just the upstream header.
            //
            // ⚠ DELIBERATELY NOT EXPOSED:
            //  • scummvm_video_hw_acceleration — load-bearing system default. It MUST stay "disabled":
            //    the core's OpenGL mode sends RETRO_HW_FRAME_BUFFER_VALID on a software-armed room with
            //    no GL context behind it, which crashed the worker (2026-07-18). Not a taste knob.
            //  • scummvm_pointer_device — plumbing, not feel: "retropad" removes mouse control outright,
            //    and which device our browser client drives is a system-level decision. Its core default
            //    is also platform-conditional, so a catalog default here would be a guess — and a wrong
            //    catalog default silently turns "left alone" into a stored override.
            //  • scummvm_samplerate — we run the room at 48 kHz; a per-game mismatch is an audio-drift
            //    bug waiting to happen, not a setting.
            //  • scummvm_gui_h_res / gui_aspect_ratio — the ScummVM launcher's own GUI, which players
            //    never see (we autoload the target straight from the .scummvm hook).
            //  • scummvm_mapper_* — button mapping belongs to the site's input layer, not per game.
            ["scummvm"] = new()
            {
                new CoreOption("scummvm_gamepad_cursor_speed", "Cursor speed (stick / D-pad)", Category.Input,
                    new[] { V("0.25","0.25x (slowest)"), V("0.5","0.5x"), V("0.75","0.75x"), V("1.0","1x (default)"),
                            V("1.5","1.5x"), V("2.0","2x"), V("2.5","2.5x"), V("3.0","3x (fastest)") },
                    "1.0",
                    "How fast the stick moves the mouse cursor. 1x suits 320x200 games; the core recommends 2x " +
                    "for 640x480 ones (Myst, Grim Fandango, the later CD talkies)."),
                new CoreOption("scummvm_gamepad_cursor_acceleration_time", "Cursor acceleration", Category.Input,
                    new[] { V("off","Off (instant full speed)"), V("0.1","0.1 s"), V("0.2","0.2 s (default)"),
                            V("0.3","0.3 s"), V("0.4","0.4 s"), V("0.5","0.5 s"), V("0.6","0.6 s"),
                            V("0.7","0.7 s"), V("0.8","0.8 s"), V("0.9","0.9 s"), V("1.0","1.0 s (slowest ramp)") },
                    "0.2",
                    "How long the cursor takes to reach full speed. Off is twitchier; a longer ramp makes " +
                    "small pixel-hunting movements easier."),
                new CoreOption("scummvm_analog_response", "Stick response curve", Category.Input,
                    new[] { V("linear","Linear"), V("quadratic","Quadratic (fine control near centre)") },
                    "linear",
                    "Quadratic gives slow, precise movement for small stick deflections and full speed at the edge."),
                new CoreOption("scummvm_analog_deadzone", "Stick deadzone", Category.Input,
                    new[] { V("0","0%"), V("5","5%"), V("10","10%"), V("15","15% (default)"),
                            V("20","20%"), V("25","25%"), V("30","30%") },
                    "15",
                    "Ignore stick movement below this much deflection. Raise it if the cursor drifts on its own."),
                new CoreOption("scummvm_mouse_speed", "Mouse speed", Category.Input,
                    new[] { V("0.05","0.05x"), V("0.1","0.1x"), V("0.15","0.15x"), V("0.2","0.2x"), V("0.25","0.25x"),
                            V("0.3","0.3x"), V("0.35","0.35x"), V("0.4","0.4x"), V("0.45","0.45x"), V("0.5","0.5x"),
                            V("0.6","0.6x"), V("0.7","0.7x"), V("0.8","0.8x"), V("0.9","0.9x"), V("1.0","1x (default)"),
                            V("1.25","1.25x"), V("1.5","1.5x"), V("1.75","1.75x"), V("2.0","2x"), V("2.5","2.5x"),
                            V("3.0","3x") },
                    "1.0",
                    "Cursor speed for a real mouse (as opposed to the stick)."),
                new CoreOption("scummvm_mouse_fine_control_speed_reduction", "Fine-control slowdown", Category.Input,
                    new[] { V("2","50% speed"), V("4","25% speed (default)"), V("10","10% speed") },
                    "4",
                    "How far the cursor slows while the fine-control button is held, for exact clicks on small hotspots."),
                new CoreOption("scummvm_framerate", "Frame rate cap", Category.Performance,
                    new[] { V("disabled","Uncapped (default)"), V("60 Hz","60 Hz"), V("50 Hz","50 Hz"),
                            V("30 Hz","30 Hz"), V("25 Hz","25 Hz") },
                    "disabled",
                    "Upper limit on the core's frame rate. The stream runs at 60, so a cap only helps if a " +
                    "game's own timing misbehaves uncapped."),
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
            ["nds"] = "melondsds", ["3ds"] = "citra",
            ["scummvm"] = "scummvm",
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
            // Extraction shape: { "cores": { "flycast": { "options": [ {..} ] }, ... } }. EVERY core option is
            // folded in (the tool mirrors what a desktop emulator config exposes — no quality-relevance filter);
            // only the renderer-selecting keys are excluded, because the render-profile selector owns those. A
            // core with no entry here simply shows the hand-tuned set.
            if (!doc.RootElement.TryGetProperty("cores", out var cores)
                || cores.ValueKind != System.Text.Json.JsonValueKind.Object) return;

            foreach (var core in cores.EnumerateObject())
            {
                if (!core.Value.TryGetProperty("options", out var opts) || opts.ValueKind != System.Text.Json.JsonValueKind.Array)
                    continue;
                foreach (var o in opts.EnumerateArray())
                {
                    var opt = ParseExtracted(o);
                    if (opt != null && !RendererSelectingKeys.Contains(opt.Key)) AddExtracted(core.Name, opt);
                }
            }
        }

        // Renderer-selecting keys are driven by the render-profile selector, never shown as plain options.
        private static readonly HashSet<string> RendererSelectingKeys = new(StringComparer.Ordinal)
        {
            "mupen64plus-rdp-plugin", "mupen64plus-rsp-plugin", "pcsx2_renderer", "beetle_psx_hw_renderer",
            // parallel_n64: gfxplugin/rspplugin ARE the renderer (GLideN64 vs angrylion/parallel) — owned by
            // the render-profile selector, never shown as a plain option (angrylion would panic CloudRetro).
            "parallel-n64-gfxplugin", "parallel-n64-rspplugin",
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

            // A few extracted entries declare a default that is not one of their own value tokens
            // (dolphin_log_level "Info" vs tokens "1".."4", …) — an extraction quirk. Coerce to the
            // first token: an invalid default would flow into the config GET's effective values and
            // make the PUT reject an UNCHANGED save ("'Info' is not a valid value").
            var def = Str("default", values.Count > 0 ? values[0].Token : "");
            if (!isRange && values.Count > 0 && !values.Any(v => string.Equals(v.Token, def, StringComparison.Ordinal)))
                def = values[0].Token;

            return new CoreOption(key!, Str("label", key!), category, values, def,
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
