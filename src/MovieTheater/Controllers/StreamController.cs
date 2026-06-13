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
        private const double AutoSeenThreshold = 0.9;

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

        public class StartRequest
        {
            public int MovieId { get; set; }
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
            if (string.IsNullOrEmpty(config.StreamGatewayBaseUrl) || string.IsNullOrEmpty(config.StreamTokenSecret))
                return StatusCode(501, new { message = "Streaming is not configured on this server." });

            var userId = GetCurrentUserId();
            if (userId == null)
                return Unauthorized();

            var movie = await movieDb.Movies.SingleOrDefaultAsync(m => m.id == request.MovieId);
            if (movie == null)
                return NotFound(new { message = "Movie not found." });

            // Age gate: the exact browse-side rule (GetMovie), so the two can't drift.
            if (!await PassesAgeGateAsync(userId.Value, movie.Rating))
                return StatusCode(403, new { message = "This movie isn't available on your account." });

            var file = await movieDb.MovieFiles
                .Where(f => f.MovieID == movie.id && f.JellyfinItemId != null && f.MissingSinceUtc == null)
                .OrderBy(f => f.Id)
                .FirstOrDefaultAsync();
            if (file?.JellyfinItemId == null)
                return NotFound(new { message = "This movie has no playable file." });

            // Optional concurrency guard — a friendly "theater full" beats a melted GPU.
            if (config.StreamingMaxConcurrentTranscodes > 0)
            {
                var active = await jellyfin.GetActiveTranscodeCountAsync();
                if (active >= config.StreamingMaxConcurrentTranscodes)
                    return StatusCode(503, new { message = "The theater is full — too many streams are running. Try again in a few minutes." });
            }

            var startTicks = (long)((request.StartSeconds ?? 0) * TicksPerSecond);
            JellyfinPlaybackInfoResult info;
            try
            {
                info = await jellyfin.GetPlaybackInfoAsync(
                    file.JellyfinItemId, request.MaxBitrateBps, request.AudioStreamIndex, request.SubtitleStreamIndex,
                    startTicks, request.ToCapabilities());
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Jellyfin PlaybackInfo failed for movie {MovieId}", movie.id);
                return StatusCode(502, new { message = "Could not reach the media server." });
            }

            var source = info.MediaSources[0];
            if (string.IsNullOrEmpty(source.TranscodingUrl))
                return StatusCode(502, new { message = "Jellyfin did not return a playable stream." });

            // Mint the capability: one movie, one session, bounded life (§3.3). Expiry =
            // duration × 1.5 + 4h, so a long evening survives but a leak goes stale.
            var durationTicks = file.DurationTicks ?? source.RunTimeTicks ?? 0;
            var lifetimeSeconds = (long)(durationTicks / TicksPerSecond * 1.5) + 4 * 3600;
            var token = StreamCapabilityToken.Mint(config.StreamTokenSecret, new StreamCapabilityToken.Payload(
                userId.Value, movie.id, info.PlaySessionId, file.JellyfinItemId,
                DateTimeOffset.UtcNow.ToUnixTimeSeconds() + lifetimeSeconds));

            string ToGatewayUrl(string jellyfinRelativeUrl) =>
                $"{config.StreamGatewayBaseUrl!.TrimEnd('/')}/s/{token}{StripApiKey(jellyfinRelativeUrl)}";

            var audioTracks = source.MediaStreams
                .Where(s => s.Type == "Audio")
                .Select(s => new { index = s.Index, label = s.DisplayTitle ?? s.Codec ?? $"Track {s.Index}", language = s.Language })
                .ToList();

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
                .Where(p => p.UserID == userId.Value && p.MovieID == movie.id && !p.Completed)
                .Select(p => (long?)p.PositionTicks)
                .FirstOrDefaultAsync();

            var videoIsCopied = source.TranscodeReasons == null
                || !source.TranscodeReasons.Any(r => r.Contains("Video", StringComparison.OrdinalIgnoreCase));

            // The codec the player actually receives: when the video is copied it's the
            // source codec; when re-encoded it's the first of the negotiated list (the
            // encode target). The transcoding url only carries the candidate list, so a
            // copied H.264 source to an HEVC-capable client would otherwise misread "hevc".
            var sourceVideoCodec = source.MediaStreams.FirstOrDefault(s => s.Type == "Video")?.Codec;
            var outputVideoCodec = videoIsCopied
                ? sourceVideoCodec ?? VideoCodecFromTranscodingUrl(source.TranscodingUrl)
                : VideoCodecFromTranscodingUrl(source.TranscodingUrl);

            return Ok(new
            {
                playSessionId = info.PlaySessionId,
                hlsUrl = ToGatewayUrl(source.TranscodingUrl),
                durationTicks,
                isDirectStream = videoIsCopied,
                // The codec the player will actually receive (copied or encoded) — drives the
                // "HEVC"/"Direct" readout and confirms §14.1 negotiation worked.
                videoCodec = outputVideoCodec,
                audioTracks,
                subtitleTracks,
                resumePositionTicks = resume,
            });
        }

        public class ProgressRequest
        {
            public string PlaySessionId { get; set; }
            public int MovieId { get; set; }
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

            var itemId = await ItemIdForMovieAsync(request.MovieId);
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
                var progress = await movieDb.MoviePlaybackProgresses
                    .SingleOrDefaultAsync(p => p.UserID == userId.Value && p.MovieID == request.MovieId);
                var durationTicks = await movieDb.MovieFiles
                    .Where(f => f.MovieID == request.MovieId && f.DurationTicks != null)
                    .Select(f => f.DurationTicks)
                    .FirstOrDefaultAsync() ?? 0;

                if (progress == null)
                {
                    progress = new MoviePlaybackProgress { UserID = userId.Value, MovieID = request.MovieId };
                    movieDb.MoviePlaybackProgresses.Add(progress);
                }
                progress.PositionTicks = request.PositionTicks;
                progress.DurationTicks = durationTicks;
                progress.UpdatedUtc = DateTime.UtcNow;

                // ≥90% watched marks Seen — streaming feeds the tracker the site exists for.
                if (durationTicks > 0 && request.PositionTicks >= durationTicks * AutoSeenThreshold && !progress.Completed)
                {
                    progress.Completed = true;
                    var alreadySeen = await movieDb.Viewings
                        .AnyAsync(v => v.UserID == userId.Value && v.MovieID == request.MovieId && v.ViewingType == "Seen");
                    if (!alreadySeen)
                    {
                        movieDb.Viewings.Add(new Viewing
                        {
                            UserID = userId.Value,
                            MovieID = request.MovieId,
                            ViewingType = "Seen",
                        });
                    }
                }

                await movieDb.SaveChangesAsync();
            }

            return Ok(new { success = true });
        }

        public class StopRequest
        {
            public string PlaySessionId { get; set; }
            public int MovieId { get; set; }
        }

        [HttpPost("/API/Stream/Stop")]
        public async Task<IActionResult> Stop([FromBody] StopRequest request)
        {
            if (string.IsNullOrEmpty(request?.PlaySessionId))
                return BadRequest();

            var itemId = await ItemIdForMovieAsync(request.MovieId);
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

        private async Task<string> ItemIdForMovieAsync(int movieId) =>
            await movieDb.MovieFiles
                .Where(f => f.MovieID == movieId && f.JellyfinItemId != null)
                .Select(f => f.JellyfinItemId)
                .FirstOrDefaultAsync();

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
