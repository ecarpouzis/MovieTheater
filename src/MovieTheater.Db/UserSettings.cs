using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace MovieTheater.Db
{
    [Table("UserSettings")]
    public class UserSettings
    {
        [Key]
        public int ID { get; set; }

        public int UserID { get; set; }

        [ForeignKey(nameof(UserID))]
        public User User { get; set; } = default!;

        public string SettingKey { get; set; }

        public string SettingValue { get; set; }
    }
}