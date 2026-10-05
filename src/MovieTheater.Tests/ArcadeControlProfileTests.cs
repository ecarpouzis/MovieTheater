using System.IO;
using System.Linq;
using MovieTheater.Arcade;
using Xunit;

namespace MovieTheater.Tests
{
    /// <summary>
    /// ArcadeControlProfile turns MAME -listxml control panels into the touch pad's profile strings. The
    /// examples are real 0.215 listxml entries; the same strings are pinned on the UI side
    /// (src/ui/src/Pages/Arcade/touch/touchPresets.test.ts) — change both together.
    /// </summary>
    public class ArcadeControlProfileTests
    {
        private const string Xml = """
            <?xml version="1.0"?>
            <mame build="0.215">
              <machine name="sf2" sourcefile="cps1.cpp">
                <rom name="x" size="1"/>
                <input players="2" coins="2" service="yes">
                  <control type="joy" player="1" buttons="6" ways="8"/>
                  <control type="joy" player="2" buttons="6" ways="8"/>
                </input>
              </machine>
              <machine name="sfa" sourcefile="capcom/cps2.cpp" cloneof="sfa_parent">
                <input players="2"><control type="joy" player="1" buttons="6" ways="8"/></input>
              </machine>
              <machine name="mk" sourcefile="midtunit.cpp">
                <input players="2"><control type="joy" player="1" buttons="6" ways="8"/></input>
              </machine>
              <machine name="pacman" sourcefile="pacman.cpp">
                <input players="2" coins="2"><control type="joy" player="1" ways="4"/><control type="joy" player="2" ways="4"/></input>
              </machine>
              <machine name="invaders" sourcefile="mw8080bw.cpp">
                <input players="2"><control type="joy" player="1" buttons="1" ways="2"/></input>
              </machine>
              <machine name="robotron" sourcefile="williams.cpp">
                <input players="1"><control type="doublejoy" ways="8" ways2="8"/></input>
              </machine>
              <machine name="centiped" sourcefile="centiped.cpp">
                <input players="1"><control type="joy" buttons="1" ways="8"/><control type="trackball" minimum="0" maximum="255"/></input>
              </machine>
              <machine name="offroad" sourcefile="leland.cpp">
                <input players="3">
                  <control type="dial" player="1" buttons="1"/><control type="pedal" player="1"/>
                  <control type="dial" player="2" buttons="1"/>
                </input>
              </machine>
              <machine name="duckhunt" sourcefile="vsnes.cpp">
                <input players="1"><control type="lightgun" buttons="1"/></input>
              </machine>
              <machine name="sinistar" sourcefile="williams.cpp">
                <input players="1"><control type="stick" buttons="2"/></input>
              </machine>
              <machine name="mjgame" sourcefile="dynax.cpp">
                <input players="1"><control type="mahjong" buttons="20"/></input>
              </machine>
              <machine name="neogeo" sourcefile="neogeo.cpp" isbios="yes">
                <input players="2"><control type="joy" player="1" buttons="4" ways="8"/></input>
              </machine>
              <machine name="z80" sourcefile="z80.cpp" isdevice="yes"/>
            </mame>
            """;

        private static System.Collections.Generic.Dictionary<string, ArcadeControlProfile.Machine> Load()
        {
            var path = Path.GetTempFileName();
            try
            {
                File.WriteAllText(path, Xml.Trim());
                return ArcadeControlProfile.Read(path).ToDictionary(m => m.Name);
            }
            finally { File.Delete(path); }
        }

        [Theory]
        [InlineData("sf2", "joy8/6/sf")]          // CPS1 six-button: FBNeo's Street Fighter layout
        [InlineData("sfa", "joy8/6/sf")]          // a source path with a folder still reads as cps2
        [InlineData("mk", "joy8/6")]              // six buttons, but not a Capcom board → numbered rows
        [InlineData("pacman", "joy4/0")]
        [InlineData("invaders", "joy4/1")]        // 2-way counts as one-direction-at-a-time
        [InlineData("robotron", "twin/0")]
        [InlineData("centiped", "trackball/1")]   // the trackball beats the joystick beside it
        [InlineData("offroad", "spinner/1")]      // dial + pedal → spinner; only player 1's controls count
        [InlineData("duckhunt", "gun/1")]
        [InlineData("sinistar", "stick/2")]
        [InlineData("mjgame", "other/20")]
        public void Classifies_the_player1_panel(string name, string expected)
        {
            Assert.Equal(expected, Load()[name].Profile);
        }

        [Fact]
        public void Reports_bios_device_and_clone_flags()
        {
            var all = Load();
            Assert.True(all["neogeo"].IsBios);
            Assert.True(all["z80"].IsDevice);
            Assert.Null(all["z80"].Profile);                // no <input> → no profile
            Assert.Equal("sfa_parent", all["sfa"].CloneOf);
        }
    }
}
