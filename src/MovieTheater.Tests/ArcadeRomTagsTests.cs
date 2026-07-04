using MovieTheater.Arcade;
using Xunit;

namespace MovieTheater.Tests
{
    public class ArcadeRomTagsTests
    {
        [Theory]
        [InlineData("Super Mario 64 (USA)", "USA")]
        [InlineData("GoldenEye (Europe)", "Europe")]
        [InlineData("Mario Kart 64 (Japan)", "Japan")]
        [InlineData("Sonic the Hedgehog 2 (World)", "World")]
        [InlineData("Zelda (USA, Europe)", "USA")]              // multi-region resolves by priority
        [InlineData("Game (Germany)", "Europe")]                // PAL country folds to Europe
        [InlineData("Something (Korea)", "Asia")]
        [InlineData("007 - GoldenEye (1997)(Nintendo)(US)[tr de]", "USA")]  // TOSEC (US) token
        [InlineData("No Region Tag", "Unknown")]
        public void Region_IsBucketed(string key, string expected)
            => Assert.Equal(expected, ArcadeRomTags.Parse(key).Region);

        [Theory]
        // Clean / official niceties stay Release.
        [InlineData("Super Mario 64 (USA)", "Release")]
        [InlineData("Final Fantasy III (USA) (Rev 1)", "Release")]
        [InlineData("Chrono Trigger (USA)", "Release")]
        [InlineData("Game (En,Fr,De)", "Release")]
        // Translations → Hack (the case that made two USA GoldenEye cards indistinguishable).
        [InlineData("007 - GoldenEye (1997)(Nintendo)(US)[tr de]", "Hack")]
        [InlineData("Dragon Quest I & II (1993)(Enix)(JP)[tr en]", "Hack")]
        [InlineData("Chrono Trigger (USA)[tr de]", "Hack")]
        // TOSEC trainer codes → Hack.
        [InlineData("Chrono Trigger (1995)(Square)(US)[t]", "Hack")]
        [InlineData("Addams Family Values (1994)(Ocean)(US)(M3)[t2]", "Hack")]
        // Existing GoodTools codes still work.
        [InlineData("Some Game (USA) [h1]", "Hack")]
        [InlineData("Some Game (USA) [b]", "BadDump")]
        [InlineData("Some Game (USA) [p]", "Pirate")]
        // Parenthesized keywords.
        [InlineData("Star Fox 2 (Beta)", "Beta")]
        [InlineData("Game (Proto)", "Proto")]
        [InlineData("Game (Demo)", "Demo")]
        [InlineData("Game (Unl)", "Unlicensed")]
        public void Variant_IsClassified(string key, string expected)
            => Assert.Equal(expected, ArcadeRomTags.Parse(key).Variant);
    }
}
