using MovieTheater.Services.LaunchBox;
using Xunit;

namespace MovieTheater.Tests
{
    /// <summary>
    /// The lobby groups cards by (System, CollapseKey) where CollapseKey = LaunchBoxMetadata.NormalizeTitle
    /// of the display Title. These lock in that cosmetically-different DUMPS of one game (different
    /// separators, article position, spacing, punctuation) fold to the SAME key — the Saturn duplicates
    /// Eric surfaced — while genuinely different titles stay apart.
    /// </summary>
    public class ArcadeCollapseKeyTests
    {
        [Theory]
        // Separator variance: " - " vs ": " (the Atlantis case) and hyphen-vs-nothing (Azel).
        [InlineData("Atlantis - The Lost Tales", "Atlantis: The Lost Tales")]
        [InlineData("Azel - Panzer Dragoon Rpg", "Azel Panzer Dragoon Rpg")]
        // Article position: the sort form ", The" vs the natural "The …" (Mansion of Hidden Souls).
        [InlineData("Mansion of Hidden Souls, The", "The Mansion of Hidden Souls")]
        // Spacing / glued words (Daytona Usa ⇄ Daytonausa) and stray punctuation (Blam! ⇄ Blam).
        [InlineData("Daytona Usa", "Daytonausa")]
        [InlineData("Blam! Machinehead", "Blam Machinehead")]
        public void NormalizeTitle_FoldsCosmeticVariants(string a, string b)
            => Assert.Equal(LaunchBoxMetadata.NormalizeTitle(a), LaunchBoxMetadata.NormalizeTitle(b));

        [Theory]
        // Different subtitle = genuinely different key: NOT folded by normalization alone (the known
        // residual that needs LaunchBox alias resolution, not the grouping key).
        [InlineData("Emit Vol 2", "Emit Vol 2 - Inochigake No Tabi")]
        // Different games that merely share a leading word must never collapse together.
        [InlineData("Dark Seed", "Dark Savior")]
        public void NormalizeTitle_KeepsDistinctTitlesApart(string a, string b)
            => Assert.NotEqual(LaunchBoxMetadata.NormalizeTitle(a), LaunchBoxMetadata.NormalizeTitle(b));
    }
}
