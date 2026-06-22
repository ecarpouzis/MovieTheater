namespace MovieTheater.Services.Poster
{
    public enum PosterImageVariant
    {
        Main,
        Thumbnail
    }

    /// <summary>
    /// Poster file namespaces. Movie and Series ids are NOT disjoint (a single id can be both a Movie row
    /// and a Series row), so a Series poster MUST live in its own bucket or it collides with the Movie's
    /// poster on disk ("{id}.png") and one entity shows the other's poster. MiscVideo has the same problem
    /// against the Movie/Series id space. Null/empty bucket = the default Movie namespace.
    /// </summary>
    public static class PosterBucket
    {
        public const string Series = "series";
        public const string Misc = "misc";

        /// <summary>The bucket a title's poster belongs in: Series titles are bucketed, movies are not.</summary>
        public static string? ForTitle(bool isSeries) => isSeries ? Series : null;
    }
}
