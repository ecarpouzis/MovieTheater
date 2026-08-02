using System.Linq;
using MovieTheater.Arcade;
using Xunit;

namespace MovieTheater.Tests
{
    // Guards the invariant behind a production bug found 2026-08-02 on ps2/Stuntman: renderer-selecting
    // keys belong to the Graphics selector alone. They are deliberately absent from every option catalog,
    // which USED to mean a stored one surfaced in the ⚙ Configure module as an "advanced" raw row — the
    // module re-submits those on every save, and the launch path merges the render profile's options only
    // as a BASE beneath the saved config. So picking OpenGL for Stuntman saved the profile, and the raw
    // pcsx2_renderer row wrote paraLLEl-GS straight back over it. The dropdown looked inert.
    public class ArcadeCoreOptionCatalogTests
    {
        private static string[] AllCores() =>
            ArcadeRendererProfiles.AllSystems
                .SelectMany(s => ArcadeRendererProfiles.For(s))
                .Select(p => p.OptionCore)
                .Distinct()
                .ToArray();

        [Fact]
        public void RendererSelectingKeysAreFlagged()
        {
            var rendererKeys = new[]
            {
                "pcsx2_renderer", "beetle_psx_hw_renderer",
                "mupen64plus-rdp-plugin", "mupen64plus-rsp-plugin",
                "parallel-n64-gfxplugin", "parallel-n64-rspplugin",
            };

            Assert.All(rendererKeys, k =>
                Assert.True(ArcadeCoreOptionCatalog.IsRendererSelecting(k),
                    $"'{k}' is a renderer key but IsRendererSelecting says otherwise — it would round-trip as an advanced row and beat the Graphics selector."));
        }

        // The precise invariant that broke: a key the Graphics selector FLIPS — one two profiles of the
        // same core set to DIFFERENT values — is what the selector's choice actually means. If such a key
        // is also storable per-game it wins at launch (profile options are merged as a base UNDER the saved
        // config), so the dropdown silently does nothing. Every flipped key must be renderer-owned, which
        // now keeps it out of both the plain catalog and the advanced escape hatch.
        //
        // Deliberately NOT asserted: profiles may also carry plain tuning that only makes sense on that
        // renderer (parallel_n64_gl's GLideN64 FB options). Those are set by one profile and simply absent
        // from the other, so they are not a flip and cannot contradict the selector.
        [Fact]
        public void EveryKeyTheGraphicsSelectorFlipsIsRendererOwned()
        {
            var flipped = ArcadeRendererProfiles.AllSystems
                .SelectMany(s => ArcadeRendererProfiles.For(s))
                .GroupBy(p => p.OptionCore)
                .SelectMany(byCore => byCore
                    .SelectMany(p => p.Options)
                    .GroupBy(kv => kv.Key)
                    // Same core, same key, more than one distinct value across profiles = a flip.
                    .Where(g => g.Select(kv => kv.Value).Distinct().Count() > 1)
                    .Select(g => new { Core = byCore.Key, Key = g.Key }))
                .Distinct()
                .ToList();

            Assert.NotEmpty(flipped);
            Assert.All(flipped, f =>
                Assert.True(ArcadeCoreOptionCatalog.IsRendererSelecting(f.Key),
                    $"{f.Core}: the Graphics selector flips '{f.Key}' between profiles, but it is not renderer-owned — a saved value would out-rank the dropdown and make it inert."));
        }

        [Fact]
        public void NoRendererKeyLeaksIntoThePlainOptionCatalog()
        {
            var cores = AllCores();
            Assert.NotEmpty(cores);
            Assert.All(cores, core =>
                Assert.All(ArcadeCoreOptionCatalog.ForCore(core), o =>
                    Assert.False(ArcadeCoreOptionCatalog.IsRendererSelecting(o.Key),
                        $"{core}: renderer key '{o.Key}' leaked into the plain option catalog.")));
        }

        [Fact]
        public void IsRendererSelectingIgnoresNullAndOrdinaryKeys()
        {
            Assert.False(ArcadeCoreOptionCatalog.IsRendererSelecting(null));
            Assert.False(ArcadeCoreOptionCatalog.IsRendererSelecting("pcsx2_nointerlacing_hint"));
        }
    }
}
