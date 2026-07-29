using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace MovieTheater.Db
{
    /// <summary>
    /// One user's accumulated watch-time on one channel for one (household-local) day — the durable
    /// trace of TV viewing. Channel playback reports <c>passive</c> progress, so before this table
    /// the family's channel watching left no record at all; curation was flying blind. Beats are
    /// accumulated in memory from the /Now poll and flushed periodically (ChannelViewTelemetryService),
    /// so rows stay one-per-user/channel/day and writes stay far off the hot path.
    /// Deliberately no FK to Channel: stats should outlive a deleted hand-made channel.
    /// </summary>
    [Table("ChannelViewStat")]
    [Index(nameof(UserId), nameof(ChannelId), nameof(Date), IsUnique = true)]
    public class ChannelViewStat
    {
        [Key]
        public int Id { get; set; }

        public int UserId { get; set; }

        public int ChannelId { get; set; }

        /// <summary>Local viewing date (household timezone, config <c>TelemetryTimeZone</c>) — the
        /// day a family evening actually belongs to, not the UTC date it straddles.</summary>
        public DateOnly Date { get; set; }

        /// <summary>Accumulated watch-seconds for that day.</summary>
        public int Seconds { get; set; }

        public DateTime UpdatedUtc { get; set; }
    }
}
