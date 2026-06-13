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
        /// JSON: { genreIds:[..], genreMode:"all"|"any", yearMin?, yearMax?,
        /// maxMpaRatingId?, unwatchedByUserId?, excludeRemoveFromRandom:true }.
        /// </summary>
        public string? FilterJson { get; set; }

        public int Seed { get; set; }

        /// <summary>"SeededShuffle" (default) or "ReleaseDate" (ascending, looping).</summary>
        [MaxLength(32)]
        public string ShuffleMode { get; set; } = "SeededShuffle";

        /// <summary>Schedule epoch — items are only generated after this instant.</summary>
        public DateTime AnchorUtc { get; set; }

        public ICollection<ChannelScheduleItem> ScheduleItems { get; set; } = [];
    }
}
