using System;

namespace MovieTheater.Db
{
    public class Viewing
    {
        public int? MovieID { get; set; }
        public int? UserID { get; set; }
        public string ViewingType { get; set; }
        public int ViewingID { get; set; }
        public string ViewingData { get; set; }
    }
}
