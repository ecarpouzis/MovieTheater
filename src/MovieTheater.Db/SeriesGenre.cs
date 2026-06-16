using System.ComponentModel.DataAnnotations.Schema;

namespace MovieTheater.Db
{
    /// <summary>Join row relating a <see cref="Series"/> to a <see cref="Genre"/> (peer of <see cref="MovieGenre"/>).</summary>
    [Table("SeriesGenre")]
    public class SeriesGenre
    {
        public int SeriesId { get; set; }

        [ForeignKey(nameof(SeriesId))]
        public Series Series { get; set; } = default!;

        public int GenreId { get; set; }

        [ForeignKey(nameof(GenreId))]
        public Genre Genre { get; set; } = default!;

        /// <summary>Order the genre appeared on IMDB (primary genre first).</summary>
        public int Ordering { get; set; }
    }
}
