using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace MovieTheater.Models
{
    public enum ViewingType { SetWatched = 0, SetWantToWatch = 1 }
    public class ViewingState
    {
        public string Username { set; get; }
        public int MovieID { set; get; }
        public bool SetActive { set; get; }
        public ViewingType Action { set; get; } 
    }
}
