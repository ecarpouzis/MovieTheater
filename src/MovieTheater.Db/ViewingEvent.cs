using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace MovieTheater.Db
{
    /// <summary>
    /// The append-only journal behind the Seen / Want / Suggested / Rated lists: one row per change,
    /// written by every code path that adds, removes or re-scores a <see cref="Viewing"/> (the toggle
    /// endpoint, the Rate page's upserts, the 2026-09 Want→Suggested migration).
    ///
    /// <para>The Viewing row carries the CURRENT provenance (who created it, when) and is enough to say
    /// "Suggested by Eric · 3 Aug 2026" in the title sheet. It cannot say when a mark was REMOVED, or
    /// who withdrew a suggestion, because un-marking deletes the row — that history lives here. Nothing
    /// reads this table on a hot path; it answers "what happened to this person's lists, and who did
    /// it" after the fact.</para>
    ///
    /// <para>The identity columns are plain <c>int?</c> with NO foreign keys, the
    /// <see cref="VideoPlaybackIncident"/> posture: a journal entry records something that happened at
    /// a moment in time and must outlive the title, the account and the Viewing row it describes —
    /// it must never become a reason a title can't be deleted.</para>
    /// </summary>
    [Table("ViewingEvent")]
    public class ViewingEvent
    {
        public const string ActionAdded = "Added";
        public const string ActionRemoved = "Removed";
        public const string ActionRescored = "Rescored";
        /// <summary>The one-off 2026-09 rename of other people's WantToWatch rows to Suggested.</summary>
        public const string ActionMigrated = "Migrated";

        public const string SourceWeb = "web";
        public const string SourceMigration = "migration";

        [Key]
        public long Id { get; set; }

        /// <summary>Whose list changed — the owner of the Viewing row.</summary>
        public int UserId { get; set; }

        /// <summary>Who made the change: the owner themself, the person marking on their behalf, or the
        /// suggester. Null only for a change with no session behind it.</summary>
        public int? ActorUserId { get; set; }

        /// <summary>The title, in whichever of the three id spaces it lives in (exactly one is set).</summary>
        public int? MovieID { get; set; }

        public int? SeriesId { get; set; }

        public int? MiscVideoId { get; set; }

        /// <summary>One of <see cref="ViewingTypes"/>.</summary>
        [MaxLength(32)]
        public string ViewingType { get; set; } = default!;

        /// <summary>Added · Removed · Rescored · Migrated.</summary>
        [MaxLength(16)]
        public string Action { get; set; } = default!;

        /// <summary>The Viewing row's data after the change — a rating's 0–100 score. Null for the lists.</summary>
        [MaxLength(64)]
        public string? Data { get; set; }

        public DateTime AtUtc { get; set; }

        /// <summary>web · migration.</summary>
        [MaxLength(16)]
        public string Source { get; set; } = SourceWeb;
    }
}
