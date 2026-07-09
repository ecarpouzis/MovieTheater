using MovieTheater.Services.Arcade;
using Xunit;

namespace MovieTheater.Tests
{
    /// <summary>
    /// Per-system "Auto" bitrate (docs/arcade-quality-plan.md Phase 5). The individual numbers are a
    /// judgement call and may be retuned; the BOUNDS are the safety properties and must not move without
    /// a deliberate decision. They are a CEILING: worker patch 0021 (ABR) walks the encoder down from
    /// them when a peer's link cannot carry it, which is what makes a generous ceiling safe.
    /// </summary>
    public class ArcadeDefaultBitrateTests
    {
        // The flat default every room used before Phase 5, and the highest ceiling Auto may hand out.
        // The cap sits ABOVE the lobby's "Max" preset on purpose: worker patch 0021 (ABR) backs the
        // encoder off from this ceiling when a peer's link can't carry it, so a generous ceiling is free.
        private const int PreviousFlatDefault = 5000;
        private const int MaxAutoCeiling = 14000;

        [Theory]
        [InlineData("gc", 14000)]   // 1280x1056 — the most starved at a flat 5 Mbps
        [InlineData("ps2", 12000)]  // 1280x896 after the 2x upscale; 6000 left it at ~95ms jitter buffer
        [InlineData("n64", 11000)] // 1280x960, supersampled from 1920x1440
        [InlineData("psp", 7000)]   //  960x544
        [InlineData("dc", 11000)]  // 1280x960 since flycast's frames stopped being decimated
        [InlineData("ps1", 6000)]
        [InlineData("arcade", 5000)]
        [InlineData("snes", 5000)]
        [InlineData("genesis", 5000)]
        public void ReturnsTheMeasuredPerSystemDefault(string system, int expected)
            => Assert.Equal(expected, CloudRetroHost.DefaultVideoBitrateKbps(system));

        [Theory]
        [InlineData("GC")]
        [InlineData("N64")]
        [InlineData("Ps1")]
        public void IsCaseInsensitive(string system)
            => Assert.Equal(CloudRetroHost.DefaultVideoBitrateKbps(system.ToLowerInvariant()),
                            CloudRetroHost.DefaultVideoBitrateKbps(system));

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("some-system-we-add-next-year")]
        public void UnknownSystemsFallBackToTheFlatDefault(string? system)
            => Assert.Equal(PreviousFlatDefault, CloudRetroHost.DefaultVideoBitrateKbps(system));

        // The two properties that keep "Auto" safe: never worse than what already shipped, and never above
        // a ceiling ABR can be trusted to walk back down from.
        [Theory]
        [InlineData("gc")]
        [InlineData("n64")]
        [InlineData("psp")]
        [InlineData("ps2")]
        [InlineData("dc")]
        [InlineData("ps1")]
        [InlineData("arcade")]
        [InlineData("nes")]
        [InlineData(null)]
        public void AutoIsAlwaysBetweenThePreviousDefaultAndTheAutoCeiling(string? system)
        {
            var kbps = CloudRetroHost.DefaultVideoBitrateKbps(system);
            Assert.InRange(kbps, PreviousFlatDefault, MaxAutoCeiling);
        }

        // The worker clamps to 500..20000; anything we emit must already sit inside that.
        [Theory]
        [InlineData("gc")]
        [InlineData("arcade")]
        public void StaysInsideTheWorkerClamp(string system)
            => Assert.InRange(CloudRetroHost.DefaultVideoBitrateKbps(system), 500, 20000);
    }
}
