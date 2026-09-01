using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace MovieTheater.Db
{
    /// <summary>
    /// One external source's opinion of how widely heard one track is (2026-08-31) — the row behind
    /// <see cref="MusicTrack.PopularityRank"/>.
    /// </summary>
    /// <remarks>
    /// <para><b>A table rather than more columns, for the reason <c>MusicAlbumGenre</c> is one.</b>
    /// The <c>Source</c> column is the load-bearing idea across this vertical: it is part of the
    /// unique key, so each pass owns and REPLACES only its own rows and any number of sources
    /// coexist without a "who wrote this last" column. Adding <c>PopularityDeezer</c>,
    /// <c>PopularitySpotify</c> … would have meant a migration per source and a widening branch in
    /// every read.</para>
    ///
    /// <para><b>Why more than one source at all.</b> Measured before this was built: Deezer's own
    /// ranking agrees with Last.fm's at Spearman ρ = 0.788 over a stratified sample of the library.
    /// That is high enough to be corroboration rather than noise, and low enough that the
    /// disagreements are real — different audiences, and one source's blind spot is not the other's.
    /// A second source also reaches the 4,045 tracks Last.fm had never heard of.</para>
    ///
    /// <para><b><see cref="Score"/> is a PERCENTILE, not the source's own number.</b> Sources are on
    /// wildly different scales — Last.fm counts listeners (1 … 4.2 million), Deezer publishes an
    /// internal rank (roughly 0 … 1,000,000), Spotify a 0–100 index of its own — and averaging those
    /// raw values would be meaningless. Converting each to "where this sits among everything else
    /// this source told us about" makes them commensurable, and is also exactly the question asked of
    /// the library: rank our music. <see cref="RawValue"/> keeps the source's own number so a
    /// re-scale never needs another request.</para>
    /// </remarks>
    [Table("MusicTrackScore")]
    public class MusicTrackScore
    {
        [Key]
        public int Id { get; set; }

        public int MusicTrackId { get; set; }

        [ForeignKey(nameof(MusicTrackId))]
        public MusicTrack Track { get; set; } = default!;

        /// <summary>Which service said it — see <see cref="MusicScoreSources"/>. Part of the unique
        /// key with the track, so a source can be re-run or retired without touching the others.</summary>
        [MaxLength(32)]
        public string Source { get; set; } = default!;

        /// <summary>
        /// 0–100: this track's percentile among every track this SAME source gave a value for.
        /// Recomputed whenever the source's coverage changes, because a percentile is a statement
        /// about a population and the population grows.
        /// </summary>
        public int Score { get; set; }

        /// <summary>
        /// The source's own number, unnormalised — Last.fm listeners, a Deezer rank, a Spotify
        /// popularity. Kept so the percentile can be recomputed offline, and because the raw count is
        /// the only thing that can express a DROP (the reason
        /// <see cref="MusicTrack.PopularityListeners"/> exists).
        /// </summary>
        public long? RawValue { get; set; }

        /// <summary>When this source last answered for this track.</summary>
        public DateTime CheckedUtc { get; set; }
    }

    /// <summary>
    /// The services that can score a track. Values are stable strings — they are written into rows
    /// and matched on, so renaming one silently orphans everything it wrote.
    /// </summary>
    public static class MusicScoreSources
    {
        /// <summary>Last.fm listener counts, via artist top-tracks. The first source, and the one
        /// <see cref="MusicTrack.Popularity"/> and <see cref="MusicTrack.PopularityListeners"/>
        /// still carry directly.</summary>
        public const string LastFm = "lastfm";

        /// <summary>Deezer's published per-track <c>rank</c>. Needs no credentials at all.</summary>
        public const string Deezer = "deezer";

        /// <summary>Spotify's own 0–100 track popularity. Needs a client id + secret.</summary>
        public const string Spotify = "spotify";
    }
}
