using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace MovieTheater.Db
{
    /// <summary>
    /// One user-contributed plot summary from a series' IMDB /plotsummary page (peer of
    /// <see cref="MoviePlotSummary"/>; distinct from the single <see cref="Series.PlotSynopsis"/>).
    /// </summary>
    [Table("SeriesPlotSummary")]
    public class SeriesPlotSummary
    {
        [Key]
        public int Id { get; set; }

        public int SeriesId { get; set; }

        [ForeignKey(nameof(SeriesId))]
        public Series Series { get; set; } = default!;

        /// <summary>Order the summary appeared on IMDB.</summary>
        public int Ordering { get; set; }

        /// <summary>IMDB contributor handle, if credited.</summary>
        public string? Author { get; set; }

        public string Text { get; set; } = default!;
    }
}
