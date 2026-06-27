using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Options;

namespace MovieTheater.Services.OpenSubtitles
{
    /// <summary>
    /// Direct client for the OpenSubtitles.com REST API (api/v1). Replaces Jellyfin's OpenSubtitles
    /// plugin, whose shared API key is chronically rate-limited — its search returns nothing even with
    /// valid credentials. We search by the IMDb id our own DB holds (the Jellyfin items are
    /// metadata-less homevideos, so Jellyfin's RemoteSearch has nothing to match on), download the
    /// chosen file under the configured account's quota, and the caller attaches it to the Jellyfin
    /// item so it streams as a normal text subtitle. Needs its own Api-Key (register a consumer at
    /// opensubtitles.com/en/consumers); downloads additionally need the account login.
    /// </summary>
    public class OpenSubtitlesApi
    {
        private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

        private readonly HttpClient http;
        private readonly OpenSubtitlesOptions opts;

        private string? token;
        private DateTimeOffset tokenExpiresUtc;
        private readonly SemaphoreSlim loginLock = new(1, 1);

        public OpenSubtitlesApi(HttpClient http, IOptions<OpenSubtitlesOptions> options)
        {
            this.http = http;
            opts = options.Value;
        }

        /// <summary>True once an Api-Key is configured — otherwise the caller falls back to the (broken) plugin.</summary>
        public bool IsConfigured => !string.IsNullOrWhiteSpace(opts.ApiKey);

        // OpenSubtitles.com uses ISO-639-1 (2-letter); our picker offers 3-letter codes.
        private static readonly Dictionary<string, string> Lang3to2 = new(StringComparer.OrdinalIgnoreCase)
        {
            ["eng"] = "en", ["spa"] = "es", ["fre"] = "fr", ["ger"] = "de", ["ita"] = "it",
            ["por"] = "pt-BR", ["jpn"] = "ja", ["kor"] = "ko", ["chi"] = "zh-CN",
        };
        public static string ToTwoLetter(string? lang) =>
            string.IsNullOrWhiteSpace(lang) ? "en" : (Lang3to2.TryGetValue(lang, out var two) ? two : lang.ToLowerInvariant());

        /// <summary>Search by IMDb id (any "tt0001234" form — non-digits are stripped), filtered to one language.</summary>
        public async Task<List<OpenSubtitleResult>> SearchAsync(string? imdbId, string language, CancellationToken cancel = default)
        {
            var numeric = new string((imdbId ?? "").Where(char.IsDigit).ToArray()).TrimStart('0');
            if (numeric.Length == 0) return new();
            var lang = ToTwoLetter(language);

            using var resp = await http.GetAsync($"subtitles?imdb_id={numeric}&languages={Uri.EscapeDataString(lang)}", cancel);
            await ThrowIfError(resp, "search");
            var body = await resp.Content.ReadFromJsonAsync<OsSearchResponse>(Json, cancel);

            return (body?.Data ?? new())
                .Where(d => d.Attributes?.Files != null && d.Attributes.Files.Count > 0)
                .Select(d => new OpenSubtitleResult
                {
                    FileId = d.Attributes!.Files![0].FileId,
                    Name = !string.IsNullOrWhiteSpace(d.Attributes.Release) ? d.Attributes.Release! : (d.Attributes.Files[0].FileName ?? "subtitle"),
                    Language = d.Attributes.Language,
                    DownloadCount = d.Attributes.DownloadCount,
                    Rating = d.Attributes.Ratings,
                    HashMatch = d.Attributes.MovieHashMatch,
                    HearingImpaired = d.Attributes.HearingImpaired,
                    FromTrusted = d.Attributes.FromTrusted,
                    AiTranslated = d.Attributes.AiTranslated || d.Attributes.MachineTranslated,
                    Uploader = d.Attributes.Uploader?.Name,
                })
                .ToList();
        }

        /// <summary>Resolve a chosen file's download link and fetch its content (text subtitle). Uses the account's quota.</summary>
        public async Task<(string content, string fileName)> DownloadAsync(int fileId, CancellationToken cancel = default)
        {
            await EnsureLoginAsync(cancel);

            using var req = new HttpRequestMessage(HttpMethod.Post, "download")
            {
                Content = JsonContent.Create(new { file_id = fileId }, options: Json),
            };
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var resp = await http.SendAsync(req, cancel);
            await ThrowIfError(resp, "download");
            var dl = await resp.Content.ReadFromJsonAsync<OsDownloadResponse>(Json, cancel);
            if (string.IsNullOrWhiteSpace(dl?.Link))
                throw new InvalidOperationException(dl?.Message ?? "OpenSubtitles returned no download link (daily quota reached?).");

            // The link is an absolute CDN url; GetStringAsync ignores the client BaseAddress for it.
            var content = await http.GetStringAsync(dl!.Link, cancel);
            return (content, dl.FileName ?? "subtitle.srt");
        }

        private async Task EnsureLoginAsync(CancellationToken cancel)
        {
            if (token != null && tokenExpiresUtc > DateTimeOffset.UtcNow.AddMinutes(5)) return;
            if (string.IsNullOrWhiteSpace(opts.Username) || string.IsNullOrWhiteSpace(opts.Password))
                throw new InvalidOperationException("OpenSubtitles username/password aren't configured — downloads need an account for the quota.");

            await loginLock.WaitAsync(cancel);
            try
            {
                if (token != null && tokenExpiresUtc > DateTimeOffset.UtcNow.AddMinutes(5)) return;
                using var resp = await http.PostAsJsonAsync("login", new { username = opts.Username, password = opts.Password }, Json, cancel);
                await ThrowIfError(resp, "login");
                var login = await resp.Content.ReadFromJsonAsync<OsLoginResponse>(Json, cancel);
                token = login?.Token ?? throw new InvalidOperationException("OpenSubtitles login returned no token.");
                tokenExpiresUtc = DateTimeOffset.UtcNow.AddHours(23); // tokens last ~24h
            }
            finally { loginLock.Release(); }
        }

        // Surface the OpenSubtitles status + body on failure so a 401 (bad key), 406 (bad user-agent) or
        // 429 (rate limited) is self-explaining rather than a bare "request failed".
        private static async Task ThrowIfError(HttpResponseMessage resp, string what)
        {
            if (resp.IsSuccessStatusCode) return;
            var body = await resp.Content.ReadAsStringAsync();
            throw new InvalidOperationException($"OpenSubtitles {what} failed ({(int)resp.StatusCode}): {body}");
        }
    }

    /// <summary>A flattened search hit for the picker.</summary>
    public class OpenSubtitleResult
    {
        public int FileId { get; set; }
        public string Name { get; set; } = "";
        public string? Language { get; set; }
        public int? DownloadCount { get; set; }
        public decimal? Rating { get; set; }
        public bool HashMatch { get; set; }
        public bool HearingImpaired { get; set; }
        public bool FromTrusted { get; set; }
        public bool AiTranslated { get; set; }
        public string? Uploader { get; set; }
    }

    // ── wire DTOs (subset of the api/v1 responses) ──
    internal class OsSearchResponse { [JsonPropertyName("data")] public List<OsDatum>? Data { get; set; } }
    internal class OsDatum { [JsonPropertyName("attributes")] public OsAttributes? Attributes { get; set; } }
    internal class OsAttributes
    {
        [JsonPropertyName("language")] public string? Language { get; set; }
        [JsonPropertyName("download_count")] public int? DownloadCount { get; set; }
        [JsonPropertyName("ratings")] public decimal? Ratings { get; set; }
        [JsonPropertyName("hearing_impaired")] public bool HearingImpaired { get; set; }
        [JsonPropertyName("from_trusted")] public bool FromTrusted { get; set; }
        [JsonPropertyName("ai_translated")] public bool AiTranslated { get; set; }
        [JsonPropertyName("machine_translated")] public bool MachineTranslated { get; set; }
        [JsonPropertyName("moviehash_match")] public bool MovieHashMatch { get; set; }
        [JsonPropertyName("release")] public string? Release { get; set; }
        [JsonPropertyName("uploader")] public OsUploader? Uploader { get; set; }
        [JsonPropertyName("files")] public List<OsFile>? Files { get; set; }
    }
    internal class OsUploader { [JsonPropertyName("name")] public string? Name { get; set; } }
    internal class OsFile { [JsonPropertyName("file_id")] public int FileId { get; set; } [JsonPropertyName("file_name")] public string? FileName { get; set; } }
    internal class OsLoginResponse { [JsonPropertyName("token")] public string? Token { get; set; } [JsonPropertyName("base_url")] public string? BaseUrl { get; set; } }
    internal class OsDownloadResponse
    {
        [JsonPropertyName("link")] public string? Link { get; set; }
        [JsonPropertyName("file_name")] public string? FileName { get; set; }
        [JsonPropertyName("message")] public string? Message { get; set; }
        [JsonPropertyName("remaining")] public int? Remaining { get; set; }
    }
}
