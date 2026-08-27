using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace MovieTheater.Db
{
    /// <summary>
    /// An artist's headline genres (R9 S10) — the top few rolled up from their albums'
    /// <see cref="MusicAlbumGenre"/> rows, so the "one per artist" grid and the artist drill can say
    /// what a shelf-mate sounds like without the client summing 2,900 albums on every render.
    /// </summary>
    /// <remarks>
    /// Deliberately CAPPED (the roll-up keeps three) rather than mirroring everything the albums say.
    /// An artist with twenty records accumulates forty genres from their tags, and a list of forty is
    /// not a description of anybody. Same (<see cref="ArtistId"/>, <see cref="Source"/>,
    /// <see cref="Genre"/>) identity as the album join, for the same reason: each pass owns and
    /// replaces only its own rows.
    /// </remarks>
    [Table("MusicArtistGenre")]
    public class MusicArtistGenre
    {
        [Key]
        public int Id { get; set; }

        public int ArtistId { get; set; }

        [ForeignKey(nameof(ArtistId))]
        public MusicArtist Artist { get; set; } = default!;

        [MaxLength(100)]
        public string Genre { get; set; } = default!;

        /// <summary>See <see cref="MusicGenreSources"/>.</summary>
        [MaxLength(32)]
        public string Source { get; set; } = default!;

        /// <summary>How many of the artist's albums are filed under this genre — the roll-up's ranking
        /// key, kept so the order is re-derivable and a tie is broken the same way twice.</summary>
        public int Weight { get; set; }

        public DateTime CreatedUtc { get; set; }
    }
}
