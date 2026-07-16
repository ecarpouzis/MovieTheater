using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace MovieTheater.Db
{
    /// <summary>
    /// Per-game emulation overrides, the source of truth for the arcade's per-game config feature
    /// (docs/arcade-per-game-config.md). Keyed by <b>normalized game identity</b> — (System, TitleKey)
    /// where TitleKey is the lowercased <see cref="ArcadeGame.Title"/> — NOT by an individual ROM file,
    /// so one row covers every region/revision/edition of a game (present and future imports). Example:
    /// (dc, "sonic adventure") → ForcedFps 30 fixes the 30fps-locked engine's double-speed for all its
    /// ROM variants at once.
    ///
    /// <para>The <c>arcade-gameconfig-export</c> CLI joins these rows to the matching <see cref="ArcadeGame"/>
    /// rows and emits the worker's <c>game-overrides.json</c> manifest (keyed by CloudRetroGameKey, which is
    /// how the emulator matches). This table can be seeded in bulk / imported from curated community lists;
    /// it is deliberately independent of the ArcadeGame rows so re-ingesting the romset never drops fixes.</para>
    /// </summary>
    [Table("ArcadeGameProfile")]
    public class ArcadeGameProfile
    {
        [Key]
        public int Id { get; set; }

        /// <summary>System code, matching <see cref="ArcadeGame.System"/> (e.g. 'dc','psp','n64').</summary>
        [MaxLength(20)]
        public string System { get; set; } = default!;

        /// <summary>Normalized identity: the lowercased <see cref="ArcadeGame.Title"/> (already tag- and
        /// version-stripped by ArcadeNaming.CleanTitle). All ROM rows whose Title lowercases to this share
        /// the profile. e.g. "sonic adventure".</summary>
        [MaxLength(200)]
        public string TitleKey { get; set; } = default!;

        /// <summary>Forced framerate for the game's true engine rate. Null = leave the core's advertised
        /// rate. Applied in the worker by overriding the libretro AV timing (paces retro_run to this).</summary>
        public double? ForcedFps { get; set; }

        /// <summary>Libretro core-option overrides as a JSON object (<c>{"key":"value"}</c>), applied per
        /// ROM before the game loads. Null/empty = none. The universal escape hatch (widescreen, region,
        /// internal resolution, frameskip, …) — mirrors RetroArch per-game core options.</summary>
        public string? CoreOptionsJson { get; set; }

        /// <summary>Explicit per-game hardware-render context: <c>"gl"</c> or <c>"vulkan"</c> (null = defer
        /// to the worker's renderer-option inference and the core config default). Exported to the worker's
        /// game-overrides.json <c>hwContext</c> field, where it is the ABSOLUTE top of the room layer's
        /// 3-level hw-context precedence (above renderer-option inference and the core default). This is the
        /// lever for cores with no renderer core-option — PPSSPP (psp), flycast (dc), Dolphin (gc) all just
        /// follow the frontend's hw context — to pin one game onto (or off of) the Vulkan-capture path.
        /// See docs/arcade-vulkan-w3w4w5-spec.md (F1) and nanoarch.GameHwContext.</summary>
        [MaxLength(10)]
        public string? HwContext { get; set; }

        /// <summary>Free-form provenance/why (e.g. "30fps-locked engine; community list").</summary>
        public string? Notes { get; set; }
    }
}
