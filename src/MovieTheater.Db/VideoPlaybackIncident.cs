using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace MovieTheater.Db
{
    /// <summary>
    /// One self-reported video playback failure, uploaded by the player the moment it happens.
    ///
    /// <para>The music side proved the point this table borrows: a playback failure that RECOVERS
    /// erases its own evidence. Video has been reconstructed after the fact from the gateway's
    /// access log and Jellyfin's ffmpeg filenames — which works, but only for the failures somebody
    /// thought to ask about, only while those logs still hold the window, and never for the ones the
    /// viewer shrugged off and refreshed away. Nothing on the client survived the moment.</para>
    ///
    /// <para>So the player posts its own log, unprompted, with no flag to enable. Rows are small,
    /// rate-limited client-side, and pruned at 180 days on insert (see the Stream/Incident endpoint):
    /// this is a debugging journal, not analytics and not history.</para>
    ///
    /// <para>The identity columns are plain <c>int?</c> with NO foreign keys, exactly as
    /// <see cref="MusicPlaybackIncident.TrackId"/> is. An incident is a record of something that went
    /// wrong at a moment in time; it must not become a reason a title can't be deleted, and a report
    /// naming an id that has since been re-mapped or removed is still evidence worth keeping.</para>
    /// </summary>
    [Table("VideoPlaybackIncident")]
    public class VideoPlaybackIncident
    {
        [Key]
        public int Id { get; set; }

        public DateTime CreatedUtc { get; set; }

        /// <summary>Who hit it. Null when the report arrives without a resolvable session.</summary>
        public int? UserId { get; set; }

        /// <summary>Short label for the thing that went wrong — "stall", "fatal", "abr-downgrade",
        /// "startup-timeout". Indexed so "how often is this still happening" is one query.</summary>
        [MaxLength(40)]
        public string Kind { get; set; } = default!;

        /// <summary>The player's own one-line summary, e.g. the MediaError name and where it fired.</summary>
        [MaxLength(400)]
        public string? Summary { get; set; }

        /// <summary>Which player was on screen — "watch" (the screening room) or "tv" (a channel).
        /// The two are separate products sharing one engine, and half of reading an incident is
        /// knowing which set of rules was in force (TV re-tunes and never seeks; Watch does both).</summary>
        [MaxLength(10)]
        public string? Player { get; set; }

        /// <summary>The title being watched, in whichever of the three id spaces it lives in. A watch-page
        /// incident carries the one that matches its kind; a TV incident usually carries none of them
        /// (the channel is the identity) unless the schedule item resolved to a title.</summary>
        public int? MovieId { get; set; }

        public int? SeriesId { get; set; }

        public int? MiscVideoId { get; set; }

        /// <summary>The Playable actually streaming — the one id that is unambiguous across the three
        /// title id spaces, and what the server logs join on.</summary>
        public int? PlayableId { get; set; }

        /// <summary>The channel, for a TV incident. Null on the watch page.</summary>
        public int? ChannelId { get; set; }

        /// <summary>Where the playhead was, in seconds. A stall at 0 is a start-up failure; a stall
        /// two hours in is the wire, and the position is how the gateway log gets cross-referenced.</summary>
        public double? PositionSeconds { get; set; }

        [MaxLength(400)]
        public string? UserAgent { get; set; }

        /// <summary>The diagnostic ring as JSON — the timestamped events with their gaps, plus the
        /// ladder state (rung, copied-or-transcoded, the last throughput estimate). The gaps are the
        /// only witness to a renderer that was frozen; the ladder state is what separates "the link
        /// died" from "we just restarted at a new rung".</summary>
        public string Payload { get; set; } = default!;
    }
}
