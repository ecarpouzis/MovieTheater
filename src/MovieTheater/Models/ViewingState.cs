using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace MovieTheater.Models
{
    public class ViewingState
    {
        public string Username { set; get; }
        public int MovieID { set; get; }
        public bool SetActive { set; get; }
        public enum ViewingType { SetWatched, SetWantToWatch }
        public ViewingType Action { set; get; } 
    }
}
