
using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;

namespace MovieTheater.Db
{
    public class UserMovies
    {
        [Key]
        public int id { get; set; }

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
        public string isWatched { get; set; }
        public string isWatchlist { get; set; }
        public int? MovieID { get; set; }
    }
}
