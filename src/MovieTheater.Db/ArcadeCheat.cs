using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace MovieTheater.Db
{
    /// <summary>
    /// One cheat the lobby can offer for one ROM. Populated by the <c>arcade-cheats-import</c> CLI
    /// (chunked/resumable/idempotent, dry-run by default).
    ///
    /// <para>Rows are keyed to a specific <see cref="ArcadeGame"/> — i.e. one ROM, not the lobby "card" —
    /// because a cheat is region-specific: a USA GameShark address is meaningless on the Japanese dump, and
    /// PCSX2's widescreen patches are per (title, region). That is why the card's version dropdown drives
    /// which cheat list you see.</para>
    ///
    /// <para>Two kinds reach the emulator by two different mechanisms, both applied per room at boot:</para>
    /// <list type="bullet">
    ///   <item><b>code</b> (<see cref="Code"/>) — a community cheat code passed to the libretro cheat API
    ///     (<c>retro_cheat_set</c>), imported from <c>libretro-database/cht</c>. Only some cores implement
    ///     that API; <c>ArcadeCheatCatalog.SupportsCheatCodes</c> is the allowlist.</item>
    ///   <item><b>option</b> (<see cref="OptionKey"/>/<see cref="OptionValue"/>) — a libretro core option that
    ///     switches on a patch the emulator already carries (PS2's <c>pcsx2_widescreen_hint</c>). Stored per
    ///     game rather than per system so it is only offered where the core actually has a patch to apply.</item>
    /// </list>
    /// </summary>
    [Table("ArcadeCheat")]
    public class ArcadeCheat
    {
        [Key]
        public int Id { get; set; }

        public int ArcadeGameId { get; set; }
        public ArcadeGame? ArcadeGame { get; set; }

        /// <summary>"code" or "option" — which mechanism applies this cheat (see the class remarks).</summary>
        [MaxLength(10)]
        public string Kind { get; set; } = "code";

        /// <summary>Display + sort order, and the import idempotency key together with the game id. Code cheats
        /// keep their source <c>cheatN_</c> index (so the list matches the upstream file, which is worth more
        /// than any re-ordering we'd invent). Option cheats take NEGATIVE ordinals, which floats them to the
        /// top of the picker — they are the curated, safe ones.</summary>
        public int Ordinal { get; set; }

        /// <summary>Human-readable label. For code cheats this is <c>cheatN_desc</c> verbatim from upstream, so
        /// a minority of entries are not in English — the picker's search box is the mitigation.</summary>
        [MaxLength(200)]
        public string Name { get; set; } = default!;

        /// <summary>Kind=code: the raw <c>cheatN_code</c> string, passed to <c>retro_cheat_set</c> unparsed and
        /// exactly as RetroArch passes it (multi-line codes are '+'-joined upstream, e.g.
        /// "810C0A90 2409+810C0A92 0000"). Upstream entries with no <c>_code</c> are RetroArch's own
        /// memory-scanner cheats, which need a frontend we don't have; they are skipped at import.</summary>
        [MaxLength(4000)]
        public string? Code { get; set; }

        /// <summary>Kind=option: the libretro core-option key/value, e.g.
        /// <c>pcsx2_widescreen_hint</c> = <c>enabled (16:9)</c>. The value must be the core's EXACT token —
        /// libretro silently ignores an unrecognized one (it is not "enabled" here).</summary>
        [MaxLength(60)]
        public string? OptionKey { get; set; }
        [MaxLength(60)]
        public string? OptionValue { get; set; }

        /// <summary>Pre-selected in the lobby picker. True only for the PS2 widescreen patch, which the core
        /// applies from its own database and therefore only exists as a row where it actually does something.</summary>
        public bool DefaultOn { get; set; }

        /// <summary>Shown under the cheat in the picker — the "why"/caveat for a curated option cheat.</summary>
        [MaxLength(200)]
        public string? Note { get; set; }

        /// <summary>Provenance, and the guard that lets one import kind re-run without clobbering the other:
        /// "libretro-cht" (bulk code cheats) or "pcsx2-gamedb" (extracted core patch table).</summary>
        [MaxLength(40)]
        public string Source { get; set; } = "libretro-cht";
    }
}
