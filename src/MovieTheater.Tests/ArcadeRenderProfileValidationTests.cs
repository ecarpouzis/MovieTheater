using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using MovieTheater.Arcade;
using Xunit;

namespace MovieTheater.Tests
{
    /// <summary>
    /// Closes D5 of docs/arcade-config-module-dead-options-plan.md: <b>every key and value token a
    /// render profile ships is validated against what the deployed cores actually declare.</b>
    ///
    /// Renderer-selecting keys are deliberately absent from the config module's catalog (the Graphics
    /// selector owns them), so before Phase 1 they were validated by NOTHING — a typo'd or retired
    /// renderer token would sit in <see cref="ArcadeRendererProfiles"/> indefinitely and fail silently,
    /// because libretro never errors on a key or token it does not recognise. The Phase 1 extraction now
    /// emits those keys into the catalog JSON's <c>rendererKeys</c> sidecar (straight from each deployed
    /// DLL's own option structs), which is what this test validates against.
    /// </summary>
    public class ArcadeRenderProfileValidationTests
    {
        // (core, key) -> declared tokens, from the rendererKeys sidecar of the embedded catalog JSON.
        private static readonly Dictionary<(string Core, string Key), HashSet<string>> SidecarTokens = LoadSidecar();

        private static Dictionary<(string, string), HashSet<string>> LoadSidecar()
        {
            var result = new Dictionary<(string, string), HashSet<string>>();
            var asm = typeof(ArcadeCoreOptionCatalog).Assembly;
            var name = asm.GetManifestResourceNames()
                .FirstOrDefault(n => n.EndsWith("core-options-catalog.json", StringComparison.OrdinalIgnoreCase))
                // The test assembly embeds the same resource (MovieTheater.Tests.csproj); prefer the web
                // assembly's copy but fall back so the test never silently validates against nothing.
                ?? throw new InvalidOperationException("core-options-catalog.json resource not found");
            using var stream = asm.GetManifestResourceStream(name)!;
            using var doc = JsonDocument.Parse(stream);
            if (!doc.RootElement.TryGetProperty("rendererKeys", out var cores)) return result;
            foreach (var core in cores.EnumerateObject())
            {
                if (!core.Value.TryGetProperty("options", out var opts)) continue;
                foreach (var o in opts.EnumerateArray())
                {
                    var key = o.GetProperty("key").GetString()!;
                    var tokens = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var v in o.GetProperty("values").EnumerateArray())
                        tokens.Add(v.GetProperty("token").GetString()!);
                    result[(core.Name, key)] = tokens;
                }
            }
            return result;
        }

        // parallel_n64 is a HAND-ONLY core (policy.json): its full declared option set is deliberately
        // not folded into the catalog, and the sidecar carries only its renderer-selecting keys. The
        // GLideN64 framebuffer keys its "parallel_n64_gl" profile ships therefore exist in neither
        // validation source. They were read from the core's own libretro_core_options.h (see the profile's
        // comment block, 2026-07-29) — this allowlist records exactly that residue so anything ELSE
        // unknown still fails. Shrink it, never grow it casually: a new entry here is a claim no test
        // checks.
        private static readonly HashSet<string> HandVerifiedUncatalogued = new(StringComparer.Ordinal)
        {
            "parallel-n64-gliden64-EnableFBEmulation",
            "parallel-n64-gliden64-EnableCopyColorToRDRAM",
            "parallel-n64-gliden64-EnableCopyDepthToRDRAM",
            "parallel-n64-gliden64-EnableLegacyBlending",
            "parallel-n64-gliden64-EnableCopyAuxToRDRAM",
            "parallel-n64-gliden64-EnableNativeResTexrects",
        };

        public static IEnumerable<object[]> AllProfileOptions() =>
            ArcadeRendererProfiles.AllSystems
                .SelectMany(s => ArcadeRendererProfiles.For(s), (s, p) => (System: s, Profile: p))
                .SelectMany(sp => sp.Profile.Options,
                    (sp, kv) => new object[] { sp.System, sp.Profile.Id, sp.Profile.OptionCore, kv.Key, kv.Value });

        [Theory]
        [MemberData(nameof(AllProfileOptions))]
        public void EveryProfileOptionKeyAndTokenIsDeclaredByItsCore(
            string system, string profileId, string core, string key, string token)
        {
            // 1) A plain catalogued option (e.g. parallel-n64-send_alist_to_lle_rsp, the mupen FB set).
            var catalogued = ArcadeCoreOptionCatalog.Find(core, key);
            if (catalogued != null)
            {
                Assert.True(catalogued.IsValidToken(token),
                    $"{system}/{profileId}: '{token}' is not a declared value of {key} on {core}.");
                return;
            }
            // 2) A renderer-selecting key, validated against the deployed DLL's own declaration.
            if (SidecarTokens.TryGetValue((core, key), out var tokens))
            {
                Assert.True(tokens.Contains(token),
                    $"{system}/{profileId}: '{token}' is not a token the deployed {core} DLL declares for {key}.");
                return;
            }
            // 3) The documented hand-verified residue.
            Assert.True(HandVerifiedUncatalogued.Contains(key),
                $"{system}/{profileId}: {key} is in no catalog, not in the rendererKeys sidecar, and not in " +
                "the hand-verified allowlist — nothing validates it. Regenerate the catalog " +
                "(scripts/extract-core-options.ps1) or justify a new allowlist entry.");
        }

        [Fact]
        public void SidecarCoversEveryRendererSelectingKeyTheProfilesShip()
        {
            // The sidecar exists FOR this test; if a catalog regeneration ever drops it (or a new
            // renderer-selecting key never lands in it), the theory above would quietly fall through to
            // the allowlist assert. Pin the coverage explicitly.
            var shipped = ArcadeRendererProfiles.AllSystems
                .SelectMany(ArcadeRendererProfiles.For)
                .SelectMany(p => p.Options.Keys.Where(ArcadeCoreOptionCatalog.IsRendererSelecting)
                    .Select(k => (p.OptionCore, Key: k)))
                .Distinct().ToList();
            Assert.NotEmpty(shipped);
            foreach (var (core, key) in shipped)
                Assert.True(SidecarTokens.ContainsKey((core, key)),
                    $"rendererKeys sidecar is missing {core}/{key} — regenerate the catalog.");
        }
    }
}
