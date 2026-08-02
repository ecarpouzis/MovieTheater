using System;
using System.Collections.Generic;
using MovieTheater.Arcade;
using Xunit;

namespace MovieTheater.Tests
{
    /// <summary>
    /// The launch-path cross-core filter (<see cref="ArcadeRoomOptionDelivery"/>): a room receives only
    /// keys its booting core can read, while the flat per-title blob keeps both cores' keys in storage.
    /// The scenarios mirror the real rows the 2026-08-02 evidence sweep flagged.
    /// </summary>
    public class ArcadeRoomOptionDeliveryTests
    {
        private static Dictionary<string, string> Opts(params (string K, string V)[] kv)
        {
            var d = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var (k, v) in kv) d[k] = v;
            return d;
        }

        [Fact]
        public void ParallelN64RoomDropsTheMupenTwinsAndKeepsItsOwn()
        {
            // Last Impact's real blob: parallel-n64 fix + the mupen twins kept for a forced mupen launch.
            var blob = Opts(
                ("parallel-n64-screensize", "1280x960"),
                ("parallel-n64-allow-unaligned-dma", "True"),
                ("parallel-n64-countperop", "1"),
                ("parallel-n64-framerate", "fullspeed"),
                ("mupen64plus-AllowUnalignedDMA", "True"),
                ("mupen64plus-CountPerOp", "1"));
            var (kept, dropped) = ArcadeRoomOptionDelivery.FilterForBootingCore("n64", "parallel_n64", blob);
            Assert.Equal(new[] { "mupen64plus-AllowUnalignedDMA", "mupen64plus-CountPerOp" }, dropped);
            Assert.Equal(4, kept.Count);
            Assert.True(kept.ContainsKey("parallel-n64-countperop"));
        }

        [Fact]
        public void MupenRoomDropsParallelN64KeysSymmetrically()
        {
            var blob = Opts(
                ("mupen64plus-CountPerOp", "1"),
                ("parallel-n64-countperop", "1"));
            var (kept, dropped) = ArcadeRoomOptionDelivery.FilterForBootingCore("n64", "mupen64plus_next", blob);
            Assert.Equal(new[] { "parallel-n64-countperop" }, dropped);
            Assert.True(kept.ContainsKey("mupen64plus-CountPerOp"));
        }

        [Fact]
        public void NullBootingCoreUsesTheSystemDefaultCore()
        {
            // No render profile resolved (or a system without the hw toggle): the system's default core
            // is the booting core — for n64 that is mupen64plus_next.
            var blob = Opts(("parallel-n64-countperop", "1"), ("mupen64plus-pak1", "memory"));
            var (kept, dropped) = ArcadeRoomOptionDelivery.FilterForBootingCore("n64", null, blob);
            Assert.Equal(new[] { "parallel-n64-countperop" }, dropped);
            Assert.True(kept.ContainsKey("mupen64plus-pak1"));
        }

        [Fact]
        public void PcsxRearmedRoomDropsBeetleCatalogKeysButPassesRendererAndOwnKeys()
        {
            var blob = Opts(
                ("beetle_psx_hw_pgxp_mode", "memory only"),     // Beetle catalog key -> foreign, dropped
                ("beetle_psx_hw_renderer", "hardware_vk"),      // renderer-selecting: in NO catalog, passes
                                                                // (the worker-side pin is the real source of
                                                                // this key in rooms; fixed separately in the fork)
                ("pcsx_rearmed_neon_enhancement_enable", "enabled"));
            var (kept, dropped) = ArcadeRoomOptionDelivery.FilterForBootingCore("ps1", "pcsx_rearmed", blob);
            Assert.Equal(new[] { "beetle_psx_hw_pgxp_mode" }, dropped);
            Assert.True(kept.ContainsKey("beetle_psx_hw_renderer"));
            Assert.True(kept.ContainsKey("pcsx_rearmed_neon_enhancement_enable"));
        }

        [Fact]
        public void ProfileDeliveredKeysUnknownToAnyCatalogAlwaysPass()
        {
            // parallel_n64 is hand-only: its gliden64-* FB keys exist in no catalog and must never be
            // withheld — they are exactly what the parallel_n64_gl profile delivers.
            var blob = Opts(
                ("parallel-n64-gliden64-EnableFBEmulation", "True"),
                ("parallel-n64-gfxplugin", "gliden64"));
            var (kept, dropped) = ArcadeRoomOptionDelivery.FilterForBootingCore("n64", "parallel_n64", blob);
            Assert.Empty(dropped);
            Assert.Equal(2, kept.Count);
        }

        [Fact]
        public void SingleCoreSystemIsANoOp()
        {
            // ps2's profiles all share OptionCore pcsx2 — nothing is foreign, advanced keys included.
            var blob = Opts(
                ("pcsx2_pgs_ssaa", "8x SSAA (can high-res)"),
                ("pcsx2_upscale_multiplier", "4x Native (~1440p)"),
                ("some_hand_entered_key", "whatever"));
            var (kept, dropped) = ArcadeRoomOptionDelivery.FilterForBootingCore("ps2", "pcsx2", blob);
            Assert.Empty(dropped);
            Assert.Equal(3, kept.Count);
        }
    }
}
