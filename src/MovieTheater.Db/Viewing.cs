
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace MovieTheater.Db
{
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

        public int UserID { get; set; }

        [ForeignKey(nameof(UserID))]
        public User User { get; set; } = default!;

        public string? ViewingType { get; set; }


        public string? ViewingData { get; set; }

    }
}
