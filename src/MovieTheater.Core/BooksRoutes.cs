namespace MovieTheater.Core
{
    /// <summary>
    /// The two path prefixes the site proxies to the Books host, named once so the Yarp route, the
    /// SPA's API module and the host's own routing cannot drift. Under <see cref="ApiPrefix"/> the
    /// prefix is stripped before forwarding (<c>/API/Books/catalog</c> → host <c>/catalog</c>); under
    /// <see cref="OpdsPrefix"/> it is kept (<c>/opds/…</c> → host <c>/opds/…</c>), because e-readers
    /// hold absolute OPDS links.
    /// </summary>
    public static class BooksRoutes
    {
        public const string ApiPrefix = "/API/Books";
        public const string OpdsPrefix = "/opds";

        public const string ApiRouteId = "books-api";
        public const string OpdsRouteId = "books-opds";
        public const string ClusterId = "books-host";
    }
}
