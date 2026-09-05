
using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace MovieTheater.Db
{
    /// <summary>
    /// One mark on one user's lists: Seen, WantToWatch or Rated (<see cref="ViewingTypes"/>), against
    /// exactly one of a Movie / Series / MiscVideo. A mark's existence IS its state — un-marking deletes
    /// the row (the <see cref="ViewingEvent"/> journal keeps the history).
    ///
    /// <para>Provenance (2026-09-04): <see cref="CreatedUtc"/> and <see cref="CreatedByUserId"/> say when
    /// the mark was made and by whom — the owner, or a friend marking on their behalf. A WantToWatch row
    /// placed by somebody else IS a suggestion (there is no separate type). Rows older than the columns
    /// carry nulls, which the UI reads as "before Sep 2026". Nothing derives recency from CreatedUtc:
    /// the recommendation engine still orders by the monotonic <see cref="ViewingID"/>, which every row has.</para>
    /// </summary>
    [Table("Viewing")]
    public class Viewing
    {
        [Key]
        public int ViewingID { get; set; }

        /// <summary>The movie this viewing is for. Null when the viewing targets a <see cref="Series"/>
        /// or <see cref="MiscVideo"/> instead (exactly one of MovieID / SeriesId / MiscVideoId is set).</summary>
        public int? MovieID { get; set; }

        [ForeignKey(nameof(MovieID))]
        public Movie? Movie { get; set; }

        /// <summary>The series this viewing is for (Seen/Want on a whole series); null for movie viewings.</summary>
        public int? SeriesId { get; set; }

        [ForeignKey(nameof(SeriesId))]
        public Series? Series { get; set; }

        /// <summary>The misc video this viewing is for (Seen/Want on a short, stage performance, etc.);
        /// null for movie/series viewings. MiscVideo has its own id space, so this is a distinct FK.</summary>
        public int? MiscVideoId { get; set; }

        [ForeignKey(nameof(MiscVideoId))]
        public MiscVideo? MiscVideo { get; set; }

        /// <summary>Whose list this mark is on.</summary>
        public int UserID { get; set; }

        [ForeignKey(nameof(UserID))]
        public User User { get; set; } = default!;

        /// <summary>One of <see cref="ViewingTypes"/>.</summary>
        [MaxLength(32)]
        public string? ViewingType { get; set; }

        /// <summary>A rating's 0–100 score; null for the lists.</summary>
        public string? ViewingData { get; set; }

        /// <summary>When the mark was made. Null = a row older than provenance (before Sept 2026).</summary>
        public DateTime? CreatedUtc { get; set; }

        /// <summary>Who made the mark — the owner themself, or the friend who placed it on their behalf
        /// (a Want placed by a friend is a suggestion; Seen on someone's behalf needs a password-verified
        /// session). Null = legacy. Restrict (the model) / NO_ACTION (the live table): a suggestion must
        /// not vanish with the suggester's account.</summary>
        public int? CreatedByUserId { get; set; }

        [ForeignKey(nameof(CreatedByUserId))]
        public User? CreatedByUser { get; set; }

    }
}
