using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace MovieTheater.Db
{
    [Table("RatingMap")]
    public class RatingMap
    {
        [Key]
        public int RatingMapID { get; set; }

        // value that maps to Movie.Rating (e.g. "PG-13" or whatever is stored in Movie.Rating)
        public string? MovieRating { get; set; }

        public int MPARatingID { get; set; }
    }
}