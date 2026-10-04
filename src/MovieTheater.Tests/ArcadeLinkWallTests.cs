using MovieTheater.Arcade;
using Xunit;

namespace MovieTheater.Tests
{
    /// <summary>
    /// The wall-seed arithmetic (worker patch 0050): max sustained × 1.15, clamped to [5000, 40000], 0 = no seed.
    /// </summary>
    public class ArcadeLinkWallTests
    {
        [Fact]
        public void No_history_means_no_seed()
        {
            Assert.Equal(0, ArcadeLinkWall.FromSustained(null));
            Assert.Equal(0, ArcadeLinkWall.FromSustained(0));
            Assert.Equal(0, ArcadeLinkWall.FromSustained(-5));
        }

        [Fact]
        public void The_34MB5J_phone_gets_its_sustained_plus_15_percent()
        {
            Assert.Equal(15074, ArcadeLinkWall.FromSustained(13108));
        }

        [Fact]
        public void Clamped_to_the_seedable_range()
        {
            Assert.Equal(5000, ArcadeLinkWall.FromSustained(1200));
            Assert.Equal(40000, ArcadeLinkWall.FromSustained(60000));
        }
    }
}
