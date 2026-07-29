using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using MovieTheater.Core;
using MovieTheater.Db;
using MovieTheater.Services;
using MovieTheater.Services.Jellyfin;
using MovieTheater.Web;

namespace MovieTheater.Controllers
{
    /// <summary>
    /// Streaming control plane (streaming-plan.md §6). Every endpoint requires a
    /// password-verified session (§3.1); Start additionally applies the same age gate
    /// as GetMovie. The data plane is the StreamGateway (§3.3): Start hands the player
    /// a signed capability URL and video bytes never touch this server.
    /// </summary>
    [Authorize(Policy = "StreamingUser")]
    public class StreamController : Controller
    {
        private const long TicksPerSecond = 10_000_000;
        // Past this fraction, resume is "done" — next play starts from the beginning. (This is resume
        // bookkeeping only; it never marks a title Seen — that's a manual user action.)
        private const double ResumeCompleteThreshold = 0.9;
        // The copy path's segment length and the force-encode decision live in Streaming/HlsCopySafety.cs
        // (dependency-free so the tests can link it). Aliased here because both are read all over this
        // controller and by ProbeKeyframesCommand.
        internal const double CopyHlsSegmentSeconds = Streaming.HlsCopySafety.CopySegmentSeconds;

        private readonly MovieDb movieDb;
        private readonly JellyfinApi jellyfin;
        private readonly MovieTheaterConfiguration config;
        private readonly ILogger<StreamController> logger;
        private readonly Streaming.TranscodeSessionRegistry transcodeSessions;

        public StreamController(MovieDb movieDb, JellyfinApi jellyfin, MovieTheaterConfiguration config, ILogger<StreamController> logger,
            Streaming.TranscodeSessionRegistry transcodeSessions)
        {
            this.movieDb = movieDb;
            this.jellyfin = jellyfin;
            this.config = config;
            this.logger = logger;
            this.transcodeSessions = transcodeSessions;
        }

        private int? GetCurrentUserId()
        {
            var claim = User.FindFirst(ClaimTypes.NameIdentifier);
            return claim != null && int.TryParse(claim.Value, out var id) ? id : null;
        }

        // A stream is "English" by its language tag (eng/en) or, failing that, a DisplayTitle that
        // leads with "English" — covers files whose audio carries a title but no language code.
        private static bool IsEnglish(JellyfinPlaybackStream s)
        {
            var lang = s.Language?.Trim().ToLowerInvariant();
            if (lang == "en" || lang == "eng" || lang == "english")
                return true;
            var title = s.DisplayTitle?.Trim();
            return title != null && title.StartsWith("English", StringComparison.OrdinalIgnoreCase);
        }

        // Director's commentary and audio-description tracks carry an English language tag too, so a
        // foreign film whose only English audio is a commentary must NOT auto-switch to it — that's a
        // worse default than the original language. Excluded from the auto-pick; still hand-selectable.
        private static bool IsCommentaryOrDescription(JellyfinPlaybackStream s)
        {
            var title = s.DisplayTitle;
            return title != null
                && (title.Contains("commentary", StringComparison.OrdinalIgnoreCase)
                    || title.Contains("description", StringComparison.OrdinalIgnoreCase));
        }

        // PGS (HDMV Presentation Graphic Stream — Blu-ray bitmap subtitles): rendered client-side by
        // libpgs as a canvas overlay, so it's delivered as an external .sup and never burned in (the
        // video stays copied). Jellyfin reports the codec as "pgssub".
        private static bool IsPgsSubtitle(JellyfinPlaybackStream s) =>
            s.Codec != null && s.Codec.Equals("pgssub", StringComparison.OrdinalIgnoreCase);

        // ASS/SSA (Advanced SubStation Alpha): a text subtitle, but delivered RAW (not flattened to WebVTT)
        // and rendered client-side by libass so its typesetting survives. Jellyfin reports codec "ass"/"ssa".
        private static bool IsAssSubtitle(JellyfinPlaybackStream s) =>
            s.Codec != null && (s.Codec.Equals("ass", StringComparison.OrdinalIgnoreCase)
                || s.Codec.Equals("ssa", StringComparison.OrdinalIgnoreCase));

        /// <summary>
        /// This viewer's Jellyfin device identity. Jellyfin keys a session by Client+DeviceId, so while
        /// every viewer shared one id they all collapsed into a single session: <c>/Sessions</c> reported
        /// one viewer and ≤1 transcode no matter how many were really running, and the dashboard couldn't
        /// say who was watching what. The client's own token gives one session per screen; a client too old
        /// to send one falls back to per-account, which at least separates different people.
        ///
        /// This does NOT separate two viewers' segment files — Jellyfin leaves DeviceId out of the
        /// TranscodingUrl, so the ffmpeg output directory is keyed without it.
        /// </summary>
        private JellyfinApi.JellyfinDevice? DeviceFor(int? userId, string? deviceToken)
        {
            var clean = new string((deviceToken ?? string.Empty).Where(char.IsLetterOrDigit).Take(40).ToArray());
            var id = clean.Length >= 8
                ? $"{JellyfinApi.DeviceId}-{clean}"
                : userId is int uid ? $"{JellyfinApi.DeviceId}-u{uid}" : null;
            if (id == null)
                return null; // nothing to identify this viewer by → the site-wide identity
            // A readable label so Jellyfin's session list names the person, not just a hash.
            var name = User.Identity?.Name;
            return new JellyfinApi.JellyfinDevice(id, string.IsNullOrWhiteSpace(name) ? "site" : name);
        }

        public class StartRequest
        {
            public int? MovieId { get; set; }                 // legacy: a movie to play (its Primary file)
            public int? PlayableId { get; set; }             // generic: any title's Playable (movie / episode / misc)
            public int? MediaFileId { get; set; }            // a specific Part / Variant / Extra to play
            public long? MaxBitrateBps { get; set; }
            public int? AudioStreamIndex { get; set; }
            public int? SubtitleStreamIndex { get; set; }
            public double? StartSeconds { get; set; }

            // Force a video re-encode instead of a stream-copy/remux. The channel player sets this only
            // as an escalation: when a mid-program join keeps failing to seek (a copy stream whose source
            // keyframe index doesn't map to the join point), a real re-encode inserts its own keyframes
            // so the seek lands. Costs a transcode, so it's off by default.
            public bool ForceTranscode { get; set; }

            // Client-detected decode capabilities (§14.1). Absent/false = the safe
            // H.264/TS baseline, so an old or non-reporting client still plays.
            public bool SupportsHevc { get; set; }
            public bool SupportsAv1 { get; set; }
            public bool SupportsHdr { get; set; }
            public bool SupportsFmp4 { get; set; }
            public bool SupportsMp3 { get; set; }   // MSE can decode MP3 audio (Chrome/Safari yes, Firefox no)
            public bool SupportsAc3 { get; set; }   // MSE decodes Dolby Digital (AC-3) → copy/keep surround
            public bool SupportsEac3 { get; set; }  // MSE decodes Dolby Digital Plus (E-AC-3)
            public int? MaxAudioChannels { get; set; } // output channels the client can emit (5.1 = 6)
            public bool SupportsHevcMain10 { get; set; }  // MSE decodes 10-bit HEVC (Main 10) → may copy it
            public bool SupportsAv110Bit { get; set; }    // MSE decodes 10-bit AV1 → may copy HDR/10-bit AV1
            public bool SupportsHeAac { get; set; }       // MSE decodes HE-AAC (SBR); else HE-AAC must transcode
            public bool SupportsDolbyVision { get; set; } // decodes Dolby Vision → DOVI ranges may pass through
            public bool SupportsMkv { get; set; }         // <video> can play a Matroska container (Chromium yes, Firefox excluded) → direct-play MKV
            public bool SupportsFlac { get; set; }        // decodes FLAC audio → direct-play/copy Blu-ray-remux tracks instead of forcing an HLS session

            // A stable per-browser id (see DeviceFor). Without it every viewer collapses into one
            // Jellyfin session, so the dashboard can't tell who is watching what.
            public string? DeviceToken { get; set; }

            public ClientCapabilities ToCapabilities() =>
                new(SupportsHevc, SupportsAv1, SupportsHdr, SupportsFmp4, SupportsMp3,
                    SupportsAc3, SupportsEac3, MaxAudioChannels ?? 2,
                    SupportsHevcMain10, SupportsAv110Bit, SupportsHeAac, SupportsDolbyVision,
                    SupportsMkv, SupportsFlac);
        }

        [HttpPost("/API/Stream/Start")]
        public async Task<IActionResult> Start([FromBody] StartRequest request)
        {
          try
          {
            // Without [ApiController], an unbindable body yields a null model rather than an auto-400.
            if (request == null)
                return BadRequest(new { message = "Invalid request." });
            if (string.IsNullOrEmpty(config.StreamGatewayBaseUrl) || string.IsNullOrEmpty(config.StreamTokenSecret))
                return StatusCode(501, new { message = "Streaming is not configured on this server." });

            var userId = GetCurrentUserId();
            if (userId == null)
                return Unauthorized();

            // Resolve which Playable to stream: an explicit PlayableId (episode / misc / a movie's),
            // else the legacy MovieId → its Playable. A specific MediaFileId plays that exact
            // Part/Variant/Extra; otherwise the Playable's Primary (Role order) is chosen.
            var playableId = request.PlayableId
                ?? await movieDb.Movies.Where(m => m.id == request.MovieId).Select(m => m.PlayableId).FirstOrDefaultAsync();
            if (playableId == null)
                return NotFound(new { message = "Nothing to play." });

            var ratingId = await ResolveEffectiveRatingIdAsync(playableId.Value);

            // Age gate: the exact browse-side rule (GetMovie), so the two can't drift.
            if (!await PassesAgeGateAsync(userId.Value, ratingId))
                return StatusCode(403, new { message = "This title isn't available on your account." });

            var fileQuery = movieDb.MediaFiles
                .Where(f => f.PlayableId == playableId.Value && f.JellyfinItemId != null && f.MissingSinceUtc == null);
            if (request.MediaFileId != null)
                fileQuery = fileQuery.Where(f => f.Id == request.MediaFileId.Value);
            var file = await fileQuery
                .OrderBy(f => f.Role)   // prefer the Primary feature over any Part/Variant/Extra
                .ThenBy(f => f.Id)
                .FirstOrDefaultAsync();
            if (file?.JellyfinItemId == null)
                return NotFound(new { message = "This title has no playable file." });

            // Optional concurrency guard — a friendly "theater full" beats a melted GPU. Counted from our
            // own in-app session registry rather than Jellyfin's /Sessions: it needs no round trip here and
            // counts only the sessions we started. Direct-play sessions don't count.
            if (config.StreamingMaxConcurrentTranscodes > 0
                && transcodeSessions.ActiveTranscodeCount() >= config.StreamingMaxConcurrentTranscodes)
            {
                return StatusCode(503, new { message = "The theater is full — too many streams are running. Try again in a few minutes." });
            }

            var startTicks = (long)((request.StartSeconds ?? 0) * TicksPerSecond);
            // Direct play serves the whole original file, so it can't honor a burned-in subtitle:
            // fall back to a transcode. ForceTranscode (the channel mid-join escalation for a title
            // whose keyframe index breaks the copy seek) also rules out direct play: only a real
            // re-encode lays down seekable keyframes. An audio selection does NOT rule it out here —
            // whether it actually blocks direct play depends on whether it names a non-default track,
            // which is only knowable after Jellyfin describes the streams (below).
            var allowDirectPlay = request.SubtitleStreamIndex == null && !request.ForceTranscode;
            var device = DeviceFor(userId.Value, request.DeviceToken);
            JellyfinPlaybackInfoResult info;
            try
            {
                info = await jellyfin.GetPlaybackInfoAsync(
                    file.JellyfinItemId, request.MaxBitrateBps, request.AudioStreamIndex, request.SubtitleStreamIndex,
                    startTicks, request.ToCapabilities(), allowDirectPlay, device: device);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Jellyfin PlaybackInfo failed for playable {PlayableId}", playableId);
                return StatusCode(502, new { message = "Could not reach the media server." });
            }

            var source = info.MediaSources[0];

            // The track that plays when nobody pins one: the container default, else the first.
            // Direct play and an unpinned transcode both land on this track.
            var allAudioStreams = source.MediaStreams.Where(s => s.Type == "Audio").ToList();
            var defaultAudioIndex = (allAudioStreams.FirstOrDefault(s => s.IsDefault) ?? allAudioStreams.FirstOrDefault())?.Index;

            // Which audio track should actually play: the caller's explicit pick, else the English
            // auto-default — when the caller expressed no preference and the default track isn't
            // English while a non-commentary English track exists, switch to it. Clients keep sending
            // no preference, so the auto-default re-applies on every start (incl. each channel advance).
            int? effectiveAudioIndex = request.AudioStreamIndex;
            if (effectiveAudioIndex == null && allAudioStreams.Count > 1)
            {
                var playing = allAudioStreams.FirstOrDefault(s => s.IsDefault) ?? allAudioStreams[0];
                if (!IsEnglish(playing)
                    && allAudioStreams.FirstOrDefault(s => IsEnglish(s) && !IsCommentaryOrDescription(s)) is { } english)
                    effectiveAudioIndex = english.Index;
            }

            // A selection naming the track that would play anyway is a no-op: don't pin it, so the
            // stream stays eligible for direct play (and the one-call path). This is what lets a viewer
            // re-select the default track — e.g. Japanese on a subbed anime, overriding the English
            // auto-default — without paying for a transcode. selectedAudioIndex (below) still reports
            // the default, so the player highlights the right entry either way.
            if (effectiveAudioIndex != null && effectiveAudioIndex == defaultAudioIndex)
                effectiveAudioIndex = null;

            // Pinning a track needs a SECOND PlaybackInfo call carrying the media source id. Jellyfin's
            // MediaInfoHelper only applies the requested AudioStreamIndex/SubtitleStreamIndex when the request
            // also names the source (`string.Equals(mediaSourceId, mediaSource.Id)`); the first call can't —
            // the id isn't known until Jellyfin answers. Without it BOTH indices were silently dropped and the
            // TranscodingUrl came back on the container's default audio, which is why picking a language (or the
            // English auto-default) never actually swapped the audio. Only pay for the extra round trip when a
            // track is actually selected — the no-preference case (most starts, and the only one that can direct
            // play) still resolves in one call.
            if ((effectiveAudioIndex != null || request.SubtitleStreamIndex != null)
                && !string.IsNullOrEmpty(source.Id))
            {
                allowDirectPlay = false;   // a pinned track can't be served by handing over the original file
                try
                {
                    info = await jellyfin.GetPlaybackInfoAsync(
                        file.JellyfinItemId, request.MaxBitrateBps, effectiveAudioIndex, request.SubtitleStreamIndex,
                        startTicks, request.ToCapabilities(), allowDirectPlay, source.Id, device);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Jellyfin PlaybackInfo (pinned tracks) failed for playable {PlayableId}", playableId);
                    return StatusCode(502, new { message = "Could not reach the media server." });
                }
                source = info.MediaSources[0];
            }

            // Serve the original file (no ffmpeg) when the browser can play it as-is and the bitrate
            // fits the chosen cap — instant start, zero GPU. Remote viewers on a capped rung whose
            // file is too big still get a transcode below. Range requests through the gateway make
            // mid-file joins (channels) and seeking work for faststart mp4.
            var directPlay = allowDirectPlay
                && source.SupportsDirectPlay
                && !string.IsNullOrEmpty(source.Container)
                && (request.MaxBitrateBps == null || (source.Bitrate ?? long.MaxValue) <= request.MaxBitrateBps.Value);

            if (!directPlay && string.IsNullOrEmpty(source.TranscodingUrl))
                return StatusCode(502, new { message = "Jellyfin did not return a playable stream." });

            // Mint the capability: one movie, one session, bounded life (§3.3). Expiry =
            // duration × 1.5 + 4h, so a long evening survives but a leak goes stale.
            var durationTicks = file.DurationTicks ?? source.RunTimeTicks ?? 0;
            var lifetimeSeconds = (long)(durationTicks / TicksPerSecond * 1.5) + 4 * 3600;
            var token = StreamCapabilityToken.Mint(config.StreamTokenSecret, new StreamCapabilityToken.Payload(
                userId.Value, playableId.Value, info.PlaySessionId, file.JellyfinItemId,
                DateTimeOffset.UtcNow.ToUnixTimeSeconds() + lifetimeSeconds));

            string ToGatewayUrl(string jellyfinRelativeUrl) =>
                $"{config.StreamGatewayBaseUrl!.TrimEnd('/')}/s/{token}{StripApiKey(jellyfinRelativeUrl)}";

            var audioStreamList = source.MediaStreams.Where(s => s.Type == "Audio").ToList();
            var audioTracks = audioStreamList
                .Select(s => new { index = s.Index, label = s.DisplayTitle ?? s.Codec ?? $"Track {s.Index}", language = s.Language, channels = s.Channels })
                .ToList();

            // What's actually playing — an explicit/auto English pick, else the container default
            // (or the first track) — so the player can highlight the live audio track in its menu.
            var selectedAudioIndex = effectiveAudioIndex
                ?? (audioStreamList.FirstOrDefault(s => s.IsDefault) ?? audioStreamList.FirstOrDefault())?.Index;

            var subtitleTracks = source.MediaStreams
                .Where(s => s.Type == "Subtitle")
                .Select(s =>
                {
                    bool isPgs = IsPgsSubtitle(s);
                    bool isAss = IsAssSubtitle(s);
                    return new
                    {
                        index = s.Index,
                        label = s.DisplayTitle ?? s.Language ?? $"Subtitle {s.Index}",
                        language = s.Language,
                        // How the client renders it: "text" = sidecar WebVTT via <track>; "ass" = raw ASS
                        // drawn client-side by libass; "image-pgs" = external .sup drawn by libpgs (all three
                        // keep the video copied); "image-burn" = VobSub/DVB with no client renderer, burned
                        // in server-side (deliveryUrl null → the player restarts with subtitleStreamIndex).
                        kind = s.IsTextSubtitleStream ? (isAss ? "ass" : "text") : (isPgs ? "image-pgs" : "image-burn"),
                        deliveryUrl = s.DeliveryUrl != null && (s.IsTextSubtitleStream || isPgs)
                            ? ToGatewayUrl(s.DeliveryUrl)
                            : null,
                    };
                })
                .ToList();

            var resume = await movieDb.MoviePlaybackProgresses
                .Where(p => p.UserID == userId.Value && p.PlayableId == playableId.Value && !p.Completed)
                .Select(p => (long?)p.PositionTicks)
                .FirstOrDefaultAsync();

            var sourceVideoStream = source.MediaStreams.FirstOrDefault(s => s.Type == "Video");
            var sourceVideoCodec = sourceVideoStream?.Codec;
            // The source's true frame rate, so the client can offer a frame-rate subtitle-sync fix
            // (an external sub authored for a different fps drifts linearly — no constant delay fixes it).
            var videoFrameRate = sourceVideoStream?.RealFrameRate ?? sourceVideoStream?.AverageFrameRate;

            string playbackUrl;
            bool isHls;
            bool videoIsCopied;
            string? outputVideoCodec;
            if (directPlay)
            {
                // Original file via Jellyfin's static endpoint — the player downloads it directly
                // (range requests) with no transcode. mediaSourceId pins which source; the gateway
                // injects the api key and confines the path to this item.
                playbackUrl = ToGatewayUrl(
                    $"/Videos/{file.JellyfinItemId}/stream.{source.Container}?static=true&mediaSourceId={Uri.EscapeDataString(source.Id)}");
                isHls = false;
                videoIsCopied = true; // nothing is re-encoded
                outputVideoCodec = sourceVideoCodec;
            }
            else
            {
                // A burned-in image subtitle forces ffmpeg to rasterize the sub onto every frame, so the
                // video is necessarily re-encoded — even though we force the burn via the params we
                // re-append below (see note) rather than through Jellyfin's negotiation, so its
                // TranscodeReasons often omits a "Video" reason. Detect the burn-in case up front and
                // treat the video as not-copied, so the "Playing" readout honestly reports a transcode.
                int? burnInImageIndex = null;
                if (request.SubtitleStreamIndex is int subIndex)
                {
                    var burnStream = source.MediaStreams.FirstOrDefault(s => s.Type == "Subtitle" && s.Index == subIndex);
                    // Burn only image subs we can't render client-side: text rides as WebVTT and PGS is drawn
                    // by libpgs (both keep the video copied), so neither is ever burned.
                    if (burnStream != null && !burnStream.IsTextSubtitleStream && !IsPgsSubtitle(burnStream))
                        burnInImageIndex = subIndex;
                }
                var burningInSubtitle = burnInImageIndex != null;

                // What Jellyfin would do left alone: copy the video unless it named a Video transcode
                // reason (or we're burning a sub in, which necessarily re-encodes).
                var wouldCopy = !burningInSubtitle
                    && (source.TranscodeReasons == null
                        || !source.TranscodeReasons.Any(r => r.Contains("Video", StringComparison.OrdinalIgnoreCase)));

                // See Streaming/HlsCopySafety.ShouldForceEncode for the mechanism and why a from-the-start
                // session is left on the (lossless, free) copy path.
                var joinsMidFile = startTicks > 0;
                var forceEncode = Streaming.HlsCopySafety.ShouldForceEncode(
                    request.ForceTranscode, wouldCopy, joinsMidFile, file.KeyframeIntervalSeconds);
                if (forceEncode && !request.ForceTranscode)
                    logger.LogInformation(
                        "Forcing re-encode: MediaFile {MediaFileId} joins mid-file at {StartSeconds}s and its keyframe spacing {Spacing}s exceeds the {SegmentSeconds}s copy segment length",
                        file.Id, startTicks / (double)TicksPerSecond, file.KeyframeIntervalSeconds, CopyHlsSegmentSeconds);
                // The case the gate deliberately lets through, logged so the tradeoff is visible if a
                // freeze ever shows up on a from-the-start session.
                else if (wouldCopy && !joinsMidFile && file.KeyframeIntervalSeconds > CopyHlsSegmentSeconds)
                    logger.LogDebug(
                        "Copying MediaFile {MediaFileId} despite {Spacing}s keyframe spacing: opens at 0, so no restart can renumber it",
                        file.Id, file.KeyframeIntervalSeconds);

                // A forced encode is never a copy, so isDirectStream and the codec readout stay honest.
                videoIsCopied = wouldCopy && !forceEncode;
                // The codec the player actually receives: when the video is copied it's the source
                // codec; when re-encoded it's the first of the negotiated list (the encode target).
                // The transcoding url only carries the candidate list, so a copied H.264 source to
                // an HEVC-capable client would otherwise misread "hevc".
                outputVideoCodec = videoIsCopied
                    ? sourceVideoCodec ?? VideoCodecFromTranscodingUrl(source.TranscodingUrl)
                    : VideoCodecFromTranscodingUrl(source.TranscodingUrl);
                var transcodingUrl = source.TranscodingUrl;
                // Forced re-encode: tell Jellyfin not to stream-copy the video (CanStreamCopyVideo
                // short-circuits on this), so ffmpeg emits its own keyframes and a mid-program seek
                // lands — copy-mode seeking depends on the source's own keyframe index, which some
                // rips lack. Reached either from the probed keyframe spacing above (server-side, before
                // the first freeze) or from the client's escalation after the cheap copy path has looped.
                if (forceEncode)
                    transcodingUrl += "&AllowVideoStreamCopy=false";
                // Fallback for a TranscodingUrl that carries no subtitle params even though an image subtitle
                // is selected to be burned in (SubtitleDeliveryMethod "Encode") — without them the transcode
                // still runs (container/audio reasons) but ffmpeg never paints the subtitle, so the sub
                // silently fails to appear. Normally moot now that the pinned second call makes Jellyfin emit
                // them itself; hence the "already present" guard — appending a duplicate SubtitleStreamIndex
                // would bind as "3,3" and fail the int? parse on Jellyfin's side. Guarded to image subs too:
                // text subs ride as sidecar WebVTT and are never burned.
                if (burnInImageIndex is int burnIndex
                    && !transcodingUrl.Contains("SubtitleStreamIndex=", StringComparison.OrdinalIgnoreCase))
                    transcodingUrl += $"&SubtitleStreamIndex={burnIndex}&SubtitleMethod=Encode";
                playbackUrl = ToGatewayUrl(transcodingUrl);
                isHls = true;
            }

            // Track this session for the concurrency guard. Direct play spawns no ffmpeg, so it's
            // registered as a non-transcode (present for lifecycle symmetry, never counted).
            transcodeSessions.Register(info.PlaySessionId, isTranscode: !directPlay);

            return Ok(new
            {
                playSessionId = info.PlaySessionId,
                hlsUrl = playbackUrl,
                // false → the player loads it as a progressive file (direct play), not via hls.js.
                isHls,
                durationTicks,
                isDirectStream = videoIsCopied,
                // The codec the player will actually receive (copied or encoded) — drives the
                // "HEVC"/"Direct" readout and confirms §14.1 negotiation worked.
                videoCodec = outputVideoCodec,
                videoFrameRate,
                audioTracks,
                subtitleTracks,
                // The tracks currently playing, so the player highlights them and (for audio) reflects
                // the server-side English auto-default without the client having to re-derive it.
                selectedAudioIndex,
                selectedSubtitleIndex = request.SubtitleStreamIndex,
                resumePositionTicks = resume,
            });
          }
          catch (Exception ex)
          {
              logger.LogError(ex, "Stream/Start failed");
              return StatusCode(500, new { message = "The stream could not be started." });
          }
        }

        public class ProgressRequest
        {
            public string PlaySessionId { get; set; }
            public int? MovieId { get; set; }
            public int? PlayableId { get; set; }
            public int? MediaFileId { get; set; }   // the exact file in play (a Part/Variant/Extra) — for the right Jellyfin item
            public long PositionTicks { get; set; }
            public bool Paused { get; set; }
            /// <summary>TV-channel playback: report to Jellyfin but write no resume/Seen.</summary>
            public bool Passive { get; set; }
            /// <summary>This browser's device token — the report must reach the session under the same
            /// device identity it was started with (see <see cref="DeviceFor"/>).</summary>
            public string? DeviceToken { get; set; }
        }

        [HttpPost("/API/Stream/Progress")]
        public async Task<IActionResult> Progress([FromBody] ProgressRequest request)
        {
            var userId = GetCurrentUserId();
            if (userId == null || string.IsNullOrEmpty(request?.PlaySessionId))
                return BadRequest();

            // Keep-alive for the concurrency guard (fires paused or playing).
            transcodeSessions.Touch(request.PlaySessionId);

            var playableId = request.PlayableId
                ?? await movieDb.Movies.Where(m => m.id == request.MovieId).Select(m => m.PlayableId).FirstOrDefaultAsync();
            if (playableId == null)
                return Ok(new { success = true });

            var itemId = await ItemIdForFileOrPlayableAsync(request.MediaFileId, playableId.Value);
            if (itemId != null)
            {
                try
                {
                    await jellyfin.ReportPlaybackProgressAsync(itemId, request.PlaySessionId, request.PositionTicks, request.Paused,
                        DeviceFor(userId.Value, request.DeviceToken));
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Jellyfin progress report failed for session {Session}", request.PlaySessionId);
                }
            }

            if (!request.Passive)
            {
                // Resume progress hangs off the Playable (works for movie / episode / misc alike).
                var progress = await movieDb.MoviePlaybackProgresses
                    .SingleOrDefaultAsync(p => p.UserID == userId.Value && p.PlayableId == playableId.Value);
                var durationTicks = await movieDb.MediaFiles
                    .Where(f => f.PlayableId == playableId.Value && f.DurationTicks != null)
                    .Select(f => f.DurationTicks)
                    .FirstOrDefaultAsync() ?? 0;

                if (progress == null)
                {
                    progress = new MoviePlaybackProgress { UserID = userId.Value, PlayableId = playableId.Value };
                    movieDb.MoviePlaybackProgresses.Add(progress);
                }
                progress.PositionTicks = request.PositionTicks;
                progress.DurationTicks = durationTicks;
                progress.UpdatedUtc = DateTime.UtcNow;

                // Reaching ~the end just closes out resume (next time starts from the beginning, not at
                // 99%). Marking a title *Seen* is a deliberate user action only (the Seen button /
                // SetViewingState) — playback never writes a Viewing row on its own.
                if (durationTicks > 0 && request.PositionTicks >= durationTicks * ResumeCompleteThreshold && !progress.Completed)
                    progress.Completed = true;

                await movieDb.SaveChangesAsync();
            }

            return Ok(new { success = true });
        }

        public class StopRequest
        {
            public string PlaySessionId { get; set; }
            public int? MovieId { get; set; }
            public int? PlayableId { get; set; }
            public int? MediaFileId { get; set; }
            /// <summary>This browser's device token, so the stop is recorded against the session that
            /// actually started (see <see cref="DeviceFor"/>). The kill itself matches on playSessionId.</summary>
            public string? DeviceToken { get; set; }
        }

        [HttpPost("/API/Stream/Stop")]
        public async Task<IActionResult> Stop([FromBody] StopRequest request)
        {
            if (string.IsNullOrEmpty(request?.PlaySessionId))
                return BadRequest();

            transcodeSessions.Remove(request.PlaySessionId);

            // Beacons on tab-close still carry the cookie, so the account fallback resolves even for a
            // client too old to send a token.
            var device = DeviceFor(GetCurrentUserId(), request.DeviceToken);

            var playableId = request.PlayableId
                ?? await movieDb.Movies.Where(m => m.id == request.MovieId).Select(m => m.PlayableId).FirstOrDefaultAsync();
            var itemId = playableId == null ? null : await ItemIdForFileOrPlayableAsync(request.MediaFileId, playableId.Value);
            try
            {
                if (itemId != null)
                    await jellyfin.ReportPlaybackStoppedAsync(itemId, request.PlaySessionId, 0, device);
                await jellyfin.StopActiveEncodingsAsync(request.PlaySessionId, device);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Jellyfin stop failed for session {Session}", request.PlaySessionId);
            }

            return Ok(new { success = true });
        }

        private async Task<string> ItemIdForPlayableAsync(int playableId) =>
            await movieDb.MediaFiles
                .Where(f => f.PlayableId == playableId && f.JellyfinItemId != null)
                .OrderBy(f => f.Role).ThenBy(f => f.Id)
                .Select(f => f.JellyfinItemId)
                .FirstOrDefaultAsync();

        // The exact file's Jellyfin item when a specific Part/Variant/Extra is in play; otherwise the
        // Playable's Primary. Keeps Progress/Stop reporting against the item the session actually opened.
        private async Task<string> ItemIdForFileOrPlayableAsync(int? mediaFileId, int playableId)
        {
            if (mediaFileId != null)
            {
                var fileItem = await movieDb.MediaFiles
                    .Where(f => f.Id == mediaFileId.Value && f.JellyfinItemId != null)
                    .Select(f => f.JellyfinItemId)
                    .FirstOrDefaultAsync();
                if (fileItem != null) return fileItem;
            }
            return await ItemIdForPlayableAsync(playableId);
        }

        // The owning title's EFFECTIVE MPA rating id (real cert → legacy → inferred), for the age
        // gate. A movie uses its own; an episode inherits its series'; a misc video inherits its
        // related movie/series (so extras/shorts are gated like the title they hang off, not left
        // wide open). A misc video with no related title stays unrestricted (id 0).
        private async Task<int> ResolveEffectiveRatingIdAsync(int playableId)
        {
            var movie = await movieDb.Movies.Where(m => m.PlayableId == playableId)
                .Select(m => new { m.MpaaRating, m.Rating, m.MpaaRatingInferred }).FirstOrDefaultAsync();
            if (movie != null)
                return RatingGate.EffectiveMpaRatingId(movieDb, movie.MpaaRating, movie.Rating, movie.MpaaRatingInferred);

            var seriesId = await movieDb.Episodes.Where(e => e.PlayableId == playableId)
                .Select(e => e.SeriesId).FirstOrDefaultAsync();
            if (seriesId != null)
            {
                var s = await movieDb.Series.Where(x => x.Id == seriesId.Value)
                    .Select(x => new { x.MpaaRating, x.Rating, x.MpaaRatingInferred }).FirstOrDefaultAsync();
                if (s != null)
                    return RatingGate.EffectiveMpaRatingId(movieDb, s.MpaaRating, s.Rating, s.MpaaRatingInferred);
            }

            // Misc video: prefer its own inferred rating (a standalone art piece is rated directly);
            // otherwise inherit the related movie/series rating so an extra can't be streamed by a
            // child whose account can't see the parent title.
            var misc = await movieDb.MiscVideos.Where(mv => mv.PlayableId == playableId)
                .Select(mv => new { mv.MpaaRatingInferred, mv.RelatedMovieId, mv.RelatedSeriesId }).FirstOrDefaultAsync();
            if (misc != null)
            {
                if (!string.IsNullOrWhiteSpace(misc.MpaaRatingInferred))
                    return RatingGate.MpaRatingIdFor(movieDb, misc.MpaaRatingInferred);
                if (misc.RelatedMovieId != null)
                {
                    var pm = await movieDb.Movies.Where(m => m.id == misc.RelatedMovieId.Value)
                        .Select(m => new { m.MpaaRating, m.Rating, m.MpaaRatingInferred }).FirstOrDefaultAsync();
                    if (pm != null)
                        return RatingGate.EffectiveMpaRatingId(movieDb, pm.MpaaRating, pm.Rating, pm.MpaaRatingInferred);
                }
                if (misc.RelatedSeriesId != null)
                {
                    var ps = await movieDb.Series.Where(s => s.Id == misc.RelatedSeriesId.Value)
                        .Select(s => new { s.MpaaRating, s.Rating, s.MpaaRatingInferred }).FirstOrDefaultAsync();
                    if (ps != null)
                        return RatingGate.EffectiveMpaRatingId(movieDb, ps.MpaaRating, ps.Rating, ps.MpaaRatingInferred);
                }
            }

            return 0;   // unmapped / orphan misc — unrestricted
        }

        private async Task<bool> PassesAgeGateAsync(int userId, int ratingId)
        {
            int ageRestriction = 100;
            var setting = await movieDb.UserSettings
                .FirstOrDefaultAsync(u => u.SettingKey == "AgeRestriction" && u.UserID == userId);
            if (setting != null && int.TryParse(setting.SettingValue, out var parsed))
                ageRestriction = parsed;
            return ratingId <= ageRestriction;
        }

        /// <summary>The first VideoCodec Jellyfin chose for the HLS output (e.g. "hevc"/"h264"),
        /// read from the transcoding url's query — purely informational for the player readout.</summary>
        private static string? VideoCodecFromTranscodingUrl(string transcodingUrl)
        {
            var queryStart = transcodingUrl.IndexOf('?');
            if (queryStart < 0)
                return null;
            var codec = transcodingUrl[(queryStart + 1)..]
                .Split('&', StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault(p => p.StartsWith("VideoCodec=", StringComparison.OrdinalIgnoreCase));
            return codec?["VideoCodec=".Length..].Split(',')[0] is { Length: > 0 } first ? first : null;
        }

        /// <summary>Removes the api_key query parameter Jellyfin embeds in relative urls — the
        /// browser must never see it; the gateway injects the token server-side instead.</summary>
        private static string StripApiKey(string relativeUrl)
        {
            var queryStart = relativeUrl.IndexOf('?');
            if (queryStart < 0)
                return relativeUrl;
            var path = relativeUrl[..queryStart];
            var kept = relativeUrl[(queryStart + 1)..]
                .Split('&', StringSplitOptions.RemoveEmptyEntries)
                .Where(p => !p.StartsWith("api_key=", StringComparison.OrdinalIgnoreCase)
                         && !p.StartsWith("ApiKey=", StringComparison.OrdinalIgnoreCase));
            var query = string.Join('&', kept);
            return query.Length > 0 ? $"{path}?{query}" : path;
        }
    }
}
