namespace MovieTheater.Core
{
    /// <summary>
    /// The music data-plane routes (music-plan.md §2.1 / §Phase 7), shared by the site (which
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

        /// <summary>
        /// The same audio, repackaged as fragmented MP4 so Media Source Extensions can accept it.
        ///
        /// <para>This is a REMUX, not a transcode: ffmpeg runs with <c>-c:a copy</c>, so the FLAC or
        /// MP3 frames are written into <c>moof</c>/<c>mdat</c> boxes byte-for-byte. Nothing is
        /// decoded and nothing is re-encoded — FLAC stays bit-perfect, which is the property the
        /// <see cref="File"/> lane exists to protect. The container is the only thing that changes,
        /// and only because MSE cannot be handed a raw .flac.</para>
        ///
        /// <para>Why it is worth having: appending tracks into ONE SourceBuffer makes a track
        /// boundary a buffered-range continuation rather than a JavaScript event, and script is
        /// exactly what a backgrounded phone stops running.</para>
        /// </summary>
        public const string Fmp4 = "MusicFmp4";

        /// <summary>
        /// The universal treatment (music-mse-plan.md §"What the incumbents do"): the audio
        /// RE-ENCODED to AAC in a fragmented MP4 — <c>audio/mp4; codecs="mp4a.40.2"</c>, 44.1 kHz,
        /// channel count preserved. The one row of the treatment matrix every MSE browser accepts.
        ///
        /// <para>Unlike <see cref="Fmp4"/> this is NOT bit-perfect: it decodes and re-encodes, so it
        /// sits at the BOTTOM of the matrix and is used only where a bit-perfect treatment isn't
        /// proven — Firefox's MSE has no MP3 decoder, some buffers refuse a sample-rate switch, and
        /// .wma/.ape/.ogg have no MSE-able form at all. The trade is deliberate and stated in the
        /// plan: measured against "playback that never stops", continuity outranks fidelity wherever
        /// the bit-perfect route is the one that can stop.</para>
        ///
        /// <para>It also buys the homogeneous-session mode: run a whole queue through this lane and
        /// there are zero <c>changeType</c>s and zero rate switches left to survive.</para>
        /// </summary>
        public const string Universal = "MusicUniversal";

        public static string Url(string gatewayBaseUrl, string token, bool transcode) =>
            $"{gatewayBaseUrl.TrimEnd('/')}/s/{token}/{(transcode ? Transcode : File)}";

        public static string Fmp4Url(string gatewayBaseUrl, string token) =>
            $"{gatewayBaseUrl.TrimEnd('/')}/s/{token}/{Fmp4}";

        public static string UniversalUrl(string gatewayBaseUrl, string token) =>
            $"{gatewayBaseUrl.TrimEnd('/')}/s/{token}/{Universal}";
    }
}
