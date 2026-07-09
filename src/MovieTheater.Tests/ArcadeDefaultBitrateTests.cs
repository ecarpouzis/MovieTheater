using MovieTheater.Services.Arcade;
using Xunit;

namespace MovieTheater.Tests
{
    /// <summary>
    /// Per-system "Auto" bitrate (docs/arcade-quality-plan.md Phase 5). The individual numbers are a
    /// judgement call and may be retuned; the BOUNDS are the safety properties and must not move without
    /// a deliberate decision — CloudRetro does no congestion control, so bitrate is a loaded gun.
    /// </summary>
    public class ArcadeDefaultBitrateTests
    {
        // The flat default every room used before Phase 5, and the lobby's existing "Max" preset.
        private const int PreviousFlatDefault = 5000;
        private const int MaxLobbyPreset = 10000;

        [Theory]
        [InlineData("gc", 10000)]   // 1280x1056 — the most starved at a flat 5 Mbps
        [InlineData("n64", 8000)]   //  960x720
        [InlineData("psp", 7000)]   //  960x544
        [InlineData("ps2", 6000)]
        [InlineData("dc", 6000)]
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

        // The two properties that make "Auto" safe to ship without adaptive bitrate:
        //   never worse than what already shipped, never above a value the user could already have picked.
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
        public void AutoIsAlwaysBetweenThePreviousDefaultAndTheMaxPreset(string? system)
        {
            var kbps = CloudRetroHost.DefaultVideoBitrateKbps(system);
            Assert.InRange(kbps, PreviousFlatDefault, MaxLobbyPreset);
        }

        // The worker clamps to 500..20000; anything we emit must already sit inside that.
        [Theory]
        [InlineData("gc")]
        [InlineData("arcade")]
        public void StaysInsideTheWorkerClamp(string system)
            => Assert.InRange(CloudRetroHost.DefaultVideoBitrateKbps(system), 500, 20000);
    }
}
