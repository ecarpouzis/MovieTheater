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

        private readonly MovieDb movieDb;
        private readonly JellyfinApi jellyfin;
        private readonly MovieTheaterConfiguration config;
        private readonly ILogger<StreamController> logger;

        public StreamController(MovieDb movieDb, JellyfinApi jellyfin, MovieTheaterConfiguration config, ILogger<StreamController> logger)
        {
            this.movieDb = movieDb;
            this.jellyfin = jellyfin;
            this.config = config;
            this.logger = logger;
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

        public class StartRequest
        {
            public int MovieId { get; set; }                 // legacy: a movie to play (its Primary file)
            public int? PlayableId { get; set; }             // generic: any title's Playable (movie / episode / misc)
            public int? MediaFileId { get; set; }            // a specific Part / Variant / Extra to play
            public long? MaxBitrateBps { get; set; }
            public int? AudioStreamIndex { get; set; }
            public int? SubtitleStreamIndex { get; set; }
            public double? StartSeconds { get; set; }

            // Client-detected decode capabilities (§14.1). Absent/false = the safe
            // H.264/TS baseline, so an old or non-reporting client still plays.
            public bool SupportsHevc { get; set; }
            public bool SupportsAv1 { get; set; }
            public bool SupportsHdr { get; set; }
            public bool SupportsFmp4 { get; set; }

            public ClientCapabilities ToCapabilities() =>
                new(SupportsHevc, SupportsAv1, SupportsHdr, SupportsFmp4);
        }

        [HttpPost("/API/Stream/Start")]
        public async Task<IActionResult> Start([FromBody] StartRequest request)
        {
          try
          {
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

            var rating = await ResolveRatingAsync(playableId.Value);

            // Age gate: the exact browse-side rule (GetMovie), so the two can't drift.
            if (!await PassesAgeGateAsync(userId.Value, rating))
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

            // Optional concurrency guard — a friendly "theater full" beats a melted GPU. A failure to
            // count (Jellyfin hiccup, unexpected /Sessions payload) must never 500 the stream — log and
            // allow rather than block.
            if (config.StreamingMaxConcurrentTranscodes > 0)
            {
                int active;
                try { active = await jellyfin.GetActiveTranscodeCountAsync(); }
                catch (Exception ex) { logger.LogWarning(ex, "Transcode-count check failed; allowing the stream"); active = 0; }
                if (active >= config.StreamingMaxConcurrentTranscodes)
                    return StatusCode(503, new { message = "The theater is full — too many streams are running. Try again in a few minutes." });
            }

            var startTicks = (long)((request.StartSeconds ?? 0) * TicksPerSecond);
            // Direct play serves the whole original file, so it can't honor a burned-in subtitle
            // or a non-default audio selection — fall back to a transcode in those cases.
            var allowDirectPlay = request.SubtitleStreamIndex == null && request.AudioStreamIndex == null;
            JellyfinPlaybackInfoResult info;
            try
            {
                info = await jellyfin.GetPlaybackInfoAsync(
                    file.JellyfinItemId, request.MaxBitrateBps, request.AudioStreamIndex, request.SubtitleStreamIndex,
                    startTicks, request.ToCapabilities(), allowDirectPlay);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Jellyfin PlaybackInfo failed for playable {PlayableId}", playableId);
                return StatusCode(502, new { message = "Could not reach the media server." });
            }

            var source = info.MediaSources[0];

            // Auto-default to English audio: when the caller expressed no preference and the track
            // that would play (the container default, else the first) isn't English while an English
            // track exists, re-resolve pinned to it. An explicit audio selection disables direct play,
            // so this only re-requests when a switch is actually needed. Clients keep sending no
            // preference, so this re-applies on every start (incl. each channel advance) for free.
            int? effectiveAudioIndex = request.AudioStreamIndex;
            if (request.AudioStreamIndex == null)
            {
                var audioStreams = source.MediaStreams.Where(s => s.Type == "Audio").ToList();
                if (audioStreams.Count > 1)
                {
                    var playing = audioStreams.FirstOrDefault(s => s.IsDefault) ?? audioStreams[0];
                    if (!IsEnglish(playing) && audioStreams.FirstOrDefault(IsEnglish) is { } english)
                    {
                        effectiveAudioIndex = english.Index;
                        allowDirectPlay = false;
                        try
                        {
                            info = await jellyfin.GetPlaybackInfoAsync(
                                file.JellyfinItemId, request.MaxBitrateBps, effectiveAudioIndex, request.SubtitleStreamIndex,
                                startTicks, request.ToCapabilities(), allowDirectPlay);
                        }
                        catch (Exception ex)
                        {
                            logger.LogError(ex, "Jellyfin PlaybackInfo (English audio) failed for playable {PlayableId}", playableId);
                            return StatusCode(502, new { message = "Could not reach the media server." });
                        }
                        source = info.MediaSources[0];
                    }
                }
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
                .Select(s => new { index = s.Index, label = s.DisplayTitle ?? s.Codec ?? $"Track {s.Index}", language = s.Language })
                .ToList();

            // What's actually playing — an explicit/auto English pick, else the container default
            // (or the first track) — so the player can highlight the live audio track in its menu.
            var selectedAudioIndex = effectiveAudioIndex
                ?? (audioStreamList.FirstOrDefault(s => s.IsDefault) ?? audioStreamList.FirstOrDefault())?.Index;

            var subtitleTracks = source.MediaStreams
                .Where(s => s.Type == "Subtitle")
                .Select(s => new
                {
                    index = s.Index,
                    label = s.DisplayTitle ?? s.Language ?? $"Subtitle {s.Index}",
                    language = s.Language,
                    // Sidecar text subs toggle client-side; image subs return null here and
                    // the player restarts with subtitleStreamIndex for a burn-in.
                    deliveryUrl = s.DeliveryUrl != null && s.IsTextSubtitleStream ? ToGatewayUrl(s.DeliveryUrl) : null,
                })
                .ToList();

            var resume = await movieDb.MoviePlaybackProgresses
                .Where(p => p.UserID == userId.Value && p.PlayableId == playableId.Value && !p.Completed)
                .Select(p => (long?)p.PositionTicks)
                .FirstOrDefaultAsync();

            var sourceVideoCodec = source.MediaStreams.FirstOrDefault(s => s.Type == "Video")?.Codec;

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
                videoIsCopied = source.TranscodeReasons == null
                    || !source.TranscodeReasons.Any(r => r.Contains("Video", StringComparison.OrdinalIgnoreCase));
                // The codec the player actually receives: when the video is copied it's the source
                // codec; when re-encoded it's the first of the negotiated list (the encode target).
                // The transcoding url only carries the candidate list, so a copied H.264 source to
                // an HEVC-capable client would otherwise misread "hevc".
                outputVideoCodec = videoIsCopied
                    ? sourceVideoCodec ?? VideoCodecFromTranscodingUrl(source.TranscodingUrl)
                    : VideoCodecFromTranscodingUrl(source.TranscodingUrl);
                playbackUrl = ToGatewayUrl(source.TranscodingUrl);
                isHls = true;
            }

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
              // Diagnostic: surface the failure (+ the throwing frame) so the watch page shows the real
              // cause instead of a dead-end 500. (Private app, admin-facing; temporary.)
              logger.LogError(ex, "Stream/Start failed");
              var frames = string.Join(" <- ", (ex.StackTrace ?? "")
                  .Split('\n').Select(l => l.Trim()).Where(l => l.StartsWith("at "))
                  .Take(3));
              return StatusCode(500, new { message = $"{ex.GetType().Name}: {ex.Message} :: {frames}" });
          }
        }

        public class ProgressRequest
        {
            public string PlaySessionId { get; set; }
            public int MovieId { get; set; }
            public int? PlayableId { get; set; }
            public int? MediaFileId { get; set; }   // the exact file in play (a Part/Variant/Extra) — for the right Jellyfin item
            public long PositionTicks { get; set; }
            public bool Paused { get; set; }
            /// <summary>TV-channel playback: report to Jellyfin but write no resume/Seen.</summary>
            public bool Passive { get; set; }
        }

        [HttpPost("/API/Stream/Progress")]
        public async Task<IActionResult> Progress([FromBody] ProgressRequest request)
        {
            var userId = GetCurrentUserId();
            if (userId == null || string.IsNullOrEmpty(request?.PlaySessionId))
                return BadRequest();

            var playableId = request.PlayableId
                ?? await movieDb.Movies.Where(m => m.id == request.MovieId).Select(m => m.PlayableId).FirstOrDefaultAsync();
            if (playableId == null)
                return Ok(new { success = true });

            var itemId = await ItemIdForFileOrPlayableAsync(request.MediaFileId, playableId.Value);
            if (itemId != null)
            {
                try
                {
                    await jellyfin.ReportPlaybackProgressAsync(itemId, request.PlaySessionId, request.PositionTicks, request.Paused);
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
            public int MovieId { get; set; }
            public int? PlayableId { get; set; }
            public int? MediaFileId { get; set; }
        }

        [HttpPost("/API/Stream/Stop")]
        public async Task<IActionResult> Stop([FromBody] StopRequest request)
        {
            if (string.IsNullOrEmpty(request?.PlaySessionId))
                return BadRequest();

            var playableId = request.PlayableId
                ?? await movieDb.Movies.Where(m => m.id == request.MovieId).Select(m => m.PlayableId).FirstOrDefaultAsync();
            var itemId = playableId == null ? null : await ItemIdForFileOrPlayableAsync(request.MediaFileId, playableId.Value);
            try
            {
                if (itemId != null)
                    await jellyfin.ReportPlaybackStoppedAsync(itemId, request.PlaySessionId, 0);
                await jellyfin.StopActiveEncodingsAsync(request.PlaySessionId);
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

        // Owning title's rating, for the age gate (a movie's own, or an episode's series'; null = misc, unrestricted).
        private async Task<string?> ResolveRatingAsync(int playableId)
        {
            var movieRating = await movieDb.Movies.Where(m => m.PlayableId == playableId)
                .Select(m => m.Rating).FirstOrDefaultAsync();
            if (movieRating != null) return movieRating;

            var seriesId = await movieDb.Episodes.Where(e => e.PlayableId == playableId)
                .Select(e => e.SeriesId).FirstOrDefaultAsync();
            if (seriesId != null)
                return await movieDb.Series.Where(s => s.Id == seriesId.Value).Select(s => s.Rating).FirstOrDefaultAsync();

            return null;   // misc video — no age rating, unrestricted
        }

        private async Task<bool> PassesAgeGateAsync(int userId, string movieRating)
        {
            int ageRestriction = 100;
            var setting = await movieDb.UserSettings
                .FirstOrDefaultAsync(u => u.SettingKey == "AgeRestriction" && u.UserID == userId);
            if (setting != null && int.TryParse(setting.SettingValue, out var parsed))
                ageRestriction = parsed;
            return RatingGate.MpaRatingIdFor(movieDb, movieRating) <= ageRestriction;
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
