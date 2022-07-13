using System.ComponentModel.DataAnnotations;

namespace MovieTheater.Db
{
    public class User
    {
        [Key]
        public int UserID { get; set; }

        public string? Username { get; set; }
    }
}
