using System;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace MovieTheater.Music
{
    /// <summary>
    /// Local, gitignored cache of RAW external metadata responses (R9 S10) — the music twin of
    /// <see cref="MovieTheater.Imdb.ImdbPageCache"/>, and deliberately the same shape: gzipped bytes
    /// under <c>data/music-cache/&lt;source&gt;/&lt;shard&gt;/&lt;key&gt;.json.gz</c> with a small JSON meta
    /// sidecar (fetched time, status, hash, source url) acting as the index. No database, no new
    /// dependency, never in git, never in prod.
    /// </summary>
    /// <remarks>
    /// <para><b>Why the raw bytes and not just the parsed number.</b> MusicBrainz asks callers to make
    /// one request a second, so a full pass over 2,900 albums is ~50 minutes of somebody else's
    /// server. Every future change to what we EXTRACT from those answers — a different tag threshold,
    /// a field we did not think to read — has to be an offline re-parse or it is another 50 minutes,
    /// and the second time it is 50 minutes we did not have to spend. That is the exact argument the
    /// IMDb cache was built on, and it applies here for the same reason.</para>
    /// <para>The key is a caller-supplied string (a MusicBrainz release-group query, a Last.fm
    /// artist/album pair) hashed to a filename, so a key can contain anything a URL can. The hash is
    /// stored in the sidecar next to the key it came from, so the cache is greppable.</para>
    /// </remarks>
    public sealed class MusicResponseCache
    {
        private readonly string root;

        /// <param name="root">Cache root; defaults to <c>data/music-cache</c> (data/ is gitignored).</param>
        /// <remarks>
        /// The default is resolved by walking UP for the repo's <c>data/</c> directory rather than
        /// taken relative to the working directory. `dotnet MovieTheater.dll` is run from wherever
        /// the CLI happens to be staged, and a cache that lands beside the binary is a cache that
        /// silently re-fetches everything the next time it is staged somewhere else — the arcade
        /// CLIs' <see cref="MovieTheater.Arcade.RepoDataPath"/> lesson, which cost three separate
        /// silent failures before it was written down.
        /// </remarks>
        public MusicResponseCache(string? root = null)
        {
            this.root = root ?? Path.Combine(Arcade.RepoDataPath.Resolve("data"), "music-cache");
        }

        public string Root => root;

        public sealed class Entry
        {
            public string Source { get; set; } = "";
            public string Key { get; set; } = "";
            public DateTime FetchedUtc { get; set; }
            public int Status { get; set; }
            public string ContentHash { get; set; } = "";
            public string SourceUrl { get; set; } = "";
        }

        public bool Has(string source, string key) => File.Exists(BodyPath(source, key));

        /// <summary>
        /// The cached body when present and (if <paramref name="maxAge"/> is given) not older than it.
        /// Null <paramref name="maxAge"/> means "any age is fine". Popularity DOES drift, so the
        /// caller passes a TTL for that leg and nothing for the tag lists, which do not.
        /// </summary>
        public async Task<string?> TryReadAsync(string source, string key, TimeSpan? maxAge = null, CancellationToken ct = default)
        {
            var meta = TryReadMeta(source, key);
            if (meta == null) return null;
            if (maxAge.HasValue && DateTime.UtcNow - meta.FetchedUtc > maxAge.Value) return null;

            var path = BodyPath(source, key);
            if (!File.Exists(path)) return null;

            await using var fs = File.OpenRead(path);
            await using var gz = new GZipStream(fs, CompressionMode.Decompress);
            using var reader = new StreamReader(gz, Encoding.UTF8);
            return await reader.ReadToEndAsync(ct);
        }

        /// <summary>Writes (or overwrites) the gzipped body plus its meta sidecar.</summary>
        public async Task SaveAsync(string source, string key, string body, string sourceUrl, int status, CancellationToken ct = default)
        {
            if (string.IsNullOrEmpty(body)) return;

            Directory.CreateDirectory(DirFor(source, key));

            var bytes = Encoding.UTF8.GetBytes(body);
            var tmp = BodyPath(source, key) + ".tmp";
            await using (var fs = File.Create(tmp))
            await using (var gz = new GZipStream(fs, CompressionLevel.Optimal))
                await gz.WriteAsync(bytes, ct);
            File.Move(tmp, BodyPath(source, key), overwrite: true);

            var meta = new Entry
            {
                Source = source,
                Key = key,
                FetchedUtc = DateTime.UtcNow,
                Status = status,
                ContentHash = Convert.ToHexString(SHA256.HashData(bytes)),
                SourceUrl = sourceUrl,
            };
            await File.WriteAllTextAsync(MetaPath(source, key), JsonSerializer.Serialize(meta), ct);
        }

        private Entry? TryReadMeta(string source, string key)
        {
            var path = MetaPath(source, key);
            if (!File.Exists(path)) return null;
            try { return JsonSerializer.Deserialize<Entry>(File.ReadAllText(path)); }
            catch { return null; }
        }

        /// <summary>Two levels of 2 hex characters — 256 directories at each level, the IMDb cache's
        /// fan-out, so no directory ever holds tens of thousands of files.</summary>
        internal static string Shard(string key)
        {
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))).ToLowerInvariant();
            return Path.Combine(hash.Substring(0, 2), hash.Substring(2, 2), hash);
        }

        private string DirFor(string source, string key) =>
            Path.GetDirectoryName(Path.Combine(root, Safe(source), Shard(key)))!;

        private string BodyPath(string source, string key) =>
            Path.Combine(root, Safe(source), Shard(key)) + ".json.gz";

        private string MetaPath(string source, string key) =>
            Path.Combine(root, Safe(source), Shard(key)) + ".meta.json";

        private static string Safe(string s)
        {
            foreach (var c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
            return s;
        }
    }
}
