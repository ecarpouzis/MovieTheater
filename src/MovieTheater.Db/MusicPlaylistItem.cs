using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace MovieTheater.Db
{
    /// <summary>One entry of a <see cref="MusicPlaylist"/>; ordered by <see cref="Position"/>.</summary>
    [Table("MusicPlaylistItem")]
    public class MusicPlaylistItem
    {
        [Key]
        public int Id { get; set; }

        public int PlaylistId { get; set; }

        [ForeignKey(nameof(PlaylistId))]
        public MusicPlaylist Playlist { get; set; } = default!;

        public int TrackId { get; set; }

        [ForeignKey(nameof(TrackId))]
        public MusicTrack Track { get; set; } = default!;

        public int Position { get; set; }
    }
}
