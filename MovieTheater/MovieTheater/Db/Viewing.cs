using System;
using System.Collections.Generic;

namespace MovieTheater.Db
{
    public class Viewing
    {
        public Nullable<int> MovieID { get; set; }
        public Nullable<int> UserID { get; set; }
        public string ViewingType { get; set; }
        public int ViewingID { get; set; }
        public string ViewingData { get; set; }
    }
}
