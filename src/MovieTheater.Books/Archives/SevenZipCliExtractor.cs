using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;

namespace MovieTheater.Books.Archives
{
    /// <summary>
    /// Last-resort extraction through the 7-Zip command line, for containers the managed readers cannot handle:
    /// ZIP entries compressed with the LZMA method (<c>System.IO.Compression</c> lists them and then throws), and
    /// RAR5 / quirky RARs that trip SharpCompress.
    ///
    /// <para>It runs only AFTER an in-process reader has already failed, so the common path pays nothing. With no
    /// 7z.exe configured or installed, <see cref="IsAvailable"/> is false and every method returns null — the
    /// readers then behave exactly as they would without this fallback.</para>
    ///
    /// <para>Entry ordering matches <see cref="ArchiveEntryOrder"/> exactly, so a page rescued by 7-Zip is the
    /// SAME page the managed reader would have served at that index.</para>
    /// </summary>
    public sealed class SevenZipCliExtractor
    {
        private readonly ILogger<SevenZipCliExtractor> logger;
        private readonly string? exePath;

        public SevenZipCliExtractor(BooksOptions options, ILogger<SevenZipCliExtractor> logger)
        {
            this.logger = logger;
            exePath = ResolveExe(options.SevenZipPath);
            logger.LogInformation(exePath != null
                ? "7-Zip CLI fallback enabled."
                : "7-Zip CLI fallback disabled (7z.exe not found; set Books:SevenZipPath to enable).");
        }

        /// <summary>True when a 7z.exe was located; when false every extraction method returns null.</summary>
        public bool IsAvailable => exePath != null;

        private static string? ResolveExe(string? configured)
        {
            if (!string.IsNullOrWhiteSpace(configured))
                return File.Exists(configured) ? configured : null;

            string[] candidates =
            [
                @"C:\Program Files\7-Zip\7z.exe",
                @"C:\Program Files (x86)\7-Zip\7z.exe",
            ];
            return candidates.FirstOrDefault(File.Exists);
        }

        /// <summary>The image at <paramref name="pageIndex"/> in page order, or null.</summary>
        public async Task<Stream?> ExtractImageAtAsync(string filePath, int pageIndex, CancellationToken ct = default)
        {
            if (exePath == null) return null;
            var entries = await ListEntriesAsync(filePath, ct);
            if (entries == null) return null;

            var images = ArchiveEntryOrder.InPageOrder(entries.Where(ArchiveEntryOrder.IsImage), p => p).ToList();
            if (pageIndex < 0 || pageIndex >= images.Count) return null;
            return await ExtractEntryAsync(filePath, images[pageIndex], ct);
        }

        /// <summary>The image entry names in page order, or null when unavailable / unlistable.</summary>
        public async Task<List<string>?> ListImagesAsync(string filePath, CancellationToken ct = default)
        {
            if (exePath == null) return null;
            var entries = await ListEntriesAsync(filePath, ct);
            return entries == null
                ? null
                : ArchiveEntryOrder.InPageOrder(entries.Where(ArchiveEntryOrder.IsImage), p => p).ToList();
        }

        /// <summary>Number of image entries 7-Zip can see, or null.</summary>
        public async Task<int?> CountImagesAsync(string filePath, CancellationToken ct = default) =>
            (await ListImagesAsync(filePath, ct))?.Count;

        /// <summary>The first entry whose file NAME matches (e.g. "ComicInfo.xml"), or null.</summary>
        public async Task<Stream?> ExtractNamedEntryAsync(string filePath, string fileName, CancellationToken ct = default)
        {
            if (exePath == null) return null;
            var entries = await ListEntriesAsync(filePath, ct);
            var match = entries?.FirstOrDefault(p =>
                string.Equals(Path.GetFileName(p), fileName, StringComparison.OrdinalIgnoreCase));
            return match == null ? null : await ExtractEntryAsync(filePath, match, ct);
        }

        /// <summary>
        /// Every non-folder entry path. <c>l -slt</c> prints a "Path = …" line per entry after a "----------"
        /// separator (the header above it carries a Path line for the archive itself, which the gate skips).
        /// </summary>
        private async Task<List<string>?> ListEntriesAsync(string filePath, CancellationToken ct)
        {
            // The exit code is ignored on purpose: 7-Zip returns 2 ("Headers Error") for an archive with one
            // corrupt header — a truncated ZIP missing its central directory, a RAR SharpCompress also rejected —
            // and still lists every entry it CAN read from the local headers. Those are exactly the files being
            // rescued. A genuinely unopenable file simply prints no entry lines.
            var stdout = await RunTextAsync(["l", "-slt", "-sccUTF-8", "--", filePath], ct);

            var paths = new List<string>();
            var pastHeader = false;
            foreach (var raw in stdout.Split('\n'))
            {
                var line = raw.TrimEnd('\r');
                if (!pastHeader)
                {
                    if (line.StartsWith("----------", StringComparison.Ordinal)) pastHeader = true;
                    continue;
                }
                if (line.StartsWith("Path = ", StringComparison.Ordinal)) paths.Add(line["Path = ".Length..]);
            }
            return paths;
        }

        private async Task<Stream?> ExtractEntryAsync(string filePath, string entryPath, CancellationToken ct)
        {
            // `x -so` streams the entry to stdout; `-spd` disables wildcard matching so literal names containing
            // [ ] * ? (common in scanned comic filenames) match exactly. The exit code is ignored here too:
            // 7-Zip can stream a perfectly good entry while exiting non-zero because some OTHER header is bad.
            // We trust the bytes if any were produced; the caller decodes them, which rejects garbage.
            var ms = new MemoryStream();
            await RunBinaryAsync(["x", "-so", "-spd", "--", filePath, entryPath], ms, ct);
            if (ms.Length == 0)
            {
                await ms.DisposeAsync();
                return null;
            }
            ms.Position = 0;
            return ms;
        }

        private ProcessStartInfo BaseStartInfo(IEnumerable<string> args)
        {
            var psi = new ProcessStartInfo
            {
                FileName = exePath!,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            foreach (var a in args) psi.ArgumentList.Add(a);
            return psi;
        }

        private async Task<string> RunTextAsync(string[] args, CancellationToken ct)
        {
            var psi = BaseStartInfo(args);
            psi.StandardOutputEncoding = Encoding.UTF8;
            psi.StandardErrorEncoding = Encoding.UTF8;
            try
            {
                using var proc = Process.Start(psi)!;
                var outTask = proc.StandardOutput.ReadToEndAsync(ct);
                var errTask = proc.StandardError.ReadToEndAsync(ct);
                await proc.WaitForExitAsync(ct);
                await errTask;
                return await outTask;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "7-Zip listing failed.");
                return "";
            }
        }

        private async Task RunBinaryAsync(string[] args, Stream destination, CancellationToken ct)
        {
            var psi = BaseStartInfo(args);
            try
            {
                using var proc = Process.Start(psi)!;
                // Drain stderr concurrently: a full pipe buffer would otherwise deadlock the stdout copy.
                var errTask = proc.StandardError.ReadToEndAsync(ct);
                await proc.StandardOutput.BaseStream.CopyToAsync(destination, ct);
                await errTask;
                await proc.WaitForExitAsync(ct);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "7-Zip extraction failed.");
            }
        }
    }
}
