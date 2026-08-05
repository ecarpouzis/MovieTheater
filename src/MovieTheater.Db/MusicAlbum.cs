using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace MovieTheater.Db
{
    /// <summary>
    /// An album in the music library (music-plan.md §2.2): one first-level subfolder of an artist
    /// folder, named <c>Artist - Album (Year)</c> by the library grammar (deeper subfolders — CD1/CD2 —
    /// still belong to this album). Identity is the folder, not the tags (§2.3); the upsert key is
    /// <see cref="FolderPath"/>.
    /// </summary>
    [Table("MusicAlbum")]
    public class MusicAlbum
    {
        [Key]
        public int Id { get; set; }

        public int ArtistId { get; set; }

        [ForeignKey(nameof(ArtistId))]
        public MusicArtist Artist { get; set; } = default!;

        /// <summary>Album title parsed from the folder name (artist prefix and year stripped).</summary>
        [MaxLength(400)]
        public string Title { get; set; } = default!;

        public int? Year { get; set; }

        /// <summary>Folder path relative to the music root, forward slashes
        /// (e.g. <c>AC-DC (1975-2000)/AC-DC - Back in Black (1980)</c>) — the ingest upsert key.</summary>
        [MaxLength(600)]
        public string FolderPath { get; set; } = default!;

        /// <summary>Bracket curation tag from the folder name (e.g. "Collector's"); null when none.</summary>
        [MaxLength(200)]
        public string? Tag { get; set; }

        /// <summary>Album art exists on the images mount (served via /MusicImage). Filled by the art pass (§2.5).</summary>
        public bool HasArt { get; set; }

        /// <summary>Average color of the art thumbnail ("#RRGGBB"), for player theming; null until the art pass.</summary>
        [MaxLength(16)]
        public string? DominantColor { get; set; }

        /// <summary>When the REMOTE art lookup (MusicBrainz → Cover Art Archive → iTunes) last ran for
        /// this album — the negative cache (§2.5). Set even on a miss so a re-run skips albums the
        /// internet has already told us it doesn't have; null means "never asked".</summary>
        public DateTime? ArtCheckedUtc { get; set; }
    }
}
