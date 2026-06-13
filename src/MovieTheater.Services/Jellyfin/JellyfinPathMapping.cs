namespace MovieTheater.Services.Jellyfin
{
    /// <summary>
    /// One prefix translation between the path form stored in the DB (e.g. <c>X:\</c>) and
    /// the form Jellyfin reports for the same files (e.g. <c>\\server\share\</c>).
    /// Bound from the JellyfinPathMappings config array.
    /// </summary>
    public class JellyfinPathMapping
    {
        public string DbPrefix { get; set; } = default!;

        public string JellyfinPrefix { get; set; } = default!;
    }
}
