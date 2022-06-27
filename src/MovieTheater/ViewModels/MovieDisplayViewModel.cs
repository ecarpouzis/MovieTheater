using MovieTheater.Db;

namespace MovieTheater.ViewModels
{
    public class MovieDisplayViewModel
    {
        public Movie Movie { get; set; }

        public Viewing PreviousWatchedMovie { get; set; }

        public Viewing PreviousSuggest { get; set; }

        public Viewing PreviousRated { get; set; }
    }
}
