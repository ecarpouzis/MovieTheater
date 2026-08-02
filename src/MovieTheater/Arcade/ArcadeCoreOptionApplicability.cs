using System;
using System.Collections.Generic;
using System.Linq;

namespace MovieTheater.Arcade
{
    /// <summary>
    /// The missing half of the <b>(core, renderer)</b> pair: which <b>render profiles</b> a core option is
    /// actually LIVE under. The config module has always filtered by CORE alone, so both PS2 profiles (same
    /// <c>OptionCore = "pcsx2"</c>) rendered the same list — including the five <c>pcsx2_pgs_*</c> levers that
    /// only paraLLEl-GS reads and the three GSdx levers that paraLLEl-GS provably never reads. A player-facing
    /// option list is a claim that every entry does something; libretro NEVER errors on a key it does not
    /// recognise, so an inert row looks exactly like a working one. This type is that claim's guard
    /// (docs/arcade-config-module-dead-options-plan.md, D3 / Phase 2).
    ///
    /// <para><b>Keyed by render-profile id, not by HwContext.</b> Surface is too coarse: parallel_n64 has TWO
    /// gl-surface profiles — <c>parallel_n64_gl</c> (GLideN64) and <c>parallel_n64_glide64</c> (Glide64) — and
    /// the <c>parallel-n64-gliden64-*</c> keys are live on exactly one of them. A HwContext-keyed model cannot
    /// express that and would show GLideN64's framebuffer knobs on the Glide64 profile.</para>
    ///
    /// <para><b>Default is VISIBLE.</b> No rule = the option is live under every profile of its core, which is
    /// the only sane state for 2D/software cores and for every key we have no evidence about. Systems with no
    /// render profiles at all never filter. "Not queried in the sample window" is NOT "not applicable" — the
    /// evidence sweep's own caveat — so anything ambiguous stays visible and gets a longer run rather than
    /// being hidden on weak evidence. Every rule below therefore carries an <see cref="Rule.Evidence"/> string
    /// (log date/room from docs/arcade/opt-reconcile-evidence-2026-08-02.md, or structural reasoning); a rule
    /// with no evidence is a guess, and ArcadeCoreOptionApplicabilityTests fails on an empty one.</para>
    ///
    /// <para><b>Profile ids live in their CORE's namespace here.</b> Ids are only unique per system ("vulkan"
    /// means paraLLEl-GS under pcsx2 and paraLLEl-RDP under mupen64plus_next), and a core is reachable from
    /// exactly one system's profile set, so (core → profile id) is unambiguous. The guard test asserts every id
    /// named below really exists among the profiles that select that OptionCore — which is also what catches
    /// Phase 3 renaming the PS2 profiles out from under these rules.</para>
    /// </summary>
    public static class ArcadeCoreOptionApplicability
    {
        /// <summary>One restriction. <paramref name="Match"/> is an exact option key, or a key PREFIX when
        /// <paramref name="IsPrefix"/> — a prefix is how a whole implementation-owned namespace is expressed
        /// (<c>pcsx2_pgs_</c> is paraLLEl-GS's own). <paramref name="Profiles"/> is the complete set of render
        /// profile ids the option is live under; every other profile of that core hides it.
        /// <paramref name="Evidence"/> is mandatory and must say WHERE the restriction comes from.
        /// <paramref name="Latent"/> marks a rule that currently matches no catalogued key (the core is
        /// hand-only and does not expose those keys to the module yet) — it is kept so the model stays honest
        /// and binds the moment the keys are catalogued, and it exempts the rule from the "matches something"
        /// guard.</summary>
        public sealed record Rule(
            string Match, bool IsPrefix, IReadOnlyList<string> Profiles, string Evidence, bool Latent = false);

        private static Rule Key(string key, string evidence, params string[] profiles) =>
            new(key, false, profiles, evidence);
        private static Rule Prefix(string prefix, string evidence, params string[] profiles) =>
            new(prefix, true, profiles, evidence);

        // ── The restrictions ──────────────────────────────────────────────────────────────────────────
        // ONLY what the evidence supports. The three cores below are the ones with a renderer split AND
        // evidence for it; beetle_psx_hw, ppsspp, flycast and dolphin have a renderer/surface split but NO
        // renderer-split evidence (dolphin/flycast reconcile clean, the beetle GL and psp/dc/gc GL profiles
        // have never once been booted), so they are deliberately absent — guessing there would hide levers
        // that work. Phase 3's boot tests are what extends this list.
        private static readonly Dictionary<string, Rule[]> ByCore = new(StringComparer.OrdinalIgnoreCase)
        {
            // ── PS2 / LRPS2. Profiles: "vulkan" (= paraLLEl-GS, the live default) and "opengl" (= GSdx).
            // ⚠ Phase 3 plans to split "vulkan" into paraLLEl-GS and a real Vulkan (GSdx) profile. When that
            // lands, the GSdx keys below must gain the new profile id — the guard test will not catch a
            // MISSING id, only a bogus one, so this is the note that has to be read.
            ["pcsx2"] = new[]
            {
                // The headline D3 finding, and the only pcsx2 restriction with hard runtime proof.
                Key("pcsx2_upscale_multiplier",
                    "worker log: DEAD under paraLLEl-GS in EVERY sample — glworker.log 2026-08-02 12:24:24 / "
                    + "11:58:06 / 01:41:33 and glworker-2.log 2026-08-01 15:32:37 (Stuntman, 007 Agent Under "
                    + "Fire), identical DEAD set each time. GSdx-side lever.",
                    "opengl"),
                Key("pcsx2_anisotropic_filtering",
                    "worker log: same DEAD set as pcsx2_upscale_multiplier — DEAD under paraLLEl-GS in every "
                    + "sample, glworker.log 2026-08-02 12:24:24 and 4 more.",
                    "opengl"),
                Key("pcsx2_blending_accuracy",
                    "worker log: same DEAD set as pcsx2_upscale_multiplier — DEAD under paraLLEl-GS in every "
                    + "sample, glworker.log 2026-08-02 12:24:24 and 4 more.",
                    "opengl"),
                // Structural, not log-derived: the sweep found the site never SENDS a pgs_* key at all, so no
                // reconcile line can speak to them. They are paraLLEl-GS's own option namespace (the drift
                // report's extraction confirms the deployed pcsx2_custom_libretro.dll declares all five), and
                // a GSdx renderer has no code that reads them.
                Prefix("pcsx2_pgs_",
                    "structural: pcsx2_pgs_* is the paraLLEl-GS implementation's OWN namespace (5 keys declared "
                    + "by the deployed pcsx2_custom DLL — docs/arcade/core-options-drift-2026-08-02.md §1); a "
                    + "GSdx renderer cannot read them. No log evidence exists either way: the sweep found the "
                    + "site never sends a pgs_*-prefixed key (evidence doc, PS2 section).",
                    "vulkan"),
                // ⚠ Deliberately NOT restricted: pcsx2_texture_filtering, pcsx2_trilinear_filtering,
                // pcsx2_dithering and the rest of the GSdx-ish family. The quality-preset notes PRESUME they
                // are the same class as anisotropic/blending, but presumption is not evidence and the sweep
                // never saw them provided. They stay visible on both profiles until a boot test says otherwise.
            },

            // ── N64 default core. Profiles: "vulkan" (rdp-plugin=parallel, paraLLEl-RDP) and "opengl"
            // (rdp-plugin=gliden64).
            ["mupen64plus_next"] = new[]
            {
                Prefix("mupen64plus-parallel-rdp-",
                    "worker log: mupen64plus-parallel-rdp-upscaling DEAD under rdp-plugin=gliden64 in every "
                    + "gliden64 sample (glworker-2.log 2026-07-31 20:56:14 / 20:51:38, 2026-07-30 15:25:47; "
                    + "glworker.log 2026-07-29 21:41:49) and LIVE under rdp-plugin=parallel (15/16 read, "
                    + "2026-08-01 23:29:23 + 60 more). The rest of the namespace is paraLLEl-RDP's own by "
                    + "construction — the plugin is not loaded at all on the GLideN64 path.",
                    "vulkan"),

                // GLideN64's framebuffer/AA set. These are the exact keys the "opengl" render profile
                // DELIVERS (ArcadeRendererProfiles n64/opengl) — the profile carries them precisely because
                // they belong to the GL plugin, and paraLLEl-RDP does its own framebuffer emulation.
                // ⚠ Evidence caveat, stated plainly: the 9-key DEAD event under gliden64 (glworker.log
                // 2026-07-29 21:41:49 / 2026-07-23 10:27:00) is CONFOUNDED — those keys arrived lowercased
                // from config.worker-gl.yaml (the config loader lowercases YAML keys, libretro option keys are
                // case-sensitive), so they were dead for a case reason, not a plugin reason. That is exactly
                // why the render profile delivers the mixed-case set instead. The restriction therefore rests
                // on the STRUCTURAL argument — these configure the GLideN64 plugin, which rdp-plugin=parallel
                // does not load — corroborated by the render profile's own comments and by the quality-preset
                // split (the "gl" tier moves 43screensize, the "vulkan" tier moves parallel-rdp-upscaling).
                Key("mupen64plus-EnableFBEmulation", GLideN64Fb, "opengl"),
                Key("mupen64plus-EnableCopyAuxToRDRAM", GLideN64Fb, "opengl"),
                Key("mupen64plus-EnableCopyColorToRDRAM", GLideN64Fb, "opengl"),
                Key("mupen64plus-EnableCopyDepthToRDRAM", GLideN64Fb, "opengl"),
                Key("mupen64plus-EnableLegacyBlending", GLideN64Fb, "opengl"),
                Key("mupen64plus-EnableNativeResTexrects", GLideN64Fb, "opengl"),
                Key("mupen64plus-MultiSampling", GLideN64Fb, "opengl"),
                Key("mupen64plus-BilinearMode", GLideN64Fb, "opengl"),
                // ⚠ Deliberately NOT restricted:
                //  • mupen64plus-169screensize — DEAD in EVERY observed room, under both plugins. That reads
                //    as aspect-dependent (the 16:9 hint is only consulted when the room runs 16:9), not
                //    renderer-dependent, so hiding it on a profile would be the wrong axis entirely. AMBIGUOUS
                //    → stays visible, flagged for a longer run.
                //  • mupen64plus-EnableCopyColorFromRDRAM / CorrectTexrectCoords — same family by NAME, but
                //    absent from every DEAD set observed. No evidence, so no restriction.
            },

            // ── N64 compatibility core. Profiles: "parallel_n64" (gfxplugin=parallel, Vulkan),
            // "parallel_n64_gl" (GLideN64), "parallel_n64_glide64" (Glide64). THIS is why applicability is
            // keyed by profile id and not by surface: the last two share hwContext "gl" and read different
            // plugin options.
            ["parallel_n64"] = new[]
            {
                Key("parallel-n64-parallel-rdp-upscaling",
                    "catalog + profile note (both hand-written from the core source): paraLLEl-RDP's "
                    + "supersampling lever, 'inert on the GLideN64 (GL) profile'. ArcadeRendererProfiles moved "
                    + "it OUT of config.worker-gl.yaml onto the Vulkan profile for exactly this reason — every "
                    + "Glide64/GLideN64 room had been handed it as dead weight. Quality presets split the same "
                    + "way (parallel_n64/vulkan moves upscaling, parallel_n64/gl moves screensize).",
                    "parallel_n64"),
                // Latent (constructed longhand for the flag): parallel_n64 is a HAND-ONLY core (policy.json)
                // and its hand catalog exposes no gliden64-* key, so today this rule matches nothing the
                // module renders — those keys are render-profile-delivered only. Kept because it is the very
                // case that forced profile-id keying, and it binds the instant one of them is catalogued.
                new Rule("parallel-n64-gliden64-", true, new[] { "parallel_n64_gl" },
                    "worker log: the whole prefix DEAD under this core's own gliden64 path in both observed "
                    + "rooms (glworker.log 2026-07-23 03:16:49 / 03:15:53, build 4fc9396, 5/11 read) — plus "
                    + "structural: it is GLideN64's namespace, so Glide64 and paraLLEl-RDP cannot read it. The "
                    + "parallel_n64_glide64 profile deliberately ships NONE of these keys "
                    + "(ArcadeRendererProfiles) while parallel_n64_gl ships six.",
                    Latent: true),
                Key("parallel-n64-gfxplugin-accuracy",
                    "catalog note, hand-written from the core source: 'Accuracy vs speed of the OpenGL graphics "
                    + "plugins (GLideN64 / Glide64 / rice) … it does nothing on the Vulkan renderer.' Applies "
                    + "to BOTH GL profiles, which is the case a surface-keyed model would have got right by "
                    + "accident and the gliden64-* rule above would not.",
                    "parallel_n64_gl", "parallel_n64_glide64"),
                // ⚠ Deliberately NOT restricted:
                //  • parallel-n64-send_alist_to_lle_rsp — its note says "no effect on the Vulkan renderer,
                //    which is already accurate", which is a REASON not to use it there rather than proof the
                //    core never reads it, and it is profile-delivered for Glide64. Visible everywhere.
                //  • parallel-n64-send_allist_to_hle_rsp — the mirror trade; it PAIRS with Vulkan. Never hide.
                //  • parallel-n64-screensize — the catalog note says paraLLEl-RDP ignores it (same class as
                //    gfxplugin-accuracy above), and the quality presets treat it as the GL lever. It is a
                //    strong CANDIDATE for a {parallel_n64_gl, parallel_n64_glide64} restriction, deliberately
                //    NOT taken in this pass: nothing outside our own hand-written note corroborates it, and
                //    unlike gfxplugin-accuracy it is the core's headline resolution control, so a wrong
                //    restriction hides the most-used lever on the core. Settle it with a Vulkan boot test.
            },
        };

        // Shared evidence string for the eight GLideN64 framebuffer/AA keys — see the block comment above it.
        private const string GLideN64Fb =
            "structural: GLideN64's own framebuffer/AA settings, which rdp-plugin=parallel does not load "
            + "(paraLLEl-RDP does its own FB emulation). Delivered as a set by the n64 'opengl' render profile "
            + "for exactly that reason. Log support is CONFOUNDED and deliberately not leaned on: the 9-key "
            + "DEAD event under gliden64 (glworker.log 2026-07-29 21:41:49, 2026-07-23 10:27:00) saw these "
            + "keys LOWERCASED by the yaml loader, so they were dead for a case reason.";

        /// <summary>Every rule, flattened with its core — for the guard tests.</summary>
        public static IEnumerable<(string Core, Rule Rule)> AllRules() =>
            ByCore.SelectMany(kv => kv.Value.Select(r => (kv.Key, r)));

        /// <summary>The rule that governs a key on a core, or null if the key is unrestricted. Exact match
        /// wins; otherwise the LONGEST matching prefix, so a narrow namespace rule can override a broad one.</summary>
        private static Rule? RuleFor(string core, string key)
        {
            if (!ByCore.TryGetValue(core, out var rules)) return null;
            Rule? best = null;
            foreach (var r in rules)
            {
                if (!r.IsPrefix)
                {
                    if (string.Equals(r.Match, key, StringComparison.Ordinal)) return r;
                    continue;
                }
                if (key.StartsWith(r.Match, StringComparison.Ordinal)
                    && (best == null || r.Match.Length > best.Match.Length)) best = r;
            }
            return best;
        }

        /// <summary>Is this option live under this render profile? Unrestricted keys, unknown cores and a null
        /// profile (a system with no Graphics selector, or a caller that has no profile in hand) are always
        /// applicable — the module must only ever hide an option it can PROVE is inert.</summary>
        public static bool IsApplicable(string? core, string? key, string? profileId)
        {
            if (core == null || key == null || profileId == null) return true;
            var rule = RuleFor(core, key);
            return rule == null || rule.Profiles.Contains(profileId, StringComparer.Ordinal);
        }

        /// <summary>The core's catalogued options that are live under a render profile — what the ⚙ module
        /// renders. Null profile = no filtering (see <see cref="IsApplicable"/>).</summary>
        public static IReadOnlyList<ArcadeCoreOptionCatalog.CoreOption> OptionsFor(string? core, string? profileId)
        {
            var all = ArcadeCoreOptionCatalog.ForCore(core);
            if (core == null || profileId == null || !ByCore.ContainsKey(core)) return all;
            return all.Where(o => IsApplicable(core, o.Key, profileId)).ToList();
        }

        /// <summary>
        /// Merge a config save into the game's existing option blob, PRESERVING every saved key the module did
        /// not render for the profile being saved. <paramref name="submitted"/> is what the save decided to
        /// store (already baseline-dropped); anything not in it and not preserved here is intentionally gone
        /// ("reset to default" = drop the key), so the two preserved classes have to be exactly right:
        ///
        /// <para><b>(1) Other cores' keys.</b> The blob is flat but a system can have several cores (ps1
        /// Beetle vs pcsx_rearmed, n64 mupen vs parallel_n64). The module only ever edits the selected core's
        /// set, so another core's overrides must survive a profile switch.</para>
        ///
        /// <para><b>(2) The selected core's keys that are INAPPLICABLE to the selected profile.</b> ⚠ This is
        /// the data-loss trap Phase 2 creates. The modal posts the FULL rendered set, and GET now renders only
        /// the profile's applicable options — so a stored GSdx <c>pcsx2_upscale_multiplier</c> is simply not
        /// posted while paraLLEl-GS is selected. Without this clause, opening the panel on the Vulkan profile
        /// and pressing Save would silently delete the OpenGL profile's tuning. Same core, same blob, invisible
        /// loss. (Before Phase 2 this could not happen: every key of the selected core was always rendered.)</para>
        ///
        /// <para>Renderer-selecting keys are in NO catalog, so they match neither class and are dropped — which
        /// is the Phase 0 behaviour (the Graphics selector owns them). Unknown/advanced keys likewise are not
        /// preserved here: the module renders them as Advanced rows and the client re-submits them.</para>
        /// </summary>
        public static Dictionary<string, string> MergeSave(
            string? system, string? core, string? profileId,
            IReadOnlyDictionary<string, string> existing, IReadOnlyDictionary<string, string> submitted)
        {
            var keep = new HashSet<string>(StringComparer.Ordinal);

            foreach (var otherCore in ArcadeRendererProfiles.For(system).Select(p => p.OptionCore).Distinct()
                         .Where(c => !string.Equals(c, core, StringComparison.Ordinal)))
                foreach (var o in ArcadeCoreOptionCatalog.ForCore(otherCore)) keep.Add(o.Key);

            foreach (var o in ArcadeCoreOptionCatalog.ForCore(core))
                if (!IsApplicable(core, o.Key, profileId)) keep.Add(o.Key);

            var final = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var kv in existing) if (keep.Contains(kv.Key)) final[kv.Key] = kv.Value;
            foreach (var kv in submitted) final[kv.Key] = kv.Value;
            return final;
        }
    }
}
