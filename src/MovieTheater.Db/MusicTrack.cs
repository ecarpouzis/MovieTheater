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

        /// <summary>
        /// How widely heard this SONG is, 0-100 (2026-08-31) - the track-level twin of
        /// <see cref="MusicAlbum.Popularity"/>, and NOT a verdict on it. Derived from Last.fm's
        /// listener count for the track by <c>music-track-popularity</c>; null until that pass has
        /// looked, and null for a song the world has never heard of.
        /// </summary>
        /// <remarks>
        /// <para><b>Same scale as the album's, deliberately.</b> Both go through
        /// <c>MusicPopularity.FromAudience</c> against the same ceiling, so a track's 62 and an
        /// album's 62 mean the same sentence - "about this many people have heard it". They appear
        /// side by side in one sheet, and two numbers that look alike but count differently would be
        /// read as a comparison the data cannot support.</para>
        /// <para><b>Why per track and not just per album.</b> "Which songs on this record are the
        /// famous ones" cannot be answered by an album number, and it is the question the tracklist
        /// is actually asked. It also answers it for a COMPILATION, where the album's own popularity
        /// says nothing about the twelve unrelated songs on it.</para>
        /// </remarks>
        public int? Popularity { get; set; }

        /// <summary>
        /// The RAW audience count <see cref="Popularity"/> was derived from (Last.fm listeners), or
        /// null when unknown. Null and 0 are different answers: 0 would say nobody has heard it.
        /// </summary>
        /// <remarks>
        /// <para><b>Kept because the 0-100 scale cannot express a DROP.</b> That scale is
        /// logarithmic by necessity - listener counts across this library span three orders of
        /// magnitude - and the cost is that neighbouring numbers hide enormous gaps: on one album 73
        /// and 50 are 112,303 listeners and 2,905, a 39x difference that reads as "23 points". A
        /// tracklist asked "how much of a drop is there between these songs" can only answer honestly
        /// from the raw count.</para>
        /// <para>It is also what makes the scale re-tunable without asking anyone's API again: the
        /// ceiling in <c>MusicPopularity</c> has already been raised once, and with the counts banked
        /// a re-score is an UPDATE rather than a re-parse of the response cache.</para>
        /// </remarks>
        public long? PopularityListeners { get; set; }

        /// <summary>
        /// Where this track sits in the LIBRARY, 0-100, agreed across every source that knows it
        /// (2026-08-31). Null until at least one source has scored it.
        /// </summary>
        /// <remarks>
        /// <para>A different question from <see cref="Popularity"/>, and both are worth having.
        /// Popularity is ABSOLUTE - "roughly this many people in the world have heard it" - and it is
        /// what the album badge and the song-row number show. This is RELATIVE: "of everything on
        /// these shelves, this song is in the Nth percentile", which is what "rank our music"
        /// actually asks and what a shelf-wide ordering wants.</para>
        /// <para>It is the mean of the per-source percentiles in <see cref="MusicTrackScore"/>, so a
        /// source that has never heard of a track does not drag it down - only the sources that
        /// answered get a vote. <see cref="PopularityRankSources"/> says how many that was, because a
        /// consensus of one is not a consensus.</para>
        /// </remarks>
        public int? PopularityRank { get; set; }

        /// <summary>How many sources <see cref="PopularityRank"/> averages. 1 means a single opinion;
        /// the UI is entitled to trust a 3 more than a 1, and a re-run that loses a source must lower
        /// this rather than silently keep the old blend.</summary>
        public int PopularityRankSources { get; set; }

        /// <summary>Which external source produced <see cref="Popularity"/> - see
        /// <see cref="MovieTheater.Music.MusicGenreSources"/>. Stamped so one source can be re-run or
        /// retired without guessing which rows came from it.</summary>
        [MaxLength(32)]
        public string? PopularitySource { get; set; }

        /// <summary>When <c>music-track-popularity</c> last asked about this track - the negative
        /// cache, stamped on a MISS as well as a hit (the <see cref="LyricsCheckedUtc"/> convention).
        /// It is that queue's only stop condition, and a miss is common by design: the lookup asks
        /// per ARTIST and a song outside their top 1,000 comes back unmentioned.</summary>
        public DateTime? PopularityCheckedUtc { get; set; }
    }
}
