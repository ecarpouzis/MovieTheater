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

        // Some upstream files use a pseudo-cheat as a heading ("NOTE: Read Description"), with the literal
        // string "folder" where the code goes. A core rejects it, so importing it would put a toggle in the
        // picker that can never do anything.
        [Fact]
        public void SkipsRetroArchsFolderHeadingRows()
        {
            var entries = ArcadeChtFile.Parse(
                "cheat0_desc = \"NOTE: Read Description\"\ncheat0_code = \"folder\"\n" +
                "cheat1_desc = \"Real\"\ncheat1_code = \"02000000 00000001\"", out _);

            Assert.Equal("Real", Assert.Single(entries).Name);
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

        // ── Per-system naming profiles ───────────────────────────────────────────────────────────────
        // The nds collection is a numbered release set whose names defeat all three No-Intro assumptions.
        // These rules are opt-in BECAUSE they are not universally safe: applied to every system they were
        // measured to change 87 already-working matches (18 lost) across the 13 systems already importing.

        private static ArcadeChtIndex NumberedSet(params string[] names) =>
            ArcadeChtIndex.Build(names.Select(n => $"C:/cht/{n}.cht"), ArcadeChtIndex.NamingProfile.NumberedSet);

        [Fact]
        public void ACatalogueNumberIsNotPartOfTheTitle()
        {
            Assert.NotNull(NumberedSet("Mario Kart DS (USA)").Match("0168 - Mario Kart DS (US)(M5)"));
            // ...but only under the profile that says so. The default rules must not strip it, or
            // "007 - Agent Under Fire" would silently lose its title's first word.
            Assert.Null(ArcadeChtIndex.Build(new[] { "C:/cht/Mario Kart DS (USA).cht" })
                                      .Match("0168 - Mario Kart DS (US)(M5)"));
        }

        [Fact]
        public void ShortRegionCodesAreReadOnlyWhereTheProfileSaysSo()
        {
            Assert.Equal(new[] { "usa" }, ArcadeChtIndex.Regions("Foo (US)", ArcadeChtIndex.NamingProfile.NumberedSet));
            Assert.Empty(ArcadeChtIndex.Regions("Foo (US)"));
        }

        // The bug this rule exists for: the region is not always the leading tag, and reading only the first
        // one made "Alice in Wonderland (DSi Enhanced) [b] (US)" look region-LESS — which let it match a
        // Europe cheat file as a wildcard. A US disc taking European addresses is the exact corruption case.
        [Fact]
        public void ARegionTagIsHonouredWhereverItAppearsInTheName()
        {
            var idx = NumberedSet("Alice in Wonderland (Europe)");
            Assert.Null(idx.Match("4762 - Alice in Wonderland (DSi Enhanced) [b] (US)"));

            Assert.Equal(new[] { "usa" },
                ArcadeChtIndex.Regions("Alice in Wonderland (DSi Enhanced) (US)", ArcadeChtIndex.NamingProfile.NumberedSet));
        }

        // THE romhack test. Upstream stores hack/translation cheat files beside the stock game's, and the
        // hack's name sits in a parenthetical that the title signature strips — so both files have the same
        // signature AND the same region, and an arbitrary pick gives a stock ROM a hack's addresses. This is
        // not hypothetical: it is what our Mario Kart DS matched on the first run.
        [Fact]
        public void PrefersTheStockDumpOverARomHacksCheatFile()
        {
            var idx = NumberedSet("Mario Kart DS (USA) (CTGP Nitro (v1.0.0))", "Mario Kart DS (USA)");
            Assert.EndsWith("Mario Kart DS (USA).cht", idx.Match("0168 - Mario Kart DS (US)(M5)"));
        }

        [Fact]
        public void PrefersAPlainDumpOverARevisionOrDeviceSuffixedOne()
        {
            Assert.EndsWith("Contra (USA).cht",
                Index("Contra (USA) (Rev 1)", "Contra (USA)").Match("Contra (USA) (Beta)"));
            Assert.EndsWith("Ape Escape (USA).cht",
                Index("Ape Escape (USA, Europe) (Game Buster)", "Ape Escape (USA)").Match("Ape Escape (USA) (Demo)"));
        }

        // Same inputs must give the same answer regardless of the order the directory listed them in —
        // otherwise which codes a game gets depends on the filesystem.
        [Fact]
        public void TheChoiceIsStableWhateverOrderTheFilesArriveIn()
        {
            var a = Index("Contra (USA) (Rev 1)", "Contra (USA) (Rev 2)").Match("Contra (USA)");
            var b = Index("Contra (USA) (Rev 2)", "Contra (USA) (Rev 1)").Match("Contra (USA)");
            Assert.Equal(a, b);
        }

        [Fact]
        public void DecorationCountsWordsInsideNestedBrackets()
        {
            Assert.Equal(0, ArcadeChtIndex.Decoration("C:/cht/Mario Kart DS.cht"));
            Assert.Equal(1, ArcadeChtIndex.Decoration("C:/cht/Mario Kart DS (USA).cht"));
            // CTGP, Nitro, v1, 0, 0 — the exact count doesn't matter, only that a hack's name scores well
            // above a bare region tag, which is what makes the stock dump win.
            Assert.Equal(5, ArcadeChtIndex.Decoration("C:/cht/Mario Kart DS (CTGP Nitro (v1.0.0)).cht"));
        }

        // A French dump is not a European one for cheat purposes; it must miss rather than guess.
        [Fact]
        public void ALanguageSpecificDumpDoesNotTakeAGenericEuropeanFilesCodes()
        {
            Assert.Null(NumberedSet("Madagascar (Europe)").Match("0165 - Madagascar (IT)"));
            Assert.NotNull(NumberedSet("Madagascar (Italy)").Match("0165 - Madagascar (IT)"));
        }
    }

    /// <summary>
    /// GameCube/Wii cheats. Dolphin's <c>retro_cheat_set</c> does not accept a code — it looks up one it
    /// already loaded by RE-SERIALIZING it and comparing strings, so the serialization below IS the wire
    /// format. A byte out of place is a cheat that silently never fires. See docs/arcade-cheats.md.
    /// </summary>
    public class DolphinGameIniTests
    {
        // Verbatim from Sys/GameSettings/GFZE01.ini, which is where "unlock all the F-Zero GX vehicles"
        // actually lives. Expected strings are the ones the core's own generated .cht carries.
        private const string FZeroGx = """
            # GFZE01 - F-Zero GX

            [OnFrame]
            # Add memory patches to be applied every frame here.

            [ActionReplay]
            # Add action replay cheats here.
            $Unlock AX Cup Tracks
            9C0030C8 00120000
            840030C8 0023DA00
            420030C8 0000FFFF
            420030C8 0002FFFF
            840030C8 FFDC2600

            [Gecko]
            $Infinite Energy [Someone]
            04123456 60000000
            *A note about the code
            0412345A 38000001
            """;

        [Fact]
        public void ActionReplayOpsAreReEmittedCanonically()
        {
            var cheats = DolphinGameIni.Parse(new[] { FZeroGx }, out _);

            var ar = Assert.Single(cheats, c => c.Kind == "ar");
            Assert.Equal("Unlock AX Cup Tracks", ar.Name);
            Assert.Equal("9C0030C8 00120000+840030C8 0023DA00+420030C8 0000FFFF+420030C8 0002FFFF+840030C8 FFDC2600",
                         ar.Code);
        }

        // Gecko lines go back VERBATIM (Dolphin keeps original_line), notes are dropped, and the "[creator]"
        // suffix is not part of the name.
        [Fact]
        public void GeckoLinesAreVerbatimAndNotesAreDropped()
        {
            var gecko = Assert.Single(DolphinGameIni.Parse(new[] { FZeroGx }, out _), c => c.Kind == "gecko");
            Assert.Equal("Infinite Energy", gecko.Name);
            Assert.Equal("04123456 60000000+0412345A 38000001", gecko.Code);
        }

        // AR codes can be stored ENCRYPTED, and Dolphin decrypts them at load. We cannot predict the ops that
        // produces, so the whole code is dropped — offering it would create a toggle whose string can never
        // match anything, which is the one outcome this subsystem is built to avoid.
        [Fact]
        public void AnEncryptedActionReplayCodeIsDroppedNotHalfImported()
        {
            var ini = """
                [ActionReplay]
                $Plain
                00000001 00000002
                $Encrypted
                ZBFV-N5WW-VDFRM
                00000003 00000004
                """;
            var cheats = DolphinGameIni.Parse(new[] { ini }, out int skipped);

            Assert.Equal(1, skipped);
            Assert.Equal("Plain", Assert.Single(cheats).Name);
        }

        // Dolphin layers a whole CHAIN of INIs per disc; the real cheat list is their union. Reading only
        // GFZE01.ini (or worse, only the generic GFZ.ini) is how a game loses most of its codes.
        [Fact]
        public void TheIniChainIsSystemThenRegionlessThenIdThenRevision()
        {
            Assert.Equal(new[] { "G.ini", "GFZ.ini", "GFZE01.ini", "GFZE01r2.ini" },
                         DolphinGameIni.IniChain("GFZE01", 2).ToArray());
        }

        [Fact]
        public void CheatsFromEveryFileInTheChainAreCombined()
        {
            var generic = "[Gecko]\n$Shared\n04000000 60000000";
            var specific = "[ActionReplay]\n$Specific\n00000001 00000002";

            var cheats = DolphinGameIni.Parse(new[] { generic, specific }, out _);
            Assert.Equal(2, cheats.Count);
            Assert.Contains(cheats, c => c.Name == "Shared");
            Assert.Contains(cheats, c => c.Name == "Specific");
        }

        // A '#' comment is stripped by Dolphin's INI reader BEFORE the cheat parser sees the line, so a
        // trailing comment is not part of the Gecko code text we have to reproduce.
        [Fact]
        public void CommentsAreRemovedTheWayDolphinsIniReaderRemovesThem()
        {
            var cheats = DolphinGameIni.Parse(new[] { "[Gecko]\n$X\n04000000 60000000  # why\n# whole line\n" }, out _);
            Assert.Equal("04000000 60000000", Assert.Single(cheats).Code);
        }

        [Fact]
        public void SectionsOtherThanTheCheatOnesAreIgnored()
        {
            var ini = "[Video_Settings]\n$NotACheat\n00000001 00000002\n[ActionReplay]\n$Real\n00000003 00000004";
            Assert.Equal("Real", Assert.Single(DolphinGameIni.Parse(new[] { ini }, out _)).Name);
        }
    }

    /// <summary>
    /// The disc-id read. Its whole job is to be certain or to say nothing: a wrong id hands a game another
    /// game's memory pokes, so every unparseable answer must come back null rather than partially filled.
    /// </summary>
    public class DolphinDiscIdTests
    {
        [Fact]
        public void ReadsTheGameIdAndRevision()
        {
            var h = DolphinDiscId.FromJson(
                """{"country":"USA","game_id":"GFZE01","internal_name":"F-ZERO GX (US Version)","region":"NTSC-U","revision":0}""");

            Assert.NotNull(h);
            Assert.Equal("GFZE01", h!.GameId);
            Assert.Equal(0, h.Revision);
            Assert.Equal("USA", h.Country);
        }

        [Fact]
        public void MissingRevisionMeansRevisionZeroNotAFailure()
        {
            Assert.Equal(0, DolphinDiscId.FromJson("""{"game_id":"GUNE5D"}""")!.Revision);
        }

        [Theory]
        [InlineData("""{"country":"USA"}""")]   // no id at all
        [InlineData("""{"game_id":""}""")]      // present but empty
        [InlineData("not json")]
        [InlineData("")]
        public void AnythingWithoutAUsableIdIsNull(string json)
        {
            Assert.Null(DolphinDiscId.FromJson(json));
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
        [InlineData("saturn")]// kronos is REAL, but our coded filenames can't be region-checked yet
        [InlineData("psp")]   // ppsspp is REAL, but no region tags to check AND its cheats outlive the room
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

        // Every allowlisted system must have SOME source of cheats — either an upstream cht folder or the
        // Dolphin-INI path. An entry with neither is a system that offers a picker it can never fill.
        [Fact]
        public void EveryCodeSystemIsBothAllowlistedAndMapped()
        {
            Assert.All(ArcadeCheatCatalog.CodeSystems, s =>
            {
                Assert.True(ArcadeCheatCatalog.SupportsCheatCodes(s));
                Assert.False(string.IsNullOrEmpty(ArcadeCheatCatalog.ChtFolder(s)));
            });
            Assert.NotEmpty(ArcadeCheatCatalog.CodeSystems);

            Assert.All(ArcadeCheatCatalog.DolphinIniSystems, s =>
            {
                Assert.True(ArcadeCheatCatalog.SupportsCheatCodes(s));
                // Dolphin's cheats come from its own INIs; a cht folder here would mean two sources fighting.
                Assert.Null(ArcadeCheatCatalog.ChtFolder(s));
            });
        }

        // GameCube/Wii cheats are inert without dolphin_cheats_enabled, whose core-side default is FALSE —
        // exactly the "accepted and discarded" failure the allowlist exists to prevent, one level up.
        [Fact]
        public void GameCubeAndWiiCheatsCarryTheCoreOptionThatMakesThemWork()
        {
            foreach (var system in new[] { "gc", "wii" })
            {
                var implied = ArcadeCheatCatalog.ImpliedOptionsForSystem(system);
                Assert.Contains(("dolphin_cheats_enabled", "enabled"), implied);
                Assert.Contains(("dolphin_cheats_import", "enabled"), implied);
            }

            // Systems whose cores need nothing extra must stay clean — an unasked-for option is a silent
            // behaviour change for every room that picks a cheat.
            Assert.Empty(ArcadeCheatCatalog.ImpliedOptionsForSystem("n64"));
            Assert.Empty(ArcadeCheatCatalog.ImpliedOptionsForSystem("nds"));
            Assert.Empty(ArcadeCheatCatalog.ImpliedOptionsForSystem(null));
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

        // The system-wide quality toggles moved from the cheat catalog to the per-game config tool's catalog.
        // Every catalogued option must label itself and carry a DEFAULT that is one of its own value tokens —
        // libretro silently ignores an unknown token, so a bad default would ship a dead option.
        [Fact]
        public void ConfigCatalogOptionsAreWellFormedAndExplainThemselves()
        {
            foreach (var system in new[] { "dc", "gc", "ps2", "ps1" })
            {
                var options = ArcadeCoreOptionCatalog.For(system);
                Assert.NotEmpty(options);
                Assert.All(options, o =>
                {
                    Assert.False(string.IsNullOrWhiteSpace(o.Label));
                    // A non-range option's default must be a valid token; a range option validates numerically.
                    Assert.True(o.IsValidToken(o.Default), $"{system}/{o.Key} default '{o.Default}' is not a valid token");
                });
            }
        }

        // The ghosting fix is an enum option gated behind a master switch. Both halves are silent-failure
        // traps: a wrong VALUE is ignored by libretro ("Align to Native" is the DLL's exact token, not
        // "enabled"), and without pcsx2_enable_hw_hacks the core never reads the option at all.
        [Fact]
        public void Ps2GhostingFixUsesTheExactEnumTokenAndImpliesTheHwHacksMasterSwitch()
        {
            var fix = ArcadeCoreOptionCatalog.Find("pcsx2", "pcsx2_half_pixel_offset");
            Assert.NotNull(fix);
            Assert.True(fix!.IsValidToken("Align to Native"));

            var implied = Assert.Single(ArcadeCheatCatalog.ImpliedOptionsFor(fix.Key));
            Assert.Equal(("pcsx2_enable_hw_hacks", "enabled"), implied);

            // Options with no gate stay implication-free — widescreen must never drag hw_hacks in,
            // because the master switch disables the GameDB auto-fixes.
            Assert.Empty(ArcadeCheatCatalog.ImpliedOptionsFor(ArcadeCheatCatalog.Ps2Widescreen.Key));
            Assert.Empty(ArcadeCheatCatalog.ImpliedOptionsFor("reicast_widescreen_cheats"));
        }

        // The relocated widescreen toggle keeps the core's EXACT enum token (not "enabled").
        [Fact]
        public void Ps2WidescreenConfigOptionUsesTheExactEnumToken()
        {
            var ws = ArcadeCoreOptionCatalog.Find("pcsx2", "pcsx2_widescreen_hint");
            Assert.NotNull(ws);
            Assert.True(ws!.IsValidToken("enabled (16:9)"));
            Assert.False(ws.IsValidToken("enabled"));
        }

        [Fact]
        public void SystemsWithNothingToConfigureReturnAnEmptyCatalog()
        {
            Assert.Empty(ArcadeCoreOptionCatalog.For("nope"));
            // nes DOES have config options in production (nestopia's extracted set) — this assembly now
            // embeds the extraction too, so the test sees what the site sees.
            Assert.True(ArcadeCoreOptionCatalog.HasAnything("nes"));
            Assert.True(ArcadeCoreOptionCatalog.HasAnything("ps2"));
        }

        // Forcing a renderer must flip the core's OWN renderer option, not just the surface — the exact
        // tokens matter (libretro ignores an unknown value; a GL surface + paraLLEl-RDP strands N64).
        [Fact]
        public void RendererProfilesFlipTheCoresRendererOptionWithExactTokens()
        {
            var n64Gl = ArcadeRendererProfiles.Options("n64", "gl");
            Assert.Equal("gliden64", n64Gl["mupen64plus-rdp-plugin"]);
            Assert.Equal("hle", n64Gl["mupen64plus-rsp-plugin"]);
            var n64Vk = ArcadeRendererProfiles.Options("n64", "vulkan");
            Assert.Equal("parallel", n64Vk["mupen64plus-rdp-plugin"]);
            Assert.Equal("parallel", n64Vk["mupen64plus-rsp-plugin"]);

            Assert.Equal("OpenGL", ArcadeRendererProfiles.Options("ps2", "gl")["pcsx2_renderer"]);
            Assert.Equal("paraLLEl-GS", ArcadeRendererProfiles.Options("ps2", "vulkan")["pcsx2_renderer"]);
            Assert.Equal("hardware_gl", ArcadeRendererProfiles.Options("ps1", "gl")["beetle_psx_hw_renderer"]);
            Assert.Equal("hardware_vk", ArcadeRendererProfiles.Options("ps1", "vulkan")["beetle_psx_hw_renderer"]);
        }

        // Surface-only cores (no renderer core-option) carry no injected options — the frontend surface
        // selects; and an unknown system/renderer is an empty, safe no-op.
        [Fact]
        public void SurfaceOnlyAndUnknownSystemsInjectNothing()
        {
            Assert.Empty(ArcadeRendererProfiles.Options("psp", "gl"));
            Assert.Empty(ArcadeRendererProfiles.Options("gc", "vulkan"));
            Assert.Empty(ArcadeRendererProfiles.Options("dc", "gl"));
            Assert.Empty(ArcadeRendererProfiles.Options("nope", "gl"));
            Assert.Empty(ArcadeRendererProfiles.Options("n64", null));
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
