using System.Linq;
using MovieTheater.Arcade;
using Xunit;

namespace MovieTheater.Tests
{
    /// <summary>The arcade grouped browse core: an index over the lobby's card aggregates, banded in memory.</summary>
    public class ArcadeGameGroupsTests
    {
        private static ArcadeGameGroups.CardLight Card(string system, string key, string title, double? rating = null, int? year = null, int players = 1, string? genres = null,
            string? developer = null, string? publisher = null, int raAchievements = 0, bool raScore = false, bool raTime = false)
            => new(system, key, title, title.ToLowerInvariant(), rating, year, players, genres, developer, publisher, raAchievements, raScore, raTime);

        private static readonly ArcadeGameGroups.CardLight[] Cards =
        {
            Card("snes", "chrono", "Chrono Trigger", 95, 1995, 1, "RPG, Adventure", "Square", "Square", raAchievements: 60, raScore: true),
            Card("snes", "smw", "Super Mario World", 92, 1990, 2, "Platform", "Nintendo EAD", "Nintendo", raAchievements: 40, raTime: true),
            Card("genesis", "sonic", "Sonic the Hedgehog", 88, 1991, 1, "Platform", "Sonic Team", "Sega", raTime: true),
            Card("genesis", "streets", "Streets of Rage", null, 1991, 2, "Beat 'em up; Action", "Sega", "Sega"),
            Card("psx", "ff7", "Final Fantasy VII", 96, 1997, 1, "RPG", "Square", "Sony"),
            Card("psx", "undated", "Undated Prototype", null, null, 8, null),
        };

        /// <summary>The per-VERSION tuples: Chrono Trigger has a USA and a Japan dump, one of them a hack.</summary>
        private static readonly ArcadeGameGroups.CardTag[] Tags =
        {
            new("snes", "chrono", "USA", null),
            new("snes", "chrono", "Japan", "Hack"),
            new("snes", "chrono", "USA", null),           // a second USA dump is still ONE shelf entry
            new("snes", "smw", "USA", null),
            new("genesis", "sonic", "Europe", "Translation"),
            new("genesis", "streets", null, null),        // an untagged dump: Unknown region, Release
            new("psx", "ff7", "USA", null),
            new("psx", "undated", null, "Prototype"),
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

        // ── R9 S8: the axes the lobby's own filters already knew about ─────────────────────────────

        [Fact]
        public void PlayerHeads_AreExactUpToFour_ThenOneFivePlusShelf_AndCarryNoLetterRail()
        {
            var index = ArcadeGameGroups.BuildIndex(Cards, "players");
            Assert.Equal(new[] { "1", "2", "5" }, index.Heads.Select(h => h.Key));
            Assert.Equal(new[] { "1 player", "2 players", "5+ players" }, index.Heads.Select(h => h.Label));
            Assert.Equal(3, index.Heads[0].Count); // chrono, sonic, ff7
            Assert.Equal(2, index.Heads[1].Count);
            Assert.Equal(1, index.Heads[2].Count); // the 8-player prototype folds into 5+
            Assert.Empty(ArcadeGameGroups.GroupLetters(index.Heads, "players"));
        }

        [Fact]
        public void RegionAndVariantAreOneCardPerVERSION_UnknownAndReleaseAreRealShelves()
        {
            var region = ArcadeGameGroups.BuildIndex(Cards, "region", null, Tags);
            // Chrono Trigger stands under BOTH its dumps' regions; the two USA dumps are one entry.
            Assert.Equal(new[] { "Europe", "Japan", "Unknown", "USA" }, region.Heads.Select(h => h.Key));
            Assert.Equal(3, region.Heads.Single(h => h.Key == "USA").Count);
            Assert.Equal(1, region.Heads.Single(h => h.Key == "Japan").Count);
            // The untagged dumps file under Unknown — never hidden, so never silently dropped.
            Assert.Equal(new[] { "streets", "undated" }, ArcadeGameGroups.Band(region, "Unknown", null, 24, 0).Select(m => m.CollapseKey));
            Assert.Equal(new[] { "chrono" }, ArcadeGameGroups.Band(region, "japan", null, 24, 0).Select(m => m.CollapseKey));

            var variant = ArcadeGameGroups.BuildIndex(Cards, "variant", null, Tags);
            Assert.Equal(new[] { "Hack", "Prototype", "Release", "Translation" }, variant.Heads.Select(h => h.Key));
            // A null Variant is the lobby's "Release" everywhere else too.
            Assert.Equal(4, variant.Heads.Single(h => h.Key == "Release").Count);
            Assert.Equal(new[] { "chrono" }, ArcadeGameGroups.Band(variant, "Hack", null, 24, 0).Select(m => m.CollapseKey));
            Assert.NotEmpty(ArcadeGameGroups.GroupLetters(variant.Heads, "variant"));
        }

        [Fact]
        public void DeveloperAndPublisherAreSeparateShelves_AndAnUncreditedCardIsNotFiled()
        {
            var dev = ArcadeGameGroups.BuildIndex(Cards, "developer");
            Assert.Equal(new[] { "Nintendo EAD", "Sega", "Sonic Team", "Square" }, dev.Heads.Select(h => h.Key));
            Assert.Equal(2, dev.Heads.Single(h => h.Key == "Square").Count); // chrono + ff7
            // "Undated Prototype" has neither credit: no shelf, rather than an "(Unknown)" bucket.
            Assert.DoesNotContain(dev.ByKey.Values.SelectMany(v => v), c => c.CollapseKey == "undated");

            var pub = ArcadeGameGroups.BuildIndex(Cards, "publisher");
            Assert.Equal(new[] { "Nintendo", "Sega", "Sony", "Square" }, pub.Heads.Select(h => h.Key));
            Assert.Equal(2, pub.Heads.Single(h => h.Key == "Sega").Count);
            Assert.Equal(new[] { ("N", 0), ("S", 1) }, ArcadeGameGroups.GroupLetters(pub.Heads, "publisher"));
        }

        [Fact]
        public void RaShelves_AreTheRailsOwnValues_ACardCanStandUnderSeveral_AndTheRestGetOne()
        {
            var index = ArcadeGameGroups.BuildIndex(Cards, "ra");
            Assert.Equal(new[] { "achievements", "highscores", "speedruns", "none" }, index.Heads.Select(h => h.Key));
            Assert.Equal(new[] { "Achievements", "High-score leaderboards", "Speedrun leaderboards", "No RetroAchievements" }, index.Heads.Select(h => h.Label));
            Assert.Equal(2, index.Heads[0].Count);  // chrono, smw
            Assert.Equal(1, index.Heads[1].Count);  // chrono's score board
            Assert.Equal(2, index.Heads[2].Count);  // smw, sonic
            Assert.Equal(3, index.Heads[3].Count);  // streets, ff7 and the undated prototype
            // Chrono Trigger has achievements AND a high-score board: it stands under both.
            Assert.Contains("chrono", ArcadeGameGroups.Band(index, "achievements", null, 24, 0).Select(m => m.CollapseKey));
            Assert.Contains("chrono", ArcadeGameGroups.Band(index, "highscores", null, 24, 0).Select(m => m.CollapseKey));
            Assert.Empty(ArcadeGameGroups.GroupLetters(index.Heads, "ra"));
        }

        [Fact]
        public void Caps()
        {
            Assert.Equal("system", ArcadeGameGroups.NormalizeGroupBy(null));
            Assert.Equal("genre", ArcadeGameGroups.NormalizeGroupBy(" Genre"));
            foreach (var by in new[] { "players", "region", "variant", "developer", "publisher", "ra" })
                Assert.Equal(by, ArcadeGameGroups.NormalizeGroupBy(by.ToUpperInvariant()));
            // Only the two per-VERSION axes pay for the extra tuple query.
            Assert.True(ArcadeGameGroups.NeedsTags("region"));
            Assert.True(ArcadeGameGroups.NeedsTags("variant"));
            foreach (var by in new[] { "system", "genre", "decade", "players", "developer", "publisher", "ra" })
                Assert.False(ArcadeGameGroups.NeedsTags(by));
            Assert.Equal(20, ArcadeGameGroups.CapGroupsTop(0));
            Assert.Equal(50, ArcadeGameGroups.CapGroupsTop(500));
            Assert.Equal(24, ArcadeGameGroups.CapPerGroupTop(0));
            Assert.Equal(60, ArcadeGameGroups.CapPerGroupTop(999));
            Assert.Equal(new[] { "A", "B" }, ArcadeGameGroups.SplitGenres(" A ,B;a"));
        }
    }
}
