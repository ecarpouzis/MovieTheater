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

        /// <summary>Channel count of the source file (2 = stereo, 6 = 5.1). The player sizes the Web
        /// Audio destination to this so the visualizer graph carries surround through instead of
        /// down-mixing it — and, just as importantly, leaves a stereo track at 2 so the OS/receiver
        /// upmixer still sees a 2-channel stream. 0 means "read the file and it wouldn't tell us",
        /// a sentinel that stops the backfill retrying it forever; null means not yet backfilled.</summary>
        public int? Channels { get; set; }

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

        /// <summary>
        /// The file's own genre frame (ID3 <c>TCON</c> / Vorbis <c>GENRE</c> / MP4 <c>©gen</c>),
        /// normalised and comma-joined when the tag names several ("Rock, Alternative"). Null when the
        /// file carries none — which is a great many of them, and not an error.
        /// </summary>
        /// <remarks>
        /// Kept per TRACK even though nothing browses by it, because the album roll-up is a MAJORITY
        /// over the tracks and a majority needs the votes. A compilation whose twelve tracks say
        /// twelve different things is a real record, and collapsing it at read time would lose the
        /// evidence that says so.
        /// </remarks>
        [MaxLength(200)]
        public string? Genre { get; set; }

        /// <summary>When the genre pass last opened this file — the negative cache, stamped on a MISS
        /// as well as a hit (the <see cref="LyricsCheckedUtc"/> convention). It is the
        /// <c>music-genres</c> queue's only stop condition: without it a library where most files
        /// carry no genre would be re-read in full by every run, forever.</summary>
        public DateTime? GenreCheckedUtc { get; set; }
    }
}
