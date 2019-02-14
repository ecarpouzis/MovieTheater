namespace MovieTheater.Db
{
    public class MoviePoster
    {
        public int PosterIndex { get; set; }
        public byte[] Poster { get; set; }
        public int MovieID { get; set; }
    }
}
