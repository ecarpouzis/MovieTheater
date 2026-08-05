using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace MovieTheater.Db
{
    /// <summary>
    /// One audio file in the music library (music-plan.md §2.2). The upsert key is
    /// <see cref="RelativePath"/> (relative to the configured music root) so the catalog carries no
    /// drive-letter specifics and the StreamGateway can resolve the file against its own mount of the
    /// same share. Folder grammar owns Artist/Album identity; tags fill Title/TrackNo/technical
    /// fields and are preserved raw in <see cref="TagArtist"/>/<see cref="TagAlbum"/> (§2.3).
    /// Vanished files get <see cref="MissingSinceUtc"/>, never deletion.
    /// </summary>
    [Table("MusicTrack")]
    public class MusicTrack
    {
        [Key]
        public int Id { get; set; }

        public int ArtistId { get; set; }

        [ForeignKey(nameof(ArtistId))]
        public MusicArtist Artist { get; set; } = default!;

        /// <summary>Null for a loose track sitting directly in the artist folder (no album subfolder).</summary>
        public int? AlbumId { get; set; }

        [ForeignKey(nameof(AlbumId))]
        public MusicAlbum? Album { get; set; }

        /// <summary>File path relative to the music root, forward slashes — the ingest upsert key and
        /// what a stream token carries (§2.1); the gateway joins it onto its own music root.</summary>
        [MaxLength(600)]
        public string RelativePath { get; set; } = default!;

        [MaxLength(260)]
        public string FileName { get; set; } = default!;

        /// <summary>Lower-case extension including the dot (".mp3").</summary>
        [MaxLength(16)]
        public string Extension { get; set; } = default!;

        public long SizeBytes { get; set; }

        public DateTime ModifiedUtc { get; set; }

        /// <summary>Display title: tag title, else filename with any "NN - " track prefix stripped.</summary>
        [MaxLength(400)]
        public string Title { get; set; } = default!;

        public int? TrackNo { get; set; }

        public int? DiscNo { get; set; }

        public double? DurationSec { get; set; }

        /// <summary>Extension-derived codec family ("mp3", "flac", "aac", …) — stable browser-capability key.</summary>
        [MaxLength(32)]
        public string? Codec { get; set; }

        public int? BitrateKbps { get; set; }

        public int? SampleRateHz { get; set; }

        /// <summary>Raw tag artist, kept verbatim for later reconciliation — folder identity wins (§2.3).</summary>
        [MaxLength(400)]
        public string? TagArtist { get; set; }

        /// <summary>Raw tag album, kept verbatim — folder identity wins (§2.3).</summary>
        [MaxLength(400)]
        public string? TagAlbum { get; set; }

        public bool HasEmbeddedArt { get; set; }

        /// <summary>Format has no native browser playback (.ape/.wv/.wma/…); hidden from play until the
        /// ffmpeg lane exists (§Phase 7).</summary>
        public bool RequiresTranscode { get; set; }

        /// <summary>Set (once) when ingest can no longer find the file; cleared when it reappears.</summary>
        public DateTime? MissingSinceUtc { get; set; }

        /// <summary>When the LRCLIB lookup last ran for this track — the lyrics negative cache (§2.7).
        /// Stamped even on a miss so a re-run skips tracks LRCLIB has no lyrics for; null = never asked.</summary>
        public DateTime? LyricsCheckedUtc { get; set; }
    }
}
