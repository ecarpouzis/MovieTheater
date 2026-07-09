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
    /// ROM filename → .cht file. The property that matters is SAFETY: a cheat code is an address poke, so
    /// matching the wrong dump corrupts state rather than failing cleanly. Upstream names carry a cheat-device
    /// suffix and often a broader region tag than the individual dump, which is why exact-compare isn't enough.
    /// </summary>
    public class ArcadeChtIndexTests
    {
        private static ArcadeChtIndex Index(params string[] names) =>
            ArcadeChtIndex.Build(names.Select(n => $"C:/cht/{n}.cht"));

        [Fact]
        public void PrefersTheExactFilename()
        {
            var idx = Index("Ape Escape (USA)", "Ape Escape (USA, Europe) (Game Buster)");
            Assert.EndsWith("Ape Escape (USA).cht", idx.Match("Ape Escape (USA)"));
        }

        [Fact]
        public void MatchesAcrossWordOrder_TheGoldenEyeCase()
        {
            var idx = Index("GoldenEye 007 (USA)");
            Assert.NotNull(idx.Match("007 - GoldenEye (USA)"));
        }

        // Our dump's region is a subset of the cheat file's. This is the common shape upstream and used to be
        // the bulk of the misses.
        [Fact]
        public void MatchesWhenOurRegionIsInsideTheCheatFilesRegionSet()
        {
            var idx = Index("Ape Escape (USA, Europe) (Game Buster)");
            Assert.NotNull(idx.Match("Ape Escape (USA)"));
        }

        [Fact]
        public void WorldExpandsToEveryRegion()
        {
            var idx = Index("Spyro the Dragon (World) (Game Buster)");
            Assert.NotNull(idx.Match("Spyro the Dragon (USA)"));
            Assert.NotNull(idx.Match("Spyro the Dragon (Japan)"));
        }

        // THE test. A PAL-only cheat file must never be handed to the NTSC dump.
        [Fact]
        public void RefusesToCrossRegions()
        {
            var idx = Index("Micro Machines V3 (Europe) (Xploder)");
            Assert.Null(idx.Match("Micro Machines V3 (USA)"));
        }

        [Fact]
        public void PicksTheRegionCompatibleCandidateOverAnIncompatibleOne()
        {
            var idx = Index("Micro Machines V3 (Europe) (Xploder)", "Micro Machines V3 (USA) (GameShark)");
            Assert.Contains("(USA)", idx.Match("Micro Machines V3 (USA)"));
        }

        // "(GameShark)" names a cheat device, not a region — it carries no dump information, so it can't be
        // read as a region mismatch. Such a file is a last-resort wildcard.
        [Fact]
        public void ADeviceOnlyTagIsAWildcardButLosesToARealRegionMatch()
        {
            Assert.NotNull(Index("Contra (GameShark)").Match("Contra (USA)"));

            var idx = Index("Contra (GameShark)", "Contra (USA)");
            Assert.EndsWith("Contra (USA).cht", idx.Match("Contra (USA)"));
        }

        // Token SET equality, not containment: an added or dropped word is a different game.
        [Fact]
        public void NeverMatchesADifferentTitleThatMerelyOverlaps()
        {
            var idx = Index("Super Star Wars - Return of the Jedi (USA)");
            Assert.Null(idx.Match("Super Return of the Jedi (USA)"));
            Assert.Null(idx.Match("Super Star Wars (USA)"));
        }

        [Fact]
        public void RegionsIgnoresNonRegionParentheticals()
        {
            Assert.Empty(ArcadeChtIndex.Regions("Contra (GameShark)"));
            Assert.Empty(ArcadeChtIndex.Regions("Contra"));
            Assert.Equal(new[] { "usa" }, ArcadeChtIndex.Regions("Contra (USA) (Rev 1)"));
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

        // Each of these was checked by disassembling the core's exported retro_cheat_set: first instruction
        // `ret` = a stub that accepts a code and discards it. `pce` is here because it was WRONGLY allowlisted
        // at first — mednafen_pce is a stub, and 621 rows across 173 games shipped as toggles that could never
        // do anything. A system belongs in CodeCapable only after the probe, never on reasoning about which
        // core family it belongs to.
        [Theory]
        [InlineData("ps2")]   // pcsx2: STUB (reads .pnach)
        [InlineData("dc")]    // flycast: STUB (internal cheat engine)
        [InlineData("arcade")]// fbneo: STUB (internal cheat engine)
        [InlineData("pce")]   // mednafen_pce: STUB — the one that got through
        [InlineData("a2600")] // stella: STUB
        [InlineData("gc")]    // dolphin's is REAL, but upstream has no cht folder for it
        [InlineData("psp")]   // ppsspp's is REAL, but unverified end-to-end here
        public void SystemsWhoseCoreIgnoresCheatCodesNeverOfferThem(string system)
        {
            Assert.False(ArcadeCheatCatalog.SupportsCheatCodes(system));
        }

        // A code-capable system must never lose its cht folder, and a folder must never outlive its
        // allowlist entry — a stale folder is how a stub core gets silently re-imported.
        [Fact]
        public void NoChtFolderExistsForASystemWeWillNotOfferCodesFor()
        {
            Assert.Null(ArcadeCheatCatalog.ChtFolder("pce"));
            Assert.All(ArcadeCheatCatalog.CodeSystems, s => Assert.NotNull(ArcadeCheatCatalog.ChtFolder(s)));
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
