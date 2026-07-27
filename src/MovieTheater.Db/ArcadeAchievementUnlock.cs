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

        /// <summary>Whether the room had the COMPETITIVE guardrail armed (no state seed, no cheats, save/load
        /// and time controls hidden). Provenance only — it is NOT what makes a run legitimate. This is the
        /// room mode the player opted into; <see cref="Clean"/> is what actually happened. Rides the wire as
        /// <c>hardcore</c> on t=104 / the mirror callback, which is why the two names differ.</summary>
        public bool Competitive { get; set; }

        /// <summary>Run-legitimacy taints sampled by the worker when the achievement fired, so the friends
        /// board / profile can show WHY a run wasn't a clean one. <see cref="Cheat"/> = cheat codes were
        /// active for the room; <see cref="Savescum"/> = a save-STATE was restored (mid-run Load, or seeded
        /// at boot — cleared by a hard reset); <see cref="Timeplay"/> = fast-forward/rewind was used.</summary>
        public bool Cheat { get; set; }
        public bool Savescum { get; set; }
        public bool Timeplay { get; set; }

        /// <summary>OBSERVED cleanliness: no cheat, no save-scum, no time manipulation. This — not the room
        /// mode — is what makes a run legitimate, so a casual room produces legit results right up until
        /// something dirties it, and the competitive toggle is only a guardrail against dirtying it.
        ///
        /// <para>A PERSISTED COMPUTED column (see <c>MovieDb</c>), deliberately: legitimacy is derived from
        /// the taints by the database itself, so no callback, backfill, or future code path can assert a
        /// clean run that the taints contradict. Read-only in EF — set the taints, not this.</para>
        ///
        /// <para>Part of the mirror's dedupe key: re-earning an achievement CLEANLY after a dirty unlock is a
        /// genuine first and gets its own row (see the unique index in <c>MovieDb</c>).</para></summary>
        public bool Clean { get; private set; }

        public DateTime UnlockedUtc { get; set; }
    }
}
