using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace MovieTheater.Db
{
    /// <summary>
    /// One user-contributed plot summary from a movie's IMDB /plotsummary page. A movie
    /// has several of these (distinct from the single long <see cref="Movie.PlotSynopsis"/>
    /// and the one-line <see cref="Movie.PlotFull"/> outline).
    /// </summary>
    [Table("MoviePlotSummary")]
    public class MoviePlotSummary
    {
        [Key]
        public int Id { get; set; }

        public int MovieID { get; set; }

        [ForeignKey(nameof(MovieID))]
        public Movie Movie { get; set; } = default!;

        /// <summary>Order the summary appeared on IMDB.</summary>
        public int Ordering { get; set; }

        /// <summary>IMDB contributor handle, if credited.</summary>
        public string? Author { get; set; }

        public string Text { get; set; } = default!;
    }
}
