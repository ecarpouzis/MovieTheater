using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace MovieTheater.Db
{
    /// <summary>
    /// One materialized slot in a channel's lineup (streaming-plan.md §8). Rows are
    /// generated ahead lazily and never rewritten, so every viewer sees the same movie
    /// at the same offset and history stays stable across library changes.
    /// </summary>
    [Table("ChannelScheduleItem")]
    public class ChannelScheduleItem
    {
        [Key]
        public long Id { get; set; }

        public int ChannelId { get; set; }

        [ForeignKey(nameof(ChannelId))]
        public Channel Channel { get; set; } = default!;

        public int MovieID { get; set; }

        [ForeignKey(nameof(MovieID))]
        public Movie Movie { get; set; } = default!;

        public DateTime StartUtc { get; set; }

        public DateTime EndUtc { get; set; }
    }
}
