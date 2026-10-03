using MovieTheater.Arcade;
using Xunit;

namespace MovieTheater.Tests
{
    /// <summary>
    /// The per-device codec hint: a device that drowned on (or refused) a codec has Auto pick the other one next
    /// time — from its own sessions, not from anything the creator's probe can see.
    /// </summary>
    public class ArcadeCodecHintTests
    {
        private static (string?, string?, int, int) Row(string codec, string path = "direct", int strong = 0, int scaleDowns = 0)
            => (codec, path, strong, scaleDowns);

        [Fact]
        public void No_history_means_no_opinion()
        {
            Assert.Null(ArcadeCodecHint.CodecToAvoid([]));
        }

        [Fact]
        public void A_device_that_drowned_on_av1_avoids_it()
        {
            Assert.Equal("av1", ArcadeCodecHint.CodecToAvoid([Row("av1", strong: 25), Row("h264")]));
        }

        [Fact]
        public void A_forced_scale_down_counts_even_with_little_strong_time()
        {
            Assert.Equal("h264", ArcadeCodecHint.CodecToAvoid([Row("h264", strong: 4, scaleDowns: 1)]));
        }

        [Fact]
        public void A_refusal_means_avoid()
        {
            Assert.Equal("av1", ArcadeCodecHint.CodecToAvoid([Row("av1", path: "refused")]));
        }

        [Fact]
        public void A_brief_wobble_is_not_drowning()
        {
            Assert.Null(ArcadeCodecHint.CodecToAvoid([Row("av1", strong: ArcadeCodecHint.StrongSeconds - 1)]));
        }

        [Fact]
        public void Only_the_last_three_sessions_on_a_codec_count()
        {
            // Three clean AV1 sessions since the bad one: the device (or its browser) has recovered.
            Assert.Null(ArcadeCodecHint.CodecToAvoid([Row("av1"), Row("av1"), Row("av1"), Row("av1", strong: 60)]));
        }

        [Fact]
        public void Both_codecs_bad_leaves_it_to_the_probe()
        {
            Assert.Null(ArcadeCodecHint.CodecToAvoid([Row("av1", strong: 30), Row("h264", scaleDowns: 2)]));
        }
    }
}
