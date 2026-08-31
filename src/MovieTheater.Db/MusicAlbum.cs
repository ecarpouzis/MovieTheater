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

        /// <summary>
        /// How well known the record is, 0–100 (R9 S10) — NOT how good it is. Derived from an external
        /// audience signal (Last.fm listeners today) by <c>music-enrich</c>; null until that pass has
        /// looked, and null forever for a record the world has never heard of.
        /// </summary>
        /// <remarks>
        /// Separate from the site's own <see cref="MusicAlbumRating"/> on purpose, and the "Top rated"
        /// order BLENDS them rather than picking one: a shelf ordered purely by popularity is a chart
        /// nobody here wrote, and a shelf ordered purely by our own ratings is empty until somebody
        /// rates something.
        /// </remarks>
        public int? Popularity { get; set; }

        /// <summary>Which external source produced <see cref="Popularity"/> — see
        /// <see cref="MusicGenreSources"/>. Bulk-written values are stamped so one source can be
        /// re-run or retired without guessing which rows came from it.</summary>
        [MaxLength(32)]
        public string? PopularitySource { get; set; }

        /// <summary>When <c>music-enrich</c> last asked about this album — the negative cache, stamped
        /// on a miss as well as a hit (the <see cref="ArtCheckedUtc"/> convention), so the external
        /// queue shrinks monotonically and terminates.</summary>
        public DateTime? PopularityCheckedUtc { get; set; }

        /// <summary>
        /// 0–100 RATING from an outside community (<see cref="MovieTheater.Music.MusicRating"/>), or
        /// null when nobody has rated the record.
        /// </summary>
        /// <remarks>
        /// A separate column from <see cref="Popularity"/> because they are separate facts: this is
        /// how good the people who heard it say it is, that is how many people heard it. The house's
        /// own <c>MusicAlbumRating</c> table stays the more authoritative source when it has votes —
        /// it just never will at this scale, which is why the rating had to come from outside.
        /// </remarks>
        public int? ExternalRating { get; set; }

        /// <summary>How many people that rating represents. Carried so the UI can say "from N" and so
        /// the shrink can be re-tuned offline without re-asking anyone's API.</summary>
        public int? ExternalRatingVotes { get; set; }

        /// <summary>Which community produced <see cref="ExternalRating"/> — see
        /// <see cref="MusicGenreSources"/>.</summary>
        [MaxLength(32)]
        public string? ExternalRatingSource { get; set; }

        /// <summary>When the rating leg last asked about this album — its own negative cache, kept
        /// apart from <see cref="PopularityCheckedUtc"/> so either leg can be re-run alone.</summary>
        public DateTime? ExternalRatingCheckedUtc { get; set; }
    }
}
