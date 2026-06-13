using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace MovieTheater.Db
{
    /// <summary>
    /// A movie's video file in the media library and its Jellyfin identity — the canonical home for
    /// "where is this movie on disk" (docs/streaming-plan.md §5). Seeded from
    /// <see cref="Movie.FilePath"/> by the <c>sync-jellyfin</c> command, which also fills
    /// the Jellyfin item id and technical fields. v1 keeps one file per movie; the schema
    /// allows more.
    /// </summary>
    [Table("MovieFile")]
    public class MovieFile
    {
        [Key]
        public int Id { get; set; }

        public int MovieID { get; set; }

        [ForeignKey(nameof(MovieID))]
        public Movie Movie { get; set; } = default!;

        /// <summary>Absolute path as the media library exposes it (e.g. <c>D:\Media\Movies\Title (Year)\Title.mkv</c>).</summary>
        [MaxLength(1024)]
        public string Path { get; set; } = default!;

        /// <summary>Jellyfin's item id for this file; null until a sync has matched it.</summary>
        [MaxLength(64)]
        public string? JellyfinItemId { get; set; }

        /// <summary>
        /// Actual file duration from Jellyfin (credits included). Channel scheduling depends
        /// on this, not the IMDB runtime.
        /// </summary>
        public long? DurationTicks { get; set; }

        [MaxLength(32)]
        public string? Container { get; set; }

        [MaxLength(32)]
        public string? VideoCodec { get; set; }

        [MaxLength(32)]
        public string? AudioCodec { get; set; }

        public int? Width { get; set; }

        public int? Height { get; set; }

        public long? SizeBytes { get; set; }

        /// <summary>Last time a sync saw this file in Jellyfin.</summary>
        public DateTime? LastSyncedUtc { get; set; }

        /// <summary>Set (once) when a sync can no longer find the file; cleared when it reappears.</summary>
        public DateTime? MissingSinceUtc { get; set; }
    }
}
