using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using MovieTheater.Core;

namespace MovieTheater.Services.Jellyfin
{
    /// <summary>
    /// Thin client for the handful of Jellyfin endpoints the site uses
    /// (docs/streaming-plan.md §6). Auth (X-Emby-Token) and the ingress gate header
    /// (X-Tunnel-Key) are attached to the HttpClient at registration.
    /// </summary>
    public class JellyfinApi
    {
        private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

        private readonly HttpClient httpClient;
        private readonly JellyfinApiOptions options;

        public JellyfinApi(HttpClient httpClient, IOptions<JellyfinApiOptions> options)
        {
            this.httpClient = httpClient;
            this.options = options.Value;
        }

        private void EnsureConfigured()
        {
            if (string.IsNullOrEmpty(options.BaseUrl))
                throw new BusinessException("JellyfinBaseUrl is not configured.");
            if (string.IsNullOrEmpty(options.ApiKey))
                throw new BusinessException("JellyfinApiKey is not configured.");
        }

        /// <summary>Server name/version — cheap connectivity check and version pin for the sync report.</summary>
        public async Task<JellyfinSystemInfo> GetSystemInfoAsync(CancellationToken cancel = default)
        {
            EnsureConfigured();
            var info = await httpClient.GetFromJsonAsync<JellyfinSystemInfo>("/System/Info", JsonOptions, cancel);
            return info ?? throw new BusinessException("Jellyfin returned an empty /System/Info response.");
        }

        private string? cachedUserId;

        /// <summary>
        /// Jellyfin's PlaybackInfo wants a user context even under API-key auth; any
        /// user works for our purposes, so the first one is fetched once and cached.
        /// </summary>
        public async Task<string> GetUserIdAsync(CancellationToken cancel = default)
        {
            if (cachedUserId != null)
                return cachedUserId;
            EnsureConfigured();
            var users = await httpClient.GetFromJsonAsync<List<JsonElement>>("/Users", JsonOptions, cancel)
                ?? throw new BusinessException("Jellyfin returned no users.");
            cachedUserId = users.Count > 0 && users[0].TryGetProperty("Id", out var id)
                ? id.GetString()
                : throw new BusinessException("Jellyfin returned no users.");
            return cachedUserId!;
        }

        /// <summary>
        /// Asks Jellyfin how to play an item under the site's fixed web profile: HLS out,
        /// H.264+AAC always allowed, copy when compatible, text subs delivered as sidecar
        /// WebVTT, image subs burned in. Direct play/stream are disabled so the answer is
        /// always an HLS TranscodingUrl — "direct stream" then means ffmpeg copies the
        /// streams into HLS containers without re-encoding.
        /// </summary>
        public async Task<JellyfinPlaybackInfoResult> GetPlaybackInfoAsync(
            string itemId, long? maxStreamingBitrate, int? audioStreamIndex, int? subtitleStreamIndex,
            long startTimeTicks, CancellationToken cancel = default)
        {
            EnsureConfigured();
            var userId = await GetUserIdAsync(cancel);

            var body = new
            {
                UserId = userId,
                MaxStreamingBitrate = maxStreamingBitrate ?? 1_000_000_000L,
                StartTimeTicks = startTimeTicks,
                AudioStreamIndex = audioStreamIndex,
                SubtitleStreamIndex = subtitleStreamIndex,
                EnableDirectPlay = false,
                EnableDirectStream = false,
                EnableTranscoding = true,
                AutoOpenLiveStream = true,
                DeviceProfile = BuildWebDeviceProfile(maxStreamingBitrate),
            };

            var response = await httpClient.PostAsJsonAsync(
                $"/Items/{Uri.EscapeDataString(itemId)}/PlaybackInfo?userId={Uri.EscapeDataString(userId)}", body, JsonOptions, cancel);
            response.EnsureSuccessStatusCode();
            var result = await response.Content.ReadFromJsonAsync<JellyfinPlaybackInfoResult>(JsonOptions, cancel);
            if (result == null || result.MediaSources.Count == 0)
                throw new BusinessException("Jellyfin returned no playable media source.");
            return result;
        }

        /// <summary>Progress report — keeps Jellyfin's transcode throttling honest.</summary>
        public Task ReportPlaybackProgressAsync(string itemId, string playSessionId, long positionTicks, bool isPaused, CancellationToken cancel = default)
        {
            EnsureConfigured();
            return httpClient.PostAsJsonAsync("/Sessions/Playing/Progress", new
            {
                ItemId = itemId,
                PlaySessionId = playSessionId,
                PositionTicks = positionTicks,
                IsPaused = isPaused,
            }, JsonOptions, cancel);
        }

        public Task ReportPlaybackStoppedAsync(string itemId, string playSessionId, long positionTicks, CancellationToken cancel = default)
        {
            EnsureConfigured();
            return httpClient.PostAsJsonAsync("/Sessions/Playing/Stopped", new
            {
                ItemId = itemId,
                PlaySessionId = playSessionId,
                PositionTicks = positionTicks,
            }, JsonOptions, cancel);
        }

        /// <summary>Kills the ffmpeg process and cleans segments immediately instead of waiting for the idle timeout.</summary>
        public Task StopActiveEncodingsAsync(string playSessionId, CancellationToken cancel = default)
        {
            EnsureConfigured();
            return httpClient.DeleteAsync(
                $"/Videos/ActiveEncodings?deviceId={Uri.EscapeDataString(DeviceId)}&playSessionId={Uri.EscapeDataString(playSessionId)}", cancel);
        }

        /// <summary>Active sessions with a video transcode running — the concurrency-guard input.</summary>
        public async Task<int> GetActiveTranscodeCountAsync(CancellationToken cancel = default)
        {
            EnsureConfigured();
            var sessions = await httpClient.GetFromJsonAsync<List<JellyfinSession>>("/Sessions", JsonOptions, cancel) ?? new();
            return sessions.Count(s => s.TranscodingInfo != null && s.NowPlayingItem != null);
        }

        public const string DeviceId = "movietheater-site";

        private static object BuildWebDeviceProfile(long? maxStreamingBitrate) => new
        {
            Name = "MovieTheaterWeb",
            MaxStreamingBitrate = maxStreamingBitrate ?? 1_000_000_000L,
            TranscodingProfiles = new object[]
            {
                new
                {
                    Container = "ts",
                    Type = "Video",
                    VideoCodec = "h264",
                    AudioCodec = "aac,mp3",
                    Protocol = "hls",
                    Context = "Streaming",
                    MaxAudioChannels = "2",
                    MinSegments = 1,
                    BreakOnNonKeyFrames = true,
                },
                new { Container = "mp3", Type = "Audio", AudioCodec = "mp3", Protocol = "http", Context = "Streaming" },
            },
            DirectPlayProfiles = Array.Empty<object>(),
            CodecProfiles = new object[]
            {
                new
                {
                    Type = "Video",
                    Codec = "h264",
                    Conditions = new object[]
                    {
                        new { Condition = "LessThanEqual", Property = "VideoLevel", Value = "51", IsRequired = false },
                    },
                },
            },
            SubtitleProfiles = new object[]
            {
                // Text subs ride as sidecar WebVTT; image subs (PGS/VobSub) burn in.
                new { Format = "vtt", Method = "External" },
                new { Format = "srt", Method = "External" },
                new { Format = "ass", Method = "External" },
                new { Format = "ssa", Method = "External" },
                new { Format = "subrip", Method = "External" },
                new { Format = "pgssub", Method = "Encode" },
                new { Format = "dvdsub", Method = "Encode" },
                new { Format = "dvbsub", Method = "Encode" },
            },
        };

        /// <summary>
        /// Enumerates every movie item in Jellyfin's library with Path, MediaSources and
        /// ProviderIds, paging through the full set.
        /// </summary>
        public async Task<List<JellyfinItem>> GetAllMovieItemsAsync(CancellationToken cancel = default)
        {
            EnsureConfigured();
            var items = new List<JellyfinItem>();
            const int pageSize = 1000;

            while (true)
            {
                var url = $"/Items?IncludeItemTypes=Movie&Recursive=true&EnableImages=false" +
                          $"&Fields=Path,MediaSources,ProviderIds&StartIndex={items.Count}&Limit={pageSize}";
                var page = await httpClient.GetFromJsonAsync<JellyfinItemsResult>(url, JsonOptions, cancel)
                    ?? throw new BusinessException("Jellyfin returned an empty /Items response.");

                items.AddRange(page.Items);
                if (page.Items.Count == 0 || items.Count >= page.TotalRecordCount)
                    return items;
            }
        }
    }

    public class JellyfinApiOptions
    {
        public string? BaseUrl { get; set; }
        public string? ApiKey { get; set; }
        public string? TunnelKey { get; set; }
    }

    public class JellyfinSystemInfo
    {
        public string? ServerName { get; set; }
        public string? Version { get; set; }
    }

    public class JellyfinItemsResult
    {
        public List<JellyfinItem> Items { get; set; } = new();
        public int TotalRecordCount { get; set; }
    }

    public class JellyfinItem
    {
        public string Id { get; set; } = default!;
        public string? Name { get; set; }
        public string? Path { get; set; }
        public long? RunTimeTicks { get; set; }
        public Dictionary<string, string>? ProviderIds { get; set; }
        public List<JellyfinMediaSource>? MediaSources { get; set; }

        [JsonIgnore]
        public string? ImdbId =>
            ProviderIds != null && ProviderIds.TryGetValue("Imdb", out var id) && !string.IsNullOrWhiteSpace(id)
                ? id : null;
    }

    public class JellyfinMediaSource
    {
        public string? Container { get; set; }
        public long? Size { get; set; }
        public List<JellyfinMediaStream>? MediaStreams { get; set; }
    }

    public class JellyfinMediaStream
    {
        public string? Type { get; set; }
        public string? Codec { get; set; }
        public int? Width { get; set; }
        public int? Height { get; set; }
        public bool IsDefault { get; set; }
    }
}
