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
        [Fact]
        public void RendererSplitCombosResolvePerSurface()
        {
            Assert.Equal("Native", ArcadeQualityPresets.For("pcsx2", "vulkan", "low")["pcsx2_pgs_ssaa"]);
            Assert.Equal("1x Native (PS2)", ArcadeQualityPresets.For("pcsx2", "gl", "low")["pcsx2_upscale_multiplier"]);
            Assert.Equal("1x", ArcadeQualityPresets.For("mupen64plus_next", "vulkan", "low")["mupen64plus-parallel-rdp-upscaling"]);
            Assert.Equal("320x240", ArcadeQualityPresets.For("mupen64plus_next", "gl", "low")["mupen64plus-43screensize"]);
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
