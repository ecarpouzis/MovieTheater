using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace MovieTheater.Db
{
    public class MoviePoster
    {
        [Key]
        public int PosterIndex { get; set; }


        [ForeignKey(nameof(Movie))]
        public int MovieID { get; set; }
        public Movie Movie { get; set; }
    }
}
