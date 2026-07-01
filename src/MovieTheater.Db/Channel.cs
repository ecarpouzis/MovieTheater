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

        public ICollection<ChannelScheduleItem> ScheduleItems { get; set; } = [];
    }
}
