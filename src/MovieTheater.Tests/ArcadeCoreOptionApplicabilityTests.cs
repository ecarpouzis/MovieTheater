using System.Collections.Generic;
using System.Linq;
using MovieTheater.Arcade;
using Xunit;

namespace MovieTheater.Tests
{
    // Phase 2 of docs/arcade-config-module-dead-options-plan.md (defect D3): the config module used to filter
    // its option list by CORE alone, but a room is a (core, RENDERER) pair — so both PS2 profiles rendered the
    // union of paraLLEl-GS's and GSdx's levers and marked none of them. libretro never errors on an option it
    // does not recognise, so an inert row is indistinguishable from a working one; only a test can hold the
    // list honest. These tests pin the two halves: switching Graphics really changes the set, and nothing a
    // profile renders is restricted away from it.
    public class ArcadeCoreOptionApplicabilityTests
    {
        private static string[] KeysFor(string core, string profileId) =>
            ArcadeCoreOptionApplicability.OptionsFor(core, profileId).Select(o => o.Key).ToArray();

        // Every (system, profile) the site can boot, paired with the core whose options it shows.
        private static IEnumerable<(string System, ArcadeRendererProfiles.RenderProfile Profile)> AllProfiles() =>
            ArcadeRendererProfiles.AllSystems.SelectMany(s => ArcadeRendererProfiles.For(s).Select(p => (s, p)));

        // ── (a) The user-visible promise: switching Graphics changes the option set ────────────────────

        // PS2 is the headline case. paraLLEl-GS and GSdx are different GS implementations sharing one
        // OptionCore, and Phase 3's boot tests ran all three PS2 renderers on one game with one provided
        // option set: the DEAD sets are an exact mirror (docs/arcade/opt-reconcile-evidence-2026-08-02.md,
        // "Phase 3 boot tests"). Note vulkan_gsdx and parallel_gs share hwContext "vulkan" — a surface-keyed
        // model could not tell them apart at all, which is the whole reason for profile-id keying.
        [Fact]
        public void Ps2ProfilesRenderDifferentOptionSets()
        {
            var pgs = KeysFor("pcsx2", "parallel_gs");     // paraLLEl-GS (default)
            var vkGsdx = KeysFor("pcsx2", "vulkan_gsdx");  // PCSX2's own GS, Vulkan surface
            var glGsdx = KeysFor("pcsx2", "opengl");       // the same GS, GL surface

            Assert.NotEqual(pgs, vkGsdx);
            // The two GSdx profiles read the same set — same GS, different surface (7/9 with the identical
            // DEAD pair in both Phase 3 arms). Only the surface differs, so the option list must not.
            Assert.Equal(vkGsdx, glGsdx);

            foreach (var gsdxOnly in new[] { "pcsx2_upscale_multiplier", "pcsx2_anisotropic_filtering", "pcsx2_blending_accuracy" })
            {
                Assert.Contains(gsdxOnly, vkGsdx);
                Assert.Contains(gsdxOnly, glGsdx);
                Assert.DoesNotContain(gsdxOnly, pgs);
            }
            // paraLLEl-GS's own levers, log-proven DEAD on both GSdx backends.
            // pcsx2_pgs_deblur and pcsx2_pgs_ss_tex joined this list on 2026-08-02: they ship in NO room, so
            // Phase 3 could not measure them and they rode the namespace argument alone. A dedicated boot
            // pair PROVIDED them (temporary ArcadeGameProfile row, deleted after) on the same game with the
            // same 11-key set: paraLLEl-GS 8/11 with both READ (glworker.log 14:50:12), Vulkan (GSdx) 7/11
            // with both in the DEAD set (14:51:38). Pinned here because the restriction is now a measurement,
            // and un-restricting them would have to overturn that measurement rather than a guess.
            foreach (var pgsOnly in new[]
                     {
                         "pcsx2_pgs_ssaa", "pcsx2_pgs_high_res_scanout",
                         "pcsx2_pgs_deblur", "pcsx2_pgs_ss_tex",
                     })
            {
                Assert.Contains(pgsOnly, pgs);
                Assert.DoesNotContain(pgsOnly, vkGsdx);
                Assert.DoesNotContain(pgsOnly, glGsdx);
            }

            // ⚠ The counterexample that keeps the prefix rule honest: despite its name, LRPS2 READS
            // pcsx2_pgs_disable_mipmaps under all three renderers (provided in all three Phase 3 arms, DEAD
            // in none). Hiding the whole pcsx2_pgs_ namespace on GSdx would hide a working lever.
            Assert.Contains("pcsx2_pgs_disable_mipmaps", pgs);
            Assert.Contains("pcsx2_pgs_disable_mipmaps", vkGsdx);
            Assert.Contains("pcsx2_pgs_disable_mipmaps", glGsdx);

            // Everything else must still be on ALL THREE — the restriction is narrow by design (no evidence
            // yet on pcsx2_texture_filtering / trilinear / dithering, so they stay visible).
            Assert.Contains("pcsx2_nointerlacing_hint", pgs);
            Assert.Contains("pcsx2_nointerlacing_hint", vkGsdx);
            Assert.Contains("pcsx2_nointerlacing_hint", glGsdx);
        }

        // Phase 3, D6: the profile set itself is a claim, and the claim used to be false — the profile
        // LABELLED "Vulkan" selected paraLLEl-GS, and PCSX2's own Vulkan was unreachable. Every renderer
        // offered here was booted and streamed first (evidence doc, "Phase 3 boot tests"); this pins the
        // shape so a future edit cannot quietly reintroduce a label that names the wrong GS.
        [Fact]
        public void Ps2OffersThreeBootVerifiedRenderersAndNoneIsMislabelled()
        {
            var ps2 = ArcadeRendererProfiles.For("ps2");
            Assert.Equal(new[] { "parallel_gs", "vulkan_gsdx", "opengl" }, ps2.Select(p => p.Id));
            Assert.Equal("parallel_gs", ArcadeRendererProfiles.Default("ps2")!.Id);

            // Each profile selects the renderer token its label names.
            Assert.Equal("paraLLEl-GS", ps2.Single(p => p.Id == "parallel_gs").Options["pcsx2_renderer"]);
            Assert.Equal("Vulkan", ps2.Single(p => p.Id == "vulkan_gsdx").Options["pcsx2_renderer"]);
            Assert.Equal("OpenGL", ps2.Single(p => p.Id == "opengl").Options["pcsx2_renderer"]);

            // The two Vulkan-surface profiles are distinguishable ONLY by id — the case that forced both
            // applicability and the quality presets off HwContext keying.
            Assert.Equal("vulkan", ps2.Single(p => p.Id == "parallel_gs").HwContext);
            Assert.Equal("vulkan", ps2.Single(p => p.Id == "vulkan_gsdx").HwContext);
            Assert.Equal("gl", ps2.Single(p => p.Id == "opengl").HwContext);

            // No label may say "Vulkan" without saying WHICH GS — that ambiguity was defect D6.1.
            Assert.All(ps2, p => Assert.True(
                !p.Label.Contains("Vulkan") || p.Label.Contains("GSdx") || p.Label.Contains("paraLLEl-GS"),
                $"ps2/{p.Id}: label '{p.Label}' says Vulkan without naming the GS implementation."));

            // Bare Force GL / Force Vulkan must still land somewhere real (the play-button fallback path).
            Assert.Equal("parallel_gs", ArcadeRendererProfiles.ForRenderer("ps2", "vulkan")!.Id);
            Assert.Equal("opengl", ArcadeRendererProfiles.ForRenderer("ps2", "gl")!.Id);
        }

        // N64 has BOTH shapes: one core with two renderers (mupen64plus_next), and a second core whose two GL
        // profiles differ from each other — which is why applicability is keyed by profile id, not HwContext.
        [Fact]
        public void N64ProfilesRenderDifferentOptionSets()
        {
            var mupenVk = KeysFor("mupen64plus_next", "vulkan");    // rdp-plugin=parallel (paraLLEl-RDP)
            var mupenGl = KeysFor("mupen64plus_next", "opengl");    // rdp-plugin=gliden64

            Assert.NotEqual(mupenVk, mupenGl);
            Assert.Contains("mupen64plus-parallel-rdp-upscaling", mupenVk);
            Assert.DoesNotContain("mupen64plus-parallel-rdp-upscaling", mupenGl);
            foreach (var glideKey in new[]
                     {
                         "mupen64plus-EnableFBEmulation", "mupen64plus-EnableCopyAuxToRDRAM",
                         "mupen64plus-EnableCopyColorToRDRAM", "mupen64plus-EnableCopyDepthToRDRAM",
                         "mupen64plus-EnableLegacyBlending", "mupen64plus-EnableNativeResTexrects",
                         "mupen64plus-MultiSampling", "mupen64plus-BilinearMode",
                     })
            {
                Assert.Contains(glideKey, mupenGl);
                Assert.DoesNotContain(glideKey, mupenVk);
            }
            // AMBIGUOUS stays visible: 169screensize was DEAD in every observed room under BOTH plugins, which
            // reads as aspect-dependent rather than renderer-dependent. Hiding it would be the wrong axis.
            Assert.Contains("mupen64plus-169screensize", mupenVk);
            Assert.Contains("mupen64plus-169screensize", mupenGl);

            var parallelVk = KeysFor("parallel_n64", "parallel_n64");
            var parallelGl = KeysFor("parallel_n64", "parallel_n64_gl");
            Assert.NotEqual(parallelVk, parallelGl);
            Assert.Contains("parallel-n64-parallel-rdp-upscaling", parallelVk);
            Assert.DoesNotContain("parallel-n64-parallel-rdp-upscaling", parallelGl);
            Assert.Contains("parallel-n64-gfxplugin-accuracy", parallelGl);
            Assert.DoesNotContain("parallel-n64-gfxplugin-accuracy", parallelVk);

            // The GL-plugin accuracy lever is live on BOTH gl-surface profiles — the case a HwContext-keyed
            // model would have got right only by accident. (The two GL profiles render the same set today:
            // the parallel-n64-gliden64-* rule that separates them is latent, because this core is hand-only
            // and the module does not expose those keys at all yet.)
            Assert.Contains("parallel-n64-gfxplugin-accuracy", KeysFor("parallel_n64", "parallel_n64_glide64"));
        }

        // Cores with a renderer/surface split but no renderer-split RESTRICTION must not be filtered at all.
        // ⚠ Updated 2026-08-02: for three of these four the reason changed from "no evidence" to a measured
        // negative. The six surface-only GL profiles were booted for the first time on that date and each
        // reconciled key-for-key with its Vulkan control — ppsspp 5/5 (glworker-2 14:34:26 vs 2026-07-30
        // 13:29:22), flycast 3/3 (glworker 14:36:38 / 14:38:30 / 14:40:33 vs glworker-2 2026-08-02 01:45:56),
        // dolphin 8/8 gc (glworker 14:42:04 vs glworker-2 2026-07-30 13:34:19) and 11/11 wii (glworker-2
        // 14:44:30 vs 2026-07-31 22:45:29) — with zero DEAD keys on either surface. So "never filtered" is
        // now the answer the logs give, not just the safe default. beetle_psx_hw is the one still unmeasured:
        // its GL profile has never been booted, and it stays here on the visible-by-default policy.
        // (Evidence: docs/arcade/opt-reconcile-evidence-2026-08-02.md, "GL-profile verification + pgs
        // evidence". Caveat that keeps this honest: a reconcile can only speak for keys a room SHIPS, so
        // flycast's commented-out reicast_oit_* knobs remain untested in either direction.)
        [Theory]
        [InlineData("beetle_psx_hw", "beetle_vulkan", "beetle_opengl")]
        [InlineData("flycast", "vulkan", "opengl")]
        [InlineData("dolphin", "vulkan", "opengl")]
        [InlineData("ppsspp", "vulkan", "opengl")]
        public void CoresWithoutEvidenceAreNeverFiltered(string core, string a, string b)
        {
            var all = ArcadeCoreOptionCatalog.ForCore(core).Select(o => o.Key).ToArray();
            Assert.NotEmpty(all);
            Assert.Equal(all, KeysFor(core, a));
            Assert.Equal(all, KeysFor(core, b));
        }

        // A system with no Graphics selector (and any caller with no profile in hand) must never filter.
        [Fact]
        public void NoProfileMeansNoFiltering()
        {
            Assert.Equal(
                ArcadeCoreOptionCatalog.ForCore("pcsx2").Select(o => o.Key),
                ArcadeCoreOptionApplicability.OptionsFor("pcsx2", null).Select(o => o.Key));
            Assert.True(ArcadeCoreOptionApplicability.IsApplicable("pcsx2", "pcsx2_pgs_ssaa", null));
            Assert.True(ArcadeCoreOptionApplicability.IsApplicable("snes9x", "snes9x_region", "vulkan"));
            Assert.True(ArcadeCoreOptionApplicability.IsApplicable(null, null, null));
        }

        // ── (b) The structural D3 guard ───────────────────────────────────────────────────────────────

        // Both directions, for every profile the site can boot: nothing rendered is inert under the profile
        // rendering it, and nothing hidden was hidden for any reason other than a recorded restriction.
        [Fact]
        public void NoProfileRendersAnOptionItCannotRead()
        {
            foreach (var (system, p) in AllProfiles())
            {
                var rendered = ArcadeCoreOptionApplicability.OptionsFor(p.OptionCore, p.Id).Select(o => o.Key).ToHashSet();
                foreach (var o in ArcadeCoreOptionCatalog.ForCore(p.OptionCore))
                {
                    var applicable = ArcadeCoreOptionApplicability.IsApplicable(p.OptionCore, o.Key, p.Id);
                    Assert.True(rendered.Contains(o.Key) == applicable,
                        $"{system}/{p.Id} ({p.OptionCore}): '{o.Key}' is " +
                        (applicable ? "applicable but not rendered" : "rendered but restricted away from this profile") + ".");
                }
            }
        }

        // A restriction names profile ids in its CORE's namespace. A typo, or a profile renamed/retired out
        // from under a rule (Phase 3 rewrites the PS2 profile set), would silently turn the rule into "hidden
        // on every profile" — the exact opposite of the visible-by-default policy.
        [Fact]
        public void EveryRestrictionNamesRealProfilesOfItsCore()
        {
            foreach (var (core, rule) in ArcadeCoreOptionApplicability.AllRules())
            {
                var ids = AllProfiles().Where(x => x.Profile.OptionCore == core).Select(x => x.Profile.Id).ToHashSet();
                Assert.True(ids.Count > 0, $"{core}: has applicability rules but no render profile selects it.");
                Assert.NotEmpty(rule.Profiles);
                Assert.All(rule.Profiles, id => Assert.True(ids.Contains(id),
                    $"{core}/'{rule.Match}': '{id}' is not a render profile of this core ({string.Join(", ", ids)})."));
            }
        }

        // Plan Phase 4.2: an applicability entry with no evidence is a guess. The evidence string must say
        // WHERE the restriction comes from — a worker-log date/room, or the structural reasoning.
        [Fact]
        public void EveryRestrictionCitesEvidence()
        {
            Assert.NotEmpty(ArcadeCoreOptionApplicability.AllRules());
            foreach (var (core, rule) in ArcadeCoreOptionApplicability.AllRules())
                Assert.True(rule.Evidence is { Length: > 40 },
                    $"{core}/'{rule.Match}': no (or too thin) evidence recorded — an applicability entry without evidence is a guess.");
        }

        // A rule that matches no catalogued key does nothing, and would go on doing nothing silently if a
        // catalog regeneration renamed the key out from under it. The one deliberate exception is flagged
        // Latent in the rule itself (parallel_n64 is hand-only and exposes no gliden64-* key to the module).
        [Fact]
        public void EveryNonLatentRestrictionMatchesACataloguedKey()
        {
            foreach (var (core, rule) in ArcadeCoreOptionApplicability.AllRules())
            {
                if (rule.Latent) continue;
                var matches = ArcadeCoreOptionCatalog.ForCore(core).Count(o =>
                    rule.IsPrefix ? o.Key.StartsWith(rule.Match, System.StringComparison.Ordinal) : o.Key == rule.Match);
                Assert.True(matches > 0,
                    $"{core}: applicability rule '{rule.Match}' matches no catalogued option — the key was renamed " +
                    "or removed by a catalog regeneration, so the restriction is silently doing nothing.");
            }
        }

        // ── (c) The data-loss trap Phase 2 creates ────────────────────────────────────────────────────

        // ⚠ The regression that motivated MergeSave. The modal posts the FULL RENDERED set, and GET now renders
        // only the selected profile's applicable options — so a stored GSdx override is simply absent from the
        // payload while paraLLEl-GS is selected. Without preservation, opening the panel on Vulkan and pressing
        // Save (changing something else entirely, or nothing) silently deletes the OpenGL profile's tuning.
        // Same core, same blob, no error, no way to notice.
        [Fact]
        public void SavingUnderOneProfileKeepsTheOtherProfilesStoredOverrides()
        {
            var existing = new Dictionary<string, string>
            {
                ["pcsx2_upscale_multiplier"] = "4x Native (~1440p/2K)",  // GSdx-only: NOT rendered under vulkan
                ["pcsx2_anisotropic_filtering"] = "16x",                 // GSdx-only
                ["pcsx2_nointerlacing_hint"] = "enabled",                // rendered under BOTH profiles
            };
            var submitted = new Dictionary<string, string> { ["pcsx2_pgs_ssaa"] = "4x SSAA (ordered, can high-res)" };

            var merged = ArcadeCoreOptionApplicability.MergeSave("ps2", "pcsx2", "parallel_gs", existing, submitted);

            Assert.Equal("4x Native (~1440p/2K)", merged["pcsx2_upscale_multiplier"]);
            Assert.Equal("16x", merged["pcsx2_anisotropic_filtering"]);
            Assert.Equal("4x SSAA (ordered, can high-res)", merged["pcsx2_pgs_ssaa"]);
            // The applicable-but-unsubmitted key must still be DROPPED: it WAS rendered, so its absence from
            // the payload is the editor resetting it to default. Preserving it would break "reset to default".
            Assert.False(merged.ContainsKey("pcsx2_nointerlacing_hint"));
        }

        // The mirror case, and the pre-existing behaviour this must not regress: another CORE's overrides
        // survive a profile switch (n64's two cores share one flat blob).
        [Fact]
        public void SavingUnderOneCoreKeepsTheOtherCoresStoredOverrides()
        {
            var existing = new Dictionary<string, string>
            {
                ["parallel-n64-cpucore"] = "dynamic_recompiler",             // parallel_n64's core, not mupen's
                ["mupen64plus-parallel-rdp-upscaling"] = "8x",               // mupen + vulkan: rendered here
                ["mupen64plus-EnableLegacyBlending"] = "True",               // mupen + GLideN64: NOT rendered here
                ["pcsx2_renderer"] = "paraLLEl-GS",                          // renderer-selecting: must be dropped
            };
            var submitted = new Dictionary<string, string> { ["mupen64plus-43screensize"] = "960x720" };

            var merged = ArcadeCoreOptionApplicability.MergeSave("n64", "mupen64plus_next", "vulkan", existing, submitted);

            Assert.Equal("dynamic_recompiler", merged["parallel-n64-cpucore"]);
            Assert.Equal("True", merged["mupen64plus-EnableLegacyBlending"]);
            Assert.Equal("960x720", merged["mupen64plus-43screensize"]);
            Assert.False(merged.ContainsKey("mupen64plus-parallel-rdp-upscaling"));
            // The Graphics selector owns renderer keys and they are in no catalog, so they match neither
            // preserved class — Phase 0's behaviour, restated here because MergeSave is now where it happens.
            Assert.False(merged.ContainsKey("pcsx2_renderer"));
        }
    }
}
