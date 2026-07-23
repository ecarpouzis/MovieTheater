using System;
using System.Collections.Generic;
using System.Linq;

namespace MovieTheater.Arcade
{
    /// <summary>
    /// Tiered default quality settings for the per-game config tool — the footer dropdown next to
    /// "Reset to defaults". A tier is a named bundle of core-option values per (core, renderer)
    /// combination; pressing Reset applies the selected tier's bundle for the game's currently
    /// selected render profile.
    ///
    /// <para><b>Ultra is the live system tuning and is deliberately EMPTY</b>: resetting to Ultra
    /// stores no overrides, so the game tracks <c>config.worker-gl.yaml</c> as it gets retuned.
    /// The other tiers pin explicit values. <b>Max</b> pushes past the live defaults (experimental
    /// or beyond the point of proven visual impact); <b>High/Medium/Low</b> step down for ROMs that
    /// push the system and slow down.</para>
    ///
    /// <para><b>⚠ Preset values must be stored VERBATIM, never baseline-dropped.</b> The config
    /// PUT normally drops a submitted value equal to the catalog default — but the catalog default
    /// is the CORE's embedded default, while the live default comes from config.worker-gl.yaml,
    /// and the two disagree on exactly the quality levers (e.g. <c>beetle_psx_hw_internal_resolution</c>:
    /// catalog default "1x(native)", live yaml "4x"). Dropping a Low tier's "1x(native)" as
    /// "equal to default" would ship the room without the key, the yaml's 4x would win, and the
    /// tier would be silently inert — the recurring silent-no-op class. The controller's tier path
    /// therefore bypasses the drop.</para>
    ///
    /// <para>Every token below is the core's EXACT value token, verified against the embedded
    /// core-options-catalog.json (extracted from the DLLs on Ziggy) — enforced by
    /// ArcadeQualityPresetsTests. 2D cores have no entries: their quality is the config-level
    /// <c>scale</c> (not per-room deliverable) and they are never the perf problem, so every tier
    /// equals Ultra there.</para>
    /// </summary>
    public static class ArcadeQualityPresets
    {
        public sealed record QualityTier(string Id, string Label);

        /// <summary>Offered tiers, in dropdown order (best first). Default selection = Ultra.</summary>
        public static readonly IReadOnlyList<QualityTier> Tiers = new[]
        {
            new QualityTier("max", "Max"),
            new QualityTier("ultra", "Ultra"),
            new QualityTier("high", "High"),
            new QualityTier("medium", "Medium"),
            new QualityTier("low", "Low"),
        };

        public const string DefaultTier = "ultra";

        public static bool IsKnown(string? tier) =>
            tier != null && Tiers.Any(t => string.Equals(t.Id, tier, StringComparison.OrdinalIgnoreCase));

        private static Dictionary<string, string> Opt(params (string Key, string Value)[] kv)
        {
            var d = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var (k, v) in kv) d[k] = v;
            return d;
        }

        // Presets keyed by (OptionCore, HwContext). HwContext null = applies to every renderer of that
        // core (the option set doesn't differ by surface); pcsx2 and mupen64plus_next split because their
        // Vulkan and GL renderers read DIFFERENT quality keys (paraLLEl-GS ignores pcsx2_upscale_multiplier;
        // GLideN64 ignores mupen64plus-parallel-rdp-upscaling). "ultra" is intentionally absent everywhere.
        private static readonly Dictionary<(string Core, string? Hw), Dictionary<string, Dictionary<string, string>>> ByCoreHw = new()
        {
            // ── PS1 Beetle (Vulkan + OpenGL — same option set). Live: internal_resolution 4x.
            // 8x was documented as "the next step if 4x proves clean + wanted" — that's Max, plus the
            // classic beetle "enhanced" bundle: 32bpp output + dithering off (clean gradients — the
            // 16bpp dither exists to hide banding the deeper buffer doesn't have), adaptive smoothing
            // (unsmears upscaled 2D/menu elements), and PGXP in its safe mode (fixes 3D wobble; the
            // hand catalog's own note: most 3D games look better, a few break — Max accepts that).
            // Swept and left alone: filter (SABR/xBR — art-style change, taste not tier),
            // super_sampling (tested & REVERTED — costs sharpness), msaa (unverifiable here — the AA
            // probe lesson), gpu/gte overclocks + cd_fastload (guest-side, per-game residue),
            // frame_duping (pacing interference).
            // Medium and Low coincide at native: below 1x there is nothing left to give back.
            [("beetle_psx_hw", null)] = new()
            {
                ["max"] = Opt(
                    ("beetle_psx_hw_internal_resolution", "8x"),
                    ("beetle_psx_hw_depth", "32bpp"),
                    ("beetle_psx_hw_dither_mode", "disabled"),
                    ("beetle_psx_hw_adaptive_smoothing", "enabled"),
                    ("beetle_psx_hw_pgxp_mode", "memory only")),
                ["high"] = Opt(("beetle_psx_hw_internal_resolution", "2x")),
                ["medium"] = Opt(("beetle_psx_hw_internal_resolution", "1x(native)")),
                ["low"] = Opt(("beetle_psx_hw_internal_resolution", "1x(native)")),
            },

            // ── PS1 pcsx_rearmed (software). Only real lever: the NEON enhanced-res (2x) renderer,
            // live-on. Swept and left alone: dithering (authentic), frameskip (pacing interference),
            // gpu_thread_rendering (auto is right), the gte/nostalls speedhacks (guest-accuracy risk).
            // No Max — this core IS the compatibility fallback; experiments belong on Beetle.
            [("pcsx_rearmed", null)] = new()
            {
                ["low"] = Opt(("pcsx_rearmed_neon_enhancement_enable", "disabled")),
            },

            // ── PS2 paraLLEl-GS (Vulkan default). Live: 16x SSAA + high-res scanout + LOD0 mipmaps.
            // upscale_multiplier / anisotropic / blending are DEAD keys under pgs (worker "[opt] DEAD
            // keys" proof — they belong to GSdx, and texture/trilinear filtering are presumed the same
            // class) — the pgs_* options are the only real levers. Max adds the two experimental
            // sharpeners the hand catalog already offers per-game (deblur, super-sample textures).
            // Swept and left alone: ee_cycle_rate/skip (guest-side underclocks, GameDB territory),
            // hw_download_mode (measured & rejected — Stuntman), the hw-hacks family (gated behind
            // pcsx2_enable_hw_hacks, which kills the GameDB auto-fixes — never a default).
            [("pcsx2", "vulkan")] = new()
            {
                // Ultra demoted 16x→8x 2026-07-22 (Eric-approved): 16x was the Phase-2 "one step past
                // the old ceiling" raise on the heaviest core, and slow-ROM reports followed. 16x is
                // now Max's job. The ladder steps below the new Ultra.
                ["max"] = Opt(
                    ("pcsx2_pgs_ssaa", "16x SSAA (can high-res)"),
                    ("pcsx2_pgs_deblur", "enabled"),
                    ("pcsx2_pgs_ss_tex", "enabled")),
                ["high"] = Opt(("pcsx2_pgs_ssaa", "4x SSAA (ordered, can high-res)")),
                ["medium"] = Opt(("pcsx2_pgs_ssaa", "2x SSAA")),
                ["low"] = Opt(
                    ("pcsx2_pgs_ssaa", "Native"),
                    ("pcsx2_pgs_high_res_scanout", "disabled")),
            },

            // ── PS2 OpenGL (GSdx). Live companions when GL is selected: upscale 2x, aniso 8x, blending
            // Medium. LRPS2 scales its BASE geometry with the multiplier, so lower tiers also shrink the
            // encoded stream — the biggest perf lever on the heaviest core we run. Swept and left
            // alone: trilinear "Forced" (glitch-prone even for Max), dithering (Unscaled is right),
            // pcrtc_antiblur (already the good default).
            [("pcsx2", "gl")] = new()
            {
                ["max"] = Opt(
                    ("pcsx2_upscale_multiplier", "4x Native (~1440p/2K)"),
                    ("pcsx2_anisotropic_filtering", "16x"),
                    ("pcsx2_blending_accuracy", "High")),
                ["high"] = Opt(("pcsx2_upscale_multiplier", "2x Native (~720p)")),
                ["medium"] = Opt(("pcsx2_upscale_multiplier", "1x Native (PS2)")),
                ["low"] = Opt(
                    ("pcsx2_upscale_multiplier", "1x Native (PS2)"),
                    ("pcsx2_anisotropic_filtering", "disabled"),
                    ("pcsx2_blending_accuracy", "Basic")),
            },

            // ── N64 paraLLEl-RDP (Vulkan default). Live: upscaling 8x (the token ceiling — Max pins it
            // explicitly so it survives any future Ultra step-down). Swept and left alone: the VI
            // filters (vi-aa/divot/dither — authentic N64 output, on by default), super-sampled-read-
            // back (True downsamples the core's OUTPUT to native, and our encode would then UPSCALE it
            // back — strictly worse on this pipeline), rdp-synchronous (accuracy pin, async glitches),
            // downscaling (weak-GPU escape, not ours).
            [("mupen64plus_next", "vulkan")] = new()
            {
                ["max"] = Opt(("mupen64plus-parallel-rdp-upscaling", "8x")),
                ["high"] = Opt(("mupen64plus-parallel-rdp-upscaling", "4x")),
                ["medium"] = Opt(("mupen64plus-parallel-rdp-upscaling", "2x")),
                ["low"] = Opt(("mupen64plus-parallel-rdp-upscaling", "1x")),
            },

            // ── N64 GLideN64 (OpenGL). Live companion: 43screensize 640x480 (2x internal). Medium/Low
            // coincide at native — GLideN64 at 320x240 has nothing further to shed. Swept and left
            // alone: MultiSampling + FXAA (both PROVEN bit-identical/inert on this hw_render setup),
            // txEnhancementMode (CPU texture filters — hitch risk + art change), HWLighting
            // (glitch-prone, and GL is the compat fallback — experiments belong on the Vulkan arm).
            [("mupen64plus_next", "gl")] = new()
            {
                ["max"] = Opt(("mupen64plus-43screensize", "960x720")),
                ["high"] = Opt(("mupen64plus-43screensize", "640x480")),
                ["medium"] = Opt(("mupen64plus-43screensize", "320x240")),
                ["low"] = Opt(("mupen64plus-43screensize", "320x240")),
            },

            // ── N64 parallel_n64 (compatibility core). Two renderers, split like mupen: Vulkan paraLLEl-RDP
            // uses the supersampling lever; GLideN64/GL uses the internal-resolution (screensize) lever —
            // the other key is inert on the wrong renderer (the silent-no-op class). Tokens verified in
            // parallel_n64_libretro.dll and hand-defined in ArcadeCoreOptionCatalog (the test assembly sees
            // only those). Low also drops to the fast dynarec for weak-perf ROMs.
            [("parallel_n64", "vulkan")] = new()
            {
                ["max"] = Opt(("parallel-n64-parallel-rdp-upscaling", "8x")),
                ["high"] = Opt(("parallel-n64-parallel-rdp-upscaling", "4x")),
                ["medium"] = Opt(("parallel-n64-parallel-rdp-upscaling", "2x")),
                ["low"] = Opt(("parallel-n64-parallel-rdp-upscaling", "1x"), ("parallel-n64-cpucore", "dynamic_recompiler")),
            },
            [("parallel_n64", "gl")] = new()
            {
                ["max"] = Opt(("parallel-n64-screensize", "1920x1440")),
                ["high"] = Opt(("parallel-n64-screensize", "1280x960")),
                ["medium"] = Opt(("parallel-n64-screensize", "640x480")),
                ["low"] = Opt(("parallel-n64-screensize", "320x240"), ("parallel-n64-cpucore", "dynamic_recompiler")),
            },

            // ── PSP (Vulkan + GL — PPSSPP has no renderer option; same keys either way). Live: internal
            // 2880x1632 (6x). PPSSPP scales its BASE geometry, and psp runs scale 0.5 (supersample), so
            // delivered size halves with the internal res: High = the proven 1920x1088→960x544 config.
            // Swept and left alone: mulitsample_level (PROVEN inert — hw FBO isn't multisampled),
            // texture_scaling/xBRZ + texture_shader (texture-load stalls are this core's crackle
            // mechanism — never invite more), frameskip/auto_frameskip (the PSP stalls are savedata/
            // IO events frameskip can't help, and it perturbs the audio-mastered pacer),
            // lower_resolution_for_effects (GPU relief on a core whose problems are CPU-side),
            // cpu_core/fast_memory/locked_cpu_speed (stability pins — see the ppsspp-core skill).
            [("ppsspp", null)] = new()
            {
                ["max"] = Opt(("ppsspp_internal_resolution", "3840x2176")),
                ["high"] = Opt(("ppsspp_internal_resolution", "1920x1088")),
                ["medium"] = Opt(("ppsspp_internal_resolution", "1440x816")),
                ["low"] = Opt(("ppsspp_internal_resolution", "960x544")),
            },

            // ── Dreamcast / NAOMI / Atomiswave — flycast (Vulkan + GL). Live: internal 2560x1920 (2x
            // supersample over the fixed 1280x960 delivery), aniso 16. flycast never scales its base, so
            // these tiers change SUPERSAMPLING only; delivery geometry is constant. Max stays on
            // per-triangle alpha sorting deliberately — per-pixel was REJECTED 2026-07-17 for menu
            // UI-quad garble (visual breakage, not perf), do not resurrect it via a tier. Also swept
            // and left alone: texupscale/xBRZ (CPU texture filter — hitch risk), pvr2_filtering (an
            // authentic-CRT-blur emulation, the opposite of what our tiers sell), threaded_rendering
            // (Option B PERMANENTLY off the table — latency), auto_skip_frame/frame_skipping (pacing
            // interference), sh4clock (guest-side), native_depth_interpolation (per-game compat).
            [("flycast", null)] = new()
            {
                ["max"] = Opt(("reicast_internal_resolution", "3840x2880")),
                ["high"] = Opt(("reicast_internal_resolution", "1920x1440")),
                ["medium"] = Opt(("reicast_internal_resolution", "1280x960")),
                ["low"] = Opt(
                    ("reicast_internal_resolution", "640x480"),
                    ("reicast_anisotropic_filtering", "4")),
            },

            // ── GameCube / Wii — Dolphin (Vulkan + GL). Live: efb_scale 3, 4x MSAA ("2"), 16x aniso
            // ("4"). Dolphin is the ONLY core whose MSAA measurably works (owns its framebuffers), so AA
            // steps down with the tiers. Aniso values are numeric: 0=off … 4=16x. Max adds per-pixel
            // lighting (accurate GX specular — the classic Dolphin "HD" toggle; a handful of titles
            // render it wrong, which per-game Dolphin GameSettings INIs may veto — Max accepts that).
            // Swept and left alone: cpu_clock_rate (guest-side), texture_cache_accuracy / gpu_texture_
            // decoding / vi_skip (perf knobs with pacing or accuracy interactions, wins unproven here),
            // shader_compilation_mode + main_cpu_thread (hard stability pins — see the yaml saga),
            // enhance_output_resampling / force_texture_filtering (art-style, taste not tier).
            [("dolphin", null)] = new()
            {
                ["max"] = Opt(
                    ("dolphin_efb_scale", "4"),
                    ("dolphin_anti_aliasing", "3"),
                    ("dolphin_pixel_lighting", "enabled")),
                ["high"] = Opt(
                    ("dolphin_efb_scale", "2"),
                    ("dolphin_anti_aliasing", "2")),
                ["medium"] = Opt(
                    ("dolphin_efb_scale", "2"),
                    ("dolphin_anti_aliasing", "1")),
                ["low"] = Opt(
                    ("dolphin_efb_scale", "1"),
                    ("dolphin_anti_aliasing", "0"),
                    ("dolphin_max_anisotropy", "2")),
            },

            // ── Saturn — Kronos (GL only). Live: original (native) resolution, so there is nothing
            // below Ultra. Max tries the 2X resolution mode — whether it moves the reported BASE
            // geometry (Dolphin-style) or only supersamples (flycast-style) is UNCONFIRMED, which is
            // exactly what Max is for; verify with scalecheck.mjs before promoting it to a default.
            // Swept and left alone: skipframe (pacing interference on a pilot-stage system),
            // video_filter_type / force_downsampling / wireframe (niche or wrong direction),
            // meshmode/bandingmode (correctness fixes, live-on, welded in the Ultra spec below).
            [("kronos", null)] = new()
            {
                ["max"] = Opt(("kronos_resolution_mode", "2X")),
            },
        };

        // ── The DECLARED Ultra ────────────────────────────────────────────────────────────────────────
        // Ultra stores nothing (see the class doc), but it is NOT undefined: this spec is the deliberate
        // statement of what Ultra means per core — the quality-lever values config.worker-gl.yaml must
        // deliver. ArcadeQualityPresetsTests WELDS the two together: it parses the yaml and fails if any
        // live value disagrees, so the yaml can't drift from the declared Ultra silently. Retuning a
        // lever therefore forces a conscious decision — "this is the new Ultra" (update BOTH files) or
        // "that value belongs in another tier" (move it to Max/High here). Flat per core: the GL and
        // Vulkan renderers' lever keys never collide within a core.
        // (reicast_alpha_sorting is welded even though no tier moves it — it guards the REJECTED
        // per-pixel mode from ever coming back as a default.)
        public static readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> UltraLiveSpec =
            new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal)
            {
                ["beetle_psx_hw"] = Opt(
                    ("beetle_psx_hw_internal_resolution", "4x"),
                    ("beetle_psx_hw_pgxp_mode", "disabled")),           // per-game opt-in, never a default
                ["pcsx_rearmed"] = Opt(
                    ("pcsx_rearmed_neon_enhancement_enable", "enabled"),
                    ("pcsx_rearmed_neon_enhancement_tex_adj_v2", "enabled")),
                ["pcsx2"] = Opt(
                    ("pcsx2_pgs_ssaa", "8x SSAA (can high-res)"),   // demoted from 16x 2026-07-22
                    ("pcsx2_pgs_high_res_scanout", "enabled"),
                    ("pcsx2_pgs_disable_mipmaps", "enabled"),
                    ("pcsx2_upscale_multiplier", "2x Native (~720p)"),
                    ("pcsx2_anisotropic_filtering", "8x"),
                    ("pcsx2_blending_accuracy", "Medium")),
                ["mupen64plus_next"] = Opt(
                    ("mupen64plus-parallel-rdp-upscaling", "8x"),
                    ("mupen64plus-43screensize", "640x480"),
                    ("mupen64plus-EnableNativeResTexrects", "Optimized"), // HUD/text stays native-crisp at upscale
                    ("mupen64plus-BilinearMode", "3point")),              // the N64's real texture filter
                ["ppsspp"] = Opt(
                    ("ppsspp_internal_resolution", "2880x1632"),
                    ("ppsspp_texture_anisotropic_filtering", "16x"),
                    ("ppsspp_smart_2d_texture_filtering", "enabled")),
                ["flycast"] = Opt(
                    ("reicast_internal_resolution", "2560x1920"),
                    ("reicast_anisotropic_filtering", "16"),
                    ("reicast_alpha_sorting", "per-triangle (normal)")),
                ["dolphin"] = Opt(
                    ("dolphin_efb_scale", "3"),
                    ("dolphin_anti_aliasing", "2"),
                    ("dolphin_max_anisotropy", "4"),
                    ("dolphin_wait_for_shaders", "enabled")),           // boot-precompile — the stall contract
                ["kronos"] = Opt(
                    ("kronos_meshmode", "enabled"),                     // VDP1 mesh transparency correctness
                    ("kronos_bandingmode", "enabled")),
            };

        private static readonly IReadOnlyDictionary<string, string> Empty =
            new Dictionary<string, string>(StringComparer.Ordinal);

        /// <summary>The option bundle a tier pins for a (core, renderer) combination. Empty for Ultra,
        /// for unknown tiers, and for cores with no presets (2D — every tier is the live default).</summary>
        public static IReadOnlyDictionary<string, string> For(string? core, string? hwContext, string? tier)
        {
            if (core == null || tier == null) return Empty;
            var t = tier.Trim().ToLowerInvariant();
            if (t == "ultra") return Empty;
            if (hwContext != null
                && ByCoreHw.TryGetValue((core, hwContext), out var byHw)
                && byHw.TryGetValue(t, out var preset)) return preset;
            return ByCoreHw.TryGetValue((core, null), out var any) && any.TryGetValue(t, out var p) ? p : Empty;
        }

        /// <summary>Every preset entry, flattened — for the token-validation test.</summary>
        public static IEnumerable<(string Core, string? Hw, string Tier, string Key, string Value)> AllEntries()
        {
            foreach (var ((core, hw), tiers) in ByCoreHw)
                foreach (var (tier, options) in tiers)
                    foreach (var (key, value) in options)
                        yield return (core, hw, tier, key, value);
        }
    }
}
