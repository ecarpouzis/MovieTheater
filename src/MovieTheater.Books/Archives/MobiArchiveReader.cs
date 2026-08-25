using System.Text;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.PixelFormats;

namespace MovieTheater.Books.Archives
{
    /// <summary>
    /// MOBI, read the only way a canvas reader can use one: the embedded JPEG jacket for the cover, and — because
    /// MOBI has no page model — a PSEUDO-PAGE rendering of the extracted readable text, greeked into lines so a
    /// reader can page through something rather than nothing. It is a fallback format in this library, not a
    /// first-class one; the real reading experience for prose is EPUB.
    /// </summary>
    public sealed class MobiArchiveReader : IArchiveReader
    {
        private const int LineCharLimit = 90;
        private const int LinesPerPage = 48;
        private const int MaxPages = 5000;

        public bool CanHandle(string fileExtension) =>
            ".mobi".Equals(fileExtension, StringComparison.OrdinalIgnoreCase);

        public Task<int> GetPageCountAsync(string filePath)
        {
            var bytes = File.ReadAllBytes(filePath);
            var text = ExtractReadableText(bytes, maxChars: 700_000);
            if (!string.IsNullOrWhiteSpace(text))
            {
                var lineCount = SplitIntoLines(text, LineCharLimit).Count;
                var pageCount = Math.Max(1, (int)Math.Ceiling(lineCount / (double)LinesPerPage));
                return Task.FromResult(Math.Clamp(pageCount, 1, MaxPages));
            }
            return Task.FromResult(Math.Clamp(EstimatePseudoPageCountFromLength(bytes.LongLength), 1, MaxPages));
        }

        public async Task<IReadOnlyList<string>> GetPageNamesAsync(string filePath)
        {
            var count = await GetPageCountAsync(filePath);
            return Enumerable.Range(1, count).Select(n => $"page {n}").ToList();
        }

        public Task<Stream> GetPageAsync(string filePath, int pageIndex)
        {
            var bytes = File.ReadAllBytes(filePath);
            var text = ExtractReadableText(bytes, maxChars: 700_000);

            if (string.IsNullOrWhiteSpace(text))
            {
                var pageCount = Math.Clamp(EstimatePseudoPageCountFromLength(bytes.LongLength), 1, MaxPages);
                if (pageIndex < 0 || pageIndex >= pageCount) throw new ArgumentOutOfRangeException(nameof(pageIndex));
                return DocumentPlaceholderRenderer.CreatePageAsync(Path.GetFileName(filePath), pageIndex, pageCount);
            }

            var lines = SplitIntoLines(text, LineCharLimit);
            var pageCountFromText = Math.Max(1, (int)Math.Ceiling(lines.Count / (double)LinesPerPage));
            if (pageIndex < 0 || pageIndex >= pageCountFromText) throw new ArgumentOutOfRangeException(nameof(pageIndex));

            var start = pageIndex * LinesPerPage;
            var count = Math.Min(LinesPerPage, Math.Max(0, lines.Count - start));
            return Task.FromResult<Stream>(RenderPseudoTextPage(lines.GetRange(start, count)));
        }

        public Task<Stream> GetCoverAsync(string filePath)
        {
            var bytes = File.ReadAllBytes(filePath);
            var cover = TryExtractEmbeddedJpeg(bytes);
            return cover is { Length: > 0 }
                ? Task.FromResult<Stream>(new MemoryStream(cover, writable: false))
                : DocumentPlaceholderRenderer.CreateCoverAsync(Path.GetFileName(filePath));
        }

        public Task<ArchiveMetadata?> ReadMetadataAsync(string filePath)
        {
            var bytes = File.ReadAllBytes(filePath);
            var text = ExtractReadableText(bytes, maxChars: 120_000);
            var pageCount = !string.IsNullOrWhiteSpace(text)
                ? Math.Max(1, (int)Math.Ceiling(SplitIntoLines(text, LineCharLimit).Count / (double)LinesPerPage))
                : EstimatePseudoPageCountFromLength(bytes.LongLength);

            return Task.FromResult<ArchiveMetadata?>(new ArchiveMetadata
            {
                IssueTitle = TryExtractMobiTitle(bytes) ?? Path.GetFileNameWithoutExtension(filePath),
                Description = BuildDescription(text),
                PageCount = Math.Clamp(pageCount, 1, MaxPages),
            });
        }

        /// <summary>Greeked text: word-shaped bars, not glyphs — there is no font dependency and no claim that
        /// the words are legible, only that the page has the SHAPE of a page.</summary>
        private static MemoryStream RenderPseudoTextPage(List<string> lines)
        {
            const int width = 1200, height = 1700, marginX = 72, marginY = 84, lineHeight = 30, spaceWidth = 12;
            using var image = new Image<Rgba32>(width, height, Color.White);

            var y = marginY;
            foreach (var line in lines)
            {
                if (y >= height - marginY) break;
                if (string.IsNullOrWhiteSpace(line)) { y += lineHeight; continue; }

                var x = marginX;
                foreach (var word in line.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                {
                    var w = Math.Max(10, Math.Min(540, word.Length * 11));
                    if (x + w >= width - marginX) break;
                    FillRect(image, x, y, w, 14, new Rgba32(24, 24, 24));
                    x += w + spaceWidth;
                }
                y += lineHeight;
            }

            var ms = new MemoryStream();
            image.Save(ms, new JpegEncoder { Quality = 88 });
            ms.Position = 0;
            return ms;
        }

        private static void FillRect(Image<Rgba32> image, int x, int y, int width, int height, Rgba32 color)
        {
            var x1 = Math.Clamp(x, 0, image.Width - 1);
            var y1 = Math.Clamp(y, 0, image.Height - 1);
            var x2 = Math.Clamp(x + width, 0, image.Width);
            var y2 = Math.Clamp(y + height, 0, image.Height);
            for (var yy = y1; yy < y2; yy++)
                for (var xx = x1; xx < x2; xx++)
                    image[xx, yy] = color;
        }

        private static List<string> SplitIntoLines(string text, int lineCharLimit)
        {
            var words = NormalizeWhitespace(text).Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var lines = new List<string>();
            var current = new StringBuilder(lineCharLimit + 8);

            foreach (var rawWord in words)
            {
                var word = rawWord.Trim();
                if (word.Length == 0) continue;
                if (current.Length == 0) { current.Append(word); continue; }
                if (current.Length + 1 + word.Length <= lineCharLimit) { current.Append(' ').Append(word); continue; }
                lines.Add(current.ToString());
                current.Clear();
                current.Append(word);
            }
            if (current.Length > 0) lines.Add(current.ToString());
            return lines;
        }

        private static int EstimatePseudoPageCountFromLength(long bytes) =>
            Math.Clamp((int)Math.Ceiling(bytes / 45_000d), 1, MaxPages);

        /// <summary>The MOBI header's title record: a 4-byte big-endian offset and length at +84/+88 from "MOBI".</summary>
        private static string? TryExtractMobiTitle(byte[] bytes)
        {
            var mobiOffset = IndexOf(bytes, "MOBI"u8.ToArray());
            if (mobiOffset < 0 || mobiOffset + 92 >= bytes.Length) return null;

            var titleOffset = ReadUInt32BigEndian(bytes, mobiOffset + 84);
            var titleLength = ReadUInt32BigEndian(bytes, mobiOffset + 88);
            if (titleOffset <= 0 || titleLength <= 0 || titleLength > 512) return null;
            if (titleOffset + titleLength > bytes.Length) return null;

            var titleBytes = bytes.AsSpan(titleOffset, titleLength).ToArray();
            var utf8 = Encoding.UTF8.GetString(titleBytes).Trim('\0', ' ', '\r', '\n', '\t');
            if (!string.IsNullOrWhiteSpace(utf8)) return utf8;
            var latin1 = Encoding.Latin1.GetString(titleBytes).Trim('\0', ' ', '\r', '\n', '\t');
            return string.IsNullOrWhiteSpace(latin1) ? null : latin1;
        }

        private static string? BuildDescription(string? text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            var normalized = NormalizeWhitespace(text);
            return normalized.Length <= 480 ? normalized : normalized[..480] + "...";
        }

        /// <summary>Runs of ≥ 30 printable bytes — long enough to be prose, not a header field or a stray byte.</summary>
        private static string ExtractReadableText(byte[] bytes, int maxChars)
        {
            var sb = new StringBuilder(Math.Min(maxChars, 8192));
            var run = new StringBuilder(256);

            for (var i = 0; i < bytes.Length && sb.Length < maxChars; i++)
            {
                var b = bytes[i];
                if ((b >= 32 && b <= 126) || b is 9 or 10 or 13) { run.Append((char)b); continue; }
                if (run.Length >= 30)
                {
                    if (sb.Length > 0) sb.Append(' ');
                    sb.Append(run);
                }
                run.Clear();
            }
            if (run.Length >= 30 && sb.Length < maxChars)
            {
                if (sb.Length > 0) sb.Append(' ');
                sb.Append(run);
            }
            if (sb.Length > maxChars) sb.Length = maxChars;
            return sb.ToString();
        }

        private static string NormalizeWhitespace(string input)
        {
            var sb = new StringBuilder(input.Length);
            var inWhitespace = false;
            foreach (var ch in input)
            {
                if (char.IsWhiteSpace(ch))
                {
                    if (inWhitespace) continue;
                    sb.Append(' ');
                    inWhitespace = true;
                }
                else
                {
                    sb.Append(ch);
                    inWhitespace = false;
                }
            }
            return sb.ToString().Trim();
        }

        /// <summary>The first JPEG (SOI…EOI) of plausible cover size embedded in the file.</summary>
        private static byte[]? TryExtractEmbeddedJpeg(byte[] bytes)
        {
            var maxScan = bytes.Length - 1;
            for (var i = 0; i < maxScan; i++)
            {
                if (bytes[i] != 0xFF || bytes[i + 1] != 0xD8) continue;
                for (var j = i + 2; j < maxScan; j++)
                {
                    if (bytes[j] != 0xFF || bytes[j + 1] != 0xD9) continue;
                    var length = j + 2 - i;
                    if (length < 8_000 || length > 12_000_000) continue;
                    var image = new byte[length];
                    Buffer.BlockCopy(bytes, i, image, 0, length);
                    return image;
                }
            }
            return null;
        }

        private static int IndexOf(byte[] bytes, byte[] pattern)
        {
            if (pattern.Length == 0 || bytes.Length < pattern.Length) return -1;
            for (var i = 0; i <= bytes.Length - pattern.Length; i++)
            {
                var matched = true;
                for (var j = 0; j < pattern.Length; j++)
                {
                    if (bytes[i + j] == pattern[j]) continue;
                    matched = false;
                    break;
                }
                if (matched) return i;
            }
            return -1;
        }

        private static int ReadUInt32BigEndian(byte[] bytes, int offset) =>
            offset + 4 > bytes.Length ? 0
                : (bytes[offset] << 24) | (bytes[offset + 1] << 16) | (bytes[offset + 2] << 8) | bytes[offset + 3];
    }
}
