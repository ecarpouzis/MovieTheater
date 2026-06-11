using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace MovieTheater.Db
{
    /// <summary>A single normalized genre (e.g. "Action"), shared across movies.</summary>
    [Table("Genre")]
    [Index(nameof(Name), IsUnique = true)]
    public class Genre
    {
        [Key]
        public int Id { get; set; }

        [MaxLength(100)]
        public string Name { get; set; } = default!;

        [InverseProperty(nameof(MovieGenre.Genre))]
        public ICollection<MovieGenre> MovieGenres { get; set; } = [];
    }
}
