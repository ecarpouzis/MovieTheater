using System.Collections.Concurrent;
using System.IO.Compression;
using Microsoft.Extensions.Logging;
using VersOne.Epub;
using VersOne.Epub.Options;
using VersOne.Epub.Schema;

namespace MovieTheater.Books.Archives
{
    public record EpubSpineItem(int Index, string Href, string Title);
    public record EpubResource(byte[] Content, string MimeType);
    public record EpubSpineInfo(IReadOnlyList<EpubSpineItem> Items, bool FixedLayout, string Direction);
    public record EpubTocEntry(string Label, int SpineIndex, string? Anchor, int Depth);

    /// <summary>
    /// The REFLOWABLE side of an EPUB: the spine, the table of contents, one chapter's HTML, and one resource's
    /// bytes — everything the iframe reader needs that an image-per-page reader cannot express.
    ///
    /// <para><b>Two facts decide how the client renders a book</b>, and both come from here:
    /// <c>FixedLayout</c> (EPUB 3 <c>rendition:layout = pre-paginated</c>, or a majority vote of the spine items'
    /// own layout properties — a comic EPUB where each spine document is one full-page image), and
    /// <c>Direction</c> (<c>rtl</c> for manga). A reflowable novel paginates via CSS columns; a fixed-layout book
    /// must not.</para>
    ///
    /// <para>Every parse is memoized per file — opening an EPUB parses the whole package, and the reader asks for
    /// the spine, the TOC and then a chapter at a time. The caches are unbounded by design and live for the
    /// process: they hold parsed structure, not page bitmaps, and the working set is the books being read.</para>
    /// </summary>
    public sealed class EpubReaderService
    {
        private readonly ILogger<EpubReaderService> logger;
        public EpubReaderService(ILogger<EpubReaderService> logger) => this.logger = logger;

        private readonly ConcurrentDictionary<string, EpubBook> bookCache = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, IReadOnlyList<EpubSpineItem>> spineCache = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, EpubSpineInfo> spineInfoCache = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, IReadOnlyList<EpubTocEntry>> tocCache = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<(string FilePath, int SpineIndex), string> chapterCache = new();
        private readonly ConcurrentDictionary<(string FilePath, string Href), EpubResource> resourceCache = new();

        public Task<IReadOnlyList<EpubSpineItem>> GetSpineAsync(string filePath)
        {
            var normalizedPath = Path.GetFullPath(filePath);
            if (spineCache.TryGetValue(normalizedPath, out var cached)) return Task.FromResult(cached);

            var book = GetBook(normalizedPath);
            var items = new List<EpubSpineItem>(book.ReadingOrder.Count);
            for (var i = 0; i < book.ReadingOrder.Count; i++)
            {
                // The CONTAINER-relative path, not the OPF-relative key: it is what the nav points at and what a
                // resource lookup resolves against, so the two halves of the reader speak the same paths.
                var doc = book.ReadingOrder[i];
                var href = NormalizeHref(doc.FilePath);
                if (href.Length == 0) href = NormalizeHref(doc.Key);
                var title = Path.GetFileNameWithoutExtension(href);
                items.Add(new EpubSpineItem(i, href, string.IsNullOrEmpty(title) ? $"Chapter {i + 1}" : title));
            }

            var result = (IReadOnlyList<EpubSpineItem>)items.AsReadOnly();
            spineCache[normalizedPath] = result;
            return Task.FromResult(result);
        }

        /// <summary>Spine plus the two rendering-mode facts. See the type remarks.</summary>
        public async Task<EpubSpineInfo> GetSpineInfoAsync(string filePath)
        {
            var normalizedPath = Path.GetFullPath(filePath);
            if (spineInfoCache.TryGetValue(normalizedPath, out var cachedInfo)) return cachedInfo;

            var items = await GetSpineAsync(normalizedPath);
            var book = GetBook(normalizedPath);

            var metaLayout = book.Schema.Package.Metadata.MetaItems?
                .FirstOrDefault(m => string.Equals(m.Property, "rendition:layout", StringComparison.OrdinalIgnoreCase))?
                .Content;
            var fixedLayout = string.Equals(metaLayout?.Trim(), "pre-paginated", StringComparison.OrdinalIgnoreCase);

            // Some books set the layout only on the spine refs, so fall back to a per-item vote.
            if (!fixedLayout)
            {
                var spineItems = book.Schema.Package.Spine.Items;
                if (spineItems is { Count: > 0 })
                {
                    var prePaginated = spineItems.Count(it =>
                        it.Properties != null && it.Properties.Contains(EpubSpineProperty.LAYOUT_PRE_PAGINATED));
                    if (prePaginated > 0 && prePaginated * 2 >= spineItems.Count) fixedLayout = true;
                }
            }

            var dir = book.Schema.Package.Spine.PageProgressionDirection?.ToString().ToUpperInvariant() ?? string.Empty;
            var info = new EpubSpineInfo(items, fixedLayout, dir.Contains("RIGHT") ? "rtl" : "ltr");
            spineInfoCache[normalizedPath] = info;
            return info;
        }

        /// <summary>
        /// The flattened table of contents (EPUB 3 nav or EPUB 2 NCX, whichever the parser resolved), each entry
        /// mapped to the SPINE INDEX of its target document so the reader can jump straight to it. An entry whose
        /// target is not in the reading order gets <c>SpineIndex = -1</c> and stays in the list as a heading.
        /// </summary>
        public Task<IReadOnlyList<EpubTocEntry>> GetTocAsync(string filePath)
        {
            var normalizedPath = Path.GetFullPath(filePath);
            if (tocCache.TryGetValue(normalizedPath, out var cached)) return Task.FromResult(cached);

            var book = GetBook(normalizedPath);

            // A spine document is indexed under EVERY name it answers to, because the two halves of the package
            // do not agree: a spine entry's Key is OPF-RELATIVE ("ch1.xhtml") while a nav link's target is
            // CONTAINER-RELATIVE ("OEBPS/ch1.xhtml"). Matching only one of them means every TOC entry in the
            // (overwhelmingly common) layout where the OPF sits in a subfolder resolves to -1 and the whole table
            // of contents becomes unclickable. The leaf name is the last resort, taken only when it is
            // unambiguous — two different chapters may not be collapsed onto one index.
            var hrefToIndex = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < book.ReadingOrder.Count; i++)
            {
                var doc = book.ReadingOrder[i];
                foreach (var name in new[] { NormalizeHref(doc.FilePath), NormalizeHref(doc.Key) })
                    if (name.Length > 0) hrefToIndex.TryAdd(name, i);
            }
            var leafToIndex = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < book.ReadingOrder.Count; i++)
            {
                var leaf = Path.GetFileName(NormalizeHref(book.ReadingOrder[i].FilePath));
                if (leaf.Length == 0) leaf = Path.GetFileName(NormalizeHref(book.ReadingOrder[i].Key));
                if (leaf.Length == 0) continue;
                if (leafToIndex.ContainsKey(leaf)) leafToIndex[leaf] = -1;   // ambiguous ⇒ refuse it
                else leafToIndex[leaf] = i;
            }

            var result = new List<EpubTocEntry>();

            void Walk(IReadOnlyList<EpubNavigationItem>? navItems, int depth)
            {
                if (navItems == null) return;
                foreach (var item in navItems)
                {
                    var title = item.Title?.Trim();
                    if (!string.IsNullOrWhiteSpace(title))
                    {
                        var targetPath = NormalizeHref(item.Link?.ContentFilePath);
                        var spineIndex = -1;
                        if (targetPath.Length > 0)
                        {
                            if (hrefToIndex.TryGetValue(targetPath, out var idx)) spineIndex = idx;
                            else if (leafToIndex.TryGetValue(Path.GetFileName(targetPath), out var leafIdx)) spineIndex = leafIdx;
                        }
                        result.Add(new EpubTocEntry(title, spineIndex, item.Link?.Anchor, depth));
                    }
                    Walk(item.NestedItems, depth + 1);
                }
            }

            Walk(book.Navigation, 0);
            var readOnly = (IReadOnlyList<EpubTocEntry>)result;
            tocCache[normalizedPath] = readOnly;
            return Task.FromResult(readOnly);
        }

        public Task<string> GetChapterHtmlAsync(string filePath, int spineIndex)
        {
            var normalizedPath = Path.GetFullPath(filePath);
            if (chapterCache.TryGetValue((normalizedPath, spineIndex), out var cached)) return Task.FromResult(cached);

            var book = GetBook(normalizedPath);
            if (spineIndex < 0 || spineIndex >= book.ReadingOrder.Count) throw new ArgumentOutOfRangeException(nameof(spineIndex));

            var html = book.ReadingOrder[spineIndex].Content ?? string.Empty;
            chapterCache[(normalizedPath, spineIndex)] = html;
            return Task.FromResult(html);
        }

        /// <summary>
        /// One resource (image, font, stylesheet) by href, resolved relative to <paramref name="baseHref"/>.
        /// The lookup walks the typed content collections first and falls back to a RAW ZIP read, because a
        /// perfectly valid EPUB can store a resource the parser's collections do not surface.
        /// </summary>
        public Task<EpubResource?> GetResourceAsync(string filePath, string href, string? baseHref = null)
        {
            var normalizedPath = Path.GetFullPath(filePath);
            var normalized = NormalizeHref(ResolveHref(baseHref, href));
            if (string.IsNullOrWhiteSpace(normalized)) return Task.FromResult<EpubResource?>(null);

            if (resourceCache.TryGetValue((normalizedPath, normalized), out var cached)) return Task.FromResult<EpubResource?>(cached);

            var c = GetBook(normalizedPath).Content;
            var resource = TryByteCollection(c.Images, normalized)
                ?? TryByteCollection(c.Fonts, normalized)
                ?? TryByteCollection(c.Audio, normalized)
                ?? TryTextCollection(c.Html, normalized)
                ?? TryTextCollection(c.Css, normalized)
                ?? TryAllFiles(c.AllFiles, normalized)
                ?? TryReadResourceFromZip(normalizedPath, normalized);

            if (resource == null)
            {
                logger.LogWarning("EPUB resource not found in any collection: {Href}", normalized);
                return Task.FromResult<EpubResource?>(null);
            }

            resourceCache[(normalizedPath, normalized)] = resource;
            return Task.FromResult<EpubResource?>(resource);
        }

        // ── typed collection helpers ──────────────────────────────────────────────────────────────────────

        private static EpubResource? TryByteCollection(
            EpubContentCollection<EpubLocalByteContentFile, EpubRemoteByteContentFile> col, string normalized)
        {
            if (col.TryGetLocalFileByKey(normalized, out var f1) && f1?.Content is { Length: > 0 })
                return new EpubResource(f1.Content, f1.ContentMimeType ?? "application/octet-stream");
            if (col.TryGetLocalFileByFilePath(normalized, out var f2) && f2?.Content is { Length: > 0 })
                return new EpubResource(f2.Content, f2.ContentMimeType ?? "application/octet-stream");

            var m = FindInLocal(col.Local, normalized);
            return m?.Content is { Length: > 0 }
                ? new EpubResource(m.Content, m.ContentMimeType ?? "application/octet-stream")
                : null;
        }

        private static EpubResource? TryTextCollection(
            EpubContentCollection<EpubLocalTextContentFile, EpubRemoteTextContentFile> col, string normalized)
        {
            if (col.TryGetLocalFileByKey(normalized, out var f1) && !string.IsNullOrEmpty(f1?.Content))
                return TextRes(f1.Content!, f1.ContentMimeType);
            if (col.TryGetLocalFileByFilePath(normalized, out var f2) && !string.IsNullOrEmpty(f2?.Content))
                return TextRes(f2.Content!, f2.ContentMimeType);

            var m = FindInLocal(col.Local, normalized);
            return m != null && !string.IsNullOrEmpty(m.Content) ? TextRes(m.Content!, m.ContentMimeType) : null;
        }

        private static EpubResource? TryAllFiles(
            EpubContentCollection<EpubLocalContentFile, EpubRemoteContentFile> col, string normalized) =>
            FindInLocal(col.Local, normalized) switch
            {
                EpubLocalByteContentFile b when b.Content is { Length: > 0 } =>
                    new EpubResource(b.Content, b.ContentMimeType ?? "application/octet-stream"),
                EpubLocalTextContentFile t when !string.IsNullOrEmpty(t.Content) =>
                    TextRes(t.Content!, t.ContentMimeType),
                _ => null,
            };

        /// <summary>Exact key/path match, then a suffix match (the stored path carries an extra prefix segment),
        /// then a UNIQUE leaf match — ambiguity is refused rather than guessed.</summary>
        private static T? FindInLocal<T>(IReadOnlyCollection<T> local, string normalized) where T : EpubLocalContentFile
        {
            var exact = local.FirstOrDefault(f =>
                NormalizeHref(f.Key).Equals(normalized, StringComparison.OrdinalIgnoreCase)
                || NormalizeHref(f.FilePath).Equals(normalized, StringComparison.OrdinalIgnoreCase));
            if (exact != null) return exact;

            var suffix = "/" + normalized;
            var sfx = local.FirstOrDefault(f =>
                NormalizeHref(f.Key).EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
                || NormalizeHref(f.FilePath).EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
            if (sfx != null) return sfx;

            var leaf = Path.GetFileName(normalized);
            var ext = Path.GetExtension(normalized);
            if (string.IsNullOrWhiteSpace(leaf)) return null;

            var candidates = local.Where(f =>
            {
                var k = Path.GetFileName(NormalizeHref(f.Key));
                return k.Equals(leaf, StringComparison.OrdinalIgnoreCase)
                       && string.Equals(Path.GetExtension(k), ext, StringComparison.OrdinalIgnoreCase);
            }).ToList();
            return candidates.Count == 1 ? candidates[0] : null;
        }

        private static EpubResource TextRes(string content, string? mime) =>
            new(System.Text.Encoding.UTF8.GetBytes(content), mime ?? "text/plain; charset=utf-8");

        // ── zip fallback ──────────────────────────────────────────────────────────────────────────────────

        private EpubResource? TryReadResourceFromZip(string filePath, string normalizedHref)
        {
            try
            {
                using var zip = ZipFile.OpenRead(filePath);
                var entry = zip.Entries.FirstOrDefault(e =>
                    NormalizeHref(e.FullName).Equals(normalizedHref, StringComparison.OrdinalIgnoreCase));

                if (entry == null)
                {
                    var suffix = "/" + normalizedHref;
                    entry = zip.Entries.FirstOrDefault(e =>
                        NormalizeHref(e.FullName).EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
                }

                if (entry == null)
                {
                    var leaf = Path.GetFileName(normalizedHref);
                    var ext = Path.GetExtension(normalizedHref);
                    // The shortest path is the most canonical when several entries share a file name.
                    entry = zip.Entries.Where(e =>
                        {
                            var n = NormalizeHref(e.FullName);
                            return Path.GetFileName(n).Equals(leaf, StringComparison.OrdinalIgnoreCase)
                                   && string.Equals(Path.GetExtension(n), ext, StringComparison.OrdinalIgnoreCase);
                        })
                        .OrderBy(e => NormalizeHref(e.FullName).Length)
                        .FirstOrDefault();
                }

                if (entry == null || entry.Length <= 0) return null;

                using var s = entry.Open();
                using var ms = new MemoryStream();
                s.CopyTo(ms);
                var bytes = ms.ToArray();
                return bytes.Length == 0 ? null : new EpubResource(bytes, MimeFromExtension(Path.GetExtension(entry.FullName)));
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "EPUB zip fallback failed for {Href}.", normalizedHref);
                return null;
            }
        }

        // ── path helpers ──────────────────────────────────────────────────────────────────────────────────

        public static string ResolveHref(string? baseHref, string? href)
        {
            if (string.IsNullOrWhiteSpace(href)) return string.Empty;
            var value = href.Trim();
            // Absolute and scheme-relative references leave the book: pass them through untouched so the caller
            // (and the browser) treat them as external rather than as a path inside the container.
            if (value.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                || value.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                || value.StartsWith("data:", StringComparison.OrdinalIgnoreCase)
                || value.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase)
                || value.StartsWith("//", StringComparison.Ordinal))
                return value;
            if (value.StartsWith('/')) return NormalizeHref(value);

            var basePath = NormalizeHref(baseHref);
            var slash = basePath.LastIndexOf('/');
            var baseDir = slash >= 0 ? basePath[..(slash + 1)] : string.Empty;
            return NormalizeHref(baseDir + value);
        }

        /// <summary>
        /// A container-relative path: forward slashes, no fragment or query, no leading slash, and
        /// <c>.</c>/<c>..</c> segments RESOLVED — which is also what stops an href from escaping the book.
        /// </summary>
        public static string NormalizeHref(string? href)
        {
            if (string.IsNullOrWhiteSpace(href)) return string.Empty;
            var value = href.Replace('\\', '/');

            var hash = value.IndexOf('#');
            if (hash >= 0) value = value[..hash];
            var query = value.IndexOf('?');
            if (query >= 0) value = value[..query];
            if (value.StartsWith('/')) value = value[1..];

            var segments = new List<string>();
            foreach (var seg in Uri.UnescapeDataString(value).Split('/', StringSplitOptions.RemoveEmptyEntries))
            {
                if (seg == ".") continue;
                if (seg == "..") { if (segments.Count > 0) segments.RemoveAt(segments.Count - 1); continue; }
                segments.Add(seg);
            }
            return string.Join('/', segments);
        }

        public static string MimeFromExtension(string? ext) => (ext ?? string.Empty).ToLowerInvariant() switch
        {
            ".jpg" or ".jpeg" => "image/jpeg",
            ".png" => "image/png",
            ".gif" => "image/gif",
            ".webp" => "image/webp",
            ".svg" => "image/svg+xml",
            ".css" => "text/css; charset=utf-8",
            ".js" => "application/javascript",
            ".xhtml" or ".html" or ".htm" => "text/html; charset=utf-8",
            ".ttf" => "font/ttf",
            ".otf" => "font/otf",
            ".woff" => "font/woff",
            ".woff2" => "font/woff2",
            _ => "application/octet-stream",
        };

        // RELAXED: real-world EPUBs violate the spec constantly, and a strict parse would refuse books that read
        // perfectly well in every other reader.
        // The parser is annotated as possibly returning null; in practice it throws on an unparseable file
        // instead, and every caller here is already inside a try that treats a throw as "not an EPUB we can
        // read". The bang says that out loud rather than adding a null branch no input can reach.
        private EpubBook GetBook(string normalizedPath) =>
            bookCache.GetOrAdd(normalizedPath, static p => EpubReader.ReadBook(p, EpubReaderOptionsPreset.RELAXED)!);
    }
}
