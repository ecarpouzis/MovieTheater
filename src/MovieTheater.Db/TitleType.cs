namespace MovieTheater.Db
{
    /// <summary>
    /// What kind of title a <see cref="Movie"/> row actually is, from IMDB's own <c>titleType</c>.
    /// Episodes are NOT here — they live in their own table (docs/metadata-enrichment-plan.md §3.3);
    /// a series row stays a <see cref="Movie"/> with <see cref="TvSeries"/>/<see cref="TvMiniSeries"/>.
    /// <see cref="Unknown"/> (the default) means "not yet classified by the IMDB scrape", which the
    /// classification pass resumes on and the default movie grid currently treats as movie-shaped.
    /// </summary>
    public enum TitleType
    {
        Unknown = 0,
        Movie = 1,
        Short = 2,
        TvShort = 3,
        TvSeries = 4,
        TvMiniSeries = 5,
        TvSpecial = 6,
        TvMovie = 7,
        Video = 8,
    }
}
