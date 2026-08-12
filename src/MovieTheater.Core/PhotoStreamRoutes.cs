namespace MovieTheater.Core
{
    /// <summary>
    /// The family-photo data-plane routes and derivative names (photos-plan.md §2.2), shared by the
    /// site (which mints the URL) and the StreamGateway (which maps the routes) so a rename cannot
    /// desync them — the <see cref="MusicStreamRoutes"/> precedent.
    ///
    /// <para>Both routes carry the SAME <see cref="PhotoCapabilityToken"/>. The ROUTE decides which
    /// root the gateway resolves the token's relative path against: <see cref="Thumb"/> against its
    /// thumbnail cache, <see cref="Original"/> against the read-only collection root. The gateway
    /// holds no database and never generates a derivative — a missing thumb is a 404 and therefore a
    /// visible ingest gap, not a lazy path (§2.2).</para>
    /// </summary>
    public static class PhotoStreamRoutes
    {
        public const string Thumb = "PhotoThumb";
        public const string Original = "PhotoOriginal";

        /// <summary>Grid card (~400px longest edge, WebP). What a timeline page requests per item.</summary>
        public const string SizeGrid = "grid";

        /// <summary>Lightbox default (~1600px longest edge, WebP).</summary>
        public const string SizeView = "view";

        /// <summary>Deep-zoom derivative (~3200px, WebP), emitted ONLY for originals a browser cannot
        /// render (HEIC/TIFF/RAW). Renderable originals deep-zoom from <see cref="Original"/> instead,
        /// which is what <c>PhotoAsset.OriginalRenderable</c> decides at mint time.</summary>
        public const string SizeZoom = "zoom";

        /// <summary>The token's size field for the untouched NAS file.</summary>
        public const string SizeOriginal = "original";

        public static string ThumbUrl(string gatewayBaseUrl, string token) =>
            $"{gatewayBaseUrl.TrimEnd('/')}/s/{token}/{Thumb}";

        public static string OriginalUrl(string gatewayBaseUrl, string token) =>
            $"{gatewayBaseUrl.TrimEnd('/')}/s/{token}/{Original}";
    }
}
