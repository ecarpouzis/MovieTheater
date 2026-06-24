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
        /// Asks Jellyfin how to play an item under a web profile built from the calling
        /// client's real codec capabilities (streaming-plan.md §14.1): HLS out, fMP4
        /// segments when the client can take them, HEVC/AV1 copied or HEVC-encoded for
        /// capable clients, H.264+AAC the universal fallback, text subs delivered as
        /// sidecar WebVTT, image subs burned in. Direct play/stream are disabled so the
        /// answer is always an HLS TranscodingUrl — "direct stream" then means ffmpeg
        /// copies the video into HLS containers without re-encoding.
        /// </summary>
        public async Task<JellyfinPlaybackInfoResult> GetPlaybackInfoAsync(
            string itemId, long? maxStreamingBitrate, int? audioStreamIndex, int? subtitleStreamIndex,
            long startTimeTicks, ClientCapabilities capabilities, bool enableDirectPlay, CancellationToken cancel = default)
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
                // When allowed (no burn-in subtitle), let Jellyfin flag a browser-playable source
                // as direct-play so the controller can serve the original file with no transcode
                // (streaming-plan §"direct play"); a TranscodingUrl is still returned as fallback.
                EnableDirectPlay = enableDirectPlay,
                EnableDirectStream = false,
                EnableTranscoding = true,
                AutoOpenLiveStream = true,
                DeviceProfile = BuildWebDeviceProfile(maxStreamingBitrate, capabilities),
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

        /// <summary>A transcode whose session hasn't checked in within this window is treated as
        /// abandoned and no longer counts against the concurrency limit. Sized to tolerate a couple of
        /// missed ~10s Stream/Progress heartbeats.</summary>
        private const int ActiveSessionStaleSeconds = 45;

        /// <summary>Active sessions with a video transcode running — the concurrency-guard input.</summary>
        public async Task<int> GetActiveTranscodeCountAsync(CancellationToken cancel = default)
        {
            EnsureConfigured();
            var sessions = await httpClient.GetFromJsonAsync<List<JellyfinSession>>("/Sessions", JsonOptions, cancel) ?? new();
            // Count a transcode unless its session has gone stale: our ~10s Stream/Progress heartbeat keeps
            // Jellyfin's LastPlaybackCheckIn fresh, so a client that dropped without a clean Stop (tab close
            // w/o beacon, network loss, sleep) stops counting within a beat or two instead of lingering as a
            // ghost until Jellyfin's own reaper clears it. A just-started session with no check-in yet (null)
            // still counts, so we never undercount a live stream mid-startup.
            var cutoff = DateTime.UtcNow.AddSeconds(-ActiveSessionStaleSeconds);
            return sessions.Count(s => s.TranscodingInfo != null && s.NowPlayingItem != null
                && !(s.LastPlaybackCheckIn is DateTime checkIn && checkIn.ToUniversalTime() <= cutoff));
        }

        public const string DeviceId = "movietheater-site";

        // VideoLevel is expressed ×30 for HEVC; 183 ≈ level 6.1, generous enough to copy
        // any 4K HEVC source rather than needlessly re-encode it.
        private const string HevcMaxLevel = "183";

        private static object BuildWebDeviceProfile(long? maxStreamingBitrate, ClientCapabilities caps)
        {
            // HEVC and AV1 can't ride MPEG-TS HLS — they require fMP4 (CMAF) segments,
            // which is also the modern default and a touch more efficient for H.264 too
            // (§14.2). Force fMP4 whenever an advanced codec is on the table; otherwise
            // honor the client's own fMP4 capability, falling back to TS for ancient ones.
            bool useFmp4 = caps.Fmp4 || caps.Hevc || caps.Av1;
            string segmentContainer = useFmp4 ? "mp4" : "ts";

            // Encode-target preference is list order: HEVC first when the client can decode
            // it (≈30-40% smaller at equal quality, §14.3), H.264 the universal fallback.
            // AV1 trails and is copy-only — we never AV1-encode — so it's never the target.
            var videoCodecs = new List<string>();
            if (caps.Hevc && useFmp4) videoCodecs.Add("hevc");
            videoCodecs.Add("h264");
            if (caps.Av1 && useFmp4) videoCodecs.Add("av1");
            string videoCodec = string.Join(',', videoCodecs);

            // HDR passthrough only to HDR-capable clients (§14.5 stretch, done here for the
            // copy path): an SDR client that *copies* an HDR HEVC source renders washed-out,
            // so restrict which ranges may be copied — non-HDR clients fall through to a
            // tonemapping transcode. The copy path is the only no-cost HDR passthrough.
            string allowedRanges = caps.Hdr ? "SDR|HDR10|HLG|HDR10Plus|DOVI" : "SDR";

            var codecProfiles = new List<object>
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
            };
            if (caps.Hevc && useFmp4)
            {
                codecProfiles.Add(new
                {
                    Type = "Video",
                    Codec = "hevc",
                    Conditions = new object[]
                    {
                        new { Condition = "LessThanEqual", Property = "VideoLevel", Value = HevcMaxLevel, IsRequired = false },
                        new { Condition = "EqualsAny", Property = "VideoRangeType", Value = allowedRanges, IsRequired = false },
                    },
                });
            }

            return new
            {
                Name = "MovieTheaterWeb",
                MaxStreamingBitrate = maxStreamingBitrate ?? 1_000_000_000L,
                TranscodingProfiles = new object[]
                {
                    new
                    {
                        Container = segmentContainer,
                        Type = "Video",
                        VideoCodec = videoCodec,
                        AudioCodec = "aac,mp3",
                        Protocol = "hls",
                        Context = "Streaming",
                        MaxAudioChannels = "2",
                        MinSegments = 1,
                        // TS needs splitting on non-keyframes; fMP4 segments on GOP boundaries.
                        BreakOnNonKeyFrames = !useFmp4,
                    },
                    new { Container = "mp3", Type = "Audio", AudioCodec = "mp3", Protocol = "http", Context = "Streaming" },
                },
                // Browser-native containers/codecs: a source matching one of these is flagged
                // SupportsDirectPlay, letting the controller serve the original file with zero
                // transcode. HEVC only when the client decodes it; mkv etc. won't match → transcode.
                DirectPlayProfiles = new object[]
                {
                    new
                    {
                        Container = "mp4,m4v,mov",
                        Type = "Video",
                        VideoCodec = caps.Hevc ? "h264,hevc" : "h264",
                        AudioCodec = "aac,mp3",
                    },
                },
                CodecProfiles = codecProfiles.ToArray(),
                SubtitleProfiles = new object[]
                {
                    // Text subs ride as sidecar WebVTT; image subs (PGS/VobSub) burn in.
                    // *Only* vtt is offered as External: the browser's <track> element parses
                    // WebVTT exclusively, so srt/subrip/ass/ssa must NOT be advertised as
                    // external-deliverable — doing so makes Jellyfin's exact-codec-match pass
                    // hand back the raw .srt/.ass as-is, which the player can't render. With vtt
                    // the only text external format, Jellyfin transcodes any text subtitle to
                    // WebVTT instead.
                    new { Format = "vtt", Method = "External" },
                    new { Format = "pgssub", Method = "Encode" },
                    new { Format = "dvdsub", Method = "Encode" },
                    new { Format = "dvbsub", Method = "Encode" },
                },
            };
        }

        /// <summary>
        /// Enumerates every movie item in Jellyfin's library with Path, MediaSources and
        /// ProviderIds, paging through the full set.
        /// </summary>
        public Task<List<JellyfinItem>> GetAllMovieItemsAsync(CancellationToken cancel = default) =>
            GetAllItemsAsync("Movie", cancel);

        /// <summary>
        /// Enumerates every episode and standalone video item (series episodes + misc videos) with the
        /// same fields, so the sync can match their file paths to Episode/MiscVideo MediaFile rows.
        /// </summary>
        public Task<List<JellyfinItem>> GetAllEpisodeAndVideoItemsAsync(CancellationToken cancel = default) =>
            GetAllItemsAsync("Episode,Video", cancel);

        private async Task<List<JellyfinItem>> GetAllItemsAsync(string includeItemTypes, CancellationToken cancel)
        {
            EnsureConfigured();
            var items = new List<JellyfinItem>();
            const int pageSize = 1000;

            while (true)
            {
                var url = $"/Items?IncludeItemTypes={Uri.EscapeDataString(includeItemTypes)}&Recursive=true&EnableImages=false" +
                          $"&Fields=Path,MediaSources,ProviderIds&StartIndex={items.Count}&Limit={pageSize}";
                var page = await httpClient.GetFromJsonAsync<JellyfinItemsResult>(url, JsonOptions, cancel)
                    ?? throw new BusinessException("Jellyfin returned an empty /Items response.");

                items.AddRange(page.Items);
                if (page.Items.Count == 0 || items.Count >= page.TotalRecordCount)
                    return items;
            }
        }

        /// <summary>
        /// Kicks off Jellyfin's library scan (the "Scan Media Library" task, across all libraries) and
        /// returns immediately — the scan runs in the background. Poll <see cref="GetScanTaskStateAsync"/>
        /// for completion. We trigger scans on demand because the periodic timer is disabled (NAS health).
        /// </summary>
        public async Task TriggerLibraryScanAsync(CancellationToken cancel = default)
        {
            EnsureConfigured();
            using var resp = await httpClient.PostAsync("/Library/Refresh", null, cancel);
            resp.EnsureSuccessStatusCode();
        }

        /// <summary>
        /// State of Jellyfin's library-scan task (Key <c>RefreshLibrary</c> / "Scan Media Library"), so a
        /// caller can wait for a triggered scan to finish before syncing. <c>Running</c> while in flight
        /// (with a 0-100 progress), <c>Idle</c> when done. <c>Found=false</c> if the task isn't present.
        /// </summary>
        public async Task<JellyfinTaskState> GetScanTaskStateAsync(CancellationToken cancel = default)
        {
            EnsureConfigured();
            var tasks = await httpClient.GetFromJsonAsync<List<JellyfinScheduledTask>>("/ScheduledTasks", JsonOptions, cancel)
                ?? throw new BusinessException("Jellyfin returned no scheduled tasks.");
            var scan = tasks.FirstOrDefault(t => t.Key == "RefreshLibrary")
                       ?? tasks.FirstOrDefault(t => t.Name == "Scan Media Library");
            return scan == null
                ? new JellyfinTaskState { State = "Idle", Found = false }
                : new JellyfinTaskState { State = scan.State ?? "Idle", Progress = scan.CurrentProgressPercentage, Found = true };
        }

        // ── Subtitles (via a configured provider plugin, e.g. OpenSubtitles) ──────────────────────
        // The libraries are set with SaveSubtitlesWithMedia=false, so a downloaded subtitle lands in
        // Jellyfin's own metadata dir (local disk), NEVER the read-only NAS. All four calls below are
        // therefore Jellyfin DB/metadata operations — none can write or delete a file on the NAS.

        /// <summary>Searches the configured subtitle providers for subtitles matching this item in the
        /// given 3-letter language (e.g. "eng"). Read-only; downloads nothing. Empty if no provider is
        /// configured/authenticated.</summary>
        public async Task<List<JellyfinRemoteSubtitle>> SearchRemoteSubtitlesAsync(string itemId, string language, CancellationToken cancel = default)
        {
            EnsureConfigured();
            var url = $"/Items/{Uri.EscapeDataString(itemId)}/RemoteSearch/Subtitles/{Uri.EscapeDataString(language)}";
            return await httpClient.GetFromJsonAsync<List<JellyfinRemoteSubtitle>>(url, JsonOptions, cancel) ?? new();
        }

        /// <summary>Downloads a chosen subtitle (id from a prior search) and attaches it to the item, into
        /// Jellyfin's metadata dir (not the NAS — see note above).</summary>
        public async Task DownloadRemoteSubtitleAsync(string itemId, string subtitleId, CancellationToken cancel = default)
        {
            EnsureConfigured();
            using var resp = await httpClient.PostAsync(
                $"/Items/{Uri.EscapeDataString(itemId)}/RemoteSearch/Subtitles/{Uri.EscapeDataString(subtitleId)}", null, cancel);
            resp.EnsureSuccessStatusCode();
        }

        /// <summary>The subtitle tracks currently on an item, so the picker can show what's attached and let
        /// the user swap. Reads the item's MediaStreams and returns just the subtitle ones.</summary>
        public async Task<List<JellyfinSubtitleStream>> GetItemSubtitleStreamsAsync(string itemId, CancellationToken cancel = default)
        {
            EnsureConfigured();
            var url = $"/Items?Ids={Uri.EscapeDataString(itemId)}&Fields=MediaSources&EnableImages=false";
            var result = await httpClient.GetFromJsonAsync<JellyfinItemsResult>(url, JsonOptions, cancel);
            var streams = result?.Items.FirstOrDefault()?.MediaSources?.FirstOrDefault()?.MediaStreams ?? new();
            return streams
                .Where(s => string.Equals(s.Type, "Subtitle", StringComparison.OrdinalIgnoreCase))
                .Select(s => new JellyfinSubtitleStream { Index = s.Index, Language = s.Language, Title = s.Title, Codec = s.Codec, IsExternal = s.IsExternal })
                .ToList();
        }

        /// <summary>Removes an EXTERNAL subtitle track (a previously-downloaded sidecar) by stream index,
        /// so the user can drop a bad pick and try another. Only ever targets the metadata-dir sidecar;
        /// the read-only NAS mount is the hard backstop that prevents touching any on-disk video.</summary>
        public async Task DeleteSubtitleAsync(string itemId, int index, CancellationToken cancel = default)
        {
            EnsureConfigured();
            using var resp = await httpClient.DeleteAsync($"/Videos/{Uri.EscapeDataString(itemId)}/Subtitles/{index}", cancel);
            resp.EnsureSuccessStatusCode();
        }
    }

    /// <summary>
    /// What the calling browser can decode, detected client-side (§14.1) and used to
    /// build a per-request <c>DeviceProfile</c>. Defaults are the safe H.264/TS baseline.
    /// </summary>
    public record ClientCapabilities(bool Hevc = false, bool Av1 = false, bool Hdr = false, bool Fmp4 = false)
    {
        /// <summary>The pre-§14 universal baseline: H.264 in MPEG-TS, nothing fancy.</summary>
        public static readonly ClientCapabilities H264Baseline = new();
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

    /// <summary>One Jellyfin scheduled task as returned by <c>/ScheduledTasks</c> (only the fields we read).</summary>
    public class JellyfinScheduledTask
    {
        public string? Name { get; set; }
        public string? Key { get; set; }
        public string? State { get; set; }   // "Idle" | "Running" | "Cancelling"
        public double? CurrentProgressPercentage { get; set; }
    }

    /// <summary>Library-scan task state for poll-to-completion (see <see cref="JellyfinApi.GetScanTaskStateAsync"/>).</summary>
    public class JellyfinTaskState
    {
        public string State { get; set; } = "Idle";
        public double? Progress { get; set; }
        public bool Found { get; set; }
        public bool IsRunning => string.Equals(State, "Running", System.StringComparison.OrdinalIgnoreCase);
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
        public int Index { get; set; }
        public string? Language { get; set; }
        public string? Title { get; set; }
        public bool IsExternal { get; set; }
    }

    /// <summary>One subtitle candidate from a provider search (<see cref="JellyfinApi.SearchRemoteSubtitlesAsync"/>).
    /// <c>IsHashMatch</c> — the subtitle was uploaded for this exact file (already in sync) — is the strongest
    /// "likely correct" signal, stronger than runtime matching.</summary>
    public class JellyfinRemoteSubtitle
    {
        public string Id { get; set; } = default!;
        public string? ProviderName { get; set; }
        public string? Name { get; set; }
        public string? Format { get; set; }
        public string? Author { get; set; }
        public string? Comment { get; set; }
        public string? ThreeLetterISOLanguageName { get; set; }
        public int? DownloadCount { get; set; }
        public bool IsHashMatch { get; set; }
        public float? CommunityRating { get; set; }
    }

    /// <summary>A subtitle track currently attached to an item (<see cref="JellyfinApi.GetItemSubtitleStreamsAsync"/>).
    /// <c>IsExternal</c> distinguishes a downloaded sidecar (removable) from one embedded in the video.</summary>
    public class JellyfinSubtitleStream
    {
        public int Index { get; set; }
        public string? Language { get; set; }
        public string? Title { get; set; }
        public string? Codec { get; set; }
        public bool IsExternal { get; set; }
    }
}
