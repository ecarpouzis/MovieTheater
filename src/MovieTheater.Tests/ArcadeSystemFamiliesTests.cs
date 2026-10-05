using MovieTheater.Arcade;
using Xunit;

namespace MovieTheater.Tests
{
    /// <summary>The lobby's Arcade tile is the coin-op FAMILY (FBNeo + MAME + NAOMI + Atomiswave); MAME has no tile.</summary>
    public class ArcadeSystemFamiliesTests
    {
        [Fact]
        public void Arcade_expands_to_every_coin_op_core_and_other_systems_pass_through()
        {
            Assert.Equal(new[] { "arcade", "mame", "naomi", "atomiswave", "snes" },
                ArcadeSystemFamilies.Expand(new[] { "arcade", "snes", "naomi" }));
            Assert.Equal(new[] { "mame" }, ArcadeSystemFamilies.Expand(new[] { "mame" }));   // an old ?system=mame link
            Assert.Empty(ArcadeSystemFamilies.Expand(new string[0]));
        }

        [Theory]
        [InlineData("mame", "arcade")]
        [InlineData("arcade", "arcade")]
        [InlineData("naomi", "naomi")]
        [InlineData("snes", "snes")]
        public void Mame_is_folded_into_the_arcade_tile(string system, string tile) =>
            Assert.Equal(tile, ArcadeSystemFamilies.TileOf(system));

        [Fact]
        public void Knows_which_systems_are_arcade()
        {
            Assert.True(ArcadeSystemFamilies.IsArcade("MAME"));
            Assert.True(ArcadeSystemFamilies.IsArcade("atomiswave"));
            Assert.False(ArcadeSystemFamilies.IsArcade("neogeo_pocket"));
            Assert.False(ArcadeSystemFamilies.IsArcade(null));
        }
    }
}
