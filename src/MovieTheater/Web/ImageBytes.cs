using System;
using System.IO;
using System.Threading.Tasks;

namespace MovieTheater.Web
{
    /// <summary>
    /// What an image actually IS, read from its first bytes rather than from its filename or from a
    /// hard-coded default.
    ///
    /// <para><b>Why the site sniffs.</b> Thumbnails moved from PNG to WebP (2026-08-31), and the two
    /// generations coexist: the files already on the images mount are PNG, everything generated from now
    /// on is WebP, and both live under the SAME name (a poster's id, a card's <c>{cardId}.png</c>, an
    /// album's cached art). Renaming them would mean rewriting <c>BoxArtPath</c> and friends and breaking
    /// every URL already in a browser cache — for nothing, because the bytes say what they are. So the
    /// serve path reads the magic number and the disk layout never has to change.</para>
    ///
    /// <para>The move was worth it: a 300 px album cover is <b>125 KB</b> as the PNG we were writing and
    /// <b>12.9 KB</b> as WebP q82 — measured on the real file, and PNG's own compression levels made no
    /// difference at all (125 KB at level 6, 122 KB at level 9 + optimize) because cover art is a
    /// PHOTOGRAPH and PNG is a lossless container for flat graphics. Nor could HTTP compression help: a
    /// PNG is already deflated. The music grid was asking for 22 of those at once — 2.75 MB for one
    /// screen, and the desktop grid measurably failed to paint a single cover inside 12 s.</para>
    /// </summary>
    public static class ImageBytes
    {
        public const string Png = "image/png";
        public const string Webp = "image/webp";
        public const string Jpeg = "image/jpeg";
        public const string Gif = "image/gif";

        /// <summary>
        /// The content type for these bytes. PNG is the fallback because it is what the site served for
        /// years — an unrecognised or truncated file keeps its old behaviour rather than becoming a
        /// download prompt.
        /// </summary>
        public static string ContentTypeOf(ReadOnlySpan<byte> bytes)
        {
            // RIFF....WEBP
            if (bytes.Length >= 12
                && bytes[0] == 0x52 && bytes[1] == 0x49 && bytes[2] == 0x46 && bytes[3] == 0x46
                && bytes[8] == 0x57 && bytes[9] == 0x45 && bytes[10] == 0x42 && bytes[11] == 0x50)
                return Webp;
            if (bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF) return Jpeg;
            if (bytes.Length >= 3 && bytes[0] == 0x47 && bytes[1] == 0x49 && bytes[2] == 0x46) return Gif;
            return Png;
        }

        /// <summary>The same read for a file on disk — 12 bytes, not the whole image.</summary>
        public static async Task<string> ContentTypeOfFileAsync(string path)
        {
            try
            {
                var head = new byte[12];
                await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 12, useAsync: true);
                var read = await fs.ReadAsync(head.AsMemory(0, 12));
                return ContentTypeOf(head.AsSpan(0, read));
            }
            catch
            {
                return Png;
            }
        }
    }
}
