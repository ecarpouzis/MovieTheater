using System;
using System.Collections.Generic;

namespace MovieTheater.ViewModels
{
    public class BrowseViewModel
    {
        public IEnumerable<MovieItem> Movies { get; set; }

        public List<int> seenMovieIDs { get; set; }
        public List<int> watchlistMovieIDs { get; set; }
        public Dictionary<int, int> ratedMovieData { get; set; }

        public class MovieItem
        {
            public int MovieID { get; set; }
            public string Title { get; set; }
            public string SimpleTitle { get; set; }
            public string Rating { get; set; }
            public DateTime? ReleaseDate { get; set; }
            public string Runtime { get; set; }
            public string Genre { get; set; }
            public string Director { get; set; }
            public string Writer { get; set; }
            public string Actors { get; set; }
            public string Plot { get; set; }
            public string PosterLink { get; set; }
            public decimal? imdbRating { get; set; }
            public string imdbID { get; set; }
            public int? tomatoRating { get; set; }
            public int? UserID { get; set; }
            public string ViewingType { get; set; }
            public bool isWatched { get; set; }
            public bool isWatchlist { get; set; }
        }
    }
}
