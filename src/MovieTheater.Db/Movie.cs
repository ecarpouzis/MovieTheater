using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace MovieTheater.Db
{
    [Table("Movie")]
    public class Movie
    {
        public string? Title { get; set; }
        public string? SimpleTitle { get; set; }
        public string? Rating { get; set; }
        public DateTime? ReleaseDate { get; set; }
        public string? Runtime { get; set; }
        public string? Genre { get; set; }
        public string? Director { get; set; }
        public string? Writer { get; set; }
        public string? Actors { get; set; }
        public string? Plot { get; set; }
        public string? PosterLink { get; set; }
        public decimal? imdbRating { get; set; }
        public string? imdbID { get; set; }
        public int? tomatoRating { get; set; }
        public DateTime? UploadedDate { get; set; }
        public bool RemoveFromRandom { get; set; }

        [Key]
        public int id { get; set; }

        public List<Viewing> Viewings { get; set; } = default!;
    }
}