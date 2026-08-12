using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MovieTheater.Core;
using MovieTheater.Services;
using MovieTheater.Services.Jellyfin;

namespace MovieTheater.Photos
{
    /// <summary>What the browser asked to play. A deliberately SMALL capability set compared with
    /// <c>StreamController.StartRequest</c> — this is home video, not the movie stack: there is no
    /// quality ladder, no audio-track politics and no subtitle pipeline to negotiate, and every field
    /// here exists only to decide whether the original file can be handed over untouched.</summary>
    public sealed class PhotoVideoStartRequest
    {
        public int AssetId { get; set; }

        /// <summary>A stable per-browser id. Jellyfin keys a SESSION by Client+DeviceId, so without one
        /// every viewer collapses into a single session and its dashboard cannot say who is watching
        /// what — the same convention <c>StreamController.DeviceFor</c> established.</summary>
        public string? DeviceToken { get; set; }

        public double? StartSeconds { get; set; }

        public long? MaxBitrateBps { get; set; }

        public bool SupportsHevc { get; set; }

        public bool SupportsFmp4 { get; set; }

        public bool SupportsMkv { get; set; }

        public bool SupportsMp3 { get; set; }

        public bool SupportsAc3 { get; set; }

        public bool SupportsEac3 { get; set; }

        public int? MaxAudioChannels { get; set; }

        public ClientCapabilities ToCapabilities() =>
            new ClientCapabilities(
                Hevc: SupportsHevc, Av1: false, Hdr: false, Fmp4: SupportsFmp4, Mp3: SupportsMp3,
                Ac3: SupportsAc3, Eac3: SupportsEac3, MaxAudioChannels: MaxAudioChannels ?? 2,
                HevcMain10: false, Av110Bit: false, HeAac: false, DolbyVision: false,
                Mkv: SupportsMkv, Flac: false);
    }

    /// <summary>The answer, or the reason there isn't one. <see cref="StatusCode"/> is carried rather
    /// than thrown so the controller stays a thin translation to HTTP and the failure modes are
    /// enumerable in one place.</summary>
    public sealed class PhotoVideoStartResult
    {
        public int StatusCode { get; set; } = 200;

        public string? Message { get; set; }

        public string? PlaySessionId { get; set; }

        public string? Url { get; set; }

        /// <summary>False → the player loads it as a progressive file (direct play), not through hls.js.</summary>
        public bool IsHls { get; set; }

        public long DurationTicks { get; set; }

        /// <summary>The original file, handed over with no ffmpeg at all. The common case for a phone
        /// video and the reason this endpoint needs none of the movie stack's machinery.</summary>
        public bool DirectPlay { get; set; }

        public string? VideoCodec { get; set; }

        public static PhotoVideoStartResult Fail(int statusCode, string message) =>
            new PhotoVideoStartResult { StatusCode = statusCode, Message = message };
    }

    /// <summary>
    /// Mints playback for ONE family video (docs/photos-plan.md §2.3: "playback reuses the existing
    /// HLS/player stack via a family-gated stream-start endpoint").
    ///
    /// <para>A seam, for the reason every external dependency in this vertical is one: no test, build
    /// or smoke may contact the live media server, and the controller's own job — checking the gate,
    /// checking the row, refusing an unsynced video — has to be assertable without one.</para>
    /// </summary>
    public interface IPhotoVideoPlayback
    {
        /// <summary>Whether this host can mint at all (gateway base URL, token secret, Jellyfin).
        /// False makes the UI hide the play button rather than offer one that 501s.</summary>
        bool Configured { get; }

        Task<PhotoVideoStartResult> StartAsync(int userId, string? userName, string jellyfinItemId,
            PhotoVideoStartRequest request, CancellationToken cancel = default);
    }

    /// <summary>
    /// The real minter: Jellyfin describes the item, the site signs a capability, and the bytes travel
    /// through the StreamGateway — the same three-step shape as <c>StreamController.Start</c>, and
    /// deliberately the same TOKEN, so the gateway's existing <c>/s/{token}/Videos/…</c> route serves
    /// this with no gateway change and no second implementation of the confinement check.
    ///
    /// <para><b>What is deliberately absent</b> versus the movie path: no age gate (the family gate
    /// upstream is the whole access decision — §2.1 has no rating logic), no resume bookkeeping, no
    /// audio auto-selection, no subtitle delivery, no ABR, no forced re-encode escalation, and no
    /// transcode-concurrency guard. A family video is one file somebody points at; every one of those
    /// exists to solve a problem the movie library has and this collection does not.</para>
    ///
    /// <para><b>The Jellyfin item id is the caller's</b>, read from the <see cref="Db.PhotoAsset"/> row
    /// by the controller after the gate passed. Nothing here accepts an id from a browser — that would
    /// turn a family-gated endpoint into a general-purpose Jellyfin proxy (§2.1's "the UI is never the
    /// gate", applied to the one endpoint that mints a capability).</para>
    /// </summary>
    public sealed class JellyfinPhotoVideoPlayback : IPhotoVideoPlayback
    {
        private const long TicksPerSecond = 10_000_000;

        private readonly JellyfinApi jellyfin;
        private readonly MovieTheaterConfiguration config;

        public JellyfinPhotoVideoPlayback(JellyfinApi jellyfin, MovieTheaterConfiguration config)
        {
            this.jellyfin = jellyfin;
            this.config = config;
        }

        public bool Configured =>
            !string.IsNullOrEmpty(config.StreamGatewayBaseUrl)
            && !string.IsNullOrEmpty(config.StreamTokenSecret)
            && !string.IsNullOrEmpty(config.JellyfinBaseUrl)
            && !string.IsNullOrEmpty(config.JellyfinApiKey);

        public async Task<PhotoVideoStartResult> StartAsync(int userId, string? userName, string jellyfinItemId,
            PhotoVideoStartRequest request, CancellationToken cancel = default)
        {
            if (!Configured)
                return PhotoVideoStartResult.Fail(501, "Video playback is not configured on this server.");

            var startTicks = (long)(Math.Max(0, request.StartSeconds ?? 0) * TicksPerSecond);
            var device = DeviceFor(userId, userName, request.DeviceToken);

            JellyfinPlaybackInfoResult info;
            try
            {
                info = await jellyfin.GetPlaybackInfoAsync(
                    jellyfinItemId, request.MaxBitrateBps, audioStreamIndex: null, subtitleStreamIndex: null,
                    startTimeTicks: startTicks, capabilities: request.ToCapabilities(), enableDirectPlay: true,
                    mediaSourceId: null, device: device, cancel: cancel);
            }
            catch (Exception)
            {
                return PhotoVideoStartResult.Fail(502, "Could not reach the media server.");
            }

            var source = info.MediaSources[0];
            var directPlay = source.SupportsDirectPlay
                             && !string.IsNullOrEmpty(source.Container)
                             && (request.MaxBitrateBps == null
                                 || (source.Bitrate ?? long.MaxValue) <= request.MaxBitrateBps.Value);

            if (!directPlay && string.IsNullOrEmpty(source.TranscodingUrl))
                return PhotoVideoStartResult.Fail(502, "The media server did not return a playable stream.");

            // Expiry = duration × 1.5 + 4h, the streaming lane's rule: long enough for a family to
            // watch an evening of old clips, short enough that a leaked URL goes stale.
            var durationTicks = source.RunTimeTicks ?? 0;
            var lifetimeSeconds = (long)(durationTicks / TicksPerSecond * 1.5) + 4 * 3600;
            var token = StreamCapabilityToken.Mint(config.StreamTokenSecret!, new StreamCapabilityToken.Payload(
                userId,
                // The payload's second field is a MOVIE id and a family video has none. Zero, not a
                // borrowed PhotoAsset id: the gateway confines this route by userId, item id and
                // expiry, and putting a photo id where a movie id belongs would be a number that reads
                // as a movie to anyone who ever inspects a token.
                0,
                info.PlaySessionId, jellyfinItemId,
                DateTimeOffset.UtcNow.ToUnixTimeSeconds() + lifetimeSeconds));

            var videoStream = source.MediaStreams.FirstOrDefault(s => s.Type == "Video");
            var url = directPlay
                ? GatewayUrl(token, $"/Videos/{jellyfinItemId}/stream.{source.Container}?static=true"
                                    + $"&mediaSourceId={Uri.EscapeDataString(source.Id ?? jellyfinItemId)}")
                : GatewayUrl(token, source.TranscodingUrl!);

            return new PhotoVideoStartResult
            {
                PlaySessionId = info.PlaySessionId,
                Url = url,
                IsHls = !directPlay,
                DurationTicks = durationTicks,
                DirectPlay = directPlay,
                VideoCodec = videoStream?.Codec,
            };
        }

        private string GatewayUrl(string token, string jellyfinRelativeUrl) =>
            $"{config.StreamGatewayBaseUrl!.TrimEnd('/')}/s/{token}{StripApiKey(jellyfinRelativeUrl)}";

        /// <summary>This viewer's Jellyfin device identity — the <c>StreamController.DeviceFor</c>
        /// convention, restated rather than shared because that one is a private controller method and
        /// duplicating six lines beats making a controller a dependency.</summary>
        private static JellyfinApi.JellyfinDevice? DeviceFor(int userId, string? userName, string? deviceToken)
        {
            var clean = new string((deviceToken ?? string.Empty).Where(char.IsLetterOrDigit).Take(40).ToArray());
            var id = clean.Length >= 8 ? $"{JellyfinApi.DeviceId}-{clean}" : $"{JellyfinApi.DeviceId}-u{userId}";
            return new JellyfinApi.JellyfinDevice(id, string.IsNullOrWhiteSpace(userName) ? "site" : userName);
        }

        /// <summary>The api key never reaches the browser: the gateway injects the server-held one.</summary>
        private static string StripApiKey(string relativeUrl)
        {
            var queryStart = relativeUrl.IndexOf('?');
            if (queryStart < 0) return relativeUrl;
            var path = relativeUrl.Substring(0, queryStart);
            var kept = relativeUrl.Substring(queryStart + 1)
                .Split(new[] { '&' }, StringSplitOptions.RemoveEmptyEntries)
                .Where(p => !p.StartsWith("api_key=", StringComparison.OrdinalIgnoreCase)
                            && !p.StartsWith("ApiKey=", StringComparison.OrdinalIgnoreCase));
            var query = string.Join("&", kept);
            return query.Length > 0 ? $"{path}?{query}" : path;
        }
    }
}
