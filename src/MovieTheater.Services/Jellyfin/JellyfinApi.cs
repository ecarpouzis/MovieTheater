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

        /// <summary>The client HttpClient allows up to 2 minutes (right for the library sync sweeps), but the
        /// playback lifecycle calls fire on a ~10s heartbeat — a hung Jellyfin must not stall each beat for
        /// two minutes and stack up requests. Bound those to a few seconds instead.</summary>
        private static readonly TimeSpan LifecycleTimeout = TimeSpan.FromSeconds(5);

        /// <summary>Progress report — keeps Jellyfin's transcode throttling honest.</summary>
        public async Task ReportPlaybackProgressAsync(string itemId, string playSessionId, long positionTicks, bool isPaused, CancellationToken cancel = default)
        {
            EnsureConfigured();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            cts.CancelAfter(LifecycleTimeout);
            using var resp = await httpClient.PostAsJsonAsync("/Sessions/Playing/Progress", new
            {
                ItemId = itemId,
                PlaySessionId = playSessionId,
                PositionTicks = positionTicks,
                IsPaused = isPaused,
            }, JsonOptions, cts.Token);
        }

        public async Task ReportPlaybackStoppedAsync(string itemId, string playSessionId, long positionTicks, CancellationToken cancel = default)
        {
            EnsureConfigured();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            cts.CancelAfter(LifecycleTimeout);
            using var resp = await httpClient.PostAsJsonAsync("/Sessions/Playing/Stopped", new
            {
                ItemId = itemId,
                PlaySessionId = playSessionId,
                PositionTicks = positionTicks,
            }, JsonOptions, cts.Token);
        }

        /// <summary>Kills the ffmpeg process and cleans segments immediately instead of waiting for the idle timeout.</summary>
        public async Task StopActiveEncodingsAsync(string playSessionId, CancellationToken cancel = default)
        {
            EnsureConfigured();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            cts.CancelAfter(LifecycleTimeout);
            using var resp = await httpClient.DeleteAsync(
                $"/Videos/ActiveEncodings?deviceId={Uri.EscapeDataString(DeviceId)}&playSessionId={Uri.EscapeDataString(playSessionId)}", cts.Token);
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

            // Audio: preserve surround up to the client's output channels (5.1 = 6) instead of force-
            // downmixing to stereo. AC-3/E-AC-3 the client can decode join the copy set, so a Dolby
            // surround track rides through losslessly; when a transcode is unavoidable (e.g. DTS) the
            // channel count is kept. A non-reporting client stays at the stereo baseline (MaxChannels 2).
            int maxAudioChannels = Math.Clamp(caps.MaxAudioChannels, 2, 8);
            string directPlayAudio = "aac,mp3" + (caps.Ac3 ? ",ac3" : "") + (caps.Eac3 ? ",eac3" : "");

            // MKV is the dominant library container. Chromium's <video> can play a Matroska file whose
            // codecs it supports (canPlayType('video/x-matroska')); Firefox reports it but preloads the
            // whole file (jellyfin-web #15521), so the client probe excludes it. When advertised, an
            // H.264/HEVC + browser-decodable-audio MKV direct-plays (raw file, no ffmpeg) instead of
            // being remuxed to HLS on every start.
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
            if (caps.Mkv)
            {
                directPlayProfiles.Add(new
                {
                    Container = "mkv",
                    Type = "Video",
                    VideoCodec = caps.Hevc ? "h264,hevc" : "h264",
                    AudioCodec = directPlayAudio,
                });
            }

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
                        AudioCodec = "aac"
                            + (caps.Mp3 ? ",mp3" : "")
                            + (caps.Ac3 ? ",ac3" : "")
                            + (caps.Eac3 ? ",eac3" : ""),
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
        bool Mkv = false)
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
