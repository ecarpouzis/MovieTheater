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

        /// <summary>Display name — the folder base name, with a stray ", The" suffix restored to a
        /// leading "The" (a tolerance; the library's grammar keeps "The" leading on disk).</summary>
        [MaxLength(300)]
        public string Name { get; set; } = default!;

        /// <summary>Sort key — the folder base name VERBATIM. The library keeps a leading "The"
        /// ("The Beatles" sorts under T, by design); nothing is inverted here.</summary>
        [MaxLength(300)]
        public string SortName { get; set; } = default!;

        /// <summary>The artist folder name under the music root, verbatim — the ingest upsert key.</summary>
        [MaxLength(300)]
        public string FolderName { get; set; } = default!;

        /// <summary>The curated "(YearRange)" from the folder name, e.g. "1975-2000"; null when the folder has none.</summary>
        [MaxLength(32)]
        public string? YearRange { get; set; }

        /// <summary>
        /// What KIND of listening this folder is: <c>null</c> = music (the overwhelming default),
        /// <c>"comedy"</c>, <c>"audiobook"</c>. See <see cref="MusicArtistKinds"/>.
        /// </summary>
        /// <remarks>
        /// The library is one tree and the folder grammar says nothing about genre, so George
        /// Carlin's 22 records and Orson Scott Card's Ender novels sit in the artist grid between
        /// Garbage and Orbital — 40-minute spoken-word tracks in the middle of browsing for music.
        /// This is the one bit of judgement the folder names can't carry.
        ///
        /// <para>NULLABLE and null-means-music on purpose: nothing has to be classified for the
        /// library to be right, an unrecognised value simply disappears from the default browse
        /// (fail quiet, not fail wrong), and a new kind costs a string rather than a migration.
        /// Audiobooks are readable straight off the disk grammar (the <c>[Audiobook]</c> folder
        /// tag); comedy is a human call and is left null whenever it is not obvious.</para>
        /// </remarks>
        [MaxLength(32)]
        public string? Kind { get; set; }
    }

    /// <summary>The values <see cref="MusicArtist.Kind"/> is allowed to take, in one place.</summary>
    /// <remarks>
    /// Strings rather than an enum because the column is the API's query parameter verbatim — an
    /// enum would need a mapping on both sides of the wire, and the set is open by design.
    /// </remarks>
    public static class MusicArtistKinds
    {
        public const string Comedy = "comedy";
        public const string Audiobook = "audiobook";

        /// <summary>Null (= music) or one of the known kinds; anything else is rejected at the API
        /// edge rather than silently matching nothing.</summary>
        public static bool IsKnown(string? kind) =>
            kind == null || kind == Comedy || kind == Audiobook;
    }
}
