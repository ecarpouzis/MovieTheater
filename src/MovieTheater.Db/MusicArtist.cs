using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace MovieTheater.Db
{
    /// <summary>
    /// An artist in the music library (music-plan.md §2.2): one top-level folder under the music
    /// root, whose name follows the library grammar <c>Artist (YearRange)</c>. Deliberately its own
    /// small table — music is not Movies, and identity comes from the curated folder tree, not tags
    /// (§2.3). Populated by the <c>music-ingest</c> CLI (chunked/resumable/idempotent, upsert on
    /// <see cref="FolderName"/>; vanished folders only flag their tracks, rows are never deleted).
    /// </summary>
    [Table("MusicArtist")]
    public class MusicArtist
    {
        [Key]
        public int Id { get; set; }

        /// <summary>Display name, article restored ("Offspring, The" folder ⇒ "The Offspring").</summary>
        [MaxLength(300)]
        public string Name { get; set; } = default!;

        /// <summary>Article-inverted sort key — the folder base name, which the library already stores inverted.</summary>
        [MaxLength(300)]
        public string SortName { get; set; } = default!;

        /// <summary>The artist folder name under the music root, verbatim — the ingest upsert key.</summary>
        [MaxLength(300)]
        public string FolderName { get; set; } = default!;

        /// <summary>The curated "(YearRange)" from the folder name, e.g. "1975-2000"; null when the folder has none.</summary>
        [MaxLength(32)]
        public string? YearRange { get; set; }
    }
}
