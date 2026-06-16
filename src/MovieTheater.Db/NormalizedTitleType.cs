namespace MovieTheater.Db
{
    /// <summary>
    /// The coarse, user-facing bucket a title rolls up to — the normalization of IMDb's granular
    /// <see cref="TitleType"/> that the Browse "Type" filter offers:
    /// <list type="bullet">
    ///   <item><see cref="Movies"/> — Movie, TvMovie, TvSpecial, Video (and the Unknown default).</item>
    ///   <item><see cref="Series"/> — TvSeries, TvMiniSeries (anything with episodes). Lives in the Series table.</item>
    ///   <item><see cref="Short"/> — Short, TvShort.</item>
    ///   <item><see cref="Misc"/> — library videos with no IMDb id of their own (the MiscVideo table).</item>
    /// </list>
    /// On a <see cref="Movie"/> row this is a persisted computed column derived from <see cref="TitleType"/>
    /// (so it is only ever <see cref="Movies"/> or <see cref="Short"/> — series live in their own table and
    /// misc videos in theirs). See <see cref="TitleTypeExtensions.Normalize"/> for the canonical mapping.
    /// </summary>
    public enum NormalizedTitleType
    {
        Movies = 0,
        Series = 1,
        Short = 2,
        Misc = 3,
    }

    public static class TitleTypeExtensions
    {
        /// <summary>
        /// Canonical mapping from IMDb's granular <see cref="TitleType"/> to the coarse
        /// <see cref="NormalizedTitleType"/> bucket. Keep this in sync with the persisted computed-column
        /// SQL on <c>Movie.NormalizedTitleType</c> (Short/TvShort = 2 ⇒ Short, everything else ⇒ Movies).
        /// </summary>
        public static NormalizedTitleType Normalize(this TitleType t) => t switch
        {
            TitleType.TvSeries or TitleType.TvMiniSeries => NormalizedTitleType.Series,
            TitleType.Short or TitleType.TvShort => NormalizedTitleType.Short,
            _ => NormalizedTitleType.Movies,
        };
    }
}
