using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace MovieTheater.Db
{
    /// <summary>
    /// Cross-device resume position + completion for streaming (streaming-plan.md §5).
    /// One row per user per <see cref="Playable"/> (movie or episode); written by
    /// /API/Stream/Progress, never by TV channel (passive) playback. Repointed from Movie to
    /// Playable by the Phase-4 cutover so episodes get resume + auto-Seen for free.
    /// </summary>
    [Table("MoviePlaybackProgress")]
    public class MoviePlaybackProgress
    {
        [Key]
        public int Id { get; set; }

        public int UserID { get; set; }

        [ForeignKey(nameof(UserID))]
        public User User { get; set; } = default!;

        public int PlayableId { get; set; }

        [ForeignKey(nameof(PlayableId))]
        public Playable Playable { get; set; } = default!;

        public long PositionTicks { get; set; }

        public long DurationTicks { get; set; }

        public DateTime UpdatedUtc { get; set; }

        /// <summary>Set once playback crossed the auto-Seen threshold (≥90%).</summary>
        public bool Completed { get; set; }
    }
}
