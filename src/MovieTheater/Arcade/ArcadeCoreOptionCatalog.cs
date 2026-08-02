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
    /// <para>The bulk of the catalog is NOT hand-written: <c>core-options-catalog.json</c> is GENERATED from
    /// the deployed DLLs by <c>scripts/extract-core-options.ps1</c> (a runtime harness — it loads each core and
    /// captures the option structs the core itself hands back, which is the only way to read the real value
    /// tokens; a static read of the binary gives a WRONG list, because identical strings are pooled by the
    /// linker). Per-core disposition lives in <c>scripts/extract-core-options/policy.json</c>: <c>fold</c>
    /// (default) writes the core's options into the JSON, <c>hand-only</c> (parallel_n64, melondsds, citra,
    /// scummvm) means the entries below are the whole catalog for that core and regeneration must not touch
    /// it. Latest run: <c>docs/arcade/core-options-drift-2026-08-02.md</c>.</para>
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
            // parallel_n64_libretro.dll (2026-07-23; re-confirmed by the runtime harness 2026-08-02 — the
            // deployed DLL declares 94 options).
            //
            // ⚠ THIS CORE IS HAND-ONLY, AND THAT IS NOW OFFICIAL POLICY — scripts/extract-core-options/policy.json
            // marks it `hand-only`, so the generator extracts it for the drift report but writes NOTHING for it
            // into core-options-catalog.json. The list below is therefore the WHOLE catalog for this core.
            // (An earlier version of this comment claimed "the startup extraction folds in the full option set
            // on top of these hand-tuned few". It never did — there has never been a parallel_n64 block in the
            // JSON — and it must not, because the core DECLARES tokens that are broken through the mupen config
            // bridge; see the screensize caveat below. The comment was describing a fold that would have been a
            // bug if it had ever happened.)
            ["parallel_n64"] = new()
            {
                // Full token list verified against libretro_core_options.h 2026-07-29 (10 values; the four
                // above 1920x1440 were absent here). This is the SHARPNESS lever on the GL plugins: they do
                // not supersample, so at 640x480 the pipeline's scale:2 is a pure 2x BLUR up to the
                // 1280x960 delivery. Raising this renders at delivery size instead of upscaling a small
                // frame. Vulkan/paraLLEl-RDP ignores it and uses parallel-rdp-upscaling instead.
                new CoreOption("parallel-n64-screensize", "Internal resolution", Category.Video,
                    // ⚠ DELIBERATELY NOT the core's full token list. The core DECLARES 1440x1080, 2240x1680,
                    // 2880x2160 and 5760x4320, but the mupen config bridge that the HLE GL plugins read
                    // (`api/config.c`, the ScreenWidth/ScreenHeight translate table) does NOT list them —
                    // and on a miss `ConfigGetParamInt` falls through to a `return 0`, so glide64/rice get
                    // ScreenWidth=0. Those four are broken, not merely unavailable, under the Glide64
                    // profile we ship for romhacks. Only bridge-known tokens are offered here.
                    // For sharper output beyond 6x, use the Vulkan profile's supersampling, not this.
                    new[] { V("320x240","1x (native, fastest)"), V("640x480","2x"), V("960x720","3x"),
                            V("1280x960","4x (good default)"), V("1600x1200","5x"),
                            V("1920x1440","6x (sharpest safe)") },
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
                // real declared list runs to 5760x4320). Read the source, or the option silently does
                // nothing. Note the follow-on lesson: the DECLARED list is still not the USABLE list —
                // see the bridge caveat on screensize above.
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
                // MT-PATCHED OPTION (our build only): this core had NO counter-factor control at all — the
                // CountPerOp global existed and the core consumed it, but nothing ever set it. It is the
                // COP0 counter factor, i.e. how much emulated CPU work happens per counter tick, and it is
                // the difference between Last Impact running properly and Mario moving in slow motion while
                // the machine still reports a perfect 60 fps (the emulated CPU is throttled, not the box).
                // Project64 pins Counter Factor=1 for that ROM in its own per-game database.
                // 0 = defer to the core's per-ROM database, which is the old behaviour.
                new CoreOption("parallel-n64-countperop", "CPU counter factor", Category.Performance,
                    new[] { V("0","Auto (ROM database)"), V("1","1 (romhacks — Project64's choice)"),
                            V("2","2"), V("3","3"), V("4","4"), V("5","5") },
                    "0",
                    "Emulated CPU work per counter tick. Some romhacks run in slow motion at the database default and need 1. A wrong value can also desync a game's audio."),
                // ⚠ The upstream NAME is misleading: this is FRAME DUPLICATION, not a speed limiter. Enabled
                // sends one frame per emulated video interrupt, which is what our fixed-cadence pipeline
                // expects; disabled sends only newly-rendered frames, so a 30fps-rendering game (SM64 and
                // its hacks) streams at ~30 and reads as choppy. mupen64plus_next does this by default
                // (FrameDuping=1), which is why the same game looked smooth there and stuttery here.
                // ⚠ READ ONCE, AT CORE BOOT (upstream guards it with `initial_boot`) — setting it per-room
                // does nothing until the worker restarts, which cost a wasted test cycle to discover.
                new CoreOption("parallel-n64-framerate", "Frame duplication", Category.Video,
                    new[] { V("fullspeed","Enabled — one frame per VI (smooth)"), V("original","Disabled — only new frames (cheaper)") },
                    "original",
                    "Sends a duplicate frame when the game does not draw a new one, so the stream stays at the full video rate. Games that render at 30fps look choppy without it. Takes effect only when the core next starts."),
                new CoreOption("parallel-n64-dithering", "Dithering", Category.Video,
                    new[] { V("enabled","Enabled (authentic)"), V("disabled","Disabled (cleaner gradients)") },
                    "enabled",
                    "The N64's output dithering. Authentic on, slightly cleaner gradients off."),
                new CoreOption("parallel-n64-virefresh", "VI refresh (overclock)", Category.Performance,
                    new[] { V("auto","Auto (from the game)"), V("1500","1500"), V("2200","2200") },
                    "auto",
                    "Overrides the video-interface refresh timing. Auto follows the game; the fixed values are a last resort for titles with timing faults."),
                // The key is spelled `disable_expmem` but its meaning is NOT inverted: the mupen bridge maps
                // enabled->DisableExtraMem=0, and `main.c` then picks rdram_size 0x800000 (8 MB) for 0 and
                // 0x400000 (4 MB) for 1. So "enabled" really does mean the Expansion Pak is present, and
                // that is the core default. Exposed because the key name invites someone to "un-disable" it
                // and land on 4 MB, which breaks the large romhacks this core is mostly used for.
                new CoreOption("parallel-n64-disable_expmem", "Expansion Pak RAM", Category.System,
                    new[] { V("enabled","8 MB — Expansion Pak fitted (default)"), V("disabled","4 MB — retail base console") },
                    "enabled",
                    "The N64 shipped with 4 MB and the Expansion Pak took it to 8 MB. Leave this on: most romhacks and a few retail games (Donkey Kong 64, Majora's Mask) require 8 MB and will not boot on 4."),
                // ⚠ NOT EXPOSED: parallel-n64-allow-large-roms. It LOOKS like the ROM size limit and is not.
                // The global it sets (AllowLargeRoms) is assigned in libretro.c and read NOWHERE — the same
                // dead-global shape CountPerOp had before we wired it up — so every value, including its
                // default of 1, does exactly nothing. The real cap was the hard-coded CART_ROM_MAX_SIZE in
                // api/frontend.c, raised 64 -> 128 MiB in our patched cores. Offering this would hand a
                // player a knob that cannot fix the failure it appears to describe.
                // MT-PATCHED OPTION (our build only). Normally set by the Glide64 render profile rather
                // than by hand — it is listed here so the ⚙ panel explains it instead of showing an
                // unexplained key, and so a player can turn it off if a game sounds fine without the cost.
                new CoreOption("parallel-n64-send_alist_to_lle_rsp", "Accurate audio with an HLE renderer", Category.System,
                    new[] { V("enabled","Accurate (LLE audio RSP)"), V("disabled","Fast (HLE audio)") },
                    "disabled",
                    "Runs N64 audio on the accurate RSP while graphics stay on the fast HLE one. Fixes romhack music that crackles under HLE audio (SM64: Last Impact) while keeping an OpenGL renderer working. Costs some CPU; no effect on the Vulkan renderer, which is already accurate."),
                // The UPSTREAM mirror of the option above, and the exact opposite trade: it makes AUDIO fast
                // while graphics stay accurate, so it is the one that pairs with the Vulkan/angrylion
                // renderers. Both are listed because they are not interchangeable — they move different
                // halves of the workload.
                // ⚠ If BOTH are enabled the FAST path wins: in `plugin.c` our LLE branch is the `else if`
                // after this one, so enabling both silently gives you HLE audio (i.e. the crackle is back).
                new CoreOption("parallel-n64-send_allist_to_hle_rsp", "Fast audio with an accurate renderer", Category.Performance,
                    new[] { V("enabled","Fast (HLE audio)"), V("disabled","Accurate (selected RSP)") },
                    "disabled",
                    "Runs N64 audio on the fast HLE RSP while graphics stay on the accurate one, which reclaims noticeable CPU under the Vulkan renderer with little audible difference in most games. Leave off for romhacks with custom music, and never combine it with \"Accurate audio with an HLE renderer\" — this one wins."),
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
            // ── D7.2, added 2026-08-02 ────────────────────────────────────────────────────────────────
            // These 18 systems have a catalogued core but were absent from this map, so CoreForSystem
            // returned null, HasAnything() was false, and the ⚙ Configure button was suppressed — an
            // INVISIBLE gap (nothing errors; the button simply never appears). sg1000 is the sharpest
            // case: its core, genesis_plus_gx, has been in the catalog the whole time.
            // Cores are the DLL config.worker-gl.yaml loads per system (`lib:`), verified by the
            // extraction in docs/arcade/core-options-drift-2026-08-02.md. Option sets here are naturally
            // small (1-42), and the Save bound is derived from the catalog, so nothing else has to move.
            ["sg1000"] = "genesis_plus_gx",
            ["pce"] = "mednafen_pce", ["ngpc"] = "mednafen_ngp", ["wsc"] = "mednafen_wswan",
            ["lynx"] = "mednafen_lynx", ["vb"] = "mednafen_vb",
            ["a2600"] = "stella", ["a7800"] = "prosystem",
            ["vectrex"] = "vecx", ["intv"] = "freeintv", ["coleco"] = "gearcoleco",
            ["channelf"] = "freechaf", ["o2em"] = "o2em", ["arcadia"] = "amiarcadia",
            ["supervision"] = "potator", ["pokemini"] = "pokemini",
            ["3do"] = "opera", ["cdi"] = "same_cdi",
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

        /// <summary>True if this key is owned by the render-profile selector. Such a key must never round-trip
        /// through the config module — not as a plain option (excluded at load below) and not as an "advanced"
        /// raw row. A stored renderer key WOULD land in the advanced set (it is deliberately absent from every
        /// catalog), where the module re-submits it on every save and it then out-ranks the Graphics dropdown
        /// in the exported overrides: picking OpenGL saved, but the raw row wrote paraLLEl-GS straight back.
        /// Found 2026-08-02 on ps2/Stuntman, whose saved blob carries pcsx2_renderer.</summary>
        public static bool IsRendererSelecting(string? key) => key != null && RendererSelectingKeys.Contains(key);

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
