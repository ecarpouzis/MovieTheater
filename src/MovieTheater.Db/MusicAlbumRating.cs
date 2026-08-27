using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace MovieTheater.Db
{
    /// <summary>
    /// One listener's score for one album, 0–100 (R9 S10) — the music side of the movies' 0–100
    /// <c>Viewing</c>/<c>SetRatings</c> feature, in its own table.
    /// </summary>
    /// <remarks>
    /// <para><b>Why not a Viewing row.</b> <c>Viewing</c>'s identity is a title in one of the three
    /// video id spaces (MovieID / SeriesId / MiscVideoId) and its semantics carry Seen and
    /// WantToWatch, neither of which means anything for a record you can put on again tomorrow.
    /// Music is not Movies — the same ruling that gave the vertical its own catalog tables.</para>
    /// <para><b>0 is a real score; unrated is NO ROW.</b> The movie rating feature learned this the
    /// hard way, and the shape is copied verbatim: clearing a rating DELETES the row rather than
    /// writing a sentinel, and the API's upsert takes null to mean "clear". One row per
    /// (user, album), enforced by a unique index, so a double-tap cannot mint a second opinion.</para>
    /// </remarks>
    [Table("MusicAlbumRating")]
    public class MusicAlbumRating
    {
        [Key]
        public int Id { get; set; }

        public int UserId { get; set; }

        [ForeignKey(nameof(UserId))]
        public User User { get; set; } = default!;

        public int AlbumId { get; set; }

        [ForeignKey(nameof(AlbumId))]
        public MusicAlbum Album { get; set; } = default!;

        /// <summary>0–100. Clamped at the API edge; 0 is a real score (see the remarks).</summary>
        public int Score { get; set; }

        public DateTime CreatedUtc { get; set; }

        public DateTime UpdatedUtc { get; set; }
    }
}
