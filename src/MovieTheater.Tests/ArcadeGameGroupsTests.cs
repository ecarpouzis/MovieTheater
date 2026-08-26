using System.Linq;
using MovieTheater.Arcade;
using Xunit;

namespace MovieTheater.Tests
{
    /// <summary>The arcade grouped browse core: an index over the lobby's card aggregates, banded in memory.</summary>
    public class ArcadeGameGroupsTests
    {
        private static ArcadeGameGroups.CardLight Card(string system, string key, string title, double? rating = null, int? year = null, int players = 1, string? genres = null)
            => new(system, key, title, title.ToLowerInvariant(), rating, year, players, genres);

        private static readonly ArcadeGameGroups.CardLight[] Cards =
        {
            Card("snes", "chrono", "Chrono Trigger", 95, 1995, 1, "RPG, Adventure"),
            Card("snes", "smw", "Super Mario World", 92, 1990, 2, "Platform"),
            Card("genesis", "sonic", "Sonic the Hedgehog", 88, 1991, 1, "Platform"),
            Card("genesis", "streets", "Streets of Rage", null, 1991, 2, "Beat 'em up; Action"),
            Card("psx", "ff7", "Final Fantasy VII", 96, 1997, 1, "RPG"),
            Card("psx", "undated", "Undated Prototype", null, null, 1, null),
        };

        [Fact]
        public void SystemHeads_UseTheLabelForOrder_AndBandsTiebreakUniquely()
        {
            var index = ArcadeGameGroups.BuildIndex(Cards, "system", s => s switch { "snes" => "Super Nintendo", "genesis" => "Sega Genesis", "psx" => "PlayStation", _ => s });
            Assert.Equal(new[] { "PlayStation", "Sega Genesis", "Super Nintendo" }, index.Heads.Select(h => h.Label));
            Assert.Equal(new[] { "psx", "genesis", "snes" }, index.Heads.Select(h => h.Key));
            Assert.Equal(2, index.Heads[0].Count);
            var band = ArcadeGameGroups.Band(index, "genesis", null, 24, 0);
            Assert.Equal(new[] { "sonic", "streets" }, band.Select(m => m.CollapseKey));
            var rating = ArcadeGameGroups.Band(index, "genesis", "rating", 24, 0);
            Assert.Equal(new[] { "sonic", "streets" }, rating.Select(m => m.CollapseKey)); // unrated last
            Assert.Equal(new[] { ("P", 0), ("S", 1) }, ArcadeGameGroups.GroupLetters(index.Heads, "system"));
        }

        [Fact]
        public void GenreHeads_SplitTheAnchorCsv_ACardInEveryGenreItCarries()
        {
            var index = ArcadeGameGroups.BuildIndex(Cards, "genre");
            Assert.Equal(new[] { "Action", "Adventure", "Beat 'em up", "Platform", "RPG" }, index.Heads.Select(h => h.Key));
            Assert.Equal(2, index.Heads.Single(h => h.Key == "Platform").Count);
            Assert.Equal(2, index.Heads.Single(h => h.Key == "RPG").Count);
            var rpg = ArcadeGameGroups.Band(index, "rpg", "year", 24, 0); // key lookup is case-insensitive
            Assert.Equal(new[] { "ff7", "chrono" }, rpg.Select(m => m.CollapseKey));
        }

        [Fact]
        public void DecadeHeads_NewestFirst_UndatedDropped_AndWindowsAreStable()
        {
            var index = ArcadeGameGroups.BuildIndex(Cards, "decade");
            Assert.Equal(new[] { "1990" }, index.Heads.Select(h => h.Key));
            Assert.Equal("1990s", index.Heads[0].Label);
            Assert.Equal(5, index.Heads[0].Count);
            var all = ArcadeGameGroups.Band(index, "1990", null, 24, 0);
            var w1 = ArcadeGameGroups.Band(index, "1990", null, 2, 0);
            var w2 = ArcadeGameGroups.Band(index, "1990", null, 2, 2);
            var w3 = ArcadeGameGroups.Band(index, "1990", null, 2, 4);
            Assert.Equal(all, w1.Concat(w2).Concat(w3));
            Assert.Empty(ArcadeGameGroups.GroupLetters(index.Heads, "decade"));
        }

        [Fact]
        public void Caps()
        {
            Assert.Equal("system", ArcadeGameGroups.NormalizeGroupBy(null));
            Assert.Equal("genre", ArcadeGameGroups.NormalizeGroupBy(" Genre"));
            Assert.Equal(20, ArcadeGameGroups.CapGroupsTop(0));
            Assert.Equal(50, ArcadeGameGroups.CapGroupsTop(500));
            Assert.Equal(24, ArcadeGameGroups.CapPerGroupTop(0));
            Assert.Equal(60, ArcadeGameGroups.CapPerGroupTop(999));
            Assert.Equal(new[] { "A", "B" }, ArcadeGameGroups.SplitGenres(" A ,B;a"));
        }
    }
}
