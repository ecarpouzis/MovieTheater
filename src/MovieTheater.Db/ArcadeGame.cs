using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace MovieTheater.Db
{
    /// <summary>
    /// A playable arcade title (arcade-plan.md §5): one ROM on Ziggy's local disk, matched to a
    /// libretro core by file extension. Deliberately its own small table — arcade games are not
    /// Movies, and none of the movie plumbing (posters pipeline, OData, viewings) applies at v1.
    /// Populated by the <c>arcade-ingest</c> CLI (chunked/resumable/idempotent, upsert on the
    /// System+RomPath unique key; vanished files are flagged <see cref="IsEnabled"/>=false, never
    /// deleted).
    /// </summary>
    [Table("ArcadeGame")]
    public class ArcadeGame
    {
        [Key]
        public int Id { get; set; }

        [MaxLength(200)]
        public string Title { get; set; } = default!;

        /// <summary>Article-inverted sort key, same convention as <c>Movie.SimpleTitle</c>.</summary>
        [MaxLength(200)]
        public string SortTitle { get; set; } = default!;

        /// <summary>'nes','snes','genesis','gb','gbc','gba','n64','ps1','arcade' (§9 matrix).</summary>
        [MaxLength(20)]
        public string System { get; set; } = default!;

        /// <summary>Path relative to the workers' read-only ROM mount — the audit/ingest key.</summary>
        [MaxLength(400)]
        public string RomPath { get; set; } = default!;

        /// <summary>The launch key: the game name as CloudRetro's filename-based library scan exposes it
        /// (t=104 <c>game_name</c> / InitSession <c>games[].title</c>). Stored per game so a filename change
        /// on Ziggy can't silently orphan the catalog row (§3.3).</summary>
        [MaxLength(200)]
        public string CloudRetroGameKey { get; set; } = default!;

        /// <summary>Controller ports this title supports (N64: 4, SNES multitap: 5, GBA: 1).</summary>
        public byte MaxPlayers { get; set; } = 1;

        /// <summary>Rating ceiling on the same scale as the TV channel age gate; a room inherits its
        /// game's ceiling and is hidden from users whose AgeRestriction is below it.</summary>
        public int RatingCeiling { get; set; }

        /// <summary>Box art file on the posters mount, served via the /ArcadeImage route. Null = none yet.</summary>
        [MaxLength(400)]
        public string? BoxArtPath { get; set; }

        /// <summary>For a just-in-time (JIT) game, the source archive on the library drive (e.g. a PSX
        /// <c>.7z</c> in the L: master collection). Null for a directly-staged ROM whose file already
        /// lives under the ROM mount. When set, the ArcadeGateway extracts this into the ROM mount on
        /// demand at play time and LRU-evicts it later (docs/arcade-jit-cache.md); the row is browsable
        /// even while <see cref="RomPath"/> is not yet materialized on disk.</summary>
        [MaxLength(500)]
        public string? SourceArchivePath { get; set; }

        public int? Year { get; set; }

        public bool IsEnabled { get; set; } = true;

        public string? Notes { get; set; }
    }
}
