using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace MovieTheater.Db
{
    /// <summary>
    /// Thin parent that both a <see cref="Movie"/> and an <see cref="Episode"/> reference, so files,
    /// playback progress, and channel slots can attach to either without duplicating those tables
    /// (docs/metadata-enrichment-plan.md §3.1). Has its own IDENTITY — it deliberately does NOT reuse
    /// <see cref="Movie.id"/> (which ~8 tables already key on).
    /// </summary>
    [Table("Playable")]
    public class Playable
    {
        [Key]
        public int Id { get; set; }

        public PlayableKind Kind { get; set; }

        /// <summary>The media files (Primary + any Part/Variant/Extra) attached to this playable.</summary>
        public ICollection<MediaFile> Files { get; set; } = [];
    }
}
