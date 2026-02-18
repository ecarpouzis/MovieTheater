using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace MovieTheater.Db
{
    [Table("RatingMPA")]
    public class RatingMPA
    {
        [Key]
        public int RatingID { get; set; }

        public int MinAge { get; set; }

        public string? MPAName { get; set; }
    }
}