using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace MovieTheater.Db
{
    /// <summary>
    /// A TV channel definition (streaming-plan.md §8): a filter over the library plus a
    /// shuffle seed. The materialized lineup lives in <see cref="ChannelScheduleItem"/>.
    /// Admin-editable (CanEditMovies).
    /// </summary>
    [Table("Channel")]
    public class Channel
    {
        [Key]
        public int Id { get; set; }

        [MaxLength(64)]
        public string Name { get; set; } = default!;

        [MaxLength(256)]
        public string? Description { get; set; }

        public int SortOrder { get; set; }

        public bool Enabled { get; set; } = true;

        /// <summary>
        /// Serialized <c>ChannelFilter</c> — the eligibility predicate bag. Beyond the original
        /// genre/year/MPAA/unwatched fields it now carries content kinds, numeric ranges (IMDb/RT/
        /// popularity/runtime), AI slider ranges + tag rules, language/country, credits, and
        /// freshness. All fields optional, so old FilterJson deserializes unchanged.
        /// </summary>
        public string? FilterJson { get; set; }

        public int Seed { get; set; }

        /// <summary>"SeededShuffle" (default) or "ReleaseDate" (ascending, looping). Superseded by
        /// <see cref="ScheduleStrategy"/> when that is set; kept for back-compat with existing rows.</summary>
        [MaxLength(32)]
        public string ShuffleMode { get; set; } = "SeededShuffle";

        /// <summary>Schedule epoch — items are only generated after this instant.</summary>
        public DateTime AnchorUtc { get; set; }

        // ── Channels 2.0 (additive, nullable) ──

        /// <summary>Stable identity for a code-defined catalog channel, so the upsert can find it even
        /// after a rename. NULL = a hand-made (admin-created) channel the catalog never touches.</summary>
        [MaxLength(64)]
        public string? CatalogKey { get; set; }

        /// <summary>How the lineup is ordered: "SeededShuffle" | "WeightedShuffle" | "ReleaseDate" |
        /// "NewestFirst" | "Marathon" | "EpisodeRoundRobin". NULL ⇒ map from the legacy
        /// <see cref="ShuffleMode"/>.</summary>
        [MaxLength(32)]
        public string? ScheduleStrategy { get; set; }

        /// <summary>Serialized <c>ChannelRotation</c> for a rotating spotlight (Director/Franchise of
        /// the Week, …): a cadence + an ordered list of subject filters resolved deterministically from
        /// the date. NULL = not a rotating channel.</summary>
        public string? RotationJson { get; set; }

        // Seasonal window (deterministic from the current date; null parts ⇒ always in-season). The
        // channel stays Enabled year-round; only its visibility/sort in the guide is gated, so its
        // lineup never goes cold off-season.
        public int? SeasonStartMonth { get; set; }
        public int? SeasonStartDay { get; set; }
        public int? SeasonEndMonth { get; set; }
        public int? SeasonEndDay { get; set; }

        /// <summary>UI grouping / family label ("Genres", "Anime", "Seasonal", …) for the channel browser.</summary>
        [MaxLength(48)]
        public string? Category { get; set; }

        /// <summary>Optional hand-set channel logo path; the UI otherwise derives a tile from a
        /// representative title's poster.</summary>
        [MaxLength(256)]
        public string? LogoPath { get; set; }

        /// <summary>The channel's effective rating ceiling, persisted by the maintainer. A restart wipes the
        /// in-memory ceiling cache but the schedule itself is already persisted, so storing the ceiling too
        /// lets the guide/list gate visibility instantly after a deploy instead of re-running the expensive
        /// eligible-set scan. NULL = not yet computed (a brand-new channel, until the maintainer warms it).</summary>
        public int? CachedCeiling { get; set; }

        /// <summary>When set, a PER-USER channel visible only to that user (their "For You" recommendation
        /// channels). NULL = a normal channel everyone sees. Only the guide/list visibility gates on this;
        /// the schedule engine is otherwise oblivious. This also stops the older per-user "Unseen by
        /// &lt;user&gt;" channels from leaking to every viewer.</summary>
        public int? OwnerUserId { get; set; }

        // ── User playlists & watch parties (docs/playlists-watchparty-plan.md; additive, nullable) ──

        /// <summary>True for a user-created playlist channel: its lineup is the explicit, hand-ordered
        /// <see cref="PlaylistItems"/> (played by the "Playlist" strategy) rather than a filter over the
        /// library. Distinguishes it from the reco "For You" channels, which also set <see cref="OwnerUserId"/>.</summary>
        public bool IsUserPlaylist { get; set; }

        /// <summary>Non-null ⇒ a private WATCH PARTY: the same explicit-lineup channel, but hidden from every
        /// shelf/guide and reached only by this URL-safe token, whose timeline waits until the lobby presses
        /// Begin. NULL = a normal channel (or a plain, always-on playlist).</summary>
        [MaxLength(32)]
        public string? WatchpartyToken { get; set; }

        /// <summary>For a watch party: the instant the lobby pressed Begin (also re-anchors the schedule to
        /// start "now"). NULL until it begins — so the party stays in its waiting room, and a server restart
        /// mid-party doesn't lose whether it had started.</summary>
        public DateTime? WatchpartyStartedUtc { get; set; }

        public ICollection<ChannelScheduleItem> ScheduleItems { get; set; } = [];

        /// <summary>The hand-picked, ordered lineup for a playlist / watch-party channel (empty for filter
        /// channels).</summary>
        public ICollection<PlaylistItem> PlaylistItems { get; set; } = [];
    }
}
