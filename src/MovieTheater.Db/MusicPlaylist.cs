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

        public DateTime CreatedUtc { get; set; }

        public List<MusicPlaylistItem> Items { get; set; } = new();
    }
}
