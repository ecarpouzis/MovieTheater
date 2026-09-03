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
        private readonly IHttpClientFactory httpClientFactory;
        private readonly JellyfinApiOptions options;

        public JellyfinApi(HttpClient httpClient, IHttpClientFactory httpClientFactory, IOptions<JellyfinApiOptions> options)
        {
            this.httpClient = httpClient;
            this.httpClientFactory = httpClientFactory;
            this.options = options.Value;
        }

        /// <summary>Named client for calls Jellyfin answers in minutes, not seconds — see the registration
        /// in <c>JellyfinServiceExtensions</c>; identical auth, 15-minute ceiling.</summary>
        public const string LongRunningClientName = "jellyfin-long-running";

        private void EnsureConfigured()
        {
            if (string.IsNullOrEmpty(options.BaseUrl))
                throw new BusinessException("JellyfinBaseUrl is not configured.");
            if (string.IsNullOrEmpty(options.ApiKey))
                throw new BusinessException("JellyfinApiKey is not configured.");
        }

        /// <summary>Who Jellyfin should believe is playing: a stable per-screen id, plus an optional
        /// human label for its dashboard. See <see cref="DeviceRequest"/>.</summary>
        public readonly record struct JellyfinDevice(string Id, string? Name = null);

        /// <summary>
        /// A playback-lifecycle request stamped with THIS VIEWER'S device identity instead of the
        /// site-wide one. Jellyfin keys a *session* by Client+DeviceId, so while every viewer shared one
        /// id they all collapsed into a single session: <c>/Sessions</c> reported one viewer and at most
        /// one transcode however many were really running, and the dashboard couldn't tell who was
        /// watching what. One id per screen makes that accounting true again.
        ///
        /// It does NOT change where ffmpeg writes: Jellyfin omits DeviceId from the TranscodingUrl it
        /// hands back (verified against 10.11.11), so the transcode's output directory is keyed without
        /// it. Don't reach for this to separate two viewers' segment files.
        ///
        /// Only the playback lifecycle needs it; library sweeps and admin calls keep the default header
        /// attached at registration. A per-request header wins over the client's default.
        /// </summary>
        private HttpRequestMessage DeviceRequest(HttpMethod method, string url, JellyfinDevice? device, HttpContent? content = null)
        {
            var request = new HttpRequestMessage(method, url) { Content = content };
            if (device is JellyfinDevice d && !string.IsNullOrEmpty(d.Id))
            {
                // Quoted header values: keep the label free of quotes/backslashes rather than escaping.
                var label = new string((d.Name ?? "site").Where(c => char.IsLetterOrDigit(c) || c is ' ' or '-' or '_' or '.').Take(40).ToArray());
                request.Headers.TryAddWithoutValidation("X-Emby-Authorization",
                    $"MediaBrowser Client=\"MovieTheater\", Device=\"{(label.Length > 0 ? label : "site")}\", DeviceId=\"{d.Id}\", Version=\"1.0\", Token=\"{options.ApiKey}\"");
            }
            return request;
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
        /// <param name="mediaSourceId">
        /// The id of the source being played. REQUIRED for <paramref name="audioStreamIndex"/> /
        /// <paramref name="subtitleStreamIndex"/> to have any effect: Jellyfin's MediaInfoHelper only copies the
        /// requested track indices onto the stream-build options when the request pins the media source
        /// (<c>string.Equals(mediaSourceId, mediaSource.Id)</c>) — with it null the indices are silently dropped
        /// and the container's default audio is what the TranscodingUrl selects. Null on the first (discovery)
        /// call, since the source id isn't known until Jellyfin answers; pass it on any follow-up that pins a track.
        /// </param>
        /// <param name="device">
        /// The VIEWER'S device identity (see <see cref="DeviceRequest"/>) — this is the call that opens
        /// the Jellyfin session, so it decides whether viewers are counted separately. Null → the
        /// site-wide identity.
        /// </param>
        public async Task<JellyfinPlaybackInfoResult> GetPlaybackInfoAsync(
            string itemId, long? maxStreamingBitrate, int? audioStreamIndex, int? subtitleStreamIndex,
            long startTimeTicks, ClientCapabilities capabilities, bool enableDirectPlay,
            string? mediaSourceId = null, JellyfinDevice? device = null, CancellationToken cancel = default)
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
                MediaSourceId = mediaSourceId,
                // When allowed (no burn-in subtitle), let Jellyfin flag a browser-playable source
                // as direct-play so the controller can serve the original file with no transcode
                // (streaming-plan §"direct play"); a TranscodingUrl is still returned as fallback.
                EnableDirectPlay = enableDirectPlay,
                EnableDirectStream = false,
                EnableTranscoding = true,
                AutoOpenLiveStream = true,
                DeviceProfile = BuildWebDeviceProfile(maxStreamingBitrate, capabilities),
            };

            using var request = DeviceRequest(
                HttpMethod.Post,
                $"/Items/{Uri.EscapeDataString(itemId)}/PlaybackInfo?userId={Uri.EscapeDataString(userId)}",
                device,
                JsonContent.Create(body, options: JsonOptions));
            using var response = await httpClient.SendAsync(request, cancel);
            response.EnsureSuccessStatusCode();
            var result = await response.Content.ReadFromJsonAsync<JellyfinPlaybackInfoResult>(JsonOptions, cancel);
            if (result == null || result.MediaSources.Count == 0)
                throw new BusinessException("Jellyfin returned no playable media source.");
            return result;
        }

        /// <summary>The client HttpClient allows up to 2 minutes (right for the library sync sweeps), but the
        /// playback lifecycle calls fire on a ~10s heartbeat — a hung Jellyfin must not stall each beat for
        /// two minutes and stack up requests. Bound those to a few seconds instead.</summary>
        private static readonly TimeSpan LifecycleTimeout = TimeSpan.FromSeconds(5);

        /// <summary>Progress report — keeps Jellyfin's transcode throttling honest.</summary>
        public async Task ReportPlaybackProgressAsync(string itemId, string playSessionId, long positionTicks, bool isPaused, JellyfinDevice? device = null, CancellationToken cancel = default)
        {
            EnsureConfigured();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            cts.CancelAfter(LifecycleTimeout);
            using var request = DeviceRequest(HttpMethod.Post, "/Sessions/Playing/Progress", device,
                JsonContent.Create(new
                {
                    ItemId = itemId,
                    PlaySessionId = playSessionId,
                    PositionTicks = positionTicks,
                    IsPaused = isPaused,
                }, options: JsonOptions));
            using var resp = await httpClient.SendAsync(request, cts.Token);
        }

        public async Task ReportPlaybackStoppedAsync(string itemId, string playSessionId, long positionTicks, JellyfinDevice? device = null, CancellationToken cancel = default)
        {
            EnsureConfigured();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            cts.CancelAfter(LifecycleTimeout);
            using var request = DeviceRequest(HttpMethod.Post, "/Sessions/Playing/Stopped", device,
                JsonContent.Create(new
                {
                    ItemId = itemId,
                    PlaySessionId = playSessionId,
                    PositionTicks = positionTicks,
                }, options: JsonOptions));
            using var resp = await httpClient.SendAsync(request, cts.Token);
        }

        /// <summary>Kills the ffmpeg process and cleans segments immediately instead of waiting for the idle
        /// timeout. Jellyfin matches the job by playSessionId whenever one is given (the deviceId is only
        /// the fallback selector), which is why stops kept working even while the jobs themselves carried
        /// no device id — so pass the session's device for correct accounting, not to make the kill land.</summary>
        public async Task StopActiveEncodingsAsync(string playSessionId, JellyfinDevice? device = null, CancellationToken cancel = default)
        {
            EnsureConfigured();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            cts.CancelAfter(LifecycleTimeout);
            using var request = DeviceRequest(HttpMethod.Delete,
                $"/Videos/ActiveEncodings?deviceId={Uri.EscapeDataString(device?.Id ?? DeviceId)}&playSessionId={Uri.EscapeDataString(playSessionId)}",
                device);
            using var resp = await httpClient.SendAsync(request, cts.Token);
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

        /// <summary>The site's own device identity — used by library sweeps, admin calls, and as the prefix
        /// for per-viewer ids. Playback should send a per-viewer id instead (see <see cref="DeviceRequest"/>),
        /// or every viewer collapses into one Jellyfin session.</summary>
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

            // Audio: preserve surround. Floor at 6 channels (5.1) rather than trusting the client's
            // AudioContext probe: maxChannelCount reads the OS OUTPUT config, which reports 2 on any
            // stereo-configured desktop even when a 5.1 receiver is attached — and a wrongly-stereo
            // profile makes Jellyfin downmix server-side through its DownMixAudioBoost volume filter,
            // which clips loud music into audible distortion. Every MSE browser decodes multichannel
            // AAC and downmixes cleanly client-side when the output really is stereo, so claiming 6
            // is safe there and strictly better on a real surround setup (discrete 5.1 survives).
            // 7.1 (8) is still honored when the client reports it.
            int maxAudioChannels = Math.Clamp(caps.MaxAudioChannels, 6, 8);
            // FLAC is the audio on most Blu-ray remuxes here; letting a FLAC-capable browser direct-play
            // it is what keeps those files off the HLS path (and its keyframe/segment pitfalls) entirely.
            string directPlayAudio = "aac,mp3" + (caps.Ac3 ? ",ac3" : "") + (caps.Eac3 ? ",eac3" : "")
                + (caps.Flac ? ",flac" : "");

            // MKV is the dominant library container, and Chromium's <video> can play a Matroska file whose
            // codecs it supports (canPlayType('video/x-matroska') — that's what caps.Mkv reports). It is
            // deliberately NOT a direct-play container here any more. Direct play means Jellyfin's static
            // /Videos/{id}/stream.mkv endpoint streaming the raw file off the NAS share, and that path was
            // measured on 2026-09-03 at 3–12 Mbps from Jellyfin itself (5–8 Mbps through the gateway) while
            // ffmpeg reading the same share ran 12× realtime and HLS segments left Caddy at 200–1000 Mbps.
            // Root cause: Kestrel's SendFile fallback reads the file in 16 KB overlapped reads, and on an SMB
            // share that pattern measures 1.7 MB/s against 64–83 MB/s for 64 KB-async or any sync read. A
            // 2.5 Mbps 576p film (The Lorax, Family Movie Night) rode it just-in-time: 3–15 s to the first
            // frame on every tune, underruns mid-film, and every browser seek re-opened the file with a
            // multi-second first byte. The HLS copy path is the same bytes (video copied; ac3/eac3/flac
            // copied when the client decodes them — see the transcoding profile below) served from the local
            // transcode cache, so an MKV loses nothing by taking it. Re-enable mkv here only once the patched
            // Jellyfin's static path (large sync reads) is deployed AND measured at a comfortable multiple of
            // the library's remux bitrates.
            var directPlayProfiles = new List<object>
            {
                new
                {
                    Container = "mp4,m4v,mov",
                    Type = "Video",
                    VideoCodec = caps.Hevc ? "h264,hevc" : "h264",
                    AudioCodec = directPlayAudio,
                },
            };

            // HDR passthrough only to HDR-capable clients (§14.5 stretch, done here for the
            // copy path): an SDR client that *copies* an HDR HEVC source renders washed-out,
            // so restrict which ranges may be copied — non-HDR clients fall through to a
            // tonemapping transcode. The copy path is the only no-cost HDR passthrough.
            // Dolby Vision (DOVI*) is split out: only a client that actually DECODES DV (≈Safari)
            // may copy a DOVI source — a non-DV browser that copies it renders broken, so without
            // the DV flag DOVI is excluded and the source tonemaps/transcodes instead.
            string allowedRanges = caps.Hdr ? "SDR|HDR10|HLG|HDR10Plus" : "SDR";
            if (caps.DolbyVision)
                allowedRanges += "|DOVI|DOVIWithHDR10|DOVIWithHLG|DOVIWithSDR|DOVIWithHDR10Plus";

            var codecProfiles = new List<object>
            {
                new
                {
                    Type = "Video",
                    Codec = "h264",
                    Conditions = new object[]
                    {
                        new { Condition = "LessThanEqual", Property = "VideoLevel", Value = "51", IsRequired = false },
                        // Exclude 10-bit H.264 (profile "high 10", i.e. Hi10P — common in fansub anime) and
                        // other exotic profiles from the copy path: browser MSE can't decode them, so a copied
                        // Hi10P source plays as green/garbage/audio-only with no fallback. Failing this
                        // condition forces an 8-bit transcode instead. We don't probe high-10 client support,
                        // so allow only the universally-decodable profiles.
                        new { Condition = "EqualsAny", Property = "VideoProfile", Value = "high|main|baseline|constrained baseline", IsRequired = false },
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
                        // Only copy 10-bit HEVC (profile "main 10") when the client decoded the Main-10 probe;
                        // otherwise restrict to 8-bit "main" so a Main-only decoder doesn't get garbage.
                        new { Condition = "EqualsAny", Property = "VideoProfile", Value = caps.HevcMain10 ? "main|main 10" : "main", IsRequired = false },
                    },
                });
            }
            if (caps.Av1 && useFmp4)
            {
                // AV1 is copy-only (we never AV1-encode), so guard what may be copied: profile "main" (our
                // probe is profile 0), 8-bit unless the client decoded the 10-bit probe, and HDR ranges only
                // when it's both an HDR display and 10-bit-capable. Without this, any AV1 (4K / 10-bit / HDR)
                // was copied on the strength of an 8-bit, low-level probe.
                var av1Conditions = new List<object>
                {
                    new { Condition = "EqualsAny", Property = "VideoProfile", Value = "main", IsRequired = false },
                    new { Condition = "EqualsAny", Property = "VideoRangeType", Value = caps.Hdr && caps.Av110Bit ? "SDR|HDR10|HLG" : "SDR", IsRequired = false },
                };
                if (!caps.Av110Bit)
                    av1Conditions.Add(new { Condition = "LessThanEqual", Property = "VideoBitDepth", Value = "8", IsRequired = false });
                codecProfiles.Add(new { Type = "Video", Codec = "av1", Conditions = av1Conditions.ToArray() });
            }
            if (!caps.HeAac)
            {
                // Client can't decode HE-AAC (SBR) → don't copy an HE-AAC track; transcode it to LC-AAC.
                // (Most browsers decode HE-AAC, so this is usually inert.)
                codecProfiles.Add(new
                {
                    Type = "VideoAudio",
                    Codec = "aac",
                    Conditions = new object[]
                    {
                        new { Condition = "NotEquals", Property = "AudioProfile", Value = "HE-AAC", IsRequired = false },
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
                        // AAC is the transcode target (first in the list); the client's decodable extras
                        // ride as COPY candidates so a matching source track is remuxed losslessly instead
                        // of re-encoded. MP3-over-MSE is Firefox-broken (copying MP3 froze playback at 0:00
                        // on an MP3-audio source, e.g. Gandhi's .avi), so it's gated on caps.Mp3. AC-3/E-AC-3
                        // (Dolby surround) are gated on real MSE decode support (Edge/Safari) — previously a
                        // Dolby track in an MKV always re-encoded to AAC because it was only in the (mp4-only)
                        // DirectPlay list, never the HLS path. Copying a 5.1 track still needs MaxAudioChannels
                        // >= the source's channels (set below), so stereo clients correctly fall back to a downmix.
                        // FLAC additionally requires fMP4 — it has no MPEG-TS mapping, so on a TS
                        // fallback client it must transcode to AAC rather than copy. hls.js handles
                        // fLaC-in-fMP4 natively (codec tables + passthrough, verified in 1.6.16).
                        AudioCodec = "aac"
                            + (caps.Mp3 ? ",mp3" : "")
                            + (caps.Ac3 ? ",ac3" : "")
                            + (caps.Eac3 ? ",eac3" : "")
                            + (caps.Flac && useFmp4 ? ",flac" : ""),
                        Protocol = "hls",
                        Context = "Streaming",
                        MaxAudioChannels = maxAudioChannels.ToString(),
                        MinSegments = 1,
                        // TS needs splitting on non-keyframes; fMP4 segments on GOP boundaries.
                        BreakOnNonKeyFrames = !useFmp4,
                    },
                    new { Container = "mp3", Type = "Audio", AudioCodec = "mp3", Protocol = "http", Context = "Streaming" },
                },
                // Browser-native containers/codecs: a source matching one of these is flagged
                // SupportsDirectPlay, letting the controller serve the original file with zero
                // transcode. HEVC only when the client decodes it; MKV only when the client probed it
                // (see directPlayProfiles above).
                DirectPlayProfiles = directPlayProfiles.ToArray(),
                CodecProfiles = codecProfiles.ToArray(),
                SubtitleProfiles = new object[]
                {
                    // Plain text subs (SRT etc.) ride as sidecar WebVTT — the browser's <track> parses only
                    // WebVTT, so srt/subrip must NOT be External (Jellyfin would hand back a raw .srt the
                    // <track> can't render); it transcodes them to WebVTT instead.
                    new { Format = "vtt", Method = "External" },
                    // ASS/SSA are delivered RAW (External) and rendered client-side by libass (SubtitlesOctopus)
                    // with full typesetting — vs flattening to WebVTT, which drops all signs/positioning/karaoke.
                    new { Format = "ass", Method = "External" },
                    new { Format = "ssa", Method = "External" },
                    // PGS rides as an external .sup rendered CLIENT-SIDE by libpgs (a canvas overlay), so the
                    // video is still copied instead of re-encoded to burn the bitmap in. The remaining image
                    // formats have no client renderer, so they're still burned in (a video re-encode).
                    new { Format = "pgssub", Method = "External" },
                    new { Format = "dvdsub", Method = "Encode" },
                    new { Format = "dvbsub", Method = "Encode" },
                },
            };
        }

        /// <summary>
        /// Enumerates every leaf media (video) item in Jellyfin — Movie, Episode AND standalone Video — with
        /// Path, MediaSources and ProviderIds, paging through the full set. Item TYPE is deliberately NOT
        /// used for routing: the sync matches each item to a DB row purely by file Path, so this behaves
        /// identically whether a library is typed (movies/tvshows) or "homevideos" (every file is a Video).
        /// The union of leaf types excludes folder containers (Series/Season/BoxSet) that carry no file path.
        /// </summary>
        public Task<List<JellyfinItem>> GetAllVideoItemsAsync(CancellationToken cancel = default) =>
            GetAllItemsAsync("Movie,Episode,Video", cancel);

        /// <summary>
        /// Fetches specific items by id, in small batches (URL-length safe). Unlike the recursive enumeration
        /// in <see cref="GetAllVideoItemsAsync"/>, an explicit id lookup returns items the recursive/parent
        /// queries hide: when Jellyfin groups multi-part movies (e.g. "Title (CD 1)"/"Title (CD 2)") as
        /// alternate "versions", the secondary parts get a PrimaryVersionId and are excluded from normal
        /// listings — yet they remain live and individually streamable. The sync uses this to confirm a row's
        /// already-stored item id is still valid so it isn't wrongly flagged missing.
        /// </summary>
        public async Task<List<JellyfinItem>> GetItemsByIdsAsync(IEnumerable<string> ids, CancellationToken cancel = default)
        {
            EnsureConfigured();
            var idList = ids.Where(s => !string.IsNullOrEmpty(s)).Distinct().ToList();
            var items = new List<JellyfinItem>();
            const int batchSize = 40;
            for (int i = 0; i < idList.Count; i += batchSize)
            {
                var csv = string.Join(",", idList.Skip(i).Take(batchSize));
                var url = $"/Items?ids={Uri.EscapeDataString(csv)}&EnableImages=false" +
                          $"&Fields=Path,MediaSources,ProviderIds&Limit={batchSize}";
                var page = await httpClient.GetFromJsonAsync<JellyfinItemsResult>(url, JsonOptions, cancel);
                if (page?.Items != null) items.AddRange(page.Items);
            }
            return items;
        }

        /// <summary>
        /// Tells Jellyfin one or more on-disk paths changed so it re-scans JUST those (the per-path scoped
        /// alternative to the full <c>/Library/Refresh</c>). The per-movie "re-link files from disk" flow
        /// posts the title's shelf folder here to pick up a replaced/renamed file without a deep full-library
        /// scan. <paramref name="paths"/> are Jellyfin-side paths. Returns immediately; the scan runs in the
        /// background (poll <see cref="GetAllVideoItemPathsAsync"/> for the new file to appear).
        /// </summary>
        public async Task NotifyPathsUpdatedAsync(IEnumerable<string> paths, CancellationToken cancel = default)
        {
            EnsureConfigured();
            var updates = paths.Where(p => !string.IsNullOrWhiteSpace(p))
                .Select(p => new { Path = p, UpdateType = "Modified" }).ToArray();
            if (updates.Length == 0) return;
            using var resp = await httpClient.PostAsJsonAsync("/Library/Media/Updated", new { Updates = updates }, JsonOptions, cancel);
            resp.EnsureSuccessStatusCode();
        }

        /// <summary>
        /// Lightweight enumeration of every leaf video item's id + path ONLY (no MediaSources payload), for
        /// cheap repeated polling: the re-link probe scans this to spot a new untracked file under one shelf
        /// without pulling the heavy media-source data each poll. Fetch the chosen item's full detail with
        /// <see cref="GetItemsByIdsAsync"/> once it's identified.
        /// </summary>
        public Task<List<JellyfinItem>> GetAllVideoItemPathsAsync(CancellationToken cancel = default) =>
            GetAllItemsAsync("Movie,Episode,Video", "Path", cancel);

        /// <summary>
        /// Every Jellyfin "extra" (special feature: featurette, deleted scene, behind-the-scenes, trailer…)
        /// across the library, with path + media detail. Extras carry an <c>ExtraType</c> and are EXCLUDED
        /// from the normal movie/episode listings (and from ParentId/Recursive queries), so they're fetched
        /// separately here — via an IncludeItemTypes=Video sweep, kept only where ExtraType is set — and then
        /// attached to their owner movie by folder. One bulk call, no per-movie lookups.
        /// </summary>
        public async Task<List<JellyfinItem>> GetAllExtraItemsAsync(CancellationToken cancel = default)
        {
            // Light sweep (path + ExtraType only) to FIND the extras, then enrich just those few with the
            // heavier MediaSources detail — instead of pulling media sources for every video in the library.
            var all = await GetAllItemsAsync("Video", "Path,ExtraType", cancel);
            var extras = all.Where(i => !string.IsNullOrEmpty(i.ExtraType)).ToList();
            if (extras.Count == 0) return extras;
            var detail = await GetItemsByIdsAsync(extras.Select(e => e.Id), cancel);
            return detail.Count > 0 ? detail : extras;
        }

        /// <summary>
        /// The special features (extras) attached to ONE item, via the per-item endpoint — the only way to
        /// reach an extra from its owner (extras are hidden from ParentId/Recursive listings). Returns the
        /// extra items (ids + paths); enrich with <see cref="GetItemsByIdsAsync"/> for MediaSources. Used by
        /// the per-movie re-link so a replaced rip's featurettes follow it.
        /// </summary>
        public async Task<List<JellyfinItem>> GetSpecialFeaturesAsync(string itemId, CancellationToken cancel = default)
        {
            EnsureConfigured();
            var userId = await GetUserIdAsync(cancel);
            var url = $"/Users/{Uri.EscapeDataString(userId)}/Items/{Uri.EscapeDataString(itemId)}/SpecialFeatures?EnableImages=false";
            return await httpClient.GetFromJsonAsync<List<JellyfinItem>>(url, JsonOptions, cancel) ?? new();
        }

        /// <summary>
        /// Folder/container items with their paths, so the re-link flow can resolve a shelf's Jellyfin item id
        /// by on-disk path and then trigger a scoped folder re-scan of it.
        /// </summary>
        public Task<List<JellyfinItem>> GetFoldersAsync(CancellationToken cancel = default) =>
            GetAllItemsAsync("Folder", "Path", cancel);

        /// <summary>
        /// The server's configured LIBRARIES and the on-disk folders each one covers
        /// (<c>/Library/VirtualFolders</c>). Used for exactly one thing: letting the movie-side sync
        /// resolve the family photo library's own locations into exclusion prefixes when
        /// <c>PhotosJellyfinLibraryId</c> is set (docs/photos-plan.md §2.3). The item listings carry no
        /// per-item library id, so this is the only way an id can widen a path-prefix exclusion.
        ///
        /// <para>Never called unless that setting is present, so the ordinary sync pays nothing for it.</para>
        /// </summary>
        public async Task<List<JellyfinVirtualFolder>> GetVirtualFoldersAsync(CancellationToken cancel = default)
        {
            EnsureConfigured();
            return await httpClient.GetFromJsonAsync<List<JellyfinVirtualFolder>>("/Library/VirtualFolders", JsonOptions, cancel)
                   ?? new List<JellyfinVirtualFolder>();
        }

        /// <summary>
        /// Path-only listing of the video items in ONE library, for <c>photos-sync-jellyfin</c> (§2.3).
        /// Scoped by ParentId so the family sync never enumerates the movie library — the mirror image
        /// of the movie sync's exclusion, and the reason neither pass can see the other's files.
        /// </summary>
        public Task<List<JellyfinItem>> GetLibraryVideoItemsAsync(string libraryId, CancellationToken cancel = default) =>
            GetVideoItemPathsUnderParentAsync(libraryId, cancel);

        /// <summary>
        /// Path-only listing of video items under a folder/parent (recursive) — the SCOPED counterpart of
        /// <see cref="GetAllVideoItemPathsAsync"/>, so the re-link probe can poll a single shelf cheaply
        /// instead of re-listing the whole library every few seconds.
        /// </summary>
        public async Task<List<JellyfinItem>> GetVideoItemPathsUnderParentAsync(string parentId, CancellationToken cancel = default)
        {
            EnsureConfigured();
            var items = new List<JellyfinItem>();
            const int pageSize = 1000;
            while (true)
            {
                var url = $"/Items?ParentId={Uri.EscapeDataString(parentId)}&IncludeItemTypes=Movie,Episode,Video&Recursive=true" +
                          $"&EnableImages=false&Fields=Path&StartIndex={items.Count}&Limit={pageSize}";
                var page = await httpClient.GetFromJsonAsync<JellyfinItemsResult>(url, JsonOptions, cancel);
                if (page == null) break;
                items.AddRange(page.Items);
                if (page.Items.Count == 0 || items.Count >= page.TotalRecordCount) break;
            }
            return items;
        }

        /// <summary>
        /// Triggers a SCOPED re-scan of a single folder item: Jellyfin validates the folder's children, so a
        /// newly-added file is indexed and a deleted one is dropped — the per-folder alternative to the full
        /// <c>/Library/Refresh</c>. Returns immediately; the scan runs in the background (poll the item list).
        /// </summary>
        public async Task RefreshItemAsync(string itemId, CancellationToken cancel = default)
        {
            EnsureConfigured();
            var url = $"/Items/{Uri.EscapeDataString(itemId)}/Refresh" +
                      $"?Recursive=true&MetadataRefreshMode=Default&ImageRefreshMode=None&ReplaceAllMetadata=false";
            using var resp = await httpClient.PostAsync(url, null, cancel);
            resp.EnsureSuccessStatusCode();
        }

        /// <summary>
        /// What one <see cref="ExtractKeyframesAsync"/> call did. Not a bare bool because the two failure
        /// modes need different handling by a backfill driver: 404 (the item or its path is gone — a
        /// permanent skip) vs 500 / transport error (worth retrying on a later pass).
        /// </summary>
        public readonly record struct KeyframeExtractOutcome(bool Ok, int StatusCode, string? Error);

        /// <summary>
        /// Asks Jellyfin to build a COMPLETE keyframe list for an item and store it in its own keyframe
        /// repository — the site-side half of the exact-segmentation patch (see
        /// <see cref="MovieTheater.Db.MediaFile.JfKeyframesUtc"/>): once an item is in that repository the patched
        /// server cuts a stream-COPIED HLS session on the file's real keyframes, so segment numbering can
        /// no longer drift and a mid-session restart can't renumber the timeline.
        ///
        /// <para>Server-side this is a full ffprobe packet walk over the media mount — tens of seconds to
        /// several minutes per file — so it rides <see cref="LongRunningClientName"/> and must only ever be
        /// driven by a bounded backfill, never a request path. Returns rather than throws on 404/500 so a
        /// batch continues past one bad file.</para>
        /// </summary>
        public async Task<KeyframeExtractOutcome> ExtractKeyframesAsync(string itemId, CancellationToken cancel = default)
        {
            EnsureConfigured();
            var client = httpClientFactory.CreateClient(LongRunningClientName);
            try
            {
                using var resp = await client.PostAsync($"/Videos/{Uri.EscapeDataString(itemId)}/ExtractKeyframes", null, cancel);
                if (resp.IsSuccessStatusCode)
                    return new KeyframeExtractOutcome(true, (int)resp.StatusCode, null);
                var body = await resp.Content.ReadAsStringAsync(cancel);
                return new KeyframeExtractOutcome(false, (int)resp.StatusCode,
                    body.Length == 0 ? resp.ReasonPhrase : body.Length <= 200 ? body : body[..200]);
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !cancel.IsCancellationRequested)
            {
                // A timeout surfaces as TaskCanceledException with OUR token unsignalled; treat it as this
                // file's failure so the batch moves on, and never swallow an operator Ctrl-C.
                return new KeyframeExtractOutcome(false, 0, e.Message.Length <= 200 ? e.Message : e.Message[..200]);
            }
        }

        /// <summary>
        /// Pushes a previously-captured keyframe list into Jellyfin's repository for an item — the
        /// restore half of keyframe custody (<see cref="MovieTheater.Db.MediaKeyframes"/>). The patched
        /// server's <c>ImportKeyframes</c> endpoint stores it exactly as an extraction would, so the
        /// exact-segmentation path lights up for the new item with no ffprobe walk at all.
        ///
        /// <para><c>keyframeTicksJson</c> is the stored JSON tick array embedded VERBATIM into the
        /// request body — a four-thousand-element list round-trips byte-faithfully instead of being
        /// parsed and re-serialized on every hop. A 404 here means the SERVER LACKS THE ENDPOINT (a
        /// stock Jellyfin after an upgrade wiped the patch) or the item is gone; callers treat it as
        /// "restore unavailable" and fall back to the nightly re-extraction, never as an error worth
        /// failing a sync over.</para>
        /// </summary>
        public async Task<KeyframeExtractOutcome> ImportKeyframesAsync(string itemId, long totalDurationTicks,
            string keyframeTicksJson, CancellationToken cancel = default)
        {
            EnsureConfigured();
            var client = httpClientFactory.CreateClient(LongRunningClientName);
            try
            {
                using var content = new StringContent(
                    $"{{\"totalDurationTicks\":{totalDurationTicks},\"keyframeTicks\":{keyframeTicksJson}}}",
                    System.Text.Encoding.UTF8, "application/json");
                using var resp = await client.PostAsync(
                    $"/Videos/{Uri.EscapeDataString(itemId)}/ImportKeyframes", content, cancel);
                if (resp.IsSuccessStatusCode)
                    return new KeyframeExtractOutcome(true, (int)resp.StatusCode, null);
                var body = await resp.Content.ReadAsStringAsync(cancel);
                return new KeyframeExtractOutcome(false, (int)resp.StatusCode,
                    body.Length == 0 ? resp.ReasonPhrase : body.Length <= 200 ? body : body[..200]);
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !cancel.IsCancellationRequested)
            {
                return new KeyframeExtractOutcome(false, 0, e.Message.Length <= 200 ? e.Message : e.Message[..200]);
            }
        }

        private async Task<List<JellyfinItem>> GetAllItemsAsync(string includeItemTypes, CancellationToken cancel) =>
            await GetAllItemsAsync(includeItemTypes, "Path,MediaSources,ProviderIds", cancel);

        private async Task<List<JellyfinItem>> GetAllItemsAsync(string includeItemTypes, string fields, CancellationToken cancel)
        {
            EnsureConfigured();
            var items = new List<JellyfinItem>();
            const int pageSize = 1000;

            while (true)
            {
                var url = $"/Items?IncludeItemTypes={Uri.EscapeDataString(includeItemTypes)}&Recursive=true&EnableImages=false" +
                          $"&Fields={Uri.EscapeDataString(fields)}&StartIndex={items.Count}&Limit={pageSize}";
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

        /// <summary>Attaches a text subtitle to an item as a new EXTERNAL sidecar (lands in Jellyfin's
        /// metadata dir, never the read-only NAS — libraries are SaveSubtitlesWithMedia=false). Used to
        /// store a subtitle fetched from OpenSubtitles so the streaming path then delivers it as WebVTT.
        /// <paramref name="language"/> is the 3-letter code (e.g. "eng"); <paramref name="format"/> the
        /// subtitle format/extension (e.g. "srt").</summary>
        public async Task UploadSubtitleAsync(string itemId, string language, string format, bool isForced, bool isHearingImpaired, byte[] data, CancellationToken cancel = default)
        {
            EnsureConfigured();
            var body = new
            {
                Language = language,
                Format = format,
                IsForced = isForced,
                IsHearingImpaired = isHearingImpaired,
                Data = Convert.ToBase64String(data),
            };
            using var resp = await httpClient.PostAsJsonAsync($"/Videos/{Uri.EscapeDataString(itemId)}/Subtitles", body, JsonOptions, cancel);
            resp.EnsureSuccessStatusCode();
        }
    }

    /// <summary>
    /// What the calling browser can decode, detected client-side (§14.1) and used to
    /// build a per-request <c>DeviceProfile</c>. Defaults are the safe H.264/TS baseline.
    /// </summary>
    public record ClientCapabilities(
        bool Hevc = false, bool Av1 = false, bool Hdr = false, bool Fmp4 = false, bool Mp3 = false,
        bool Ac3 = false, bool Eac3 = false, int MaxAudioChannels = 2,
        bool HevcMain10 = false, bool Av110Bit = false, bool HeAac = false, bool DolbyVision = false,
        bool Mkv = false, bool Flac = false)
    {
        /// <summary>The pre-§14 universal baseline: H.264 in MPEG-TS, stereo, nothing fancy.</summary>
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

    /// <summary>One configured Jellyfin library and the on-disk folders it covers
    /// (<see cref="JellyfinApi.GetVirtualFoldersAsync"/>). Only the three fields the family-library
    /// exclusion reads.</summary>
    public class JellyfinVirtualFolder
    {
        public string? Name { get; set; }

        /// <summary>The library's item id — what <c>PhotosJellyfinLibraryId</c> holds.</summary>
        public string? ItemId { get; set; }

        public List<string> Locations { get; set; } = new();
    }

    public class JellyfinItem
    {
        public string Id { get; set; } = default!;
        public string? Name { get; set; }
        public string? Path { get; set; }
        /// <summary>Set when this item is an "extra" (special feature) of another title — "Featurette",
        /// "BehindTheScenes", "DeletedScene", "Trailer", etc. Null for a normal movie/episode/video.</summary>
        public string? ExtraType { get; set; }
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
