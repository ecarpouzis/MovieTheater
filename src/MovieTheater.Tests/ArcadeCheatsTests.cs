using System.Linq;
using MovieTheater.Arcade;
using Xunit;

namespace MovieTheater.Tests
{
    /// <summary>
    /// The libretro <c>.cht</c> parser. The properties here are all about NOT shipping a cheat that silently
    /// does nothing (or worse, pokes the wrong memory) — see docs/arcade-cheats.md.
    /// </summary>
    public class ArcadeChtFileTests
    {
        [Fact]
        public void ParsesDescriptionsAndCodesInSourceOrder()
        {
            var text = """
                cheats = 3

                cheat0_desc = "Music Speed"
                cheat0_code = "810C0A90 2409+810C0A92 0000"
                cheat0_enable = false

                cheat1_desc = "Infinite Lives"
                cheat1_code = "80165FBD 0001"
                cheat1_enable = false
                """;

            var entries = ArcadeChtFile.Parse(text, out int withoutCode);

            Assert.Equal(0, withoutCode);
            Assert.Equal(2, entries.Count);
            Assert.Equal(new[] { 0, 1 }, entries.Select(e => e.Ordinal));
            Assert.Equal("Music Speed", entries[0].Name);
            // Multi-line codes are '+'-joined upstream and go to retro_cheat_set verbatim.
            Assert.Equal("810C0A90 2409+810C0A92 0000", entries[0].Code);
        }

        [Fact]
        public void SkipsEntriesWithNoCode_TheyAreRetroArchsOwnMemoryScannerCheats()
        {
            // cheat1 is the address/value form RetroArch pokes itself. We have no scanner; importing it
            // would create a toggle that does nothing.
            var text = """
                cheats = 2
                cheat0_desc = "Real code"
                cheat0_code = "80165FBD 0001"
                cheat1_desc = "Scanner cheat"
                cheat1_address = "8016"
                cheat1_value = "1"
                """;

            var entries = ArcadeChtFile.Parse(text, out int withoutCode);

            Assert.Single(entries);
            Assert.Equal("Real code", entries[0].Name);
            Assert.Equal(1, withoutCode);
        }

        [Fact]
        public void DropsAnOverlongCodeRatherThanTruncatingIt()
        {
            // Half a code pokes the wrong addresses, so a too-long entry must vanish, not get cut.
            var big = new string('A', ArcadeChtFile.MaxCodeLength + 1);
            var entries = ArcadeChtFile.Parse($"cheat0_desc = \"x\"\ncheat0_code = \"{big}\"", out _);
            Assert.Empty(entries);
        }

        [Fact]
        public void IgnoresTheCheatsHeaderAndToleratesCrlf()
        {
            // The "cheats = N" header has no '_' and real files disagree with their own count.
            var entries = ArcadeChtFile.Parse("cheats = 99\r\ncheat0_desc = \"A\"\r\ncheat0_code = \"1\"\r\n", out _);
            Assert.Single(entries);
            Assert.Equal("A", entries[0].Name);
            Assert.Equal("1", entries[0].Code);
        }

        [Fact]
        public void NamesAnUndescribedCheatRatherThanDroppingIt()
        {
            var entries = ArcadeChtFile.Parse("cheat4_code = \"ABCD 0001\"", out _);
            Assert.Single(entries);
            Assert.Equal("Cheat 5", entries[0].Name); // 1-based for humans
        }
    }

    /// <summary>
    /// Which systems may offer which cheats. These assertions guard the one failure mode the whole design is
    /// built to avoid: a toggle in the lobby that the emulator will ignore.
    /// </summary>
    public class ArcadeCheatCatalogTests
    {
        [Theory]
        [InlineData("n64")]
        [InlineData("ps1")]
        [InlineData("snes")]
        [InlineData("nes")]
        [InlineData("genesis")]
        [InlineData("gba")]
        public void CodeCapableSystemsHaveBothACoreThatAppliesCodesAndAnUpstreamFolder(string system)
        {
            Assert.True(ArcadeCheatCatalog.SupportsCheatCodes(system));
            Assert.NotNull(ArcadeCheatCatalog.ChtFolder(system));
        }

        [Theory]
        [InlineData("ps2")]   // LRPS2 reads .pnach; retro_cheat_set is a stub
        [InlineData("gc")]    // Dolphin reads Gecko/AR INIs
        [InlineData("psp")]   // PPSSPP reads a cwcheat db
        [InlineData("dc")]    // flycast has its own internal cheat engine
        [InlineData("arcade")]// fbneo likewise
        public void SystemsWhoseCoreIgnoresCheatCodesNeverOfferThem(string system)
        {
            Assert.False(ArcadeCheatCatalog.SupportsCheatCodes(system));
        }

        [Fact]
        public void EveryCodeSystemIsBothAllowlistedAndMapped()
        {
            Assert.All(ArcadeCheatCatalog.CodeSystems, s =>
            {
                Assert.True(ArcadeCheatCatalog.SupportsCheatCodes(s));
                Assert.False(string.IsNullOrEmpty(ArcadeCheatCatalog.ChtFolder(s)));
            });
            Assert.NotEmpty(ArcadeCheatCatalog.CodeSystems);
        }

        // libretro silently ignores an unrecognized option VALUE. PCSX2's widescreen hint is an enum
        // ("enabled (16:9)"), not the usual bool — getting this wrong makes a no-op toggle that looks fine.
        [Fact]
        public void Ps2WidescreenUsesTheCoresExactEnumValueAndIsPreSelected()
        {
            Assert.Equal("pcsx2_widescreen_hint", ArcadeCheatCatalog.Ps2Widescreen.Key);
            Assert.Equal("enabled (16:9)", ArcadeCheatCatalog.Ps2Widescreen.Value);
            Assert.True(ArcadeCheatCatalog.Ps2Widescreen.DefaultOn);
        }

        [Fact]
        public void Ps2NoInterlacingIsOfferedButNeverDefaultOn()
        {
            Assert.Equal("pcsx2_nointerlacing_hint", ArcadeCheatCatalog.Ps2NoInterlacing.Key);
            Assert.False(ArcadeCheatCatalog.Ps2NoInterlacing.DefaultOn);
        }

        // We can't tell per game whether flycast/Dolphin will actually do anything, so neither may be
        // pre-selected — a default-on toggle has to be one we know applies to THAT game.
        [Fact]
        public void SystemWideOptionCheatsAreNeverDefaultOnAndAlwaysExplainThemselves()
        {
            foreach (var system in new[] { "dc", "gc" })
            {
                var options = ArcadeCheatCatalog.SystemOptionCheats(system);
                Assert.NotEmpty(options);
                Assert.All(options, o =>
                {
                    Assert.False(o.DefaultOn);
                    Assert.False(string.IsNullOrWhiteSpace(o.Note));
                });
            }
        }

        [Fact]
        public void SystemsWithNothingToOfferReturnAnEmptyListAndNoPicker()
        {
            Assert.Empty(ArcadeCheatCatalog.SystemOptionCheats("n64"));
            Assert.Empty(ArcadeCheatCatalog.SystemOptionCheats("nope"));
            Assert.False(ArcadeCheatCatalog.AnyCheatsPossible("naomi"));
            Assert.True(ArcadeCheatCatalog.AnyCheatsPossible("ps2"));
        }

        // A raw memory poke per cheat: a long list of conflicting codes reliably wedges a game, and one
        // upstream file offers 941 of them.
        [Fact]
        public void RoomAndImportCapsAreBounded()
        {
            Assert.InRange(ArcadeCheatCatalog.MaxCheatsPerRoom, 1, 64);
            Assert.InRange(ArcadeCheatCatalog.MaxCheatsPerGame, 50, 1000);
        }
    }
}
