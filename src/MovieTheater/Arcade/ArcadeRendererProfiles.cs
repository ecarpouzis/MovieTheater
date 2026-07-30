using System;
using System.Collections.Generic;
using System.Linq;

namespace MovieTheater.Arcade
{
    /// <summary>
    /// The per-system <b>render profiles</b> that drive the config tool's graphics selection. A render
    /// profile is one named graphics choice for a system, mapping to the whole stack the worker needs:
    /// which <b>core</b> to boot (an optional per-room core-key override), the frontend <b>surface</b>
    /// (<c>hwContext</c>), and the core's own <b>renderer-selecting options</b>. Choosing a renderer must
    /// set ALL of these together — flipping only the surface strands cores that pick their renderer from a
    /// core-option (N64 paraLLEl-RDP asks Vulkan on a GL surface → no video), and for PS1 the pre-Vulkan
    /// OpenGL path is a genuinely different CORE (<c>pcsx_rearmed</c>), not just a renderer option.
    ///
    /// <para>Delivery is per-room at Start: <c>CoreKey</c> → <c>&amp;core=</c> (worker StartGameRequest.Core →
    /// HandleGameStart core override), <c>HwContext</c> → <c>&amp;hwctx=</c>, <c>Options</c> → merged into the
    /// room's <c>CoreOptions</c> as a base beneath the game's saved config (patch-0027, overrides config +
    /// manifest). The GL companion settings (GLideN64 FB opts/res; pcsx2 GL options) already sit inert in
    /// <c>config.worker-gl.yaml</c> and activate when the renderer flips — so only the renderer-SELECTING
    /// keys live here. <see cref="OptionCore"/> tells the module which core's option catalog to show.</para>
    ///
    /// <para>The first profile listed per system is the current live default. Value tokens are the cores'
    /// EXACT tokens (config.worker-gl.yaml + the DLLs).</para>
    /// </summary>
    public static class ArcadeRendererProfiles
    {
        /// <param name="Id">Stable id stored in <see cref="Db.ArcadeGameProfile.RenderProfile"/>.</param>
        /// <param name="CoreKey">Per-room core-key override (config cores.List); null = the system's default core.</param>
        /// <param name="HwContext">"gl"/"vulkan"/null (software cores have no hw surface).</param>
        /// <param name="OptionCore">Catalog key for the options the module shows when this profile is selected.</param>
        public sealed record RenderProfile(
            string Id, string Label, string? CoreKey, string? HwContext, string OptionCore,
            IReadOnlyDictionary<string, string> Options, bool IsDefault);

        private static Dictionary<string, string> Opt(params (string, string)[] kv)
        {
            var d = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var (k, v) in kv) d[k] = v;
            return d;
        }
        private static readonly Dictionary<string, string> None = new(StringComparer.Ordinal);

        // Per-system profiles. First = current live default.
        private static readonly Dictionary<string, RenderProfile[]> BySystem = new(StringComparer.OrdinalIgnoreCase)
        {
            ["n64"] = new[]
            {
                new RenderProfile("vulkan", "mupen64plus-next · Vulkan (paraLLEl-RDP)", null, "vulkan", "mupen64plus_next",
                    Opt(("mupen64plus-rdp-plugin", "parallel"), ("mupen64plus-rsp-plugin", "parallel")), true),
                // GLideN64 (GL) fallback for the default core. The mixed-case FB options ride THIS path,
                // not config.yaml — the config loader lowercases YAML keys and libretro keys are
                // case-sensitive, so config copies are silently dead (the "DEAD keys" reconcile). Delivered
                // here they apply case-preserved (t=104 room-options). CopyAuxToRDRAM=True renders aux-buffer
                // content (fixes blank cutscene/intro planes, e.g. Bomberman); NativeResTexrects=Optimized
                // keeps HUD/text sharp when upscaled. These were previously inert in config.
                new RenderProfile("opengl", "mupen64plus-next · OpenGL (GLideN64)", null, "gl", "mupen64plus_next",
                    Opt(("mupen64plus-rdp-plugin", "gliden64"), ("mupen64plus-rsp-plugin", "hle"),
                        ("mupen64plus-EnableCopyColorToRDRAM", "Async"),
                        ("mupen64plus-EnableCopyDepthToRDRAM", "Software"),
                        ("mupen64plus-EnableLegacyBlending", "False"),
                        ("mupen64plus-EnableCopyAuxToRDRAM", "True"),
                        ("mupen64plus-EnableNativeResTexrects", "Optimized"),
                        ("mupen64plus-BilinearMode", "3point")), false),
                // A DIFFERENT CORE (not a mupen renderer): parallel_n64's own R4300 runs romhacks that crash
                // mupen64plus-next (SM64: Last Impact). CoreKey "parallel_n64" → &core= per-room override.
                // Two renderers, same as mupen: Vulkan (paraLLEl-RDP, the primary/best) and GL (GLideN64,
                // fallback) — angrylion is banned. The profile's Options flip gfxplugin/rspplugin + surface;
                // both share the "n64-parallel_n64" save namespace (same core). See config.worker-gl.yaml.
                // parallel-rdp-upscaling belongs HERE, not in config.worker-gl.yaml. Config options are
                // per-CORE, so a config entry ships to EVERY profile — and on the two GL profiles below,
                // paraLLEl-RDP's supersampling lever does nothing at all. Every Glide64/GLideN64 room was
                // being handed `parallel-rdp-upscaling=8x` as dead weight, which is exactly the kind of
                // inert-but-present option that makes a room's real configuration unreadable. Delivered
                // from the Vulkan profile it ships only where it has an effect.
                new RenderProfile("parallel_n64", "parallel_n64 core · Vulkan (paraLLEl-RDP)", "parallel_n64", "vulkan", "parallel_n64",
                    Opt(("parallel-n64-gfxplugin", "parallel"), ("parallel-n64-rspplugin", "parallel"),
                        // 8x, matching what config.worker-gl.yaml has always delivered — moving the option
                        // must not silently change the VALUE too, or "where did my supersampling go" becomes
                        // the next mystery. Remove the config copy once this is confirmed live.
                        ("parallel-n64-parallel-rdp-upscaling", "8x")), false),
                // GLideN64 (GL) fallback. The FB options are mixed-case libretro keys and MUST ride this
                // render-profile path (t=104 room-options), NOT config.yaml — the config loader lowercases
                // YAML keys and libretro option keys are case-sensitive, so they'd be silently ignored there
                // (the DEAD-keys reconcile). This is mupen's proven GL framebuffer set: CopyAuxToRDRAM=True
                // renders aux-color-buffer content (blank cutscene/intro planes otherwise) and
                // NativeResTexrects=Optimized keeps HUD/text sharp when upscaled. Tokens from
                // parallel_n64's libretro_core_options.h.
                new RenderProfile("parallel_n64_gl", "parallel_n64 core · OpenGL (GLideN64)", "parallel_n64", "gl", "parallel_n64",
                    Opt(("parallel-n64-gfxplugin", "gliden64"), ("parallel-n64-rspplugin", "hle"),
                        ("parallel-n64-gliden64-EnableFBEmulation", "True"),
                        ("parallel-n64-gliden64-EnableCopyColorToRDRAM", "Async"),
                        ("parallel-n64-gliden64-EnableCopyDepthToRDRAM", "Software"),
                        ("parallel-n64-gliden64-EnableLegacyBlending", "False"),
                        ("parallel-n64-gliden64-EnableCopyAuxToRDRAM", "True"),
                        ("parallel-n64-gliden64-EnableNativeResTexrects", "Optimized")), false),
                // ── Glide64 (GL) — THE ROMHACK RENDERER, added 2026-07-29 ───────────────────────────
                // The libretro-era answer to Project64's Jabo's Direct3D. Late-2010s SM64 romhacks were
                // authored and tested against Jabo's/Glide64, and they lean on its exact (often
                // inaccurate) handling of custom display lists: SM64: Last Impact renders the hack's
                // CUSTOM assets as solid black silhouettes on BOTH gliden64 and paraLLEl-RDP, while
                // Eric confirmed live in Project64 3.0.1 that switching to Jabo's Direct3D fixes the
                // visuals AND the music. Jabo's itself is closed-source and P64-only, so it cannot be
                // ported — but parallel_n64 is the ONLY core in our fleet that still BUNDLES Glide64
                // (gfxplugin tokens: gln64|gliden64|rice|glide64|angrylion|parallel, verified in the
                // DLL), and we had never once run it. rspplugin=hle to match the era the plugin targets.
                // If Glide64 still shows black assets, `rice` (Rice Video) is the next closest legacy
                // plugin in the same core. angrylion stays BANNED (it hard-panics the GL scaffolding).
                // Ships gfxplugin + rspplugin + the AUDIO SPLIT, and deliberately NO gliden64-* options:
                // those are GLideN64's and are inert under Glide64 (the parallel_n64_gl profile above
                // rightly carries them; this one must not).
                //
                // send_alist_to_lle_rsp is part of the PROFILE, not a per-game opt-in, because it is a
                // property of this combination rather than of any one ROM: Glide64 is an HLE graphics
                // plugin, so the active RSP must be HLE, which would otherwise drag AUDIO onto the HLE
                // audio microcode — and that microcode renders SM64: Last Impact's custom music with a
                // constant crackle (proven: BAZR clean on LLE audio, rspplugin=parallel killed the crackle
                // but broke graphics, and Project64 — correct on this game — ships exactly this split,
                // m_AudioHle=false + m_GraphicsHle=true). Our patched core routes audio to cxd4 while
                // graphics stay HLE. Anyone selecting Glide64 wants that pairing; making them find a
                // separate option to avoid a crackle would be a trap.
                new RenderProfile("parallel_n64_glide64", "parallel_n64 core · Glide64 (romhack compat)", "parallel_n64", "gl", "parallel_n64",
                    Opt(("parallel-n64-gfxplugin", "glide64"), ("parallel-n64-rspplugin", "hle"),
                        ("parallel-n64-send_alist_to_lle_rsp", "enabled")), false),
            },
            ["ps2"] = new[]
            {
                new RenderProfile("vulkan", "Vulkan (paraLLEl-GS)", null, "vulkan", "pcsx2",
                    Opt(("pcsx2_renderer", "paraLLEl-GS")), true),
                new RenderProfile("opengl", "OpenGL", null, "gl", "pcsx2",
                    Opt(("pcsx2_renderer", "OpenGL")), false),
            },
            ["ps1"] = new[]
            {
                new RenderProfile("beetle_vulkan", "Beetle (Vulkan)", null, "vulkan", "beetle_psx_hw",
                    Opt(("beetle_psx_hw_renderer", "hardware_vk")), true),
                new RenderProfile("beetle_opengl", "Beetle (OpenGL)", null, "gl", "beetle_psx_hw",
                    Opt(("beetle_psx_hw_renderer", "hardware_gl")), false),
                // The pre-Vulkan "PSX GL" core — a CORE-LIB swap (config core-key "pcsx_rearmed"), software
                // rendered so no hw surface. The site keys its saves under a distinct namespace from Beetle.
                new RenderProfile("pcsx_rearmed", "pcsx_rearmed (OpenGL)", "pcsx_rearmed", null, "pcsx_rearmed",
                    None, false),
            },
            // Surface-only systems: the frontend surface alone selects the renderer (no renderer core-option).
            ["psp"] = SurfaceOnly("ppsspp"),
            ["dc"] = SurfaceOnly("flycast"),
            ["naomi"] = SurfaceOnly("flycast"),
            ["atomiswave"] = SurfaceOnly("flycast"),
            ["gc"] = SurfaceOnly("dolphin"),
            ["wii"] = SurfaceOnly("dolphin"),
        };

        private static RenderProfile[] SurfaceOnly(string optionCore) => new[]
        {
            new RenderProfile("vulkan", "Vulkan", null, "vulkan", optionCore, None, true),
            new RenderProfile("opengl", "OpenGL", null, "gl", optionCore, None, false),
        };

        /// <summary>Every system that offers a render-profile choice (for the play-button dropdown map).</summary>
        public static IReadOnlyCollection<string> AllSystems => BySystem.Keys;

        /// <summary>The render profiles offered for a system (empty if the system has no graphics choice).</summary>
        public static IReadOnlyList<RenderProfile> For(string? system) =>
            system != null && BySystem.TryGetValue(system, out var p) ? p : Array.Empty<RenderProfile>();

        /// <summary>The system's default (live) profile, or null.</summary>
        public static RenderProfile? Default(string? system) =>
            For(system).FirstOrDefault(p => p.IsDefault) ?? For(system).FirstOrDefault();

        /// <summary>Resolve a stored profile id to its profile, falling back to the system default.</summary>
        public static RenderProfile? Resolve(string? system, string? id) =>
            (id != null ? For(system).FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.Ordinal)) : null)
            ?? Default(system);

        /// <summary>Map a bare renderer ("gl"/"vulkan") to the system's first profile with that surface — the
        /// play-button Force GL/Vulkan path (a quick surface toggle; PS1 "gl" → Beetle-OpenGL, not the
        /// deliberate pcsx_rearmed config choice).</summary>
        public static RenderProfile? ForRenderer(string? system, string? renderer)
        {
            var r = renderer?.Trim().ToLowerInvariant() switch { "gl" => "gl", "vulkan" => "vulkan", _ => (string?)null };
            return r == null ? null : For(system).FirstOrDefault(p => p.HwContext == r);
        }

        /// <summary>Back-compat convenience: the renderer-selecting options for a system + bare renderer.</summary>
        public static IReadOnlyDictionary<string, string> Options(string? system, string? renderer) =>
            ForRenderer(system, renderer)?.Options ?? None;
    }
}
