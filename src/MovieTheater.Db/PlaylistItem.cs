using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace MovieTheater.Db
{
    /// <summary>
    /// One entry in a user-created playlist channel's hand-picked, ordered lineup
    /// (docs/playlists-watchparty-plan.md). A playlist — and a watch party, which is the same thing with
    /// a Begin-gate + shareable link — is a <see cref="Channel"/> whose lineup is these explicit rows
    /// rather than a filter over the library. It airs by <see cref="Playable"/> id, so movies, episodes,
    /// and misc all work through the existing schedule/render path, and the "Playlist" schedule strategy
    /// plays them in <see cref="Position"/> order, looping.
    /// </summary>
    [Table("PlaylistItem")]
    public class PlaylistItem
    {
        [Key]
        public long Id { get; set; }

        public int ChannelId { get; set; }

        [ForeignKey(nameof(ChannelId))]
        public Channel Channel { get; set; } = default!;

        public int PlayableId { get; set; }

        [ForeignKey(nameof(PlayableId))]
        public Playable Playable { get; set; } = default!;

        /// <summary>0-based position in the playlist; the "Playlist" strategy airs items in this order.</summary>
        public int Position { get; set; }
    }
}
