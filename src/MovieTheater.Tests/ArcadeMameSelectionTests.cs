using System;
using System.Collections.Generic;
using System.IO;
using MovieTheater.Arcade;
using Xunit;
using M = MovieTheater.Arcade.ArcadeControlProfile.Machine;

namespace MovieTheater.Tests
{
    /// <summary>
    /// ArcadeMameSelection decides which MAME machines become site cards. The cases are real 0.289 machines
    /// and the progettoSNAPS sections they sit in; each rule is pinned with one machine it must drop and the
    /// video games it must keep.
    /// </summary>
    public class ArcadeMameSelectionTests
    {
        private static readonly ArcadeMameSelection.Folders Folders = new(
            MachineType: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["sf2"] = "Capcom CPS-I", ["100lions"] = "Fruit Machine", ["v4cmaze"] = "Barcrest MPU4 mod.",
                ["galspnbl"] = "Arcade Video game", ["ibm5150"] = "Computer",
            },
            Category: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["sf2"] = "Fighter / Versus", ["galspnbl"] = "Arcade / Pinball", ["5acespkr"] = "Casino / Cards",
                ["bm1stmix"] = "Music Game / Instruments", ["mole"] = "Whac-A-Mole / Hammer",
                ["musclem"] = "Arcade / Strength Tester",
            },
            GameOrNoGame: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["24cdjuke"] = "No Game" },
            Mess: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "nes" });

        private static M Game(string name, string? clone = null, int displays = 1, string? status = "good",
            IReadOnlySet<string>? ctl = null, bool bios = false, bool dev = false, bool mech = false, bool runnable = true,
            IReadOnlyList<(string, string?)>? disks = null, string desc = "") =>
            new(name, clone, "x.cpp", bios, dev, "joy8/2", desc, null, null, mech, runnable, status, displays, 2,
                disks, ctl ?? new HashSet<string> { "joy" });

        [Theory]
        [InlineData("sf2")]            // a fighter on a joystick
        [InlineData("galspnbl")]       // "Arcade / Pinball" is VIDEO pinball — kept
        public void Keeps_playable_video_games(string name) =>
            Assert.Null(ArcadeMameSelection.ExcludeReason(Game(name), Folders));

        [Fact]
        public void Drops_each_non_game_for_its_own_reason()
        {
            string? Why(M m) => ArcadeMameSelection.ExcludeReason(m, Folders);
            Assert.Equal("bios", Why(Game("neogeo", bios: true)));
            Assert.Equal("device", Why(Game("z80", dev: true)));
            Assert.Equal("mechanical", Why(Game("slotx", mech: true)));
            Assert.Equal("not runnable", Why(Game("awbios2", runnable: false)));
            Assert.Equal("computer/console (mess.ini)", Why(Game("nes")));
            Assert.Equal("not a game", Why(Game("24cdjuke")));
            Assert.Equal("machine type: Fruit Machine", Why(Game("100lions")));
            Assert.Equal("machine type: Barcrest MPU4 mod.", Why(Game("v4cmaze")));     // pub fruit-quiz boards
            Assert.Equal("machine type: Computer", Why(Game("ibm5150")));
            Assert.Equal("category: Casino / Cards", Why(Game("5acespkr")));
            Assert.Equal("category: Music Game / Instruments", Why(Game("bm1stmix")));
            Assert.Equal("category: Whac-A-Mole / Hammer", Why(Game("mole")));
            Assert.Equal("category: Arcade / Strength Tester", Why(Game("musclem")));
            Assert.Equal("no screen", Why(Game("screenless", displays: 0)));
            Assert.Equal("panel: joy+mahjong", Why(Game("mjgame", ctl: new HashSet<string> { "mahjong", "joy" })));
            Assert.Equal("does not work (preliminary driver)", Why(Game("broken", status: "preliminary")));
        }

        [Fact]
        public void Imperfect_drivers_stay_in()
        {
            Assert.Null(ArcadeMameSelection.ExcludeReason(Game("sf2", status: "imperfect"), Folders));
        }

        [Theory]
        [InlineData("1991", 1991)]
        [InlineData("199?", null)]
        [InlineData("19??", null)]
        [InlineData(null, null)]
        public void Takes_only_exact_years(string? year, int? expected) => Assert.Equal(expected, ArcadeMameSelection.YearOf(year));

        [Fact]
        public void Titles_a_clone_from_its_parent()
        {
            var all = new Dictionary<string, M>(StringComparer.OrdinalIgnoreCase)
            {
                ["sf2"] = Game("sf2", desc: "Street Fighter II: The World Warrior (World 910522)"),
                ["sf2j"] = Game("sf2j", clone: "sf2", desc: "Street Fighter II: The World Warrior (Japan 911210)"),
            };
            Assert.Equal("Street Fighter II: The World Warrior (World 910522)", ArcadeMameSelection.TitleSourceFor(all["sf2j"], all));
        }

        [Fact]
        public void Finds_a_clones_merged_chd_in_its_parents_folder_and_reports_missing_ones()
        {
            var root = Directory.CreateTempSubdirectory("chds").FullName;
            try
            {
                Directory.CreateDirectory(Path.Combine(root, "kinst"));
                File.WriteAllText(Path.Combine(root, "kinst", "kinst.chd"), "x");
                var parent = Game("kinst", disks: new[] { ("kinst", (string?)null) });
                var clone = Game("kinst13", clone: "kinst", disks: new[] { ("kinst", (string?)"kinst") });
                var lost = Game("area51", disks: new[] { ("area51", (string?)null) });

                Assert.Equal((Path.Combine(root, "kinst"), (string?)null), ArcadeMameSelection.ChdDirFor(parent, root));
                Assert.Equal((Path.Combine(root, "kinst"), (string?)null), ArcadeMameSelection.ChdDirFor(clone, root));
                Assert.Equal((null, "CHD missing: area51"), ArcadeMameSelection.ChdDirFor(lost, root));
                Assert.Equal((null, null), ArcadeMameSelection.ChdDirFor(Game("sf2"), root));   // no disks, nothing to find
                Assert.Equal((null, "needs a CHD (no --chds root given)"), ArcadeMameSelection.ChdDirFor(parent, null));
            }
            finally { Directory.Delete(root, true); }
        }

        [Theory]
        [InlineData("1942 (PlayChoice-10)", "Capcom", "Capcom", "PlayChoice-10")]
        [InlineData("Super Street Fighter II - The New Challengers (scrambled bootleg of Mega Drive version)", "bootleg / Capcom", "Capcom", "Mega Drive bootleg")]
        [InlineData("Xain'd Sleena (SC 3.0, Magnet System)", "EFO SA / Cedar", "Technos", "Magnet System")]
        [InlineData("Star Wars (Sega, US)", "Sega", "Atari", "Sega")]                 // a different game, same name
        [InlineData("Freeze (Atari) (prototype)", "Atari Games", "Cinematronics", "Atari Games")]
        [InlineData("Tetris (Korean bootleg of Mirrorsoft PC-XT Tetris)", "bootleg", "Atari Games", "bootleg")]
        public void A_colliding_title_keeps_what_makes_it_different(string desc, string maker, string otherMaker, string suffix)
        {
            Assert.Equal((ArcadeMameSelection.CollisionAction.Rename, (string?)suffix), ArcadeMameSelection.DecideCollision(desc, maker, otherMaker));
        }

        [Fact]
        public void The_same_game_from_the_same_maker_is_retired_not_renamed()
        {
            // "Atari Games" vs "Atari" compare on the first word: one company, one game, another revision.
            Assert.Equal(ArcadeMameSelection.CollisionAction.SameGame,
                ArcadeMameSelection.DecideCollision("Relief Pitcher (System 1, prototype)", "Atari Games", "Atari").Action);
        }

        [Fact]
        public void Reads_progettosnaps_folder_inis()
        {
            var path = Path.GetTempFileName();
            try
            {
                File.WriteAllText(path, "[FOLDER_SETTINGS]\nRootFolderIcon=mame\n\n[ROOT_FOLDER]\n\n[Fruit Machine]\n100lions\n;comment\n[Arcade Video game]\nsf2\n");
                var map = ArcadeMameSelection.ReadIni(path);
                Assert.Equal("Fruit Machine", map["100lions"]);
                Assert.Equal("Arcade Video game", map["SF2"]);   // case-insensitive
                Assert.False(map.ContainsKey("RootFolderIcon=mame"));
                Assert.Equal(2, map.Count);
            }
            finally { File.Delete(path); }
        }
    }
}
