using System.ComponentModel.DataAnnotations.Schema;

namespace MovieTheater.Db
{
    /// <summary>Join row relating a <see cref="Movie"/> to a <see cref="Genre"/>.</summary>
    [Table("MovieGenre")]
    public class MovieGenre
    {
        public int MovieID { get; set; }

        [ForeignKey(nameof(MovieID))]
        public Movie Movie { get; set; } = default!;

        public int GenreId { get; set; }

        [ForeignKey(nameof(GenreId))]
        public Genre Genre { get; set; } = default!;

        /// <summary>Order the genre appeared on IMDB (primary genre first).</summary>
        public int Ordering { get; set; }
    }
}
