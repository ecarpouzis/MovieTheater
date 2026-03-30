using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace MovieTheater.Db
{
    [Table("MoviePosterDetails")]
    public class MoviePosterDetails
    {
        [Key]
        public int MovieId { get; set; }

        public string? PosterLink { get; set; }
        public int PosterVersion { get; set; }

        public string? DominantColor { get; set; }

        [ForeignKey(nameof(MovieId))]
        public Movie Movie { get; set; } = default!;
    }
}
