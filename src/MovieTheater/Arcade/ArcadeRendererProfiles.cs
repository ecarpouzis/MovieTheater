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
                new RenderProfile("opengl", "mupen64plus-next · OpenGL (GLideN64)", null, "gl", "mupen64plus_next",
                    Opt(("mupen64plus-rdp-plugin", "gliden64"), ("mupen64plus-rsp-plugin", "hle")), false),
                // A DIFFERENT CORE (not a mupen renderer): parallel_n64's own R4300 runs romhacks that crash
                // mupen64plus-next (SM64: Last Impact). CoreKey "parallel_n64" → &core= per-room override.
                // Two renderers, same as mupen: Vulkan (paraLLEl-RDP, the primary/best) and GL (GLideN64,
                // fallback) — angrylion is banned. The profile's Options flip gfxplugin/rspplugin + surface;
                // both share the "n64-parallel_n64" save namespace (same core). See config.worker-gl.yaml.
                new RenderProfile("parallel_n64", "parallel_n64 core · Vulkan (paraLLEl-RDP)", "parallel_n64", "vulkan", "parallel_n64",
                    Opt(("parallel-n64-gfxplugin", "parallel"), ("parallel-n64-rspplugin", "parallel")), false),
                new RenderProfile("parallel_n64_gl", "parallel_n64 core · OpenGL (GLideN64)", "parallel_n64", "gl", "parallel_n64",
                    Opt(("parallel-n64-gfxplugin", "gliden64"), ("parallel-n64-rspplugin", "hle")), false),
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
