
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace MovieTheater.Db
{
    [Table("Viewing")]
    public class Viewing
    {
        [Key]
        public int ViewingID { get; set; }

        public int MovieID { get; set; }

        [ForeignKey(nameof(MovieID))]
        public Movie Movie { get; set; } = default!

        public int UserID { get; set; }

        [ForeignKey(nameof(UserID))]
        public User User { get; set; } = default!;

        public string? ViewingType { get; set; }


        public string? ViewingData { get; set; }

    }
}
