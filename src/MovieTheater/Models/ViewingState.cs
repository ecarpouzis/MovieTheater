using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace MovieTheater.Models
{
    /// <summary>
    /// What <c>POST /API/SetViewingState</c> does. Both act on the target's lists — the caller's own, or,
    /// via <see cref="ViewingState.ForUserId"/>, a friend's: a Want placed on a friend's list IS the
    /// suggestion (anyone signed in may); Seen on a friend's behalf needs a password-verified session.
    /// </summary>
    public enum ViewingType { SetWatched = 0, SetWantToWatch = 1 }

    public class ViewingState
    {
        public int MovieID { set; get; }
        /// <summary>"movie" (default), "series", or "misc" — movie/series share an id space and "misc"
        /// uses MiscVideo's own id space, so the caller states which target the id refers to.</summary>
        public string Kind { set; get; } = "movie";
        public bool SetActive { set; get; }
        public ViewingType Action { set; get; }
        /// <summary>Whose list to act on. Null = the caller's own.</summary>
        public int? ForUserId { set; get; }
    }
}
