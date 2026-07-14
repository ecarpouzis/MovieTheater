using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace MovieTheater.Db
{
    /// <summary>
    /// The durable log of an arcade room (arcade-plan.md §5). Live room state — seats, presence,
    /// bind status — is in-memory in <c>ArcadeRoomService</c> (the ChannelSkipService pattern);
    /// this row is the persistent record for listing/auditing and the reaper's end-stamp.
    ///
    /// Note the two distinct ids: <see cref="RoomCode"/> is OURS (the short, URL-safe invite code
    /// that appears in links), while <see cref="CloudRetroRoomId"/> is bound only after the
    /// creator's browser makes the CloudRetro room (§8) — the backend cannot create rooms, so it
    /// is null until the Bind call reports it back.
    /// </summary>
    [Table("ArcadeSession")]
    public class ArcadeSession
    {
        [Key]
        public int Id { get; set; }

        public int ArcadeGameId { get; set; }

        public ArcadeGame? ArcadeGame { get; set; }

        /// <summary>Our invite code (short, URL-safe base32) — what appears in the share link.</summary>
        [MaxLength(16)]
        public string RoomCode { get; set; } = default!;

        /// <summary>The CloudRetro room id, bound after the creator's browser makes the room (§8).
        /// Format "&lt;int64-hex&gt;___&lt;game title&gt;" — contains '___' and spaces, so ALWAYS
        /// URL-encode it when placing it in a query param. Null until bound.</summary>
        [MaxLength(300)]
        public string? CloudRetroRoomId { get; set; }

        public int CreatedByUserId { get; set; }

        public DateTime CreatedUtc { get; set; }

        /// <summary>Stamped when the room ends (all seats aged out, or the creator ended it). Null = live.</summary>
        public DateTime? EndedUtc { get; set; }

        /// <summary>
        /// Last time a player's browser heartbeated this room, written DURABLY (throttled to ~30 s by
        /// <c>ArcadeRoomService.ShouldPersistHeartbeat</c>, so it costs one UPDATE per room per 30 s).
        /// Null = never heartbeated since this column existed; treat <see cref="CreatedUtc"/> as the floor.
        ///
        /// This exists because "is this room alive?" could previously only be answered from the pod's
        /// IN-MEMORY registry, which a restart wipes — so every deploy orphaned its live rows as
        /// EndedUtc=NULL forever (795 of them by 2026-07-14). Reconciling against the in-memory set alone
        /// cannot fix that: at startup that set is EMPTY, so it would close the very rooms that are still
        /// playing and about to Rehydrate on their next heartbeat. A durable liveness stamp is the only
        /// signal that survives the restart, which is what <c>ArcadeRoomReaperService</c> reconciles on.
        /// </summary>
        public DateTime? LastSeenUtc { get; set; }
    }
}
