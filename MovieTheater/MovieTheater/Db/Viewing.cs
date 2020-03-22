
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace MovieTheater.Db
{
    [Table("Viewing")]
    public class Viewing
    {
        public int MovieID { get; set; }

        public int UserID { get; set; }

        public string ViewingType { get; set; }

        [Key]
        public int ViewingID { get; set; }

        public string ViewingData { get; set; }

        [ForeignKey(nameof(MovieID))]
        public Movie Movie { get; set; }
    }
}
