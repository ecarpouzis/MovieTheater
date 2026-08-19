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
        // ── "Sync from Jellyfin" admin button ────────────────────────────────────────────────────────
        // The periodic Jellyfin library scan is disabled (NAS health), so making freshly-mapped content
        // streamable takes two steps: tell Jellyfin to scan the disk, then run the sync that stamps
        // JellyfinItemId onto our MediaFile rows. BOTH, and the sequencing between them, belong to the
        // server: RunSync starts one background job that does the whole thing and SyncStatus reports
        // where it is. The browser is a spectator.
        //
        // It used to chain the phases itself, and that was the bug — a tab closed during the twelve
        // minute scan stranded the run silently (2026-08-15: the scan completed at 23:18 and the sync
        // was simply never asked for; nothing in the DB or the UI said so, and the operator reasonably
        // believed they had synced). TriggerScan and ScanStatus remain for diagnosing Jellyfin itself,
        // no longer as steps anyone has to chain.

        // Ask Jellyfin to scan, without running a sync. Diagnostic; the normal path is RunSync.
        [HttpPost("/API/Admin/Jellyfin/TriggerScan")]
        public async Task<IActionResult> JellyfinTriggerScan()
        {
            if (!await IsCurrentUserEditor()) return Forbid();
            try
            {
                await jellyfinApi.TriggerLibraryScanAsync();
                return Ok(new { triggered = true });
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Jellyfin scan trigger failed");
                return StatusCode(502, new { triggered = false, message = "Could not reach Jellyfin to start a scan: " + ex.Message });
            }
        }

        // The library-scan task's raw state. Diagnostic; the job watches this itself now.
        // { running, progress (0-100 or null), found, state }.
        [HttpGet("/API/Admin/Jellyfin/ScanStatus")]
        public async Task<IActionResult> JellyfinScanStatus()
        {
            if (!await IsCurrentUserEditor()) return Forbid();
            try
            {
                var st = await jellyfinApi.GetScanTaskStateAsync();
                return Ok(new { running = st.IsRunning, progress = st.Progress, found = st.Found, state = st.State });
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Jellyfin scan-status read failed");
                return StatusCode(502, new { message = "Could not reach Jellyfin to read scan status: " + ex.Message });
            }
        }

        // START the whole operation as ONE server-side background job — scan, wait, sync — and return
        // immediately. Nothing about the outcome depends on the caller's connection surviving: the
        // job's state lives on the server (JellyfinSyncRunner) and SyncStatus reports it. Single-
        // flight: a second click while one runs just follows the run in flight.
        // scan=false syncs against the library as it stands, for when a scan has only just finished.
        [HttpPost("/API/Admin/Jellyfin/RunSync")]
        public async Task<IActionResult> JellyfinRunSync([FromQuery] bool scan = true)
        {
            if (!await IsCurrentUserEditor()) return Forbid();
            var started = jellyfinSyncRunner.TryStart(User.Identity?.Name, withScan: scan);
            var snap = jellyfinSyncRunner.Snapshot();
            // startedUtc lets the follower see WHICH run it's following — an in-flight run that began
            // earlier may predate whatever the caller was hoping to pick up.
            return Ok(new { started, alreadyRunning = !started, startedUtc = snap.StartedUtc, phase = snap.Phase });
        }

        // The job's state. { running, phase } while in flight; then { done, summary } or
        // { done, error }. A pod restart forgets the last run — reported honestly as
        // { done: false, running: false } rather than inventing an outcome.
        [HttpGet("/API/Admin/Jellyfin/SyncStatus")]
        public async Task<IActionResult> JellyfinSyncStatus()
        {
            if (!await IsCurrentUserEditor()) return Forbid();
            var (running, startedUtc, finishedUtc, report, error, phase) = jellyfinSyncRunner.Snapshot();
            if (running) return Ok(new { running = true, startedUtc, phase });
            if (error != null) return Ok(new { running = false, done = true, startedUtc, finishedUtc, error });
            if (report != null) return Ok(new { running = false, done = true, startedUtc, finishedUtc, summary = SyncSummary(report) });
            return Ok(new { running = false, done = false });
        }

        private static object SyncSummary(MovieTheater.Services.Jellyfin.JellyfinSyncReport rep)
        {
            static List<string> Sample(IReadOnlyList<string> xs, int n = 20) =>
                xs.Count <= n ? new List<string>(xs) : new List<string>(xs).GetRange(0, n);

            return new
            {
                server = rep.ServerName,
                version = rep.Version,
                moviesMatched = rep.MoviesMatched,
                moviesTotal = rep.MoviesTotal,
                created = rep.Created,
                updated = rep.Updated,
                repointed = rep.Repointed.Count,
                extrasAttached = rep.ExtrasAttached,
                extrasUnplaced = rep.ExtrasUnplaced,
                supersededOrphans = rep.SupersededOrphans,
                possibleRenames = rep.PossibleRenames.Count,
                moviesMissing = rep.MissingMovies.Count,
                epMatched = rep.EpMatched,
                epTotal = rep.EpTotal,
                untracked = rep.Untracked.Count,
                untranslatable = rep.Untranslatable.Count,
                imdbFallbacks = rep.ImdbFallbacks.Count,
                candidateUpgrades = rep.CandidateUpgrades,
                candidateNewTitles = rep.CandidateNewTitles,
                candidateSeriesEpisodes = rep.CandidateSeriesEpisodes,
                candidateSeriesGroups = rep.CandidateSeriesGroups,
                candidateUnclassified = rep.CandidateUnclassified,
                candidatesSuperseded = rep.CandidatesSuperseded,
                candidateError = rep.CandidateError,
                keyframeError = rep.KeyframeError,
                scanNote = rep.ScanNote,
                resolveError = rep.ResolveError,
                resolution = rep.Resolution == null ? null : new
                {
                    moviesCreated = rep.Resolution.MoviesCreated,
                    moviesConvertedToUpgrade = rep.Resolution.MoviesConvertedToUpgrade,
                    seriesIdentified = rep.Resolution.SeriesIdentified,
                    seriesEnriched = rep.Resolution.SeriesEnriched,
                    episodesCatalogued = rep.Resolution.EpisodesCatalogued,
                    episodeFilesMapped = rep.Resolution.EpisodeFilesMapped,
                    needsAttention = rep.Resolution.NeedsAttention,
                    notes = rep.Resolution.Notes,
                },
                samples = new
                {
                    repointed = Sample(rep.Repointed),
                    possibleRenames = Sample(rep.PossibleRenames),
                    missingTitles = Sample(rep.MissingMovies),
                    imdbFallbacks = Sample(rep.ImdbFallbacks),
                },
            };
        }
    }
}
