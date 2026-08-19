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
        // ── Sync-scan candidates (the sync's untracked findings, made actionable) ─────────────────────
        // A sync run classifies every untracked file into SyncCandidate rows (upgrade of an existing
        // movie / new title / unclassified). These endpoints drive the review surface: list them,
        // apply an upgrade (re-point in place), resolve new titles into quarantined ReviewBatch rows
        // that flow through the normal ingest review, correct a wrong classification, or reject.
        // Resolution is CHUNKED (a few folders per call, the UI loops) — each folder costs external
        // metadata lookups, so no single request is ever asked to survive the whole pile.

        [HttpGet("/API/Admin/IngestReview/SyncCandidates")]
        public async Task<IActionResult> SyncCandidatesList()
        {
            if (!await IsCurrentUserEditor()) return Forbid();
            var all = await movieDb.SyncCandidates
                .Where(c => c.Status == SyncCandidateStatus.Pending)
                .OrderBy(c => c.Kind).ThenBy(c => c.Path)
                .ToListAsync();
            var ingestedCount = await movieDb.SyncCandidates.CountAsync(c => c.Status == SyncCandidateStatus.Ingested);

            // Episode candidates never appear as loose rows — they fold into one card per show below.
            var episodeCands = all.Where(c => c.Kind == SyncCandidateKind.SeriesEpisode).ToList();
            var pending = all.Where(c => c.Kind != SyncCandidateKind.SeriesEpisode).ToList();
            var seriesGroups = await BuildSyncSeriesGroupsAsync(episodeCands);

            var targetIds = pending.Where(c => c.TargetMovieId != null).Select(c => c.TargetMovieId!.Value).Distinct().ToList();
            var targets = await movieDb.Movies.Where(m => targetIds.Contains(m.id))
                .Select(m => new
                {
                    m.id,
                    m.Title,
                    Year = m.ReleaseDate != null ? m.ReleaseDate.Value.Year : (m.ImdbReleaseDate != null ? m.ImdbReleaseDate.Value.Year : (int?)null),
                    m.PlayableId,
                    // Old file already dead = the safest kind of upgrade; still-live = replacing a working copy.
                    OldFileMissing = m.PlayableId == null || !movieDb.MediaFiles.Any(f =>
                        f.PlayableId == m.PlayableId && f.Role == MovieFileRole.Primary
                        && f.JellyfinItemId != null && f.MissingSinceUtc == null),
                })
                .ToDictionaryAsync(m => m.id);

            return Ok(new
            {
                counts = new
                {
                    upgrades = pending.Count(c => c.Kind == SyncCandidateKind.Upgrade),
                    newTitles = pending.Count(c => c.Kind == SyncCandidateKind.NewTitle),
                    unclassified = pending.Count(c => c.Kind == SyncCandidateKind.Unclassified),
                    ingested = ingestedCount,
                    seriesGroups = seriesGroups.Count,
                    seriesEpisodeFiles = episodeCands.Count,
                    // How many shows still need work before their card is complete — the number the
                    // "Resolve series" button loops over, and the honest "how much is left".
                    seriesUnresolved = seriesGroups.Count(g => !g.Complete),
                },
                seriesGroups,
                items = pending.Select(c => new
                {
                    id = c.Id,
                    kind = c.Kind == SyncCandidateKind.Upgrade ? "upgrade" : c.Kind == SyncCandidateKind.NewTitle ? "new" : "unclassified",
                    path = c.Path,
                    sizeBytes = c.SizeBytes,
                    signal = c.Signal,
                    oldPath = c.OldPath,
                    targetMovieId = c.TargetMovieId,
                    targetTitle = c.TargetMovieId != null && targets.TryGetValue(c.TargetMovieId.Value, out var t) ? t.Title : null,
                    targetYear = c.TargetMovieId != null && targets.TryGetValue(c.TargetMovieId.Value, out var t2) ? t2.Year : null,
                    oldFileMissing = c.TargetMovieId != null && targets.TryGetValue(c.TargetMovieId.Value, out var t3) ? t3.OldFileMissing : (bool?)null,
                    parsedTitle = c.ParsedTitle,
                    parsedYear = c.ParsedYear,
                    resolvedImdbId = c.ResolvedImdbId,
                    resolutionError = c.ResolutionError,
                    firstSeenUtc = c.FirstSeenUtc,
                    lastSeenUtc = c.LastSeenUtc,
                }),
            });
        }

        // ── Series-episode candidate groups ───────────────────────────────────────────────────────────
        // One show = one card, however many episode files it brought. The card carries the show's
        // identity (matched series or a parse of the folder), what the resolver still owes it
        // (identify → enumerate episodes → map files), and the per-file episode list so the reviewer
        // sees S01E07 → "Episode 7" rather than a wall of release names.

        public class SyncSeriesGroupDto
        {
            public string Folder { get; set; } = default!;
            public string? Title { get; set; }
            public int? Year { get; set; }
            public string? Signal { get; set; }
            public int? SeriesId { get; set; }
            public string? SeriesTitle { get; set; }
            public string? SeriesImdbId { get; set; }
            /// <summary>The pending series card is still quarantined in this batch (null once approved).</summary>
            public string? SeriesReviewBatch { get; set; }
            public bool SeriesHasPoster { get; set; }
            public int EpisodeRowsKnown { get; set; }
            public int FileCount { get; set; }
            public int SeasonCount { get; set; }
            public List<int> Seasons { get; set; } = new();
            /// <summary>Files whose (season, episode) has no Episode row yet — the numbering
            /// disagreements a reviewer must see rather than have guessed at.</summary>
            public int UnmatchedFiles { get; set; }
            public string? Error { get; set; }
            /// <summary>How the disk's season numbering disagrees with the catalogue's, in words;
            /// null when they agree. While this is set, NOTHING maps by number — so the card must
            /// report every file as unmatched rather than showing the by-number lookup's answer,
            /// which is precisely the answer that would be wrong.</summary>
            public string? ShapeMismatch { get; set; }
            /// <summary>The disk and the catalogue hold the same NUMBER of episodes but split them
            /// into seasons differently, and nothing is mapped yet — the one situation where mapping
            /// in absolute order is meaningful, offered to the reviewer as an explicit choice.</summary>
            public bool CanMapAbsolute { get; set; }
            /// <summary>Nothing left for the resolver: the show is identified, its episodes are
            /// enumerated, and every file has been mapped (so its candidates left Pending).</summary>
            public bool Complete { get; set; }
            /// <summary>What the resolver would do next — shown on the card so the loop is legible.</summary>
            public string NextStep { get; set; } = default!;
            public List<SyncSeriesFileDto> Files { get; set; } = new();
        }

        private sealed record SeriesLite(int Id, string? Title, string? ImdbId, string? ReviewBatch, bool HasPoster);

        public class SyncSeriesFileDto
        {
            public int Id { get; set; }
            public string Path { get; set; } = default!;
            public long? SizeBytes { get; set; }
            public int? Season { get; set; }
            public int? Episode { get; set; }
            public int? SpansToEpisode { get; set; }
            public string? EpisodeTitle { get; set; }
            public bool Matched { get; set; }
        }

        /// <summary>
        /// Folds pending episode candidates into one DTO per show and works out, for each, what the
        /// resolver still owes it. Deliberately read-only and side-effect free — the same computation
        /// drives both the card and <see cref="SyncCandidatesResolveSeries"/>'s work queue, so the
        /// progress the reviewer sees is the progress the loop is actually making.
        /// </summary>
        private async Task<List<SyncSeriesGroupDto>> BuildSyncSeriesGroupsAsync(List<SyncCandidate> episodeCands)
        {
            if (episodeCands.Count == 0) return new List<SyncSeriesGroupDto>();

            var seriesIds = episodeCands.Where(c => c.TargetSeriesId != null)
                .Select(c => c.TargetSeriesId!.Value).Distinct().ToList();
            var seriesById = seriesIds.Count == 0
                ? new Dictionary<int, SeriesLite>()
                : (await movieDb.Series.Where(s => seriesIds.Contains(s.Id))
                    .Select(s => new SeriesLite(s.Id, s.Title, s.imdbID, s.ReviewBatch,
                        s.PosterDetails != null && s.PosterDetails.PosterVersion > 0))
                    .ToListAsync()).ToDictionary(s => s.Id);
            // (season, episode) → title, for every series any group points at.
            var epRows = seriesIds.Count == 0
                ? new List<(int SeriesId, int Season, int Episode, string? Title)>()
                : (await movieDb.Episodes.Where(e => e.SeriesId != null && seriesIds.Contains(e.SeriesId!.Value))
                    .Select(e => new { SeriesId = e.SeriesId!.Value, e.SeasonNumber, e.EpisodeNumber, e.Title })
                    .ToListAsync())
                    .Select(e => (SeriesId: e.SeriesId, Season: e.SeasonNumber, Episode: e.EpisodeNumber, Title: e.Title)).ToList();
            var epLookup = epRows.ToDictionary(e => (e.SeriesId, e.Season, e.Episode), e => e.Title);
            // The same Episode rows the mapper's shape check reads, grouped per series.
            var epRowsBySeries = epRows
                .GroupBy(e => e.SeriesId)
                .ToDictionary(g => g.Key, g => (IReadOnlyCollection<Episode>)g
                    .Select(e => new Episode { SeasonNumber = e.Season, EpisodeNumber = e.Episode, Title = e.Title })
                    .ToList());
            var epCountBySeries = epRows.GroupBy(e => e.SeriesId).ToDictionary(g => g.Key, g => g.Count());
            // Episodes of those series that ALREADY hold a file — absolute-order mapping is only
            // meaningful on a show where nothing is mapped yet.
            var mappedEpBySeries = seriesIds.Count == 0
                ? new Dictionary<int, int>()
                : (await movieDb.Episodes
                    .Where(e => e.SeriesId != null && seriesIds.Contains(e.SeriesId!.Value) && e.PlayableId != null
                        && movieDb.MediaFiles.Any(f => f.PlayableId == e.PlayableId))
                    .GroupBy(e => e.SeriesId!.Value).Select(g => new { g.Key, n = g.Count() })
                    .ToListAsync()).ToDictionary(x => x.Key, x => x.n);

            var groups = new List<SyncSeriesGroupDto>();
            foreach (var g in episodeCands
                .GroupBy(c => c.SeriesFolder ?? ParentDirOfPath(c.Path) ?? c.Path, StringComparer.OrdinalIgnoreCase)
                .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
            {
                var members = g.OrderBy(c => c.SeasonNumber ?? 0).ThenBy(c => c.EpisodeNumber ?? 0).ThenBy(c => c.Path).ToList();
                var head = members[0];
                var sid = members.Select(c => c.TargetSeriesId).FirstOrDefault(x => x != null);
                SeriesLite? s = sid != null && seriesById.TryGetValue(sid.Value, out var sv) ? sv : null;

                var dto = new SyncSeriesGroupDto
                {
                    Folder = g.Key,
                    Title = head.ParsedTitle,
                    Year = head.ParsedYear,
                    Signal = head.Signal,
                    SeriesId = sid,
                    SeriesTitle = s?.Title,
                    SeriesImdbId = s?.ImdbId,
                    SeriesReviewBatch = s?.ReviewBatch,
                    SeriesHasPoster = s?.HasPoster == true,
                    EpisodeRowsKnown = sid != null && epCountBySeries.TryGetValue(sid.Value, out var ec) ? ec : 0,
                    FileCount = members.Count,
                    Seasons = members.Where(c => c.SeasonNumber != null).Select(c => c.SeasonNumber!.Value).Distinct().OrderBy(n => n).ToList(),
                    Error = members.Select(c => c.ResolutionError).FirstOrDefault(e => e != null),
                };
                dto.SeasonCount = dto.Seasons.Count;
                dto.Files = members.Select(c => new SyncSeriesFileDto
                {
                    Id = c.Id,
                    Path = c.Path,
                    SizeBytes = c.SizeBytes,
                    Season = c.SeasonNumber,
                    Episode = c.EpisodeNumber,
                    SpansToEpisode = c.SpansToEpisode,
                    EpisodeTitle = sid != null && c.SeasonNumber != null && c.EpisodeNumber != null
                        && epLookup.TryGetValue((sid.Value, c.SeasonNumber.Value, c.EpisodeNumber.Value), out var et) ? et : null,
                    Matched = sid != null && c.SeasonNumber != null && c.EpisodeNumber != null
                        && epLookup.ContainsKey((sid.Value, c.SeasonNumber.Value, c.EpisodeNumber.Value)),
                }).ToList();
                // The card must agree with the mapper. When the season shapes disagree, mapping by
                // number is refused wholesale — so reporting the by-number lookup's 83-of-84 here
                // would tell the reviewer the show is nearly done when in fact nothing will attach.
                if (sid != null && epRowsBySeries.TryGetValue(sid.Value, out var catalogue))
                {
                    dto.ShapeMismatch = MovieTheater.Services.Series.SyncSeriesMatcher
                        .SeasonShapeMismatch(members, catalogue);
                    if (dto.ShapeMismatch != null)
                        foreach (var f in dto.Files) f.Matched = false;
                }
                dto.UnmatchedFiles = dto.Files.Count(f => !f.Matched);
                dto.CanMapAbsolute =
                    sid != null
                    && dto.EpisodeRowsKnown > 0
                    && dto.UnmatchedFiles > 0
                    && (mappedEpBySeries.TryGetValue(sid.Value, out var already) ? already : 0) == 0
                    && dto.Files.Count(f => f.Season != null && f.Episode != null) == dto.EpisodeRowsKnown;

                dto.NextStep =
                    sid == null ? "identify the show"
                    : !dto.SeriesHasPoster ? "enrich the show (poster, plot, rating)"
                    : dto.EpisodeRowsKnown == 0 ? "enumerate its episodes"
                    : dto.UnmatchedFiles == dto.FileCount ? "enumerate the missing seasons"
                    : "map the files to episodes";
                // These candidates are Pending by definition (the query filters on it), so a group that
                // still exists always has files left to map — "complete" is about the SHOW being ready,
                // which is what tells the reviewer the resolver has nothing more to do here.
                dto.Complete = sid != null && dto.EpisodeRowsKnown > 0 && dto.UnmatchedFiles == 0;
                if (dto.Complete) dto.NextStep = "map the files to episodes";
                groups.Add(dto);
            }
            return groups;
        }

        private static string? ParentDirOfPath(string? p)
        {
            if (string.IsNullOrEmpty(p)) return null;
            var s = p.Replace('/', '\\').TrimEnd('\\');
            var i = s.LastIndexOf('\\');
            return i <= 0 ? null : s.Substring(0, i);
        }

        public class SyncCandidateIdRequest { public int Id { get; set; } }
        public class SyncCandidateIdsRequest { public List<int> Ids { get; set; } = new(); }

        [HttpPost("/API/Admin/IngestReview/SyncCandidates/ApplyUpgrade")]
        public async Task<IActionResult> SyncCandidateApplyUpgrade([FromBody] SyncCandidateIdRequest req)
        {
            if (!await IsCurrentUserEditor()) return Forbid();
            var res = await jellyfinSyncService.ApplyUpgradeCandidateAsync(req.Id, TruncCol(User.Identity?.Name, 64));
            return Ok(new { success = res.Ok, message = res.Message, movieTitle = res.MovieTitle, newPath = res.NewPath, nowStreamable = res.NowStreamable, extrasAttached = res.ExtrasAttached, partsAttached = res.PartsAttached });
        }

        public class SyncCandidateVariantRequest { public int Id { get; set; } public string? Label { get; set; } }

        // "It belongs to that movie, but it isn't an upgrade" — attach the file as an alternate version
        // (Role=Variant) beside the existing Primary instead of replacing it. Additive and reversible:
        // the movie's main file, FilePath and metadata are untouched, and the variant can be promoted
        // later from the title's file list (IngestReview/MoveFile).
        [HttpPost("/API/Admin/IngestReview/SyncCandidates/AttachVariant")]
        public async Task<IActionResult> SyncCandidateAttachVariant([FromBody] SyncCandidateVariantRequest req)
        {
            if (!await IsCurrentUserEditor()) return Forbid();
            var res = await jellyfinSyncService.AttachVariantCandidateAsync(req.Id, req.Label, TruncCol(User.Identity?.Name, 64));
            return Ok(new { success = res.Ok, message = res.Message, movieTitle = res.MovieTitle, newPath = res.NewPath, nowStreamable = res.NowStreamable });
        }

        [HttpPost("/API/Admin/IngestReview/SyncCandidates/Reject")]
        public async Task<IActionResult> SyncCandidatesReject([FromBody] SyncCandidateIdsRequest req)
        {
            if (!await IsCurrentUserEditor()) return Forbid();
            var rows = await movieDb.SyncCandidates
                .Where(c => req.Ids.Contains(c.Id) && c.Status == SyncCandidateStatus.Pending)
                .ToListAsync();
            var now = DateTime.UtcNow;
            foreach (var c in rows)
            {
                c.Status = SyncCandidateStatus.Rejected;
                c.ResolvedUtc = now;
                c.ResolvedBy = TruncCol(User.Identity?.Name, 64);
            }
            await movieDb.SaveChangesAsync();
            return Ok(new { rejected = rows.Count });
        }

        public class SyncCandidateUpdateRequest
        {
            public int Id { get; set; }
            /// <summary>"upgrade" | "new" | "unclassified" | "series" — omit to keep the current kind.</summary>
            public string? Kind { get; set; }
            public string? Title { get; set; }
            public int? Year { get; set; }
            /// <summary>Hand-picked IMDb id; short-circuits name resolution for this candidate.</summary>
            public string? ImdbId { get; set; }
            /// <summary>Upgrade target when reclassifying to "upgrade" by hand.</summary>
            public int? TargetMovieId { get; set; }
            /// <summary>The show an episode candidate belongs to; also settable on a whole group.</summary>
            public int? TargetSeriesId { get; set; }
            /// <summary>Apply this edit to EVERY pending candidate sharing the row's SeriesFolder. A
            /// correction to a show ("this is the wrong series", "here's the right tt") is a statement
            /// about the show, and fixing it one file at a time across 84 rows is not review, it's
            /// data entry.</summary>
            public bool ApplyToGroup { get; set; }
        }

        // Correct a wrong classification or parse on a still-pending candidate: retitle a NewTitle,
        // pin its IMDb id, or re-point an upgrade at the right movie. The row stays Pending — this
        // only changes what Approve/Resolve will do with it.
        [HttpPost("/API/Admin/IngestReview/SyncCandidates/Update")]
        public async Task<IActionResult> SyncCandidateUpdate([FromBody] SyncCandidateUpdateRequest req)
        {
            if (!await IsCurrentUserEditor()) return Forbid();
            var c = await movieDb.SyncCandidates.FirstOrDefaultAsync(x => x.Id == req.Id);
            if (c == null) return NotFound(new { success = false, message = "Candidate not found." });
            if (c.Status != SyncCandidateStatus.Pending)
                return BadRequest(new { success = false, message = $"Candidate is {c.Status}, not Pending." });

            // An edit aimed at a SHOW applies to every pending file of that show — see ApplyToGroup.
            var affected = new List<SyncCandidate> { c };
            if (req.ApplyToGroup && !string.IsNullOrEmpty(c.SeriesFolder))
                affected = await movieDb.SyncCandidates
                    .Where(x => x.Status == SyncCandidateStatus.Pending && x.SeriesFolder == c.SeriesFolder)
                    .ToListAsync();

            if (req.TargetSeriesId != null)
            {
                var ts = await movieDb.Series.FirstOrDefaultAsync(s => s.Id == req.TargetSeriesId);
                if (ts == null) return BadRequest(new { success = false, message = $"No series {req.TargetSeriesId}." });
                foreach (var x in affected) x.TargetSeriesId = ts.Id;
            }

            // ImdbId: null = leave alone, "" = clear a pin (a rejected resolution must be un-pinnable
            // or re-resolving recreates the same wrong movie), non-empty = validate + pin.
            if (req.ImdbId != null)
            {
                if (req.ImdbId.Length == 0) foreach (var x in affected) x.ResolvedImdbId = null;
                else if (!IsValidImdbId(req.ImdbId)) return BadRequest(new { success = false, message = $"'{req.ImdbId}' is not a valid IMDb id." });
                else foreach (var x in affected) x.ResolvedImdbId = req.ImdbId;
            }
            if (req.Title != null) foreach (var x in affected) x.ParsedTitle = TruncCol(req.Title.Trim(), 512);
            if (req.Year != null) foreach (var x in affected) x.ParsedYear = req.Year;

            if (!string.IsNullOrEmpty(req.Kind))
            {
                switch (req.Kind.ToLowerInvariant())
                {
                    case "series":
                        // Rescue an episode file the classifier left unclassified (an odd file name, a
                        // folder with no SxxExx): give it a series folder to group under so it joins the
                        // show's card instead of sitting alone forever.
                        foreach (var x in affected)
                        {
                            x.Kind = SyncCandidateKind.SeriesEpisode;
                            x.TargetMovieId = null; x.OldPath = null;
                            x.Signal = "manual";
                            x.SeriesFolder ??= TruncCol(ParentDirOfPath(x.Path), 1024);
                            if (x.SeasonNumber == null || x.EpisodeNumber == null)
                            {
                                var ep = MovieTheater.Services.Jellyfin.MovieFolderParser.ParseEpisode(
                                    MovieTheater.Services.Jellyfin.MovieFolderParser.SeriesFolderLeaf(x.Path));
                                if (ep != null)
                                {
                                    x.SeasonNumber = ep.Value.Season;
                                    x.EpisodeNumber = ep.Value.Episode;
                                    x.SpansToEpisode = ep.Value.Spans != ep.Value.Episode ? ep.Value.Spans : null;
                                }
                            }
                        }
                        break;
                    case "new":
                        if (string.IsNullOrWhiteSpace(c.ParsedTitle) && string.IsNullOrEmpty(c.ResolvedImdbId))
                            return BadRequest(new { success = false, message = "A new-title candidate needs a title or an IMDb id." });
                        foreach (var x in affected)
                        {
                            x.Kind = SyncCandidateKind.NewTitle;
                            x.TargetMovieId = null; x.Signal = null; x.OldPath = null;
                            x.TargetSeriesId = null; x.SeriesFolder = null; x.SeriesListOwned = false;
                        }
                        break;
                    case "upgrade":
                        var target = req.TargetMovieId != null
                            ? await movieDb.Movies.FirstOrDefaultAsync(m => m.id == req.TargetMovieId)
                            : null;
                        if (target == null) return BadRequest(new { success = false, message = "An upgrade candidate needs a valid TargetMovieId." });
                        // An upgrade is a statement about ONE file replacing ONE movie — never fanned
                        // out over a group, which would point every episode at the same movie.
                        c.Kind = SyncCandidateKind.Upgrade;
                        c.TargetMovieId = target.id; c.Signal = "manual"; c.OldPath = target.FilePath;
                        c.TargetSeriesId = null; c.SeriesFolder = null; c.SeriesListOwned = false;
                        affected = new List<SyncCandidate> { c };
                        break;
                    case "unclassified":
                        foreach (var x in affected)
                        {
                            x.Kind = SyncCandidateKind.Unclassified;
                            x.TargetMovieId = null; x.Signal = null; x.OldPath = null;
                            x.TargetSeriesId = null; x.SeriesFolder = null; x.SeriesListOwned = false;
                        }
                        break;
                    default:
                        return BadRequest(new { success = false, message = $"Unknown kind '{req.Kind}'." });
                }
            }
            // Any hand edit pins the row: the next sync's refresh must not clobber a reviewer's
            // correction with the same machine classification that was wrong the first time. Clearing
            // the error is what puts a blocked group back in the resolver's queue.
            foreach (var x in affected)
            {
                x.ResolutionError = null;
                x.PinnedByReviewer = true;
            }
            await movieDb.SaveChangesAsync();
            return Ok(new { success = true, updated = affected.Count });
        }


        // ── Candidate resolution ──────────────────────────────────────────────────────────────────────
        // The sync job now runs all of this itself the moment classification finishes, so a completed
        // sync leaves a finished review queue rather than a pile of candidates. These endpoints stay
        // for re-running a piece BY HAND after a correction — fix a title, clear an error, resolve
        // again — which is the only reason a person should ever need to press them.

        [HttpPost("/API/Admin/IngestReview/SyncCandidates/Resolve")]
        public async Task<IActionResult> SyncCandidatesResolve([FromQuery] int limit = 3)
        {
            if (!await IsCurrentUserEditor()) return Forbid();
            var r = await candidateResolver.ResolveNewTitlesChunkAsync(Math.Clamp(limit, 1, 10), TruncCol(User.Identity?.Name, 64));
            return Ok(new { processed = r.Processed, created = r.Created, converted = r.Converted, failed = r.Failed, remaining = r.Remaining, done = r.Done });
        }

        [HttpPost("/API/Admin/IngestReview/SyncCandidates/ResolveSeries")]
        public async Task<IActionResult> SyncCandidatesResolveSeries([FromQuery] int limit = 4)
        {
            if (!await IsCurrentUserEditor()) return Forbid();
            var r = await candidateResolver.ResolveSeriesChunkAsync(Math.Clamp(limit, 1, 10), TruncCol(User.Identity?.Name, 64));
            return Ok(new
            {
                processed = r.Processed, identified = r.Identified, enriched = r.Enriched,
                seasonsEnumerated = r.SeasonsEnumerated, episodesAdded = r.EpisodesAdded,
                filesMapped = r.FilesMapped, failed = r.Failed, remaining = r.Remaining,
                blocked = r.Blocked, done = r.Done, log = r.Log,
            });
        }

        [HttpPost("/API/Admin/IngestReview/SyncCandidates/MapSeriesAbsolute")]
        public async Task<IActionResult> SyncCandidatesMapSeriesAbsolute([FromBody] SyncCandidateIdRequest req)
        {
            if (!await IsCurrentUserEditor()) return Forbid();
            var r = await candidateResolver.MapSeriesAbsoluteAsync(req.Id, TruncCol(User.Identity?.Name, 64));
            if (r.Message != null) return BadRequest(new { success = false, message = r.Message });
            return Ok(new { success = r.Success, mapped = r.Mapped, unmatched = r.Unmatched, total = r.Total });
        }
    }
}
