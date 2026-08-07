using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace MovieTheater.Db
{
    /// <summary>
    /// A user-owned ordered list of music tracks (music-plan.md §2.2). Deliberately its own table,
    /// NOT a <see cref="Channel"/> playlist — those assume video playables and TV scheduling; music
    /// needs queue semantics instead.
    /// </summary>
    [Table("MusicPlaylist")]
    public class MusicPlaylist
    {
        [Key]
        public int Id { get; set; }

        public int UserId { get; set; }

        [ForeignKey(nameof(UserId))]
        public User User { get; set; } = default!;

        [MaxLength(200)]
        public string Name { get; set; } = default!;

        /// <summary>
        /// The owner's one auto-managed Favorites list — what the heart in the play bar writes to.
        /// </summary>
        /// <remarks>
        /// A flag on the ordinary playlist table rather than a MusicFavorite table of its own, because
        /// favorites ARE a playlist in every way that matters: you play them, shuffle them, reorder
        /// them and see them in the manager. A parallel table would have meant a second set of every
        /// one of those verbs, plus a "virtual playlist" the manager had to special-case anyway.
        ///
        /// What the flag buys is the opposite of sharing: a favorites list may not be shared, renamed
        /// or deleted (see MusicController), so it is exactly one person's, permanently. Sharing is
        /// refused structurally, not just at the Share verb — LoadAccessiblePlaylistAsync ignores share
        /// rows for a favorites list, so even a stray grant could not open it.
        /// </remarks>
        public bool IsFavorites { get; set; }

        public DateTime CreatedUtc { get; set; }

        public List<MusicPlaylistItem> Items { get; set; } = new();
    }
}
