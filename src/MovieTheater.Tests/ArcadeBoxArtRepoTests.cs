using System.Linq;
using MovieTheater.Arcade;
using Xunit;

namespace MovieTheater.Tests
{
    /// <summary>
    /// Which systems the box-art cascade is even allowed to ask libretro-thumbnails about, and on which
    /// branch. Both have bitten: six systems with thousands of coverless cards were simply absent from the
    /// map — Saturn had 865 cards with no cover against 2,296 boxarts sitting in its repo — and CD-i answered
    /// 404 to everything because the code asked for /master/ of a repo whose default branch is main, which
    /// reads exactly like "this system has no art".
    /// </summary>
    public class ArcadeBoxArtRepoTests
    {
        [Theory]
        [InlineData("saturn")]
        [InlineData("cdi")]
        [InlineData("3do")]
        [InlineData("intv")]
        [InlineData("o2em")]
        [InlineData("coleco")]
        [InlineData("channelf")]
        public void SystemsWithCoverlessCardsHaveARepo(string system)
            => Assert.True(ArcadeBoxArt.HasRepo(system), $"{system} has a real libretro-thumbnails repo — map it.");

        [Fact]
        public void CdiIsAddressedOnMainNotMaster()
            => Assert.Equal("main", ArcadeBoxArt.BranchFor(ArcadeBoxArt.ThumbRepo["cdi"]));

        [Fact]
        public void EveryOtherRepoStaysOnMaster()
            => Assert.All(ArcadeBoxArt.ThumbRepo.Values.Distinct().Where(r => r != "Philips - CD-i"),
                          r => Assert.Equal("master", ArcadeBoxArt.BranchFor(r)));
    }
}
