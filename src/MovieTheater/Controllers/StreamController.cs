using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Claims;
using System.Text.Json;
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
        // Below this, a stored position isn't worth resuming to. Must match WatchPage's own threshold
        // for offering the Resume card — the pre-positioning below is only right if the client agrees.
        private const double ResumeMinimumSeconds = 60;
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
        // worse default than the original language. Nor may one be STARTED on: a disc that flags every
        // audio track default (Space Jam's 4K rip flags both the feature and the commentary) leaves the
        // choice to Jellyfin, which picked the commentary. Excluded from the auto-pick and escaped when
        // it's what would play; still hand-selectable.
        private static bool IsCommentaryOrDescription(JellyfinPlaybackStream s)
        {
            // DisplayTitle normally embeds the raw Title, but a track Jellyfin couldn't decorate can
            // leave it codec-only — read both so a tagged commentary is never missed.
            return Mentions(s.DisplayTitle) || Mentions(s.Title);

            static bool Mentions(string? title) =>
                title != null
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
        /// The frame width a capped rung should actually be encoded at, or null to leave Jellyfin's own
        /// choice alone. Jellyfin infers the output resolution from the bitrate ceiling and its ladder
        /// BOTTOMS OUT AT 720p, so the 1.5 Mbps rung — the menu's "480p", and the rung auto-mobile opens
        /// on — came back as 1280x720 at 1,116,000 bps: well under half what 720p24 needs, and a label
        /// that lies about what's being delivered. Measured on Matilda: The Musical (HEVC/DV, so every
        /// open re-encodes) 2026-08-22: three separate opens at that rung died in Firefox 129/Linux with
        /// MediaError DECODE two segments in, while the 4 Mbps rung played 105 segments straight through
        /// on the same connection minutes either side. Whether the under-bitrated 720p encode is what
        /// Firefox's decoder choked on is unproven — but a rung that claims 480p should encode 480p.
        /// The upper rungs are left alone because Jellyfin already picks 1080p for them.
        /// </summary>
        private static int? MaxWidthForCeiling(long? maxBitrateBps) => maxBitrateBps switch
        {
            null => null,
            <= 2_000_000 => 854,    // "480p" rung — Jellyfin would give 720p
            <= 5_000_000 => 1280,   // "720p" rung — pins what Jellyfin already chose
            _ => null,
        };

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

            // Resume position (per Playable — movie / episode / misc alike). Read BEFORE PlaybackInfo,
            // not just for the response, because it decides where this session's ffmpeg starts.
            var resume = await movieDb.MoviePlaybackProgresses
                .Where(p => p.UserID == userId.Value && p.PlayableId == playableId.Value && !p.Completed)
                .Select(p => (long?)p.PositionTicks)
                .FirstOrDefaultAsync();

            // A resume open sends no StartSeconds — the client learns the resume point FROM this
            // response and then seeks with hls.js. Jellyfin therefore built a TranscodingUrl with no
            // StartTimeTicks, spawned ffmpeg at segment 0, and was killed and respawned with -ss the
            // instant hls.js asked for the resume segment. Measured 2026-08-17: every open burned a
            // throwaway ffmpeg (121-1330 frames of wasted encode) plus 1-3 s of dead time before the
            // real session began. Starting the FIRST spawn at the resume point removes both.
            //
            // Only where the client would actually resume there: the Resume card is offered on a
            // Primary open past ResumeMinimumSeconds, and the stored position is a WHOLE-MOVIE clock,
            // so one running past this file belongs to a later part (the client changes part rather
            // than seeking here) and must not pre-position. "From the beginning" reuses this session
            // without restarting it, so it now pays the one respawn that resume used to pay.
            var resumeStartTicks = request.StartSeconds == null
                && request.MediaFileId == null
                && resume is long resumeTicks
                && resumeTicks > ResumeMinimumSeconds * TicksPerSecond
                && resumeTicks < (file.DurationTicks ?? 0)
                    ? resumeTicks
                    : (long?)null;

            var startTicks = request.StartSeconds != null
                ? (long)(request.StartSeconds.Value * TicksPerSecond)
                : resumeStartTicks ?? 0;
            // Direct play serves the whole original file, so it can't honor a burned-in subtitle:
            // fall back to a transcode. ForceTranscode (the channel mid-join escalation for a title
            // whose keyframe index breaks the copy seek) also rules out direct play: only a real
            // re-encode lays down seekable keyframes. An audio selection does NOT rule it out here —
            // whether it actually blocks direct play depends on whether it names a non-default track,
            // which is only knowable after Jellyfin describes the streams (below).
            // ...and so does an audio track the client can't decode. The device profile already says
            // which audio codecs may be handed over raw, but Jellyfin skips that check on a file whose
            // audio track carries no default flag (see ClientCapabilities.CanDirectPlayAudio for the
            // measurement): a browser without a Dolby decoder was handed such an AC-3 MKV whole and
            // played it silent. Decided here, before the call, because once Jellyfin has answered
            // "direct play" there is no TranscodingUrl to fall back to; declined, it returns the HLS
            // copy — same video bytes, audio re-containered or re-encoded as the client needs. A null
            // AudioCodec (unsynced row) declines too.
            var caps = request.ToCapabilities();
            var allowDirectPlay = request.SubtitleStreamIndex == null && !request.ForceTranscode
                && caps.CanDirectPlayAudio(file.AudioCodec);

            var device = DeviceFor(userId.Value, request.DeviceToken);
            JellyfinPlaybackInfoResult info;
            try
            {
                info = await jellyfin.GetPlaybackInfoAsync(
                    file.JellyfinItemId, request.MaxBitrateBps, request.AudioStreamIndex, request.SubtitleStreamIndex,
                    startTicks, caps, allowDirectPlay, device: device);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Jellyfin PlaybackInfo failed for playable {PlayableId}", playableId);
                return StatusCode(502, new { message = "Could not reach the media server." });
            }

            var source = info.MediaSources[0];

            // The track that plays when nobody pins one. Jellyfin STATES its own choice in
            // DefaultAudioStreamIndex, and that's the one that reaches ffmpeg and that direct play is
            // negotiated against — so take it rather than re-deriving it. The two genuinely disagree:
            // when a file flags more than one audio track default, "first IsDefault stream" is a guess,
            // and on Space Jam's 4K rip (feature + commentary, both flagged default) Jellyfin answers
            // with the COMMENTARY. Fall back to the old derivation only when it states nothing.
            var allAudioStreams = source.MediaStreams.Where(s => s.Type == "Audio").ToList();
            var defaultAudioIndex = source.DefaultAudioStreamIndex is int jellyfinDefault
                    && allAudioStreams.Any(s => s.Index == jellyfinDefault)
                ? jellyfinDefault
                : (allAudioStreams.FirstOrDefault(s => s.IsDefault) ?? allAudioStreams.FirstOrDefault())?.Index;

            // Which audio track should actually play: the caller's explicit pick, else the auto-default.
            // With no preference expressed, two things make the would-play track the wrong one to start
            // on — it isn't English, or it IS English but it's a commentary/description — and the answer
            // to both is the first plain English track. Failing that, escaping a commentary default to
            // any plain track still beats opening on the commentary, while a merely foreign default is
            // left alone (the original language beats no English at all). Clients keep sending no
            // preference, so this re-applies on every start (incl. each channel advance).
            int? effectiveAudioIndex = request.AudioStreamIndex;
            if (effectiveAudioIndex == null && allAudioStreams.Count > 1)
            {
                var playing = allAudioStreams.FirstOrDefault(s => s.Index == defaultAudioIndex) ?? allAudioStreams[0];
                if (!IsEnglish(playing) || IsCommentaryOrDescription(playing))
                {
                    var better = allAudioStreams.FirstOrDefault(s => IsEnglish(s) && !IsCommentaryOrDescription(s))
                        ?? (IsCommentaryOrDescription(playing)
                            ? allAudioStreams.FirstOrDefault(s => !IsCommentaryOrDescription(s))
                            : null);
                    if (better != null)
                        effectiveAudioIndex = better.Index;
                }
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
                        startTicks, caps, allowDirectPlay, source.Id, device);
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

            // What's actually playing — an explicit/auto pick, else the unpinned default resolved above
            // — so the player can highlight the live audio track in its menu. Re-deriving the default
            // here from IsDefault would reintroduce the disagreement with Jellyfin's own choice and
            // highlight a track that isn't the one you're hearing.
            var selectedAudioIndex = effectiveAudioIndex ?? defaultAudioIndex;

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

                // Non-null on this branch: the guard above returns 502 when a non-direct-play source
                // carries no TranscodingUrl, so the copy tests below always have a url to read.
                var transcodingUrl = source.TranscodingUrl!;

                // What Jellyfin would do left alone. Four independent ways the video is NOT copied,
                // and all four must be asked — each catches a case the others miss (see the helpers;
                // the reason list alone is wrong for three of them).
                var wouldCopy = !burningInSubtitle
                    && !NamesVideoTranscodeReason(source)
                    && BitrateCeilingAllowsVideoCopy(transcodingUrl, sourceVideoStream)
                    && OutputCodecAllowsVideoCopy(transcodingUrl, sourceVideoCodec);

                // Copy is safe from ANY join point since the 2026-08-02 keyframe backfill completed:
                // the patched Jellyfin segments every copied session at the source's real keyframes
                // (exact, anchor-free — see .claude/skills/hls-copy-freeze), so a mid-session restart
                // reproduces identical segments and nothing can renumber the timeline. The old
                // mid-file force-encode gate (HlsCopySafety) is gone with the sampled-probe columns
                // it read; ForceTranscode remains as the client's explicit escalation.
                var forceEncode = request.ForceTranscode;

                // A forced encode is never a copy, so isDirectStream and the codec readout stay honest.
                videoIsCopied = wouldCopy && !forceEncode;
                // The codec the player actually receives: when the video is copied it's the source
                // codec; when re-encoded it's the first of the negotiated list (the encode target).
                // The transcoding url only carries the candidate list, so a copied H.264 source to
                // an HEVC-capable client would otherwise misread "hevc".
                outputVideoCodec = videoIsCopied
                    ? sourceVideoCodec ?? VideoCodecFromTranscodingUrl(transcodingUrl)
                    : VideoCodecFromTranscodingUrl(transcodingUrl);
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
                // Pin the frame size on the low rungs (see MaxWidthForCeiling). Jellyfin carries MaxWidth
                // from this url through master.m3u8 into main.m3u8 and the RESOLUTION attribute, so the one
                // append covers the whole session. ONLY on a re-encode: a copied video is never scaled, and
                // naming a width below the source's would turn a copy INTO an encode.
                if (!videoIsCopied
                    && MaxWidthForCeiling(request.MaxBitrateBps) is int maxWidth
                    && !transcodingUrl.Contains("MaxWidth=", StringComparison.OrdinalIgnoreCase))
                    transcodingUrl += $"&MaxWidth={maxWidth}";
                // Tell the patched Jellyfin where playback will START. hls.js fetches the fMP4 init file
                // before its first segment, and stock Jellyfin spawns ffmpeg from the head of the file for
                // it — then kills and respawns at the join segment a moment later (two spawns, 2–5 s dead,
                // on every channel tune and every resume; measured 2026-09-03). The patched init handler
                // (hls-copy-freeze skill, patch #4) reads mtJoinTicks and spawns at that segment. The
                // playlist copies the query string onto every segment URL, so the param rides along;
                // stock Jellyfin ignores it.
                if (startTicks > 0)
                    transcodingUrl += $"&mtJoinTicks={startTicks}";
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
                // The SOURCE video's own bitrate — the number that decides copy vs re-encode (Jellyfin
                // won't copy a video into a ceiling below it). ABR needs it: every rung at or above it
                // delivers the identical copied stream, so dropping onto one is a restart that changes
                // nothing. Null when the source carries no measured bitrate (the ladder then walks
                // rung by rung, as before).
                videoBitrateBps = sourceVideoStream?.BitRate,
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

        /// <summary>How long a self-report is worth keeping.</summary>
        /// <remarks>
        /// This table is diagnostics, not history: a report is read while chasing a live complaint,
        /// and half a year later it says nothing anyone will act on. Six months still spans "it did
        /// this again, like last winter", which is the longest reach anyone has actually needed.
        /// </remarks>
        private static readonly TimeSpan IncidentRetention = TimeSpan.FromDays(180);

        /// <summary>At most this many expired rows are swept per insert.</summary>
        /// <remarks>
        /// The prune rides the only path that GROWS the table, so it costs nothing when nobody is
        /// failing and there is no timer, no background service and no idle work to forget about.
        /// The cap keeps one unlucky report from paying for a year of backlog in a single request —
        /// the incidents arrive faster than they expire whenever it matters, so a small batch per
        /// insert is enough to hold the bound.
        /// </remarks>
        private const int IncidentPruneBatch = 50;

        /// <summary>
        /// Receives a playback failure report the player sends about itself.
        /// </summary>
        /// <remarks>
        /// The video failures worth chasing are the ones nobody can hold still: the picture freezes,
        /// the viewer refreshes (or shrugs and goes to bed), and everything that knew why is gone.
        /// Until now the only witness was on the SERVER — the gateway's access log and the names of
        /// Jellyfin's ffmpeg logs — which reconstructs a session beautifully and only for the failures
        /// somebody thought to ask about soon enough. The player itself, which knew at the time, said
        /// nothing. The music player has reported its own failures for months and it is the reason
        /// the sleeping-phone stall was root-caused at all; this is that instrument, for video.
        ///
        /// <para>It arrives as a <c>sendBeacon</c> with <c>text/plain</c> — deliberately a CORS-simple
        /// request, because the page making it may be mid-freeze or unloading and will not survive a
        /// preflight — which is why the body is read and parsed here rather than model-bound.</para>
        ///
        /// <para>Auth is the controller's own <c>StreamingUser</c> policy: a report is only useful if
        /// the failing session can send it, and every video session already holds that policy (there
        /// is no way to be playing video without it). The payload is capped and the client rate-limits
        /// itself to one a minute with a per-session ceiling.</para>
        /// </remarks>
        [HttpPost("/API/Stream/Incident")]
        public async Task<IActionResult> Incident()
        {
            var userId = GetCurrentUserId();
            if (userId == null) return Unauthorized();

            using var reader = new StreamReader(Request.Body);
            var raw = await reader.ReadToEndAsync();
            if (string.IsNullOrWhiteSpace(raw)) return BadRequest(new { message = "Empty report." });
            // A runaway client must not be able to write unbounded rows.
            if (raw.Length > 256 * 1024) raw = raw.Substring(0, 256 * 1024);

            string kind = "unknown", summary = null, userAgent = null, player = null;
            int? movieId = null, seriesId = null, miscVideoId = null, playableId = null, channelId = null;
            double? positionSeconds = null;
            try
            {
                using var doc = JsonDocument.Parse(raw);
                var root = doc.RootElement;
                if (root.TryGetProperty("kind", out var k)) kind = k.GetString() ?? "unknown";
                if (root.TryGetProperty("summary", out var s)) summary = s.GetString();
                if (root.TryGetProperty("userAgent", out var ua)) userAgent = ua.GetString();
                if (root.TryGetProperty("player", out var p)) player = p.GetString();
                movieId = ReadInt(root, "movieId");
                seriesId = ReadInt(root, "seriesId");
                miscVideoId = ReadInt(root, "miscVideoId");
                playableId = ReadInt(root, "playableId");
                channelId = ReadInt(root, "channelId");
                if (root.TryGetProperty("positionSeconds", out var pos) && pos.ValueKind == JsonValueKind.Number
                    && pos.TryGetDouble(out var posValue))
                    positionSeconds = posValue;
            }
            catch (JsonException)
            {
                // Keep it anyway: a report we can't parse is still evidence that something fired,
                // and the raw payload is the part worth reading.
                kind = "unparseable";
            }

            // Retention, taken immediately before the insert so the bound rides the only path that
            // grows the table. Loaded-then-removed rather than a set-based delete because this same
            // action runs against SQLite under test, where ExecuteDelete cannot translate a Take —
            // and an untaken delete is the unbounded sweep the cap exists to prevent. The removals
            // ride the insert's SaveChanges, so it stays one round trip either way.
            var cutoff = DateTime.UtcNow - IncidentRetention;
            var expired = await movieDb.VideoPlaybackIncidents
                .Where(i => i.CreatedUtc < cutoff)
                .OrderBy(i => i.Id)
                .Take(IncidentPruneBatch)
                .ToListAsync();
            if (expired.Count > 0) movieDb.VideoPlaybackIncidents.RemoveRange(expired);

            movieDb.VideoPlaybackIncidents.Add(new VideoPlaybackIncident
            {
                CreatedUtc = DateTime.UtcNow,
                UserId = userId,
                Kind = Truncate(kind, 40),
                Summary = Truncate(summary, 400),
                Player = Truncate(player, 10),
                MovieId = movieId,
                SeriesId = seriesId,
                MiscVideoId = miscVideoId,
                PlayableId = playableId,
                ChannelId = channelId,
                PositionSeconds = positionSeconds,
                UserAgent = Truncate(userAgent, 400),
                Payload = raw,
            });
            await movieDb.SaveChangesAsync();
            return Ok(new { recorded = true, pruned = expired.Count });
        }

        /// <summary>A numeric property as an int, or null — a client that sends a string, a null or
        /// nothing at all just leaves that id unknown rather than losing the whole report.</summary>
        private static int? ReadInt(JsonElement root, string name) =>
            root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
                && value.TryGetInt32(out var parsed) ? parsed : null;

        private static string Truncate(string s, int max) =>
            string.IsNullOrEmpty(s) ? s : (s.Length <= max ? s : s.Substring(0, max));

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
            => QueryValueFromTranscodingUrl(transcodingUrl, "VideoCodec")?.Split(',')[0] is { Length: > 0 } first
                ? first : null;

        private static string? QueryValueFromTranscodingUrl(string transcodingUrl, string key)
        {
            var queryStart = transcodingUrl.IndexOf('?');
            if (queryStart < 0)
                return null;
            var prefix = key + "=";
            var param = transcodingUrl[(queryStart + 1)..]
                .Split('&', StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault(p => p.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
            return param?[prefix.Length..];
        }

        /// <summary>
        /// Whether Jellyfin named a reason that forces the VIDEO to be re-encoded (as opposed to an
        /// audio- or container-only reason, which leaves the video copied).
        ///
        /// Read from BOTH places it can appear, because the obvious one is empty: against 10.11
        /// <c>MediaSource.TranscodeReasons</c> came back null for every case measured — including a
        /// plain "VideoCodecNotSupported" — while the TranscodingUrl carried the real list in its
        /// query. Reading only the media source silently answered "no video reason, so it's a copy"
        /// for every HLS session ever started, which is how a full re-encode came to report itself as
        /// a stream copy. The url's value is one comma-separated parameter, not a repeated one.
        /// </summary>
        private static bool NamesVideoTranscodeReason(JellyfinPlaybackMediaSource source)
        {
            var reasons = source.TranscodeReasons ?? new List<string>();
            if (source.TranscodingUrl is { } url
                && QueryValueFromTranscodingUrl(url, "TranscodeReasons") is { Length: > 0 } fromUrl)
                reasons = reasons.Concat(Uri.UnescapeDataString(fromUrl).Split(',')).ToList();
            return reasons.Any(r => r.Contains("Video", StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// Whether the bitrate ceiling Jellyfin picked still permits a video stream copy. It refuses to
        /// copy a video into a ceiling below the source's own bitrate — it re-encodes instead, rescaling
        /// and tone-mapping to fit — so a capped rung silently turns a copy into a full encode.
        ///
        /// Neither reason list detects that, which is why this compares numbers instead. Measured against
        /// Space Jam's 4K rip (source video 20,371,866 bps, HEVC Main 10 HDR10, 3840x2076):
        ///   VideoBitrate=7,616,000  → re-encoded, master advertises RESOLUTION=2560x1384 VIDEO-RANGE=SDR
        ///   VideoBitrate=20,616,000 → copied,     master advertises RESOLUTION=3840x2076 VIDEO-RANGE=PQ
        /// Yet <c>MediaSource.TranscodeReasons</c> was NULL for both, and the url's own reason read
        /// "ContainerBitrateExceedsLimit" for both — true either way, since the container total exceeds
        /// the cap whether the fix is to squeeze the video or only the (TrueHD) audio. Only the numbers
        /// separate the two cases, the same way Jellyfin's own CanStreamCopyVideo does.
        ///
        /// Unknown either side (no VideoBitrate param, or a source with no measured bitrate) returns
        /// true and leaves the verdict to the reason check, as before.
        /// </summary>
        private static bool BitrateCeilingAllowsVideoCopy(string transcodingUrl, JellyfinPlaybackStream? sourceVideoStream)
        {
            if (sourceVideoStream?.BitRate is not long sourceBps || sourceBps <= 0)
                return true;
            var requested = QueryValueFromTranscodingUrl(transcodingUrl, "VideoBitrate");
            return !long.TryParse(requested, out var requestedBps) || requestedBps >= sourceBps;
        }

        /// <summary>
        /// Whether the source's own video codec is among the output candidates Jellyfin negotiated.
        /// A codec missing from that list cannot be stream-copied — it has to be re-encoded.
        ///
        /// This exists because the reason list is not merely misplaced, it is INCOMPLETE. Measured on
        /// the same 4K HEVC file against an H.264-only client: at a 30 Mbps ceiling Jellyfin reported
        /// "VideoCodecNotSupported", but at 21 Mbps it reported ONLY "ContainerBitrateExceedsLimit"
        /// and dropped the codec reason entirely — while the master playlist showed a re-encode
        /// either way. The bitrate test can't catch it (the ceiling clears the source), so without
        /// this an H.264-only browser on a capped rung still reports a copy. The candidate list never
        /// lies: it's what ffmpeg is allowed to emit.
        /// </summary>
        private static bool OutputCodecAllowsVideoCopy(string transcodingUrl, string? sourceVideoCodec)
        {
            if (string.IsNullOrEmpty(sourceVideoCodec))
                return true;
            var candidates = QueryValueFromTranscodingUrl(transcodingUrl, "VideoCodec");
            if (string.IsNullOrEmpty(candidates))
                return true;
            return candidates.Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Any(c => c.Trim().Equals(sourceVideoCodec, StringComparison.OrdinalIgnoreCase));
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
