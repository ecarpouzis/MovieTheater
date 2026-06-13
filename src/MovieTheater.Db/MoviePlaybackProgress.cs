using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace MovieTheater.Db
{
    /// <summary>
    /// Cross-device resume position + completion for streaming (streaming-plan.md §5).
    /// One row per user per movie; written by /API/Stream/Progress, never by TV
    /// channel (passive) playback.
    /// </summary>
    [Table("MoviePlaybackProgress")]
    public class MoviePlaybackProgress
    {
        [Key]
        public int Id { get; set; }

        public int UserID { get; set; }

        [ForeignKey(nameof(UserID))]
        public User User { get; set; } = default!;

        public int MovieID { get; set; }

        [ForeignKey(nameof(MovieID))]
        public Movie Movie { get; set; } = default!;

        public long PositionTicks { get; set; }

        public long DurationTicks { get; set; }

        public DateTime UpdatedUtc { get; set; }

        /// <summary>Set once playback crossed the auto-Seen threshold (≥90%).</summary>
        public bool Completed { get; set; }
    }
}
