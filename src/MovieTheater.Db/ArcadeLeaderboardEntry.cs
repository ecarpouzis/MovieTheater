using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace MovieTheater.Db
{
    /// <summary>
    /// A player's BEST result on one RetroAchievements leaderboard, mirrored into the app DB to power the
    /// site's friends-only board (per game) alongside a link out to the global RA board. RA leaderboards
    /// already encode both high-score and speedrun boards — <see cref="Format"/> distinguishes a SCORE
    /// board (higher is better) from a TIME/FRAMES board (lower is better) — so this one row type backs
    /// both "high scores" and "speedruns".
    ///
    /// <para>Source of truth is RetroAchievements (rcheevos submits under the player's linked account); this
    /// is our copy, harvested via <c>/API/Arcade/Internal/LeaderboardSubmitted</c> (mirror of the
    /// <see cref="ArcadeSave"/> harvest path). We keep only the user's BEST per board — a worse later
    /// attempt never replaces it — so the friends board can rank directly off these rows.</para>
    /// </summary>
    [Table("ArcadeLeaderboardEntry")]
    public class ArcadeLeaderboardEntry
    {
        [Key]
        public int Id { get; set; }

        /// <summary>Owning site user (the room creator whose RA account set the result).</summary>
        public int UserId { get; set; }

        [MaxLength(64)]
        public string RaUser { get; set; } = default!;

        /// <summary>Best-effort link to our catalog row (from the rcheevos game hash); null if unresolved.</summary>
        public int? ArcadeGameId { get; set; }

        public ArcadeGame? ArcadeGame { get; set; }

        [MaxLength(40)]
        public string? RaGameHash { get; set; }

        /// <summary>The RetroAchievements leaderboard id — the mirror's per-user best key (see the unique
        /// index in <c>MovieDb</c>: one best row per (UserId, RaLeaderboardId)).</summary>
        public long RaLeaderboardId { get; set; }

        [MaxLength(200)]
        public string? Title { get; set; }

        /// <summary>The raw leaderboard value (score points, or time in frames/milliseconds per
        /// <see cref="Format"/>). Compared by <see cref="Format"/> to decide which of two attempts is better.</summary>
        public long Value { get; set; }

        /// <summary>RA value format token: <c>SCORE</c>, <c>VALUE</c>, <c>TIME</c>/<c>FRAMES</c>,
        /// <c>MILLISECS</c>, etc. Decides both the display formatting and the ranking direction
        /// (TIME/FRAMES/MILLISECS → lower is better; everything else → higher is better).</summary>
        [MaxLength(20)]
        public string Format { get; set; } = "SCORE";

        /// <summary>Whether the best result was set in hardcore (competitive) mode. Informational — RA
        /// leaderboards themselves are hardcore-only, but we record it for display parity with unlocks.</summary>
        public bool Hardcore { get; set; }

        /// <summary>Run-legitimacy taints for the BEST result (kept in step with <see cref="Value"/> — a new
        /// best overwrites them). <see cref="Cheat"/> = cheat codes active; <see cref="Savescum"/> = a
        /// save-STATE was loaded mid-run; <see cref="Timeplay"/> = fast-forward/rewind used. The board shows a
        /// hardcore badge only for a legit run (Hardcore &amp;&amp; none set) and a why-icon otherwise. See the
        /// matching fields on <see cref="ArcadeAchievementUnlock"/>.</summary>
        public bool Cheat { get; set; }
        public bool Savescum { get; set; }
        public bool Timeplay { get; set; }

        public DateTime AchievedUtc { get; set; }

        public DateTime UpdatedUtc { get; set; }
    }
}
