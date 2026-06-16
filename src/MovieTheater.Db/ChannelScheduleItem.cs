using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace MovieTheater.Db
{
    /// <summary>
    /// One materialized slot in a channel's lineup (streaming-plan.md §8). Rows are
    /// generated ahead lazily and never rewritten, so every viewer sees the same content
    /// at the same offset and history stays stable across library changes. Repointed from
    /// Movie to <see cref="Playable"/> by the Phase-4 cutover so channels can air episodes too.
    /// </summary>
    [Table("ChannelScheduleItem")]
    public class ChannelScheduleItem
    {
        [Key]
        public long Id { get; set; }

        public int ChannelId { get; set; }

        [ForeignKey(nameof(ChannelId))]
        public Channel Channel { get; set; } = default!;

        public int PlayableId { get; set; }

        [ForeignKey(nameof(PlayableId))]
        public Playable Playable { get; set; } = default!;

        public DateTime StartUtc { get; set; }

        public DateTime EndUtc { get; set; }
    }
}
