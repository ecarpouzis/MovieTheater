using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace MovieTheater.Db
{
    /// <summary>
    /// Poster metadata for a <see cref="Series"/> (peer of <see cref="MoviePosterDetails"/>). The poster
    /// IMAGE file lives on disk keyed by id (<c>{SeriesId}.png</c>); because a series keeps its old Movie
    /// id, the existing file is reused with no re-download. This row carries the link, cache-bust version,
    /// and dominant color.
    /// </summary>
    [Table("SeriesPosterDetails")]
    public class SeriesPosterDetails
    {
        [Key]
        public int SeriesId { get; set; }

        public string? PosterLink { get; set; }
        public int PosterVersion { get; set; }
        public string? DominantColor { get; set; }

        [ForeignKey(nameof(SeriesId))]
        public Series Series { get; set; } = default!;
    }
}
