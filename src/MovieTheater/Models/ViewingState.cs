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
        /// <summary>"movie" (default), "series", or "misc" — movie/series share an id space and "misc"
        /// uses MiscVideo's own id space, so the caller states which target the id refers to.</summary>
        public string Kind { set; get; } = "movie";
        public bool SetActive { set; get; }
        public ViewingType Action { set; get; }
    }
}
