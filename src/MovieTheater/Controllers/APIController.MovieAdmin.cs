using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.OData.Query;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using MovieTheater.Db;
using MovieTheater.Models;
using MovieTheater.Normalization;
using MovieTheater.Services;
using MovieTheater.Services.ImdbApi;
using MovieTheater.Services.Poster;
using MovieTheater.Services.BoardgameImage;
using MovieTheater.Services.Tmdb;
using MovieTheater.Services.Omdb;
using MovieTheater.Services.Google;
using MovieTheater.Services.Bgg;

namespace MovieTheater.Controllers
{
    public partial class APIController
    {
        // The batch-insert page's name→details cascade; one implementation, shared with the resolver.
        [HttpPost("/API/GetMoviesFromNames")]
        public async Task<List<Movie>> GetMoviesFromNames([FromBody] string[] movieNames, bool forceBackupLogic = false) =>
            await candidateResolver.GetMoviesFromNames(movieNames, forceBackupLogic);

        // ── Per-movie "Re-link files from disk" (movie edit page) ─────────────────────────────────────
        // When a movie's video file is replaced on disk (new rip, old file deleted, folder renamed), its DB
        // path goes stale and the watch button breaks. These two endpoints re-associate the NEW file to the
        // SAME movie row IN PLACE — every rating/viewing/poster/tag is kept — without a full-library scan.
        // Split so neither call has to outlive a proxy timeout: RelinkRefresh kicks a SCOPED re-scan of just
        // this title's shelf; Relink is a single idempotent probe the UI polls until the file is re-pointed.

        [HttpPost("/API/Admin/Movie/RelinkRefresh")]
        public async Task<IActionResult> MovieRelinkRefresh(int movieId)
        {
            if (!await IsCurrentUserEditor()) return Forbid();
            try
            {
                var r = await jellyfinSyncService.TriggerMovieFolderRefreshAsync(movieId);
                return r.Ok
                    ? Ok(new { ok = true, message = r.Message, shelfItemId = r.ShelfItemId })
                    : BadRequest(new { ok = false, message = r.Message });
            }
            catch (Exception ex)
            {
                return StatusCode(502, new { ok = false, message = "Could not reach Jellyfin to start a re-scan: " + ex.Message });
            }
        }

        [HttpPost("/API/Admin/Movie/Relink")]
        public async Task<IActionResult> MovieRelink(int movieId, string? shelfItemId = null)
        {
            if (!await IsCurrentUserEditor()) return Forbid();
            try
            {
                var r = await jellyfinSyncService.TryRelinkMovieFilesAsync(movieId, shelfItemId);
                return Ok(new
                {
                    done = r.Done,
                    scanning = r.Scanning,
                    primaryRepointed = r.PrimaryRepointed,
                    nowStreamable = r.NowStreamable,
                    oldPath = r.OldPath,
                    newPath = r.NewPath,
                    extrasAdded = r.ExtrasAdded,
                    message = r.Message,
                });
            }
            catch (Exception ex)
            {
                return StatusCode(502, new { done = false, message = "Re-link failed: " + ex.Message });
            }
        }

        // ── Subtitle picker (movie modal) ──────────────────────────────────────────────────────────
        // Find/download subtitles for a movie through Jellyfin's subtitle provider (the OpenSubtitles
        // plugin). Libraries are set to NOT save subtitles with media, so downloads land in Jellyfin's
        // metadata dir, never the read-only NAS. Editor-gated.

        // Resolve a movie to the Jellyfin item id of its streamable Primary file (null if not synced).
        private async Task<string?> GetMovieJellyfinItemId(int movieId)
        {
            var playableId = (await movieDb.Movies.Where(m => m.id == movieId).Select(m => m.PlayableId).FirstOrDefaultAsync());
            if (playableId == null) return null;
            return await movieDb.MediaFiles
                .Where(f => f.PlayableId == playableId && f.JellyfinItemId != null && f.MissingSinceUtc == null)
                .OrderBy(f => f.Role)
                .Select(f => f.JellyfinItemId)
                .FirstOrDefaultAsync();
        }

        // The subtitle tracks currently attached to the movie + whether it's synced to Jellyfin at all.
        [HttpGet("/API/Admin/Jellyfin/Subtitles")]
        public async Task<IActionResult> JellyfinSubtitlesList(int movieId)
        {
            if (!await IsCurrentUserEditor()) return Forbid();
            var itemId = await GetMovieJellyfinItemId(movieId);
            if (itemId == null) return Ok(new { synced = false, current = Array.Empty<object>() });
            try
            {
                var subs = await jellyfinApi.GetItemSubtitleStreamsAsync(itemId);
                return Ok(new { synced = true, current = subs.Select(s => new { index = s.Index, language = s.Language, title = s.Title, codec = s.Codec, external = s.IsExternal }) });
            }
            catch (Exception ex) { return StatusCode(502, new { message = "Could not read subtitles from Jellyfin: " + ex.Message }); }
        }

        // Search providers; returns candidates ranked hash-match-first (made for THIS exact file → in sync),
        // then most-downloaded, then highest community rating.
        [HttpPost("/API/Admin/Jellyfin/Subtitles/Search")]
        public async Task<IActionResult> JellyfinSubtitlesSearch(int movieId, string language = "eng")
        {
            if (!await IsCurrentUserEditor()) return Forbid();
            var lang = string.IsNullOrWhiteSpace(language) ? "eng" : language;

            // Preferred path: search OpenSubtitles.com directly by the IMDb id our DB holds. Jellyfin's
            // items are metadata-less homevideos (no IMDb id), so its own RemoteSearch can't match — and
            // its plugin's shared key is rate-limited besides.
            if (openSubtitles.IsConfigured)
            {
                var imdbId = await movieDb.Movies.Where(m => m.id == movieId).Select(m => m.imdbID).FirstOrDefaultAsync();
                if (string.IsNullOrWhiteSpace(imdbId))
                    return BadRequest(new { message = "This movie has no IMDb id on file, so subtitles can't be matched. Set its IMDb id, then search." });
                try
                {
                    var subs = await openSubtitles.SearchAsync(imdbId, lang);
                    var ranked = subs
                        .OrderByDescending(s => s.HashMatch)        // made for THIS exact file → already in sync
                        .ThenBy(s => s.AiTranslated)                 // demote machine/AI translations
                        .ThenByDescending(s => s.FromTrusted)
                        .ThenByDescending(s => s.DownloadCount ?? 0)
                        .ThenByDescending(s => s.Rating ?? 0)
                        .Select(s => new
                        {
                            id = s.FileId.ToString(),
                            provider = "OpenSubtitles",
                            name = s.Name,
                            language = s.Language,
                            downloads = s.DownloadCount,
                            rating = s.Rating,
                            hashMatch = s.HashMatch,
                            hearingImpaired = s.HearingImpaired,
                            trusted = s.FromTrusted,
                            aiTranslated = s.AiTranslated,
                            uploader = s.Uploader,
                        })
                        .ToList();
                    return Ok(new { count = ranked.Count, results = ranked });
                }
                catch (Exception ex) { return StatusCode(502, new { message = "Subtitle search failed: " + ex.Message }); }
            }

            // Fallback: the legacy Jellyfin plugin search (only when OpenSubtitles isn't configured).
            var itemId = await GetMovieJellyfinItemId(movieId);
            if (itemId == null) return BadRequest(new { message = "This movie isn't synced to Jellyfin yet — run \"Sync from Jellyfin\" first." });
            try
            {
                var subs = await jellyfinApi.SearchRemoteSubtitlesAsync(itemId, lang);
                var ranked = subs
                    .OrderByDescending(s => s.IsHashMatch)
                    .ThenByDescending(s => s.DownloadCount ?? 0)
                    .ThenByDescending(s => s.CommunityRating ?? 0)
                    .Select(s => new
                    {
                        id = s.Id,
                        provider = s.ProviderName,
                        name = s.Name,
                        format = s.Format,
                        author = s.Author,
                        comment = s.Comment,
                        language = s.ThreeLetterISOLanguageName,
                        downloads = s.DownloadCount,
                        hashMatch = s.IsHashMatch,
                        rating = s.CommunityRating,
                    })
                    .ToList();
                return Ok(new { count = ranked.Count, results = ranked });
            }
            catch (Exception ex) { return StatusCode(502, new { message = "Subtitle search failed (is a provider configured and signed in?): " + ex.Message }); }
        }

        // Download a chosen candidate (subtitleId from a prior search) and attach it to the movie.
        [HttpPost("/API/Admin/Jellyfin/Subtitles/Download")]
        public async Task<IActionResult> JellyfinSubtitlesDownload(int movieId, string subtitleId, string language = "eng")
        {
            if (!await IsCurrentUserEditor()) return Forbid();
            if (string.IsNullOrWhiteSpace(subtitleId)) return BadRequest(new { message = "subtitleId is required." });
            var itemId = await GetMovieJellyfinItemId(movieId);
            if (itemId == null) return BadRequest(new { message = "Movie isn't synced to Jellyfin." });
            try
            {
                // OpenSubtitles path: the id is a numeric file_id — download the text and attach it to the
                // Jellyfin item as an external sidecar (so the streaming path then serves it as WebVTT).
                if (openSubtitles.IsConfigured && int.TryParse(subtitleId, out var fileId))
                {
                    var (content, _) = await openSubtitles.DownloadAsync(fileId);
                    await jellyfinApi.UploadSubtitleAsync(
                        itemId, string.IsNullOrWhiteSpace(language) ? "eng" : language, "srt",
                        isForced: false, isHearingImpaired: false, System.Text.Encoding.UTF8.GetBytes(content));
                    return Ok(new { downloaded = true });
                }

                await jellyfinApi.DownloadRemoteSubtitleAsync(itemId, subtitleId);
                return Ok(new { downloaded = true });
            }
            catch (Exception ex) { return StatusCode(502, new { downloaded = false, message = "Download failed: " + ex.Message }); }
        }

        // Remove a downloaded subtitle (to swap for another). Guarded to EXTERNAL tracks only — never an
        // embedded subtitle inside the on-disk video — and the read-only NAS mount is the hard backstop.
        [HttpPost("/API/Admin/Jellyfin/Subtitles/Delete")]
        public async Task<IActionResult> JellyfinSubtitlesDelete(int movieId, int index)
        {
            if (!await IsCurrentUserEditor()) return Forbid();
            var itemId = await GetMovieJellyfinItemId(movieId);
            if (itemId == null) return BadRequest(new { message = "Movie isn't synced to Jellyfin." });
            try
            {
                var target = (await jellyfinApi.GetItemSubtitleStreamsAsync(itemId)).FirstOrDefault(s => s.Index == index);
                if (target == null) return NotFound(new { message = "No subtitle at that index." });
                if (!target.IsExternal) return BadRequest(new { message = "That subtitle is embedded in the video file — only downloaded (external) subtitles can be removed." });
                await jellyfinApi.DeleteSubtitleAsync(itemId, index);
                return Ok(new { deleted = true });
            }
            catch (Exception ex) { return StatusCode(502, new { deleted = false, message = "Remove failed: " + ex.Message }); }
        }
    }
}
