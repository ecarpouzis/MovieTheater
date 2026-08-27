using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace MovieTheater.Db
{
    /// <summary>
    /// How many times one listener has played one track, and when they last did (R9 closing pass) —
    /// the only play telemetry the music vertical has ever had.
    /// </summary>
    /// <remarks>
    /// <para><b>Why an aggregate and not an event log.</b> The two questions this exists to answer are
    /// "what is the most played album/artist" and "what was played recently", and both must be cheap
    /// enough to ride the shelf rows the browse already fetches (the per-shelf fetch rule: the browse
    /// holds its whole shelf client-side, so a sort can only exist if the number is ON the row). One
    /// row per (user, track) makes "most played" a SUM over a table bounded by
    /// listeners × tracks-ever-played, and "recently played" a MAX over the same rows — no retention
    /// policy, no growth with listening time. An event table would answer both with a COUNT over a
    /// row per play forever, which is the shape that eventually needs pruning and a rollup job to be
    /// affordable; the history it would buy (a real listening timeline) is not a question anyone here
    /// has asked. If it ever is, this table stays valid and the log is added beside it.</para>
    ///
    /// <para><b>Idempotency lives in <see cref="LastStartedUtc"/>.</b> A beacon can arrive twice — a
    /// retry, a <c>pagehide</c> flush racing the in-flight send, two tabs. The client sends the
    /// moment playback STARTED, floored to the minute, and a report whose minute equals the one
    /// already recorded is a no-op. So one play is one increment however many times it is reported,
    /// which is what lets the beacon be fire-and-forget.</para>
    ///
    /// <para><b>Privacy.</b> The rows are per user. The aggregate the site SHOWS is library-wide
    /// (summed across everyone), so a card says how often a record gets played here, never by whom.</para>
    /// </remarks>
    [Table("MusicPlayStat")]
    public class MusicPlayStat
    {
        [Key]
        public int Id { get; set; }

        public int UserId { get; set; }

        [ForeignKey(nameof(UserId))]
        public User User { get; set; } = default!;

        public int MusicTrackId { get; set; }

        [ForeignKey(nameof(MusicTrackId))]
        public MusicTrack Track { get; set; } = default!;

        /// <summary>Plays counted for this listener × track. Never decremented.</summary>
        public int PlayCount { get; set; }

        /// <summary>Server clock when the last counted play was recorded — what "Recently played" orders by.</summary>
        public DateTime LastPlayedUtc { get; set; }

        /// <summary>
        /// The client's own "playback started" stamp for the last counted play, FLOORED TO THE MINUTE.
        /// It is the idempotency key: a report carrying this same minute has already been counted.
        /// </summary>
        public DateTime LastStartedUtc { get; set; }
    }
}
