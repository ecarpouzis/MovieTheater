namespace MovieTheater.Core
{
    /// <summary>
    /// The two music data-plane routes (music-plan.md §2.1 / §Phase 7), shared by the site (which
    /// mints the URL) and the StreamGateway (which maps the routes) so a rename can't desync them.
    ///
    /// <para>Both lanes carry the SAME <see cref="MusicCapabilityToken"/> — 4 fields, unchanged. The
    /// ROUTE, not the token, is what decides the treatment: <see cref="File"/> serves the bytes as
    /// they are (Range-capable, bit-perfect), <see cref="Transcode"/> pipes the file through ffmpeg
    /// as mp3 for formats no browser decodes. Keeping one token shape means no version skew between
    /// a deployed site and a not-yet-redeployed gateway.</para>
    /// </summary>
    public static class MusicStreamRoutes
    {
        public const string File = "MusicFile";
        public const string Transcode = "MusicTranscode";

        public static string Url(string gatewayBaseUrl, string token, bool transcode) =>
            $"{gatewayBaseUrl.TrimEnd('/')}/s/{token}/{(transcode ? Transcode : File)}";
    }
}
