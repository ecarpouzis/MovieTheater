using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace MovieTheater.Db
{
    /// <summary>
    /// One self-reported playback failure, uploaded by the player the moment it happens.
    ///
    /// <para>This table exists because the bug it serves erases its own evidence. The music failures
    /// worth chasing all happen on a phone with the screen off, and the player RECOVERS: by the time
    /// anyone can look, the track has resumed and the in-memory diagnostic ring is gone with the
    /// reload. Asking the listener to catch the moment has failed every time it has been tried —
    /// the moment is over before there is anything to catch.</para>
    ///
    /// <para>So the player posts the log itself, unprompted, with no flag to enable. Rows are small
    /// and rate-limited client-side; this is a debugging journal, not analytics.</para>
    /// </summary>
    [Table("MusicPlaybackIncident")]
    public class MusicPlaybackIncident
    {
        [Key]
        public int Id { get; set; }

        public DateTime CreatedUtc { get; set; }

        /// <summary>Who hit it. Null when the report arrives without a resolvable session.</summary>
        public int? UserId { get; set; }

        /// <summary>Short label for the thing that went wrong — "boundary", "error", "park".
        /// Indexed so "how often is this still happening" is one query.</summary>
        [MaxLength(40)]
        public string Kind { get; set; } = default!;

        /// <summary>The player's own one-line summary, e.g. the MediaError name and where it fired.</summary>
        [MaxLength(400)]
        public string? Summary { get; set; }

        /// <summary>The track playing (or being loaded) when it happened, if known.</summary>
        public int? TrackId { get; set; }

        [MaxLength(400)]
        public string? UserAgent { get; set; }

        /// <summary>The diagnostic ring as JSON — the timestamped events with their gaps, which is
        /// the only witness to a renderer that was frozen.</summary>
        public string Payload { get; set; } = default!;
    }
}
