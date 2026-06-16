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
        /// <summary>"movie" (default) or "series" — the id space is shared, so the caller states which.</summary>
        public string Kind { set; get; } = "movie";
        public bool SetActive { set; get; }
        public ViewingType Action { set; get; }
    }
}
