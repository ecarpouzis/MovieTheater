using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace MovieTheater.Db
{
    /// <summary>
    /// Grants one user access to someone else's <see cref="MusicPlaylist"/> (music-plan.md §2.2).
    ///
    /// <para>A row means "this user may see AND edit this playlist" — sharing here is collaborative
    /// by design, not read-only: the point is a communal list several people add to. The two things
    /// that stay with the owner are deleting the playlist and deciding who else gets in, because
    /// both are irreversible for everyone else holding a share.</para>
    ///
    /// <para>Its own table rather than a column on the playlist: a playlist can be shared with any
    /// number of people, and the pair is what has to be unique.</para>
    /// </summary>
    [Table("MusicPlaylistShare")]
    public class MusicPlaylistShare
    {
        [Key]
        public int Id { get; set; }

        public int PlaylistId { get; set; }
        [ForeignKey(nameof(PlaylistId))]
        public MusicPlaylist Playlist { get; set; } = default!;

        /// <summary>The user the playlist is shared WITH (never the owner).</summary>
        public int UserId { get; set; }
        [ForeignKey(nameof(UserId))]
        public User User { get; set; } = default!;

        public DateTime CreatedUtc { get; set; }
    }
}
