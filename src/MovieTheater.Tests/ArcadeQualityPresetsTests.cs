using System.Linq;
using MovieTheater.Arcade;

namespace MovieTheater.Tests
{
    // The quality-tier presets ("Reset to defaults" dropdown) are option bundles shipped straight to
    // libretro cores, and libretro silently ignores an unknown key OR value token — a typo'd preset
    // would look applied and do nothing. Every preset entry must therefore resolve against the real
    // per-core option catalog (this test assembly embeds the extracted catalog for exactly this).
    public class ArcadeQualityPresetsTests
    {
        [Fact]
        public void EveryPresetKeyAndValueResolvesInTheCoreOptionCatalog()
        {
            var entries = ArcadeQualityPresets.AllEntries().ToList();
            Assert.NotEmpty(entries);
            Assert.All(entries, e =>
            {
                var opt = ArcadeCoreOptionCatalog.Find(e.Core, e.Key);
                Assert.True(opt != null, $"{e.Core}/{e.Tier}: unknown option key '{e.Key}'");
                Assert.True(opt!.IsValidToken(e.Value),
                    $"{e.Core}/{e.Tier}: '{e.Value}' is not a valid token for {e.Key}");
            });
        }

        // Ultra is the live system tuning: it must pin NOTHING, so a game reset to Ultra tracks
        // config.worker-gl.yaml as it gets retuned instead of freezing today's values.
        [Fact]
        public void UltraIsAlwaysEmpty()
        {
            Assert.Empty(ArcadeQualityPresets.AllEntries().Where(e => e.Tier == "ultra"));
            Assert.Empty(ArcadeQualityPresets.For("dolphin", "vulkan", "ultra"));
            Assert.Empty(ArcadeQualityPresets.For("pcsx2", "vulkan", "ultra"));
        }

        // Every preset lives under a tier the dropdown actually offers.
        [Fact]
        public void EveryPresetTierIsAKnownTier()
        {
            Assert.All(ArcadeQualityPresets.AllEntries(), e =>
                Assert.True(ArcadeQualityPresets.IsKnown(e.Tier), $"unknown tier '{e.Tier}'"));
            Assert.True(ArcadeQualityPresets.IsKnown(ArcadeQualityPresets.DefaultTier));
        }

        // Renderer-specific presets must exist for the combos whose quality keys differ by renderer:
        // paraLLEl-GS ignores pcsx2_upscale_multiplier, GLideN64 ignores parallel-rdp-upscaling.
        // pcsx2 is keyed by PROFILE ID (its two Vulkan-surface profiles read disjoint levers); the N64 cores
        // are still keyed by surface, and pass a null profile the way a surface-only caller would.
        [Fact]
        public void RendererSplitCombosResolvePerSurface()
        {
            Assert.Equal("Native", ArcadeQualityPresets.For("pcsx2", "parallel_gs", "vulkan", "low")["pcsx2_pgs_ssaa"]);
            Assert.Equal("1x Native (PS2)", ArcadeQualityPresets.For("pcsx2", "opengl", "gl", "low")["pcsx2_upscale_multiplier"]);
            Assert.Equal("1x", ArcadeQualityPresets.For("mupen64plus_next", "vulkan", "low")["mupen64plus-parallel-rdp-upscaling"]);
            Assert.Equal("320x240", ArcadeQualityPresets.For("mupen64plus_next", "gl", "low")["mupen64plus-43screensize"]);
        }

        // ── THE PS2 PRESET TRAP (plan Phase 3) ────────────────────────────────────────────────────────
        // "vulkan_gsdx" shares hwContext "vulkan" with "parallel_gs", so a surface-keyed lookup would hand a
        // GSdx room the paraLLEl-GS bundle. The controller's applicability filter would then strip every key
        // in it and store NOTHING — "Reset to Max" as a silently dead button, on the one path that
        // deliberately skips the baseline-drop. This asserts the two bundles are actually disjoint, that each
        // profile gets its own, and that neither survives the filter of the other.
        [Theory]
        [InlineData("parallel_gs")]
        [InlineData("vulkan_gsdx")]
        [InlineData("opengl")]
        public void EveryPs2ProfileGetsATierBundleItsOwnRoomCanRead(string profileId)
        {
            var hw = ArcadeRendererProfiles.For("ps2").Single(p => p.Id == profileId).HwContext;
            foreach (var tier in new[] { "max", "high", "medium", "low" })
            {
                var bundle = ArcadeQualityPresets.For("pcsx2", profileId, hw, tier);
                Assert.NotEmpty(bundle);
                Assert.All(bundle, kv => Assert.True(
                    ArcadeCoreOptionApplicability.IsApplicable("pcsx2", kv.Key, profileId),
                    $"ps2/{profileId}/{tier}: '{kv.Key}' is inert on the very profile the tier was applied " +
                    "for — the apply-time filter would drop it and the reset would store nothing."));
            }
        }

        // …and the mirror: the paraLLEl-GS-only and GSdx-only keys really are exclusive, so the bundles
        // cannot be silently swapped without this failing.
        [Fact]
        public void Ps2GsdxAndParallelGsBundlesShareNoKey()
        {
            foreach (var tier in new[] { "max", "high", "medium", "low" })
            {
                var pgs = ArcadeQualityPresets.For("pcsx2", "parallel_gs", "vulkan", tier).Keys;
                var gsdx = ArcadeQualityPresets.For("pcsx2", "vulkan_gsdx", "vulkan", tier).Keys;
                Assert.Empty(pgs.Intersect(gsdx));
                // Both GSdx profiles are the same GS on different surfaces (Phase 3: identical reconcile),
                // so they must resolve to the identical bundle.
                Assert.Equal(gsdx, ArcadeQualityPresets.For("pcsx2", "opengl", "gl", tier).Keys);
                // A GSdx reset stores no paraLLEl-only key, and vice versa.
                Assert.All(gsdx, k => Assert.False(ArcadeCoreOptionApplicability.IsApplicable("pcsx2", k, "parallel_gs")));
                Assert.All(pgs, k => Assert.False(ArcadeCoreOptionApplicability.IsApplicable("pcsx2", k, "vulkan_gsdx")));
            }
        }

        // ── Presets vs applicability (plan Phase 2.5) ─────────────────────────────────────────────────
        // A preset is keyed by a SCOPE — a render-profile id, an hwContext, or null (core-wide) — and a
        // surface scope is ONE NOTCH COARSER than a profile, while the config module hides options that are
        // inert on the selected profile. If a tier pinned a key that no profile in its scope can read,
        // "Reset to High" would store an override the room ignores: the silent-no-op class, arriving through
        // the one path that deliberately skips the baseline-drop. parallel_n64 is the case that makes this
        // real — its "gl" bundle serves BOTH gl profiles (parallel_n64_gl = GLideN64 and
        // parallel_n64_glide64 = Glide64), so it may only contain keys at least one of them reads; a bundle
        // of gliden64-* keys would be half inert. It contains screensize + cpucore, which both read.
        // (The controller ALSO filters at apply time, so the invariant holds even if a future preset breaks
        // this — but a preset that needs the filter is a preset that is lying about a renderer, and this test
        // is what says so.)
        [Fact]
        public void EveryPresetKeyIsLiveOnSomeProfileInItsScope()
        {
            foreach (var e in ArcadeQualityPresets.AllEntries())
            {
                // A scope matches a profile by ID first (the pcsx2 case) and by surface otherwise; null
                // matches every profile of the core. Resolving both ways here is what keeps this test honest
                // after Phase 3 re-keyed pcsx2 — matching on HwContext alone would have quietly found NO
                // candidates for the profile-keyed entries and skipped them.
                var candidates = ArcadeRendererProfiles.AllSystems
                    .SelectMany(s => ArcadeRendererProfiles.For(s))
                    .Where(p => p.OptionCore == e.Core
                                && (e.Scope == null || p.Id == e.Scope || p.HwContext == e.Scope))
                    .ToList();
                if (candidates.Count == 0) continue;   // no Graphics selector for this core → nothing filters
                Assert.True(
                    candidates.Any(p => ArcadeCoreOptionApplicability.IsApplicable(e.Core, e.Key, p.Id)),
                    $"{e.Core}/{e.Scope ?? "any"}/{e.Tier}: '{e.Key}' is not live on ANY render profile in that " +
                    "scope, so applying the tier would store a key the room cannot read.");
            }
        }

        // The scope of every preset entry must name something real. A profile rename (Phase 3 rewrote the PS2
        // ids) would otherwise leave a bundle keyed to a profile that no longer exists — the lookup would fall
        // through to the surface bundle, or to nothing, with no error anywhere.
        [Fact]
        public void EveryPresetScopeNamesARealProfileOrSurface()
        {
            foreach (var e in ArcadeQualityPresets.AllEntries().Where(e => e.Scope != null))
            {
                var profiles = ArcadeRendererProfiles.AllSystems
                    .SelectMany(s => ArcadeRendererProfiles.For(s))
                    .Where(p => p.OptionCore == e.Core).ToList();
                Assert.True(
                    profiles.Any(p => p.Id == e.Scope || p.HwContext == e.Scope),
                    $"{e.Core}: preset scope '{e.Scope}' is neither a render-profile id nor an hwContext of " +
                    $"this core ({string.Join(", ", profiles.Select(p => $"{p.Id}/{p.HwContext}"))}).");
            }
        }

        // Surface-agnostic cores fall back from any hwContext to the core-wide preset, and a core
        // with no presets (2D) returns empty for every tier — the tiers all equal the live default.
        [Fact]
        public void HwContextFallsBackToCoreWidePresets()
        {
            Assert.Equal(
                ArcadeQualityPresets.For("dolphin", "vulkan", "low"),
                ArcadeQualityPresets.For("dolphin", "gl", "low"));
            Assert.NotEmpty(ArcadeQualityPresets.For("flycast", null, "low"));
            Assert.Empty(ArcadeQualityPresets.For("snes9x", "gl", "low"));
            Assert.Empty(ArcadeQualityPresets.For(null, null, "low"));
        }

        // pcsx_rearmed (software) has exactly one lever and only Low uses it — everything else must
        // stay the live default rather than pinning noise.
        [Fact]
        public void SoftwareCoreOnlyStepsDownAtLow()
        {
            Assert.Empty(ArcadeQualityPresets.For("pcsx_rearmed", null, "high"));
            Assert.Equal("disabled",
                ArcadeQualityPresets.For("pcsx_rearmed", null, "low")["pcsx_rearmed_neon_enhancement_enable"]);
        }

        // The declared-Ultra spec must itself use real keys and tokens.
        [Fact]
        public void UltraSpecKeysAndValuesResolveInTheCoreOptionCatalog()
        {
            foreach (var (core, options) in ArcadeQualityPresets.UltraLiveSpec)
                foreach (var (key, value) in options)
                {
                    var opt = ArcadeCoreOptionCatalog.Find(core, key);
                    Assert.True(opt != null, $"{core}: unknown Ultra-spec key '{key}'");
                    Assert.True(opt!.IsValidToken(value), $"{core}: '{value}' is not a valid token for {key}");
                }
        }

        // ── The WELD ──────────────────────────────────────────────────────────────────────────────
        // Ultra stores nothing, which is only safe because the live worker config already delivers
        // exactly the declared Ultra values. This test parses docker/arcade/config.worker-gl.yaml and
        // fails on any disagreement, so the yaml cannot drift from the declared Ultra silently:
        // retuning a lever forces a conscious "this is the new Ultra" (update both) or "that value
        // belongs in another tier" decision. It also guards rejected modes (flycast per-pixel alpha)
        // from quietly coming back as live defaults.
        [Fact]
        public void LiveWorkerConfigMatchesTheDeclaredUltraSpec()
        {
            var yamlPath = FindRepoFile(Path.Combine("docker", "arcade", "config.worker-gl.yaml"));
            var occurrences = ParseYamlOptionLines(File.ReadAllLines(yamlPath));

            foreach (var (core, options) in ArcadeQualityPresets.UltraLiveSpec)
                foreach (var (key, value) in options)
                {
                    Assert.True(occurrences.TryGetValue(key, out var found) && found.Count > 0,
                        $"{core}: Ultra-spec key '{key}' not found in config.worker-gl.yaml — " +
                        "either add it to the yaml or drop it from UltraLiveSpec.");
                    Assert.All(found!, v => Assert.True(v == value,
                        $"{core}: config.worker-gl.yaml has {key}: '{v}' but the declared Ultra is '{value}'. " +
                        "Update UltraLiveSpec (new Ultra) or the yaml (mis-tuned default) — deliberately."));
                }
        }

        // Every ACTIVE (non-comment) `key: value` / `key: "value"` line in the yaml, keyed by option
        // key. Comments (whole-line and trailing) are ignored, so rejected alternatives that live on
        // commented-out lines never count.
        private static Dictionary<string, List<string>> ParseYamlOptionLines(string[] lines)
        {
            var quoted = new System.Text.RegularExpressions.Regex("^([A-Za-z0-9_-]+):\\s*\"([^\"]*)\"");
            var bare = new System.Text.RegularExpressions.Regex("^([A-Za-z0-9_-]+):\\s*([^\\s#]+)\\s*(#.*)?$");
            var map = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            foreach (var raw in lines)
            {
                var line = raw.Trim();
                if (line.StartsWith("#")) continue;
                var m = quoted.Match(line);
                if (!m.Success) m = bare.Match(line);
                if (!m.Success) continue;
                (map.TryGetValue(m.Groups[1].Value, out var list)
                    ? list
                    : map[m.Groups[1].Value] = new List<string>()).Add(m.Groups[2].Value);
            }
            return map;
        }

        private static string FindRepoFile(string relative)
        {
            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            {
                var candidate = Path.Combine(dir.FullName, relative);
                if (File.Exists(candidate)) return candidate;
            }
            throw new FileNotFoundException($"'{relative}' not found walking up from {AppContext.BaseDirectory}");
        }
    }
}
