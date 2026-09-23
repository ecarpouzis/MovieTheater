using System.Reflection;
using System.Text.Json;
using MovieTheater.Services.Jellyfin;
using Xunit;

namespace MovieTheater.Tests
{
    /// <summary>
    /// The DeviceProfile the site hands Jellyfin for a browser session (JellyfinApi.BuildWebDeviceProfile),
    /// pinned on the three numbers the 2026-09-18/20 tablet night turned on:
    ///  - the audio channel count is the CLIENT's (floor 2, ceiling 8) — the old floor of 6 gave every
    ///    5.1 title to Chromium/Android as six channels its audio HAL mangled;
    ///  - the HEVC level ceiling is what the client probed, defaulting to 6.1 (183) as before;
    ///  - a MaxVideoWidth becomes a Width condition on EVERY video codec profile, so a wider source is
    ///    encoded down instead of copied (a 4K HEVC remux/encode was a stall or a decode fatal there).
    /// </summary>
    public class WebDeviceProfileTests
    {
        private static readonly MethodInfo Build = typeof(JellyfinApi).GetMethod(
            "BuildWebDeviceProfile", BindingFlags.NonPublic | BindingFlags.Static)!;

        private static JsonElement Profile(ClientCapabilities caps, long? maxBitrate = null)
        {
            var profile = Build.Invoke(null, new object?[] { maxBitrate, caps })!;
            return JsonSerializer.SerializeToElement(profile);
        }

        private static IEnumerable<JsonElement> VideoCodecProfiles(JsonElement profile) =>
            profile.GetProperty("CodecProfiles").EnumerateArray()
                .Where(p => p.GetProperty("Type").GetString() == "Video");

        private static string? Condition(JsonElement codecProfile, string property) =>
            codecProfile.GetProperty("Conditions").EnumerateArray()
                .Where(c => c.GetProperty("Property").GetString() == property)
                .Select(c => c.GetProperty("Value").GetString())
                .FirstOrDefault();

        private static string TranscodingMaxAudioChannels(JsonElement profile) =>
            profile.GetProperty("TranscodingProfiles").EnumerateArray()
                .First(p => p.GetProperty("Type").GetString() == "Video")
                .GetProperty("MaxAudioChannels").GetString()!;

        [Theory]
        [InlineData(2, "2")]   // the tablet: stereo, as asked
        [InlineData(6, "6")]   // a Dolby-decoding desktop: 5.1 preserved
        [InlineData(8, "8")]   // 7.1 honoured
        [InlineData(1, "2")]   // never below stereo
        [InlineData(12, "8")]  // never above 7.1
        public void AudioChannelsAreTheClientsOwnNumberWithinTwoToEight(int asked, string expected)
        {
            var profile = Profile(new ClientCapabilities(Hevc: true, Fmp4: true, MaxAudioChannels: asked));
            Assert.Equal(expected, TranscodingMaxAudioChannels(profile));
        }

        [Fact]
        public void HevcLevelCeilingIsWhatTheClientProbedAndDefaultsToSixPointOne()
        {
            var probed = Profile(new ClientCapabilities(Hevc: true, Fmp4: true, HevcMaxLevel: 123));
            var hevc = VideoCodecProfiles(probed).Single(p => p.GetProperty("Codec").GetString() == "hevc");
            Assert.Equal("123", Condition(hevc, "VideoLevel"));

            var def = Profile(new ClientCapabilities(Hevc: true, Fmp4: true));
            hevc = VideoCodecProfiles(def).Single(p => p.GetProperty("Codec").GetString() == "hevc");
            Assert.Equal("183", Condition(hevc, "VideoLevel"));
        }

        [Fact]
        public void MaxVideoWidthBecomesAWidthConditionOnEveryVideoCodecProfile()
        {
            var caps = new ClientCapabilities(Hevc: true, Av1: true, Fmp4: true, MaxVideoWidth: 1920);
            var profiles = VideoCodecProfiles(Profile(caps)).ToList();
            Assert.Equal(new[] { "h264", "hevc", "av1" }, profiles.Select(p => p.GetProperty("Codec").GetString()));
            Assert.All(profiles, p => Assert.Equal("1920", Condition(p, "Width")));
            // The existing conditions are still there beside it.
            Assert.Equal("51", Condition(profiles[0], "VideoLevel"));
        }

        [Fact]
        public void NoMaxVideoWidthMeansNoWidthCondition()
        {
            var caps = new ClientCapabilities(Hevc: true, Av1: true, Fmp4: true);
            Assert.All(VideoCodecProfiles(Profile(caps)), p => Assert.Null(Condition(p, "Width")));
        }
    }
}
