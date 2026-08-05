namespace MovieTheater.Core
{
    /// <summary>
    /// Extension → MIME type for the audio formats the music vertical serves (music-plan.md §2.1).
    /// Shared by the site (told to the &lt;audio&gt; element) and the StreamGateway (the Content-Type
    /// it serves) so the two ends can't disagree about what a file is.
    /// </summary>
    public static class MusicMimeTypes
    {
        /// <summary>Lower-case extension with or without the leading dot; unknown ⇒ octet-stream.</summary>
        public static string FromExtension(string extension)
        {
            var ext = extension.StartsWith('.') ? extension : "." + extension;
            switch (ext.ToLowerInvariant())
            {
                case ".mp3": return "audio/mpeg";
                case ".flac": return "audio/flac";
                case ".m4a": return "audio/mp4";
                case ".aac": return "audio/aac";
                case ".ogg":
                case ".oga":
                case ".opus": return "audio/ogg";
                case ".wav": return "audio/wav";
                default: return "application/octet-stream";
            }
        }
    }
}
