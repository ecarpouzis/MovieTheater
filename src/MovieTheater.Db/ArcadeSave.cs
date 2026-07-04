using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace MovieTheater.Db
{
    /// <summary>
    /// A durable, user-owned arcade save (docs/arcade-saves-plan.md). Replaces CloudRetro's room-scoped
    /// saves: a save belongs to a <b>user + game + slot</b>, so whoever started a room can leave and later
    /// resume it (or pick another slot). This row is the METADATA index — the poster pattern: tiny row
    /// here, the actual bytes live on disk under the gateway's save store (<c>D:\ArcadeStorage\savestore</c>).
    ///
    /// <para>Two <see cref="Kind"/>s. <c>sram</c> = the in-game battery / memory-card save — raw cartridge
    /// memory, portable across libretro frontends, so it's the artifact a future EmuDeck/RetroArch sync
    /// keys on. <c>state</c> = a full emulator snapshot: perfect for "resume the exact frame online" but
    /// core+version specific (<see cref="CoreName"/>/<see cref="CoreVersion"/> gate whether it's safe to
    /// load), never portable — online-scoped only.</para>
    ///
    /// <para>The gateway harvests these during/after a session and (re)seeds a chosen slot before the game
    /// boots; CloudRetro is made to name its save file deterministically so the gateway knows it up front
    /// (no emulator patch needed — the room id it uses is <c>&lt;our-id&gt;___&lt;gameName&gt;</c>).</para>
    /// </summary>
    [Table("ArcadeSave")]
    public class ArcadeSave
    {
        [Key]
        public int Id { get; set; }

        /// <summary>Owning user (same int key as <c>ArcadeSession.CreatedByUserId</c>).</summary>
        public int UserId { get; set; }

        public int ArcadeGameId { get; set; }

        public ArcadeGame? ArcadeGame { get; set; }

        /// <summary>Denormalized system code ('snes','n64','ps1',…) — a future EmuDeck sync-mapping key.</summary>
        [MaxLength(20)]
        public string System { get; set; } = default!;

        /// <summary><c>sram</c> (portable in-game/battery/memory-card save) or <c>state</c> (full snapshot).</summary>
        [MaxLength(10)]
        public string Kind { get; set; } = default!;

        /// <summary>Stable slot number for this (user, game, kind). SRAM canonically uses slot 0; save
        /// states can have several. The user-facing NAME is <see cref="Label"/> — renaming never changes
        /// the slot id or the on-disk filename.</summary>
        public int SlotId { get; set; }

        /// <summary>User-editable slot name (null = the unnamed "Continue"/canonical slot).</summary>
        [MaxLength(100)]
        public string? Label { get; set; }

        /// <summary>Core that produced a <c>state</c> — a load is only safe on a matching core+version.
        /// Null for <c>sram</c> (SRAM is core-agnostic).</summary>
        [MaxLength(60)]
        public string? CoreName { get; set; }

        [MaxLength(40)]
        public string? CoreVersion { get; set; }

        /// <summary>Blob path relative to the save-store root (e.g. <c>&lt;userId&gt;/&lt;gameId&gt;/sram.srm</c>).</summary>
        [MaxLength(400)]
        public string StorageRelPath { get; set; } = default!;

        public long SizeBytes { get; set; }

        /// <summary>SHA-256 of the blob — dedupe + the conflict key for the eventual cross-device sync.</summary>
        [MaxLength(64)]
        public string? Sha256 { get; set; }

        /// <summary><c>online</c> (harvested from a room) or <c>imported</c> (uploaded, e.g. from EmuDeck).</summary>
        [MaxLength(10)]
        public string Source { get; set; } = "online";

        /// <summary>True if written by the emulator's periodic autosave rather than an explicit save/snapshot.</summary>
        public bool IsAutosave { get; set; }

        public DateTime CreatedUtc { get; set; }

        public DateTime UpdatedUtc { get; set; }
    }
}
