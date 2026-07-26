using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace MovieTheater.Db
{
    /// <summary>
    /// A RetroAchievements unlock, MIRRORED into the app DB for site UI (in-room toast, the profile
    /// "My Achievements" view). RetroAchievements itself is the source of truth — rcheevos in the worker
    /// submits the unlock to retroachievements.org under the player's OWN linked account (ToS: one account
    /// per human). This row is only our copy, harvested via the secret-gated
    /// <c>/API/Arcade/Internal/AchievementUnlocked</c> callback (the same worker→gateway→site path
    /// <see cref="ArcadeSave"/> harvest uses), because the k8s pod can't read Ziggy's disk or talk to RA.
    ///
    /// <para>A room runs ONE shared emulator, so RA runs under the ROOM CREATOR's account — the unlock is
    /// attributed to whichever site user linked that RA account (<see cref="UserId"/>), resolved from
    /// <see cref="RaUser"/> at callback time.</para>
    /// </summary>
    [Table("ArcadeAchievementUnlock")]
    public class ArcadeAchievementUnlock
    {
        [Key]
        public int Id { get; set; }

        /// <summary>Owning site user (the room creator whose RA account earned it). Same int key as
        /// <c>ArcadeSession.CreatedByUserId</c>.</summary>
        public int UserId { get; set; }

        /// <summary>The linked RetroAchievements username the unlock came in under — kept for provenance
        /// even though <see cref="UserId"/> is resolved from it at harvest time.</summary>
        [MaxLength(64)]
        public string RaUser { get; set; } = default!;

        /// <summary>Best-effort link to our catalog row (matched from the rcheevos game hash). Null when the
        /// played ROM isn't in <c>ArcadeGame</c> or the hash didn't resolve — the unlock still stands.</summary>
        public int? ArcadeGameId { get; set; }

        public ArcadeGame? ArcadeGame { get; set; }

        /// <summary>rcheevos' own content hash of the loaded ROM (the RA game key). The map to
        /// <see cref="ArcadeGameId"/> is best-effort; this is always recorded.</summary>
        [MaxLength(40)]
        public string? RaGameHash { get; set; }

        /// <summary>The RetroAchievements achievement id — the mirror's dedupe key with <see cref="Hardcore"/>.</summary>
        public long RaAchievementId { get; set; }

        [MaxLength(200)]
        public string? Title { get; set; }

        public int Points { get; set; }

        /// <summary>True = earned in hardcore (competitive) mode. Softcore and hardcore unlocks are distinct
        /// on RA, so they're distinct rows here too (see the unique index in <c>MovieDb</c>).</summary>
        public bool Hardcore { get; set; }

        public DateTime UnlockedUtc { get; set; }
    }
}
