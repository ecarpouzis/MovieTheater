using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace MovieTheater.Db
{
    /// <summary>
    /// A video file in the media library and its Jellyfin identity (docs/streaming-plan.md §5,
    /// metadata-enrichment-plan.md §3.1/§3.5). Renamed from <c>MovieFile</c> and repointed from
    /// <see cref="Movie"/> to <see cref="Playable"/> so episodes carry files too. One logical title is
    /// a single <see cref="MovieFileRole.Primary"/> plus 0..n <see cref="MovieFileRole.Part"/> /
    /// <see cref="MovieFileRole.Variant"/> / <see cref="MovieFileRole.Extra"/> files — none of which
    /// have an IMDb id of their own. Seeded by the file-mapping pass; <c>sync-jellyfin</c> fills the
    /// Jellyfin id and technical fields.
    /// </summary>
    [Table("MediaFile")]
    public class MediaFile
    {
        [Key]
        public int Id { get; set; }

        public int PlayableId { get; set; }

        [ForeignKey(nameof(PlayableId))]
        public Playable Playable { get; set; } = default!;

        /// <summary>Absolute path as the media library exposes it (e.g. <c>D:\Media\Movies\Title (Year)\Title.mkv</c>).</summary>
        [MaxLength(1024)]
        public string Path { get; set; } = default!;

        /// <summary>What this file is relative to its title (feature / split part / alternate cut / extra).</summary>
        public MovieFileRole Role { get; set; } = MovieFileRole.Primary;

        /// <summary>Order of a split <see cref="MovieFileRole.Part"/> (CD1/CD2, disc 1/2); null otherwise.</summary>
        public int? PartNumber { get; set; }

        /// <summary>Human label for a <see cref="MovieFileRole.Variant"/>/<see cref="MovieFileRole.Extra"/> (e.g. "Director's Cut", "Behind the Scenes").</summary>
        [MaxLength(128)]
        public string? Label { get; set; }

        /// <summary>Jellyfin's item id for this file; null until a sync has matched it.</summary>
        [MaxLength(64)]
        public string? JellyfinItemId { get; set; }

        /// <summary>Actual file duration from Jellyfin (credits included); channel scheduling depends on this, not the IMDB runtime.</summary>
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

        // ── Technical detail (metadata-enrichment-plan.md §3.5), filled by sync-jellyfin ──
        public bool? IsHdr { get; set; }

        [MaxLength(16)]
        public string? HdrFormat { get; set; }      // "HDR10" | "DolbyVision" | "HLG"

        [MaxLength(16)]
        public string? AudioLayout { get; set; }     // "5.1" | "7.1" | "Atmos" | "Stereo"

        public int? AudioChannels { get; set; }

        public double? FrameRate { get; set; }

        public int? BitDepth { get; set; }

        /// <summary>
        /// Worst-case spacing between this file's own video keyframes, in seconds, sampled mid-file by
        /// the <c>probe-keyframes</c> command (docs/transcode-restart-freeze-plan.md §Part 1) — Jellyfin's
        /// API doesn't expose it, so it comes from ffprobe rather than <c>sync-jellyfin</c>. When it
        /// exceeds the copy path's HLS segment length, a stream-copied session's segment numbering drifts
        /// from where ffmpeg can actually cut and mid-session restarts renumber the timeline, so
        /// <c>StreamController</c> forces a real encode instead. Null = not probed; the controller must
        /// NOT force on null (today's behavior).
        /// </summary>
        public double? KeyframeIntervalSeconds { get; set; }

        /// <summary>Last time a sync saw this file in Jellyfin.</summary>
        public DateTime? LastSyncedUtc { get; set; }

        /// <summary>Set (once) when a sync can no longer find the file; cleared when it reappears.</summary>
        public DateTime? MissingSinceUtc { get; set; }
    }
}
