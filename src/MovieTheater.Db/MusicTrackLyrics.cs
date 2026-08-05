using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace MovieTheater.Db
{
    /// <summary>
    /// Lyrics for one track (music-plan.md §2.7) — 1:1 with <see cref="MusicTrack"/>, cascades with it.
    /// Source precedence is embedded tag &gt; sidecar .lrc &gt; LRCLIB fetch; the enrichment CLI never
    /// overwrites an embedded/sidecar row.
    /// </summary>
    [Table("MusicTrackLyrics")]
    public class MusicTrackLyrics
    {
        /// <summary>PK = FK: one lyrics row per track.</summary>
        [Key]
        public int TrackId { get; set; }

        [ForeignKey(nameof(TrackId))]
        public MusicTrack Track { get; set; } = default!;

        /// <summary>Unsynchronized full-text lyrics; null when only synced lines exist.</summary>
        public string? PlainText { get; set; }

        /// <summary>Time-synced lyrics in LRC form ("[mm:ss.xx] line"); null when unavailable.</summary>
        public string? SyncedLrc { get; set; }

        /// <summary>"embedded" | "sidecar" | "lrclib".</summary>
        [MaxLength(32)]
        public string Source { get; set; } = default!;

        public DateTime FetchedUtc { get; set; }
    }
}
