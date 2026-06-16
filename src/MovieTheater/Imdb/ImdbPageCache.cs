using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace MovieTheater.Imdb
{
    /// <summary>
    /// Local, gitignored cache of raw IMDB page HTML so each page is fetched once, ever, and future
    /// parser changes become offline re-parses with zero IMDB traffic (docs/metadata-enrichment-plan.md
    /// §5.4). Bytes are gzipped on disk under <c>data/imdb-cache/tt/&lt;shard&gt;/&lt;ttid&gt;/&lt;pageType&gt;.html.gz</c>
    /// with a small JSON meta sidecar (fetched time, status, hash, source url) acting as the index — no
    /// database, no new dependency. Lives entirely on the scrape machine; never enters git or prod.
    /// </summary>
    public sealed class ImdbPageCache
    {
        private readonly string root;

        /// <param name="root">Cache root; defaults to <c>data/imdb-cache</c> under the current directory (data/ is gitignored).</param>
        public ImdbPageCache(string root = null)
        {
            this.root = root ?? Path.Combine("data", "imdb-cache");
        }

        public string Root => root;

        public sealed class Entry
        {
            public string ImdbId { get; set; }
            public string PageType { get; set; }
            public DateTime FetchedUtc { get; set; }
            public int Status { get; set; }
            public string ContentHash { get; set; }
            public string SourceUrl { get; set; }
        }

        /// <summary>True when a cached copy exists, regardless of age.</summary>
        public bool Has(string imdbId, string pageType) => File.Exists(HtmlPath(imdbId, pageType));

        /// <summary>
        /// Returns cached HTML when present and (if <paramref name="maxAge"/> is given) not older than it.
        /// A null <paramref name="maxAge"/> means "any age is fine" — most IMDB facts don't change, so the
        /// default is to reuse forever; pass a TTL only for volatile fields like the live rating.
        /// </summary>
        public async Task<string> TryReadAsync(string imdbId, string pageType, TimeSpan? maxAge = null, CancellationToken ct = default)
        {
            var meta = TryReadMeta(imdbId, pageType);
            if (meta == null) return null;
            if (maxAge.HasValue && DateTime.UtcNow - meta.FetchedUtc > maxAge.Value) return null;

            var path = HtmlPath(imdbId, pageType);
            if (!File.Exists(path)) return null;

            await using var fs = File.OpenRead(path);
            await using var gz = new GZipStream(fs, CompressionMode.Decompress);
            using var reader = new StreamReader(gz, Encoding.UTF8);
            return await reader.ReadToEndAsync();
        }

        /// <summary>Writes (or overwrites) the gzipped HTML plus its meta sidecar.</summary>
        public async Task SaveAsync(string imdbId, string pageType, string html, string sourceUrl, int status, CancellationToken ct = default)
        {
            if (string.IsNullOrEmpty(html)) return;

            var dir = DirFor(imdbId);
            Directory.CreateDirectory(dir);

            var bytes = Encoding.UTF8.GetBytes(html);
            var tmp = HtmlPath(imdbId, pageType) + ".tmp";
            await using (var fs = File.Create(tmp))
            await using (var gz = new GZipStream(fs, CompressionLevel.Optimal))
                await gz.WriteAsync(bytes, ct);
            File.Move(tmp, HtmlPath(imdbId, pageType), overwrite: true);

            var meta = new Entry
            {
                ImdbId = imdbId,
                PageType = pageType,
                FetchedUtc = DateTime.UtcNow,
                Status = status,
                ContentHash = Convert.ToHexString(SHA256.HashData(bytes)),
                SourceUrl = sourceUrl,
            };
            await File.WriteAllTextAsync(MetaPath(imdbId, pageType),
                JsonSerializer.Serialize(meta, new JsonSerializerOptions { WriteIndented = false }), ct);
        }

        public Entry TryReadMeta(string imdbId, string pageType)
        {
            var path = MetaPath(imdbId, pageType);
            if (!File.Exists(path)) return null;
            try { return JsonSerializer.Deserialize<Entry>(File.ReadAllText(path)); }
            catch (JsonException) { return null; }
        }

        // ── Path layout ───────────────────────────────────────────────────
        // data/imdb-cache/tt/<2>/<2>/<ttid>/<pageType>.html.gz  (+ .meta.json)
        // Sharded by the first digits of the numeric id to keep directories small.

        private string DirFor(string imdbId)
        {
            var digits = new string((imdbId ?? "").Where(char.IsDigit).ToArray()).PadLeft(7, '0');
            return Path.Combine(root, "tt", digits.Substring(0, 2), digits.Substring(2, 2), imdbId);
        }

        private string HtmlPath(string imdbId, string pageType) => Path.Combine(DirFor(imdbId), Safe(pageType) + ".html.gz");
        private string MetaPath(string imdbId, string pageType) => Path.Combine(DirFor(imdbId), Safe(pageType) + ".meta.json");

        private static string Safe(string pageType) =>
            new string((pageType ?? "page").Select(c => char.IsLetterOrDigit(c) || c == '-' || c == '_' ? c : '-').ToArray());
    }
}
