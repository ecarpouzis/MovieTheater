using System.ComponentModel.DataAnnotations;

namespace MovieTheater.Db
{
    public class User
    {
        public string Username { get; set; }

        [Key]
        public int UserID { get; set; }
    }
}
