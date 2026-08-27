using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace MovieTheater.Db
{
    /// <summary>
    /// One genre an album is filed under, with the PROVENANCE that produced it (R9 S10). A join
    /// rather than a column because an album is legitimately several things at once — the tags on a
    /// single record routinely say "Rock", "Alternative" and "Indie" — and the Music rail's Genre
    /// facet is a set membership test, not a string compare.
    /// </summary>
    /// <remarks>
    /// <para><b>Source is part of the identity.</b> The tag pass and the external pass disagree about
    /// this album by design (the files say "Alternative", MusicBrainz says "indie rock"), and neither
    /// is allowed to overwrite the other: each owns its own rows, keyed
    /// (<see cref="AlbumId"/>, <see cref="Source"/>, <see cref="Genre"/>). A re-run of one pass
    /// replaces only its own rows for that album, which is what makes both passes idempotent without
    /// a "who wrote this last" column.</para>
    /// <para>The genre string is stored VERBATIM-after-normalisation rather than pointing at a
    /// dimension table. The movie side has <c>Genre</c> because IMDb hands out a closed vocabulary of
    /// about two dozen; music tags are an open set of thousands with no authority behind them, so a
    /// lookup table would be a table of typos with foreign keys attached.</para>
    /// </remarks>
    [Table("MusicAlbumGenre")]
    public class MusicAlbumGenre
    {
        [Key]
        public int Id { get; set; }

        public int AlbumId { get; set; }

        [ForeignKey(nameof(AlbumId))]
        public MusicAlbum Album { get; set; } = default!;

        /// <summary>Display form of the genre, normalised for case/whitespace by
        /// <c>MusicGenres.Normalize</c> ("Alternative Rock"). Never null, never empty.</summary>
        [MaxLength(100)]
        public string Genre { get; set; } = default!;

        /// <summary>Which pass produced this row — see <see cref="MusicGenreSources"/>. Bulk-written
        /// rows are stamped so a later sweep can find, re-run or retire exactly one pass's output.</summary>
        [MaxLength(32)]
        public string Source { get; set; } = default!;

        /// <summary>How strongly the source asserts it: the tag pass stores how many of the album's
        /// tracks carry the genre, the external passes store the vote/score they were given. Used to
        /// order the genres of one album (the first is the album's headline genre) and to break ties
        /// in the artist roll-up.</summary>
        public int Weight { get; set; }

        public DateTime CreatedUtc { get; set; }
    }

    /// <summary>The provenance values <see cref="MusicAlbumGenre.Source"/> and
    /// <see cref="MusicArtistGenre.Source"/> take, in one place.</summary>
    public static class MusicGenreSources
    {
        /// <summary>Read off the files' own tags (ID3 TCON / Vorbis GENRE / MP4 ©gen) — the free leg.</summary>
        public const string Tags = "tags";

        /// <summary>MusicBrainz release-group tags.</summary>
        public const string MusicBrainz = "musicbrainz";

        /// <summary>Last.fm album top tags.</summary>
        public const string LastFm = "lastfm";
    }
}
