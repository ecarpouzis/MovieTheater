using System.Linq;
using MovieTheater.Arcade;

namespace MovieTheater.Tests
{
    // ScummVM's config entry is hand-written (no extraction — the core's source isn't checked out on
    // Ziggy), so nothing but a test stands between a typo and a silently inert setting: libretro
    // ignores an unknown key OR value token without a word. These pin the two things that actually
    // bite — token validity and the options we deliberately refuse to expose.
    public class ArcadeScummvmOptionsTests
    {
        [Fact]
        public void ScummvmIsConfigurableAndItsCursorOptionsAreExposed()
        {
            // The ⚙ Configure button is gated on the system having catalog entries.
            Assert.True(ArcadeCoreOptionCatalog.HasAnything("scummvm"));
            Assert.Equal("scummvm", ArcadeCoreOptionCatalog.CoreForSystem("scummvm"));

            var keys = ArcadeCoreOptionCatalog.ForCore("scummvm").Select(o => o.Key).ToList();
            Assert.Contains("scummvm_gamepad_cursor_speed", keys);
            Assert.Contains("scummvm_gamepad_cursor_acceleration_time", keys);
            Assert.Contains("scummvm_analog_response", keys);
            Assert.Contains("scummvm_analog_deadzone", keys);
            Assert.Contains("scummvm_mouse_speed", keys);
            Assert.Contains("scummvm_mouse_fine_control_speed_reduction", keys);
        }

        [Fact]
        public void EveryScummvmOptionDefaultIsOneOfItsOwnTokens()
        {
            var options = ArcadeCoreOptionCatalog.ForCore("scummvm");
            Assert.NotEmpty(options);
            Assert.All(options, o =>
            {
                Assert.StartsWith("scummvm_", o.Key);
                Assert.NotEmpty(o.Values);
                Assert.True(o.IsValidToken(o.Default),
                    $"{o.Key}: default '{o.Default}' is not one of its own value tokens");
                // A duplicated token makes one of the two dropdown entries unreachable.
                Assert.Equal(o.Values.Count, o.Values.Select(v => v.Token).Distinct().Count());
            });
        }

        // The three we refuse to hand to players, each for a different reason (see the catalog comment).
        // scummvm_video_hw_acceleration is the load-bearing one: it must stay "disabled" because the
        // core's OpenGL mode hands back RETRO_HW_FRAME_BUFFER_VALID on a software-armed room with no GL
        // context behind it, which crashed the worker (2026-07-18). Exposing it as a per-game toggle
        // would put that crash one dropdown away.
        [Theory]
        [InlineData("scummvm_video_hw_acceleration")]
        [InlineData("scummvm_pointer_device")]
        [InlineData("scummvm_samplerate")]
        [InlineData("scummvm_gui_h_res")]
        public void LoadBearingScummvmOptionsAreNotConfigurable(string key)
        {
            Assert.DoesNotContain(key, ArcadeCoreOptionCatalog.ForCore("scummvm").Select(o => o.Key));
        }

        // ScummVM has no video lever at all (software 2D renderer), so Controls has to be a real
        // category or the panel would open on an empty rail.
        [Fact]
        public void ScummvmOptionsUseTheInputAndPerformanceCategoriesOnly()
        {
            var cats = ArcadeCoreOptionCatalog.ForCore("scummvm").Select(o => o.Category).Distinct().ToList();
            Assert.Contains(ArcadeCoreOptionCatalog.Category.Input, cats);
            Assert.All(cats, c => Assert.Contains(c, new[]
            {
                ArcadeCoreOptionCatalog.Category.Input,
                ArcadeCoreOptionCatalog.Category.Performance,
            }));
        }
    }
}
