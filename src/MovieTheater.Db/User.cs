using System.ComponentModel.DataAnnotations;

namespace MovieTheater.Db
{
    public class User
    {
        [Key]
        public int UserID { get; set; }

        public string? Username { get; set; }

        public DateTime? LastLogin { get; set; }

        // Null means the account is passwordless (legacy communal login).
        // Set via PasswordHasher<User>; never store plaintext.
        public string? PasswordHash { get; set; }
    }
}
