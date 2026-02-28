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

        public string SettingKey { get; set; }

        public string SettingValue { get; set; }
    }
}