using MovieTheater.Arcade;
using Xunit;

namespace MovieTheater.Tests
{
    /// <summary>
    /// Rewind arming is a per-CORE fact, and these tests exist to keep it one. The client used to
    /// answer it from the system alone, which cannot be right for a system with two cores whose
    /// serialize costs differ 5x (N64: parallel_n64 2.42 ms, mupen64plus_next 11.61 ms — the latter
    /// measured as an ~8% frame-rate tax on every room, so its ring is deliberately not armed).
    ///
    /// A wrong answer here is not cosmetic: true-when-unarmed puts a Rewind button in the room that
    /// the worker accepts and silently ignores, which is the exact failure mode the arcade has been
    /// bitten by before (a core option with the wrong prefix, a cheat a core stubs out).
    /// </summary>
    public class ArcadeRewindSupportTests
    {
        [Theory]
        [InlineData("nes")]
        [InlineData("snes")]
        [InlineData("genesis")]
        [InlineData("gba")]
        [InlineData("arcade")]
        [InlineData("3do")]
        public void DefaultCoreArmed_ForTheSerializeCheapTier(string system)
        {
            Assert.True(ArcadeRewindSupport.IsArmed(system, null));
            Assert.True(ArcadeRewindSupport.IsArmed(system, ""));
        }

        [Theory]
        [InlineData("psp")]   // noSaveStates — cannot be serialized at all
        [InlineData("ps2")]   // noSaveStates
        [InlineData("gc")]
        [InlineData("dc")]
        [InlineData("capture")]
        public void HeavyAndUnserializableSystemsAreNotArmed(string system)
        {
            Assert.False(ArcadeRewindSupport.IsArmed(system, null));
        }

        [Fact]
        public void N64_IsArmedOnParallelN64ButNotTheDefaultCore()
        {
            // The whole reason the capability travels per room: same system, opposite answers.
            Assert.False(ArcadeRewindSupport.IsArmed("n64", null));
            Assert.False(ArcadeRewindSupport.IsArmed("n64", ""));
            Assert.True(ArcadeRewindSupport.IsArmed("n64", "parallel_n64"));
        }

        [Fact]
        public void AnAlternateCoreDoesNotInheritItsSystemsArming()
        {
            // A 2D system is armed on its default core; that says nothing about some other core
            // someone bolts on later, whose serialize cost nobody has measured.
            Assert.True(ArcadeRewindSupport.IsArmed("ps1", null) == false); // ps1's default core isn't armed
            Assert.False(ArcadeRewindSupport.IsArmed("snes", "some_other_core"));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("not-a-system")]
        public void UnknownOrMissingSystemIsNeverArmed(string? system)
        {
            Assert.False(ArcadeRewindSupport.IsArmed(system, null));
            Assert.False(ArcadeRewindSupport.IsArmed(system, "parallel_n64"));
        }
    }
}
