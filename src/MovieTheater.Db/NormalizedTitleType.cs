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
        /// Coarse mapping from IMDb's granular <see cref="TitleType"/> to the <see cref="NormalizedTitleType"/>
        /// bucket. The authoritative version is the persisted computed-column SQL on
        /// <c>Movie.NormalizedTitleType</c>, which ALSO maps a short-runtime <see cref="TitleType.Video"/>
        /// (&lt; 45 min — many IMDb shorts are tagged "video") to Short; this runtime-aware rule can't be
        /// expressed on <see cref="TitleType"/> alone, so this helper is a coarse approximation.
        /// </summary>
        public static NormalizedTitleType Normalize(this TitleType t) => t switch
        {
            TitleType.TvSeries or TitleType.TvMiniSeries => NormalizedTitleType.Series,
            TitleType.Short or TitleType.TvShort => NormalizedTitleType.Short,
            _ => NormalizedTitleType.Movies,
        };
    }
}
