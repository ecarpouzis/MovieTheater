using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Caching.Memory;
using VersOne.Epub;
using VersOne.Epub.Schema;

namespace MovieTheater.Books.Archives
{
    /// <summary>
    /// EPUB as a PAGE source — the image-extraction path used for covers and for fixed-layout comic EPUBs read
    /// through the canvas reader. The reflowable side of an EPUB (spine documents, TOC, chapter HTML, resource
    /// bytes) lives in <see cref="EpubReaderService"/>; this reader is what makes an EPUB answer the same
    /// <see cref="IArchiveReader"/> questions as a CBZ.
    ///
    /// <para><b>The cover is NOT spine page 0.</b> For a reflowable novel the first spine document is routinely a
    /// title page, a publisher logo or a low-res asset, which is the whole reason a book's card looked wrong.
    /// <see cref="GetCoverAsync"/> walks four candidate sources best-first and takes the first that
    /// <see cref="CoverImageAnalyzer.IsUsableCover"/> accepts, then falls back to a raw-zip read of the OPF
    /// manifest (which rescues covers VersOne does not surface, e.g. a root-level cover image), and only then to
    /// a generated jacket.</para>
    /// </summary>
    public sealed class EpubArchiveReader : IArchiveReader
    {
        private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(30);

        private readonly IMemoryCache cache;
        public EpubArchiveReader(IMemoryCache cache) => this.cache = cache;

        public bool CanHandle(string fileExtension) =>
            ".epub".Equals(fileExtension, StringComparison.OrdinalIgnoreCase);

        public Task<int> GetPageCountAsync(string filePath) => Task.FromResult(GetCachedImages(filePath).Count);

        public Task<IReadOnlyList<string>> GetPageNamesAsync(string filePath)
        {
            var count = GetCachedImages(filePath).Count;
            return Task.FromResult<IReadOnlyList<string>>(Enumerable.Range(1, count).Select(n => $"page {n}").ToList());
        }

        public Task<Stream> GetPageAsync(string filePath, int pageIndex)
        {
            var images = GetCachedImages(filePath);
            if (pageIndex < 0 || pageIndex >= images.Count) throw new ArgumentOutOfRangeException(nameof(pageIndex));
            return Task.FromResult<Stream>(new MemoryStream(images[pageIndex], writable: false));
        }

        public async Task<Stream> GetCoverAsync(string filePath)
        {
            EpubBook book;
            try { book = await EpubReader.ReadBookAsync(filePath); }
            catch
            {
                // VersOne could not parse the package at all. Some otherwise-readable EPUBs trip it up, so try a
                // raw-zip cover read before giving up.
                if (TryReadCoverFromZip(filePath) is { } rawCover && CoverImageAnalyzer.IsUsableCover(rawCover))
                    return new MemoryStream(rawCover, writable: false);
                return await DocumentPlaceholderRenderer.CreateCoverAsync(Path.GetFileName(filePath));
            }

            var imageMap = BuildImageMap(book);
            // Warm the page cache from the spine while the book is open, so GetPageAsync does not re-read the file.
            var spineImages = ExtractSpineImages(book, imageMap);
            PopulateCache(filePath, spineImages, imageMap);

            IEnumerable<byte[]> Candidates()
            {
                // 1. VersOne's resolved cover: <meta name="cover"> (EPUB 2) + properties="cover-image" (EPUB 3).
                if (book.CoverImage is { Length: > 0 } declared) yield return declared;
                // 2. an OPF manifest item flagged or named as the cover.
                if (FindCoverInManifest(book, imageMap) is { Length: > 0 } manifest) yield return manifest;
                // 3. the first real image in spine (reading) order.
                foreach (var img in spineImages) yield return img;
                // 4. any image in the manifest, as a last resort.
                foreach (var img in imageMap.Values) yield return img;
            }

            foreach (var bytes in Candidates())
                if (CoverImageAnalyzer.IsUsableCover(bytes))
                    return new MemoryStream(bytes, writable: false);

            if (TryReadCoverFromZip(filePath) is { } zipCover && CoverImageAnalyzer.IsUsableCover(zipCover))
                return new MemoryStream(zipCover, writable: false);

            // No usable artwork anywhere: typeset a jacket from the book's own metadata.
            var author = book.AuthorList is { Count: > 0 } authors ? string.Join(", ", authors) : book.Author;
            return await DocumentPlaceholderRenderer.CreateCoverAsync(book.Title, author, Path.GetFileName(filePath));
        }

        public Task<ArchiveMetadata?> ReadMetadataAsync(string filePath)
        {
            try
            {
                var book = EpubReader.ReadBook(filePath);
                var opf = book.Schema.Package.Metadata;
                var pageCount = GetCachedImages(filePath).Count;

                var writers = new List<string>();
                var pencillers = new List<string>();
                var colorists = new List<string>();
                var translators = new List<string>();
                var editors = new List<string>();

                foreach (var c in opf.Creators)
                    RouteContributor(c.Creator, c.Role, writers, pencillers, colorists, translators, editors, fallbackToWriters: true);
                foreach (var c in opf.Contributors)
                    RouteContributor(c.Contributor, c.Role, writers, pencillers, colorists, translators, editors, fallbackToWriters: false);

                var pubDate = (opf.Dates.FirstOrDefault(d => string.Equals(d.Event, "publication", StringComparison.OrdinalIgnoreCase))
                    ?? opf.Dates.FirstOrDefault())?.Date;
                var identifier = (opf.Identifiers.FirstOrDefault(i => string.Equals(i.Scheme, "isbn", StringComparison.OrdinalIgnoreCase))
                    ?? opf.Identifiers.FirstOrDefault())?.Identifier;
                var tags = opf.Subjects.Select(s => s.Subject).Where(s => !string.IsNullOrWhiteSpace(s)).ToList();

                return Task.FromResult<ArchiveMetadata?>(new ArchiveMetadata
                {
                    IssueTitle = string.IsNullOrWhiteSpace(book.Title) ? null : book.Title,
                    Description = book.Description,
                    Publisher = opf.Publishers.FirstOrDefault()?.Publisher,
                    Language = opf.Languages.FirstOrDefault()?.Language,
                    PublicationDate = pubDate,
                    Identifier = identifier,
                    Tags = tags.Count > 0 ? string.Join(", ", tags) : null,
                    Writers = writers.Count > 0 ? string.Join(", ", writers) : null,
                    Pencillers = pencillers.Count > 0 ? string.Join(", ", pencillers) : null,
                    Colorist = colorists.Count > 0 ? string.Join(", ", colorists) : null,
                    Translator = translators.Count > 0 ? string.Join(", ", translators) : null,
                    Editor = editors.Count > 0 ? string.Join(", ", editors) : null,
                    PageCount = pageCount,
                });
            }
            catch
            {
                return Task.FromResult<ArchiveMetadata?>(null);
            }
        }

        // ── image extraction ──────────────────────────────────────────────────────────────────────────────

        private static Dictionary<string, byte[]> BuildImageMap(EpubBook book)
        {
            var map = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
            if (book.Content?.Images?.Local == null) return map;
            foreach (var img in book.Content.Images.Local)
                if (img.Content is { Length: > 0 })
                    map[NormalizePath(img.FilePath)] = img.Content;
            return map;
        }

        /// <summary>
        /// The OPF manifest's cover, by three heuristics in priority order: the EPUB 3 <c>cover-image</c>
        /// property, a cover-ish item id, then a file name containing "cover".
        /// </summary>
        private static byte[]? FindCoverInManifest(EpubBook book, Dictionary<string, byte[]> imageMap)
        {
            var items = book.Schema?.Package?.Manifest?.Items;
            if (items == null) return null;

            byte[]? byProperties = null, byId = null, byName = null;
            foreach (var item in items)
            {
                var key = NormalizePath(item.Href);
                if (!imageMap.TryGetValue(key, out var bytes)) continue;

                if (byProperties is null && item.Properties?.Contains(EpubManifestProperty.COVER_IMAGE) == true)
                    byProperties = bytes;

                if (byId is null)
                {
                    // Any cover-ish id: the exact convention plus compound ids like "coverimagestandard" — but
                    // never an unrelated id that merely CONTAINS the substring (e.g. "discover").
                    var id = item.Id?.ToLowerInvariant();
                    if (id is not null && (id is "cover" or "cover-image" or "coverimage" or "cover_image" || id.StartsWith("cover")))
                        byId = bytes;
                }

                if (byName is null && item.MediaType?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) == true)
                {
                    var stem = Path.GetFileNameWithoutExtension(item.Href ?? "").ToLowerInvariant();
                    if (stem.Contains("cover")) byName = bytes;
                }
            }
            return byProperties ?? byId ?? byName;
        }

        /// <summary>Walk the spine in reading order, taking the image each document references.</summary>
        private static List<byte[]> ExtractSpineImages(EpubBook book, Dictionary<string, byte[]> imageMap)
        {
            var result = new List<byte[]>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (book.ReadingOrder == null) return result;

            foreach (var spineItem in book.ReadingOrder)
            {
                var src = ExtractFirstImageSrc(spineItem.Content ?? string.Empty);
                if (src == null) continue;
                var key = NormalizePath(ResolveRelative(spineItem.FilePath, src));
                if (!seen.Add(key)) continue;
                if (imageMap.TryGetValue(key, out var bytes)) result.Add(bytes);
            }
            return result;
        }

        private void PopulateCache(string filePath, List<byte[]> spineImages, Dictionary<string, byte[]> imageMap)
        {
            var cacheKey = CacheKey(filePath);
            if (cache.TryGetValue(cacheKey, out _)) return;
            var images = spineImages.Count > 0 ? spineImages : imageMap.Values.ToList();
            Store(cacheKey, images);
        }

        private List<byte[]> GetCachedImages(string filePath)
        {
            var key = CacheKey(filePath);
            if (cache.TryGetValue(key, out List<byte[]>? images) && images != null) return images;

            var book = EpubReader.ReadBook(filePath);
            var imgMap = BuildImageMap(book);
            images = ExtractSpineImages(book, imgMap);
            if (images.Count == 0) images = imgMap.Values.ToList();
            Store(key, images);
            return images;
        }

        private static string CacheKey(string filePath) => "books:epub:images:" + filePath;

        // The shared IMemoryCache runs with a SizeLimit (BooksOptions.CacheEntryLimit), so every entry must
        // declare a size or the Set throws. One extracted-image list = one unit, matching the browse caches.
        private void Store(string key, List<byte[]> images) =>
            cache.Set(key, images, new MemoryCacheEntryOptions { SlidingExpiration = CacheDuration, Size = 1 });

        private static readonly Regex ImgSrcRegex =
            new(@"<img\b[^>]*\bsrc\s*=\s*[""']([^""']+)[""']", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // Comic EPUBs routinely wrap the page image in an SVG rather than an <img>.
        private static readonly Regex SvgImageHrefRegex =
            new(@"<image\b[^>]*\b(?:xlink:)?href\s*=\s*[""']([^""']+)[""']", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static string? ExtractFirstImageSrc(string html)
        {
            var m = ImgSrcRegex.Match(html);
            if (m.Success) return m.Groups[1].Value.Trim();
            var s = SvgImageHrefRegex.Match(html);
            return s.Success ? s.Groups[1].Value.Trim() : null;
        }

        // ── raw-zip cover fallback ────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Read the cover straight out of the zip via the OPF manifest, bypassing VersOne. Used only when the
        /// normal path yields nothing usable; it rescues covers VersOne does not expose and files it cannot parse.
        /// </summary>
        private static byte[]? TryReadCoverFromZip(string filePath)
        {
            try
            {
                using var zip = ZipFile.OpenRead(filePath);
                var byPath = new Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);
                foreach (var e in zip.Entries) byPath[NormalizePath(e.FullName)] = e;

                if (!byPath.TryGetValue("meta-inf/container.xml", out var containerEntry)) return null;
                var opfHref = Regex.Match(ReadEntryText(containerEntry),
                    @"full-path\s*=\s*[""']([^""']+)[""']", RegexOptions.IgnoreCase).Groups[1].Value;
                if (string.IsNullOrWhiteSpace(opfHref)) return null;
                if (!byPath.TryGetValue(NormalizePath(opfHref), out var opfEntry)) return null;
                var opf = ReadEntryText(opfEntry);

                var items = new List<(string Id, string Href, string Media, string Props)>();
                foreach (Match m in Regex.Matches(opf, @"<item\b[^>]*?/?>", RegexOptions.IgnoreCase | RegexOptions.Singleline))
                {
                    var tag = m.Value;
                    items.Add((Attr(tag, "id"), Attr(tag, "href"), Attr(tag, "media-type").ToLowerInvariant(), Attr(tag, "properties").ToLowerInvariant()));
                }
                if (items.Count == 0) return null;

                bool IsImage((string Id, string Href, string Media, string Props) it) =>
                    it.Media.StartsWith("image/", StringComparison.Ordinal)
                    || Regex.IsMatch(it.Href, @"\.(jpe?g|png|gif|webp)$", RegexOptions.IgnoreCase);

                // Best-first, mirroring FindCoverInManifest: <meta name="cover">, then properties="cover-image",
                // then a cover-ish id/name, then the largest image (the cover is almost always the biggest asset).
                string? coverHref = null;
                var metaId = Regex.Match(opf,
                    @"<meta\b[^>]*\bname\s*=\s*[""']cover[""'][^>]*\bcontent\s*=\s*[""']([^""']+)[""']",
                    RegexOptions.IgnoreCase).Groups[1].Value;
                if (string.IsNullOrEmpty(metaId))
                    metaId = Regex.Match(opf,
                        @"<meta\b[^>]*\bcontent\s*=\s*[""']([^""']+)[""'][^>]*\bname\s*=\s*[""']cover[""']",
                        RegexOptions.IgnoreCase).Groups[1].Value;

                if (!string.IsNullOrEmpty(metaId))
                    coverHref = items.FirstOrDefault(i => i.Id == metaId && IsImage(i)).Href;
                coverHref = string.IsNullOrEmpty(coverHref) ? items.FirstOrDefault(i => i.Props.Contains("cover-image") && IsImage(i)).Href : coverHref;
                if (string.IsNullOrEmpty(coverHref))
                    coverHref = items.FirstOrDefault(i => IsImage(i) && LooksLikeCover(i.Id, i.Href)).Href;

                if (string.IsNullOrEmpty(coverHref))
                {
                    coverHref = items.Where(IsImage)
                        .Select(i => (i.Href, Entry: byPath.GetValueOrDefault(NormalizePath(ResolveRelative(opfHref, i.Href)))))
                        .Where(x => x.Entry != null)
                        .OrderByDescending(x => x.Entry!.Length)
                        .Select(x => x.Href)
                        .FirstOrDefault();
                }
                if (string.IsNullOrEmpty(coverHref)) return null;

                if (!byPath.TryGetValue(NormalizePath(ResolveRelative(opfHref, coverHref)), out var coverEntry)) return null;
                using var s = coverEntry.Open();
                using var ms = new MemoryStream();
                s.CopyTo(ms);
                return ms.ToArray();
            }
            catch
            {
                return null;
            }
        }

        private static bool LooksLikeCover(string id, string href)
        {
            if (id.ToLowerInvariant().StartsWith("cover", StringComparison.Ordinal)) return true;
            var stem = Path.GetFileNameWithoutExtension(href).ToLowerInvariant();
            return stem.Contains("cover") || stem.Contains("cvi");
        }

        private static string ReadEntryText(ZipArchiveEntry entry)
        {
            using var s = entry.Open();
            using var r = new StreamReader(s, Encoding.UTF8);
            return r.ReadToEnd();
        }

        private static string Attr(string tag, string name)
        {
            var m = Regex.Match(tag, name + @"\s*=\s*[""']([^""']*)[""']", RegexOptions.IgnoreCase);
            return m.Success ? m.Groups[1].Value : string.Empty;
        }

        // ── path helpers ──────────────────────────────────────────────────────────────────────────────────

        private static string NormalizePath(string? path)
        {
            if (string.IsNullOrWhiteSpace(path)) return string.Empty;
            var p = path.Replace('\\', '/');
            var q = p.IndexOf('?'); if (q >= 0) p = p[..q];
            var h = p.IndexOf('#'); if (h >= 0) p = p[..h];
            if (p.StartsWith('/')) p = p[1..];
            return p;
        }

        private static string ResolveRelative(string? basePath, string href)
        {
            var src = href.Replace('\\', '/');
            if (src.StartsWith('/') || src.StartsWith("http", StringComparison.OrdinalIgnoreCase)) return NormalizePath(src);

            var baseDir = string.Empty;
            if (!string.IsNullOrWhiteSpace(basePath))
            {
                var slash = basePath.Replace('\\', '/').LastIndexOf('/');
                if (slash >= 0) baseDir = basePath[..(slash + 1)];
            }

            var parts = new List<string>();
            foreach (var seg in (baseDir + src).Split('/', StringSplitOptions.RemoveEmptyEntries))
            {
                if (seg == ".") continue;
                if (seg == "..") { if (parts.Count > 0) parts.RemoveAt(parts.Count - 1); }
                else parts.Add(seg);
            }
            return string.Join('/', parts);
        }

        private static void RouteContributor(
            string name, string? role,
            List<string> writers, List<string> pencillers, List<string> colorists,
            List<string> translators, List<string> editors, bool fallbackToWriters)
        {
            switch (role?.ToLowerInvariant().Trim())
            {
                case "ill" or "illustrator": pencillers.Add(name); break;
                case "clr" or "colorist" or "colourist": colorists.Add(name); break;
                case "trl" or "translator": translators.Add(name); break;
                case "edt" or "editor": editors.Add(name); break;
                default:
                    // "aut"/"author"/no role, and anything unrecognized: a CREATOR is an author by default,
                    // a CONTRIBUTOR with an unknown role is not attributed at all.
                    if (fallbackToWriters) writers.Add(name);
                    break;
            }
        }
    }
}
