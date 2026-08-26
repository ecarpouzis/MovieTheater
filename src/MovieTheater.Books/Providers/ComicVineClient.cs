using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using MovieTheater.Books.Db;

namespace MovieTheater.Books.Providers
{
    /// <summary>A ComicVine volume, as much of it as the catalog keeps.</summary>
    public sealed record CvVolumeDto(long Id, string? Name, int? StartYear, string? PublisherName,
        int? CountOfIssues, string? Deck, string? Description, string? ImageUrl, string? SiteDetailUrl);

    /// <summary>A ComicVine issue. The cover date is the reading order's best signal.</summary>
    public sealed record CvIssueDto(long Id, long VolumeId, string? Name, string? IssueNumber,
        string? CoverDate, string? StoreDate, string? Deck, string? Description, string? ImageUrl, string? SiteDetailUrl);

    /// <summary>
    /// The cache-first store for provider responses, in the LEGS file. Every scraper asks this first and the
    /// network second — which is why a re-scrape of an already-seen series costs no API budget at all, and why
    /// the 20,286 rows the migration carried were worth carrying.
    ///
    /// <para>It is the only part of the provider layer that a test needs to fake to run offline, so it is a
    /// class of its own with a plain connection rather than a context.</para>
    /// </summary>
    public sealed class ProviderCacheStore
    {
        private readonly string? legsPath;
        public ProviderCacheStore(string? legsPath) => this.legsPath = legsPath;

        public bool Enabled => legsPath != null && File.Exists(legsPath);

        public string? Get(Provider provider, string requestKey)
        {
            if (!Enabled) return null;
            using var conn = Open(readOnly: true);
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT ResponseJson FROM ProviderResponseCache WHERE Provider = $p AND RequestKey = $k";
            cmd.Parameters.AddWithValue("$p", (int)provider);
            cmd.Parameters.AddWithValue("$k", requestKey);
            var o = cmd.ExecuteScalar();
            return o is string s ? s : null;
        }

        public void Put(Provider provider, string requestKey, string json)
        {
            if (legsPath == null) return;
            using var conn = Open(readOnly: false);
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
INSERT INTO ProviderResponseCache (Provider, RequestKey, ResponseJson, FetchedAt) VALUES ($p, $k, $j, $t)
ON CONFLICT(Provider, RequestKey) DO UPDATE SET ResponseJson = excluded.ResponseJson, FetchedAt = excluded.FetchedAt";
            cmd.Parameters.AddWithValue("$p", (int)provider);
            cmd.Parameters.AddWithValue("$k", requestKey);
            cmd.Parameters.AddWithValue("$j", json);
            cmd.Parameters.AddWithValue("$t", DateTime.UtcNow.ToString("O"));
            cmd.ExecuteNonQuery();
        }

        private SqliteConnection Open(bool readOnly)
        {
            var conn = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = legsPath,
                Mode = readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWrite,
                Pooling = false,
            }.ToString());
            conn.Open();
            return conn;
        }
    }

    /// <summary>
    /// The ComicVine HTTP client.
    ///
    /// <para><b>Cache first, always.</b> Every call checks <see cref="ProviderCacheStore"/> before the wire
    /// and writes back into it after, so a re-run of a scrape is free and a test never needs the network.</para>
    ///
    /// <para><b>One rate gate per RESOURCE BUCKET.</b> ComicVine enforces ~200 requests an hour PER resource
    /// type, so search / volume / issue are independent pools; a single global lock would serialise pools that
    /// the API is happy to run in parallel. Within a bucket one request is in flight at a time and requests are
    /// spaced by <see cref="BucketInterval"/> (20 s against an 18 s limit — the margin is deliberate).</para>
    ///
    /// <para><b>The API key is plain configuration</b> (`Books:ComicVineApiKey`). The standalone kept a
    /// per-user DPAPI key vault and a controller to manage it; that concept is DELETED — one key belongs to the
    /// host, not to an account, and a secret in the host's own settings file is the same trust boundary every
    /// other secret here uses.</para>
    ///
    /// <para>With no key configured the client is simply OFF: every call answers from the cache or returns
    /// nothing. That is also how the tests run — no key, a fake handler, no socket.</para>
    /// </summary>
    public sealed class ComicVineClient
    {
        public const string BaseUrl = "https://comicvine.gamespot.com/api";

        /// <summary>200 requests/hour per bucket is ~18 s apart; 20 s is the safety margin.</summary>
        public static readonly TimeSpan DefaultBucketInterval = TimeSpan.FromSeconds(20);

        /// <summary>A transient 420/429 is retried in-client rather than persisted as an error on the link.</summary>
        public const int RateLimitMaxRetries = 3;

        private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new();
        private static readonly ConcurrentDictionary<string, DateTime> LastRelease = new();

        private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

        private readonly HttpClient http;
        private readonly ProviderCacheStore cache;
        private readonly string? apiKey;
        private readonly ILogger<ComicVineClient> logger;

        /// <summary>
        /// The spacing this instance enforces per bucket. It is an instance value rather than a constant so a
        /// host that negotiates a different quota — and a test that must not sit through 20 s of real delay —
        /// can set it. The DEFAULT is the safe one; nothing in production passes anything else.
        /// </summary>
        public TimeSpan BucketInterval { get; }

        public ComicVineClient(HttpClient http, ProviderCacheStore cache, string? apiKey, ILogger<ComicVineClient> logger, TimeSpan? bucketInterval = null)
        {
            this.http = http;
            this.cache = cache;
            this.apiKey = string.IsNullOrWhiteSpace(apiKey) ? null : apiKey.Trim();
            this.logger = logger;
            BucketInterval = bucketInterval ?? DefaultBucketInterval;
        }

        /// <summary>False when no key is configured: the scrapers then run cache-only and never touch the wire.</summary>
        public bool CanFetch => apiKey != null;

        /// <summary>How long a caller must wait before this bucket will accept another request.</summary>
        public static TimeSpan Delay(string bucket, DateTime now, TimeSpan? interval = null)
        {
            var window = interval ?? DefaultBucketInterval;
            if (!LastRelease.TryGetValue(bucket, out var last)) return TimeSpan.Zero;
            var elapsed = now - last;
            return elapsed >= window ? TimeSpan.Zero : window - elapsed;
        }

        /// <summary>The resource pool a URL belongs to. Must stay consistent between throttle and release.</summary>
        public static string BucketFor(string url) =>
            url.Contains("/search/", StringComparison.Ordinal) ? "search"
            : url.Contains("/volume", StringComparison.Ordinal) ? "volume"
            : url.Contains("/issue", StringComparison.Ordinal) ? "issue"
            : url.Contains("/publisher", StringComparison.Ordinal) ? "publisher"
            : "other";

        /// <summary>Search volumes by name, with the publisher and year folded into the query so the search
        /// engine can rank across all three rather than on the name alone.</summary>
        public async Task<List<CvVolumeDto>> SearchVolumesAsync(string query, string? publisher, int? year, CancellationToken ct = default)
        {
            var url = $"{BaseUrl}/search/?format=json&resources=volume&limit=20&query={Uri.EscapeDataString(BuildQuery(query, publisher, year))}" +
                      "&field_list=id,name,start_year,publisher,count_of_issues,deck,description,image,site_detail_url";
            var json = await GetAsync(url, VolumeSearchKey(query), ct);
            return json == null ? new List<CvVolumeDto>() : ParseVolumes(json);
        }

        public async Task<CvVolumeDto?> GetVolumeAsync(long volumeId, CancellationToken ct = default)
        {
            var url = $"{BaseUrl}/volume/4050-{volumeId}/?format=json" +
                      "&field_list=id,name,start_year,publisher,count_of_issues,deck,description,image,site_detail_url";
            var json = await GetAsync(url, $"volume:{volumeId}", ct);
            return json == null ? null : ParseVolumes(json).FirstOrDefault();
        }

        /// <summary>Every issue of a volume, one page of 100 at a time — the scrape's own bounded unit.</summary>
        public async Task<List<CvIssueDto>> GetVolumeIssuesAsync(long volumeId, int offset, CancellationToken ct = default)
        {
            var url = $"{BaseUrl}/issues/?format=json&limit=100&offset={offset}&filter=volume:{volumeId}" +
                      "&field_list=id,volume,name,issue_number,cover_date,store_date,deck,description,image,site_detail_url";
            var json = await GetAsync(url, $"volume-issues:{volumeId}:{offset}", ct);
            return json == null ? new List<CvIssueDto>() : ParseIssues(json, volumeId);
        }

        /// <summary>The normalized, publisher/year-independent cache key a volume search is seeded under.</summary>
        public static string VolumeSearchKey(string query) =>
            "volsearch:" + string.Join(' ', query.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries));

        private static string BuildQuery(string query, string? publisher, int? year)
        {
            var parts = new List<string> { query.Trim() };
            if (!string.IsNullOrWhiteSpace(publisher)) parts.Add(publisher.Trim());
            if (year is int y) parts.Add(y.ToString(System.Globalization.CultureInfo.InvariantCulture));
            return string.Join(' ', parts);
        }

        /// <summary>Cache, then (if a key is configured) the wire, then the cache again.</summary>
        private async Task<string?> GetAsync(string url, string cacheKey, CancellationToken ct)
        {
            var cached = cache.Get(Provider.Cv, cacheKey);
            if (cached != null) return cached;
            if (!CanFetch) return null;

            var bucket = BucketFor(url);
            var gate = Gates.GetOrAdd(bucket, _ => new SemaphoreSlim(1, 1));
            await gate.WaitAsync(ct);
            try
            {
                for (var attempt = 0; ; attempt++)
                {
                    var wait = Delay(bucket, DateTime.UtcNow, BucketInterval);
                    if (wait > TimeSpan.Zero) await Task.Delay(wait, ct);

                    using var request = new HttpRequestMessage(HttpMethod.Get, url + "&api_key=" + apiKey);
                    // ComicVine 403s a request with no User-Agent; the header is not optional.
                    request.Headers.TryAddWithoutValidation("User-Agent", "MovieTheater-Books/1.0");
                    using var response = await http.SendAsync(request, ct);
                    LastRelease[bucket] = DateTime.UtcNow;

                    if ((int)response.StatusCode is 420 or 429)
                    {
                        if (attempt >= RateLimitMaxRetries) { logger.LogWarning("comicvine: rate limited on {Bucket} after {N} retries", bucket, attempt); return null; }
                        await Task.Delay(BucketInterval, ct);
                        continue;
                    }
                    if (!response.IsSuccessStatusCode) { logger.LogWarning("comicvine: {Status} for {Bucket}", (int)response.StatusCode, bucket); return null; }

                    var body = await response.Content.ReadAsStringAsync(ct);
                    cache.Put(Provider.Cv, cacheKey, body);
                    return body;
                }
            }
            finally { gate.Release(); }
        }

        public static List<CvVolumeDto> ParseVolumes(string json)
        {
            var list = new List<CvVolumeDto>();
            using var doc = JsonDocument.Parse(json);
            foreach (var e in Results(doc.RootElement))
            {
                if (!e.TryGetProperty("id", out var idEl) || !idEl.TryGetInt64(out var id)) continue;
                list.Add(new CvVolumeDto(id,
                    Str(e, "name"),
                    Int(e, "start_year"),
                    e.TryGetProperty("publisher", out var pub) && pub.ValueKind == JsonValueKind.Object ? Str(pub, "name") : null,
                    Int(e, "count_of_issues"),
                    Str(e, "deck"), Str(e, "description"),
                    e.TryGetProperty("image", out var img) && img.ValueKind == JsonValueKind.Object ? Str(img, "medium_url") : null,
                    Str(e, "site_detail_url")));
            }
            return list;
        }

        public static List<CvIssueDto> ParseIssues(string json, long fallbackVolumeId)
        {
            var list = new List<CvIssueDto>();
            using var doc = JsonDocument.Parse(json);
            foreach (var e in Results(doc.RootElement))
            {
                if (!e.TryGetProperty("id", out var idEl) || !idEl.TryGetInt64(out var id)) continue;
                var volumeId = e.TryGetProperty("volume", out var v) && v.ValueKind == JsonValueKind.Object && v.TryGetProperty("id", out var vid) && vid.TryGetInt64(out var vv)
                    ? vv : fallbackVolumeId;
                list.Add(new CvIssueDto(id, volumeId,
                    Str(e, "name"), Str(e, "issue_number"), Str(e, "cover_date"), Str(e, "store_date"),
                    Str(e, "deck"), Str(e, "description"),
                    e.TryGetProperty("image", out var img) && img.ValueKind == JsonValueKind.Object ? Str(img, "medium_url") : null,
                    Str(e, "site_detail_url")));
            }
            return list;
        }

        private static IEnumerable<JsonElement> Results(JsonElement root)
        {
            if (!root.TryGetProperty("results", out var results)) yield break;
            if (results.ValueKind == JsonValueKind.Array) { foreach (var e in results.EnumerateArray()) yield return e; }
            else if (results.ValueKind == JsonValueKind.Object) yield return results;
        }

        private static string? Str(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

        private static int? Int(JsonElement e, string name)
        {
            if (!e.TryGetProperty(name, out var v)) return null;
            return v.ValueKind switch
            {
                JsonValueKind.Number when v.TryGetInt32(out var n) => n,
                JsonValueKind.String when int.TryParse(v.GetString(), out var s) => s,
                _ => null,
            };
        }

        /// <summary>
        /// The match score the standalone's heuristic uses: an exact normalized name is 100, a containment is
        /// 80, and the year and publisher each add a little. The `StoredTopScore` on a settled link is a
        /// snapshot of this, kept so the stale-match check can ask "would today's search still pick you?"
        /// without re-reading the candidate blob.
        /// </summary>
        public static int Score(string query, CvVolumeDto candidate, string? publisher, int? year)
        {
            var q = Norm(query);
            var n = Norm(candidate.Name ?? "");
            var score = q == n ? 100 : n.Contains(q, StringComparison.Ordinal) || q.Contains(n, StringComparison.Ordinal) ? 80 : 0;
            if (score == 0) return 0;
            if (year is int y && candidate.StartYear == y) score += 10;
            else if (year is int y2 && candidate.StartYear is int cy && Math.Abs(cy - y2) <= 1) score += 5;
            if (!string.IsNullOrWhiteSpace(publisher) && candidate.PublisherName != null
                && Norm(candidate.PublisherName).Contains(Norm(publisher), StringComparison.Ordinal)) score += 8;
            return Math.Min(100, score);
        }

        public static string Norm(string s) =>
            string.Join(' ', new string(s.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : ' ').ToArray())
                .Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }
}
