using MovieTheater.Arcade;
using Xunit;

namespace MovieTheater.Tests
{
    /// <summary>
    /// Disc-tag parsing that drives multi-disc grouping + the .m3u launch key (docs/arcade-dedupe-
    /// multidisc-plan.md). Covers the abbreviated "cdN" / "disque N" tags the coded Saturn/PCE romsets
    /// carry, which the original "(Disc N)"-only parser silently missed — every such game showed as
    /// separate disc cards and never got in-game disc swap.
    /// </summary>
    public class ArcadeVersionsTests
    {
        [Theory]
        // Redump / No-Intro parenthesized tags (the always-worked cases — regression guard).
        [InlineData("Final Fantasy IX (USA) (Disc 1)", 1)]
        [InlineData("Final Fantasy IX (USA) (Disc 2)", 2)]
        [InlineData("Metal Gear Solid (Disk 1)", 1)]
        [InlineData("penn and teller's smoke and mirrors (usa, prototype) (disc 1)", 1)]
        [InlineData("Some RPG (USA) (Disc 1 of 3)", 1)]
        // Free-text trailing "- Disc N".
        [InlineData("Baldur's Gate - Disc 3", 3)]
        // Abbreviated "cdN" the coded Saturn set carries (previously MISSED → returned 0).
        [InlineData("0691-atlantis-fre-cd1", 1)]
        [InlineData("0695-command_&_conquer-fre-cd2", 2)]
        [InlineData("elves2-cd1", 1)]
        [InlineData("chisato moritako cd2", 2)]
        [InlineData("command & conquer - teil 1 der tiberiumkonflikt cd1 gdi (e) [ger]", 1)]
        [InlineData("d cd1 eur fr", 1)]
        [InlineData("Some Saturn Game (CD 2)", 2)]
        // NOT discs — must return 0. The bounding keeps these from matching.
        [InlineData("Discworld (Europe)", 0)]
        [InlineData("0029-discworld-eur-v2", 0)]
        [InlineData("Sonic CD (USA)", 0)]              // "CD" with no trailing disc number
        [InlineData("Super Mario 64 (USA)", 0)]
        [InlineData("18wheelr", 0)]                     // naomi shortname, no disc tag
        [InlineData("Ronald McDonald 2", 0)]            // embedded "cd" not separator-bounded
        [InlineData(null, 0)]
        public void DiscNumber_ParsesEveryTagShape(string? key, int expected)
            => Assert.Equal(expected, ArcadeVersions.DiscNumber(key));

        [Theory]
        // The disc tag is stripped so both discs of a game collapse to ONE .m3u launch key.
        [InlineData("Final Fantasy IX (USA) (Disc 1)", "Final Fantasy IX (USA)")]
        [InlineData("Final Fantasy IX (USA) (Disc 2)", "Final Fantasy IX (USA)")]
        [InlineData("penn and teller's smoke and mirrors (usa, prototype) (disc 1)",
                    "penn and teller's smoke and mirrors (usa, prototype)")]
        [InlineData("Baldur's Gate - Disc 3", "Baldur's Gate")]
        [InlineData("0691-atlantis-fre-cd1", "0691-atlantis-fre")]
        [InlineData("0695-command_&_conquer-fre-cd2", "0695-command_&_conquer-fre")]
        [InlineData("elves2-cd1", "elves2")]
        [InlineData("chisato moritako cd2", "chisato moritako")]
        // A single-disc / non-disc key is returned unchanged.
        [InlineData("Super Mario 64 (USA)", "Super Mario 64 (USA)")]
        [InlineData("0029-discworld-eur-v2", "0029-discworld-eur-v2")]
        [InlineData("Sonic CD (USA)", "Sonic CD (USA)")]
        public void M3uKey_StripsDiscTagToSharedBase(string key, string expected)
            => Assert.Equal(expected, ArcadeVersions.M3uKey(key));

        // The two discs of a coded-name game must collapse to the SAME key so they group into one card.
        [Theory]
        [InlineData("0691-atlantis-fre-cd1", "0691-atlantis-fre-cd2")]
        [InlineData("Final Fantasy IX (USA) (Disc 1)", "Final Fantasy IX (USA) (Disc 3)")]
        public void M3uKey_DiscsOfOneGameShareABase(string disc1, string discN)
            => Assert.Equal(ArcadeVersions.M3uKey(disc1), ArcadeVersions.M3uKey(discN));
    }
}
