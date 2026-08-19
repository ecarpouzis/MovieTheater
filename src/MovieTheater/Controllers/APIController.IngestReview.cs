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
        // ── Library-ingest review (editor-gated) ─────────────────────────────────────
        // Surfaces the rows the bulk library ingest created (ReviewBatch != null) — still
        // quarantined from browse — so they can be Approved (un-quarantined into the
        // library), Rejected (deleted), or corrected before they're trusted. The whole
        // batch is reversible: every ingested row carries its ReviewBatch tag.

        public class IngestReviewItemDto
        {
            public int id { get; set; }
            // Which table this id lives in: "movie" | "series" | "misc". MiscVideo has its own id
            // sequence, so every detail/approve/reject must carry this — a bare id is ambiguous.
            public string Kind { get; set; } = "movie";
            public string? Title { get; set; }
            public string? SimpleTitle { get; set; }
            public string? imdbID { get; set; }
            public string? TitleType { get; set; }
            /// <summary>Resolved release year — compared to the on-disk folder year to confirm a match.</summary>
            public int? Year { get; set; }
            /// <summary>Authoritative IMDb title from the last scrape/enrich. Lets the card show the IMDb
            /// cross-check from stored data — no per-card live OMDB lookup on page load.</summary>
            public string? ImdbScrapedTitle { get; set; }
            /// <summary>Current stored poster link (pre-fills the editable Poster URL field).</summary>
            public string? PosterLink { get; set; }
            public string? ReviewBatch { get; set; }
            public string? ReviewProvenance { get; set; }
            public string? ReviewConfidence { get; set; }
            public string? ReviewSourcePath { get; set; }
            public bool IsSeries { get; set; }
            public int FileCount { get; set; }      // movie-shaped / misc: mapped media files
            public int PlayableCount { get; set; }  // …of those, Jellyfin-ready right now (streamable)
            public int MissingCount { get; set; }   // …of those, flagged gone by a sync (MissingSinceUtc)
            public int EpisodeTotal { get; set; }   // series: total episodes
            public int EpisodeHave { get; set; }    // series: episodes that have a file ("have X of Y")
            public int EpisodePlayable { get; set; }// series: episodes with a Jellyfin-ready file
            // Scraper's own uncertainty flag (wrong-looking match, ambiguous title, etc.).
            public bool ImdbNeedsReview { get; set; }
            public string? ImdbReviewReason { get; set; }
            // ── misc-video only ──
            public string? Category { get; set; }
            public string? RelatedTitle { get; set; }
            public string? CollectionName { get; set; }
        }

        [HttpGet("/API/Admin/IngestReview/List")]
        public async Task<IActionResult> IngestReviewList(string scope = "batch")
        {
            if (!await IsCurrentUserEditor()) return Forbid();
            bool gapsScope = string.Equals(scope, "gaps", StringComparison.OrdinalIgnoreCase);

            // File / episode summaries so each card shows "N files" / "have X of Y" and, crucially,
            // whether those files are actually *streamable* now (synced to Jellyfin, not gone missing)
            // — an unplayable title is a concern the reviewer must see. Computed first so the "oddities"
            // scope below can select live titles whose files aren't streamable.
            var fileByPlayable = (await movieDb.MediaFiles.GroupBy(f => f.PlayableId)
                .Select(g => new
                {
                    g.Key,
                    n = g.Count(),
                    playable = g.Count(f => f.JellyfinItemId != null && f.MissingSinceUtc == null),
                    missing = g.Count(f => f.MissingSinceUtc != null),
                    primary = g.Count(f => f.Role == MovieFileRole.Primary),
                }).ToListAsync()).ToDictionary(x => x.Key, x => x);
            var epTotal = await movieDb.Episodes.Where(e => e.SeriesId != null).GroupBy(e => e.SeriesId!.Value)
                .Select(g => new { g.Key, n = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.n);
            var epHave = await movieDb.Episodes
                .Where(e => e.SeriesId != null && e.PlayableId != null && movieDb.MediaFiles.Any(f => f.PlayableId == e.PlayableId))
                .GroupBy(e => e.SeriesId!.Value).Select(g => new { g.Key, n = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.n);
            var epPlayable = await movieDb.Episodes
                .Where(e => e.SeriesId != null && e.PlayableId != null
                    && movieDb.MediaFiles.Any(f => f.PlayableId == e.PlayableId && f.JellyfinItemId != null && f.MissingSinceUtc == null))
                .GroupBy(e => e.SeriesId!.Value).Select(g => new { g.Key, n = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.n);

            // "oddities" scope additionally surfaces LIVE (already-approved, ReviewBatch == null) titles
            // with a file oddity — files present but none streamable, a file gone missing, or no Primary
            // — that haven't been explicitly acknowledged (OddityAcknowledgedUtc). A live title with no
            // files at all is a different concern (a gap), not surfaced here.
            bool oddScope = string.Equals(scope, "oddities", StringComparison.OrdinalIgnoreCase);
            var oddPlayableIds = oddScope
                ? fileByPlayable.Where(kv => kv.Value.n > 0 && (kv.Value.playable == 0 || kv.Value.missing > 0 || kv.Value.primary == 0))
                    .Select(kv => kv.Key).ToHashSet()
                : new HashSet<int>();

            // Movies only — series-typed rows now live in the Series table (added below). A row still
            // in a ReviewBatch is the exception: a title filed as a movie that IMDb calls a
            // mini-series is exactly what a reviewer has to rule on, and excluding it by type would
            // quarantine it into invisibility — present in the DB, absent from the queue, hidden from
            // browse. The exclusion applies to LIVE rows, which is where it came from.
            var raw = await movieDb.Movies
                .Where(m => (m.ReviewBatch != null
                        || (m.TitleType != TitleType.TvSeries && m.TitleType != TitleType.TvMiniSeries))
                    && (m.ReviewBatch != null
                        || (oddScope && m.ReviewBatch == null && m.OddityAcknowledgedUtc == null
                            && m.PlayableId != null && oddPlayableIds.Contains(m.PlayableId.Value))))
                .Select(m => new { m.id, m.Title, m.SimpleTitle, m.imdbID, m.TitleType, m.PlayableId, m.ReviewBatch, m.ReviewProvenance, m.ReviewConfidence, m.ReviewSourcePath, m.ImdbNeedsReview, m.ImdbReviewReason, m.ReleaseDate, m.ImdbReleaseDate, m.ImdbScrapedTitle, PosterLink = m.PosterDetails != null ? m.PosterDetails.PosterLink : null })
                .ToListAsync();

            // Lowest-trust first so the riskiest resolutions get eyeballed before the easy bulk.
            static int ConfRank(string? c) => (c ?? "").ToUpperInvariant() switch { "LOW" => 0, "MEDIUM" => 1, "NONE" => 0, "HIGH" => 2, _ => 3 };
            static int ProvRank(string? p) => p switch { "manual" => -1, "web-search" => 0, "suggestion-api" => 1, "finalsort-cache" => 2, _ => 3 };

            var items = raw
                .Select(m => new IngestReviewItemDto
                {
                    id = m.id,
                    Kind = "movie",
                    Title = m.Title,
                    SimpleTitle = m.SimpleTitle,
                    imdbID = m.imdbID,
                    TitleType = m.TitleType.ToString(),
                    Year = m.ReleaseDate != null ? m.ReleaseDate.Value.Year : (m.ImdbReleaseDate != null ? m.ImdbReleaseDate.Value.Year : (int?)null),
                    ImdbScrapedTitle = m.ImdbScrapedTitle,
                    PosterLink = m.PosterLink,
                    ReviewBatch = m.ReviewBatch,
                    ReviewProvenance = m.ReviewProvenance,
                    ReviewConfidence = m.ReviewConfidence,
                    ReviewSourcePath = m.ReviewSourcePath,
                    ImdbNeedsReview = m.ImdbNeedsReview,
                    ImdbReviewReason = m.ImdbReviewReason,
                    IsSeries = false,
                    FileCount = (m.PlayableId != null && fileByPlayable.TryGetValue(m.PlayableId.Value, out var fc)) ? fc.n : 0,
                    PlayableCount = (m.PlayableId != null && fileByPlayable.TryGetValue(m.PlayableId.Value, out var pc)) ? pc.playable : 0,
                    MissingCount = (m.PlayableId != null && fileByPlayable.TryGetValue(m.PlayableId.Value, out var mc)) ? mc.missing : 0,
                })
                .ToList();

            // Series (their own table now), with "have X of Y" episode summaries via SeriesId. In "gaps"
            // scope we ALSO surface series that have episodes not yet streamable (epPlayable < total) even
            // if already approved (ReviewBatch == null), so they can be hand-mapped.
            var gapSeriesIds = gapsScope
                ? epTotal.Where(kv => (epPlayable.TryGetValue(kv.Key, out var p) ? p : 0) < kv.Value).Select(kv => kv.Key).ToHashSet()
                : new HashSet<int>();
            // A series oddity: episodes are mapped but some aren't streamable (file missing / not synced) —
            // epHave > epPlayable. (Plain unmapped gaps belong to the "gaps" scope, not here.)
            var oddSeriesIds = oddScope
                ? epHave.Where(kv => kv.Value > (epPlayable.TryGetValue(kv.Key, out var p) ? p : 0)).Select(kv => kv.Key).ToHashSet()
                : new HashSet<int>();
            // A gap/oddity on an already-approved series is flagged ONCE for review; once the reviewer
            // acknowledges it (OddityAcknowledgedUtc), the known gap must not keep re-surfacing. Pending
            // (ReviewBatch != null) rows always show regardless.
            var seriesRaw = await movieDb.Series
                .Where(s => s.ReviewBatch != null
                    || (gapSeriesIds.Contains(s.Id) && s.OddityAcknowledgedUtc == null)
                    || (oddScope && oddSeriesIds.Contains(s.Id) && s.OddityAcknowledgedUtc == null))
                .Select(s => new { s.Id, s.Title, s.SimpleTitle, s.imdbID, s.TitleType, s.ReviewBatch, s.ReviewProvenance, s.ReviewConfidence, s.ReviewSourcePath, s.ImdbNeedsReview, s.ImdbReviewReason, s.ReleaseDate, s.ImdbReleaseDate, s.StartYear, s.ImdbScrapedTitle, PosterLink = s.PosterDetails != null ? s.PosterDetails.PosterLink : null })
                .ToListAsync();
            items.AddRange(seriesRaw.Select(s => new IngestReviewItemDto
            {
                id = s.Id,
                Kind = "series",
                Title = s.Title,
                SimpleTitle = s.SimpleTitle,
                imdbID = s.imdbID,
                TitleType = s.TitleType.ToString(),
                Year = s.ReleaseDate != null ? s.ReleaseDate.Value.Year : (s.ImdbReleaseDate != null ? s.ImdbReleaseDate.Value.Year : s.StartYear),
                ImdbScrapedTitle = s.ImdbScrapedTitle,
                PosterLink = s.PosterLink,
                ReviewBatch = s.ReviewBatch,
                ReviewProvenance = s.ReviewProvenance,
                ReviewConfidence = s.ReviewConfidence,
                ReviewSourcePath = s.ReviewSourcePath,
                ImdbNeedsReview = s.ImdbNeedsReview,
                ImdbReviewReason = s.ImdbReviewReason,
                IsSeries = true,
                EpisodeTotal = epTotal.TryGetValue(s.Id, out var et) ? et : 0,
                EpisodeHave = epHave.TryGetValue(s.Id, out var eh) ? eh : 0,
                EpisodePlayable = epPlayable.TryGetValue(s.Id, out var ep) ? ep : 0,
            }));

            // Lowest-trust first, then group franchises/related titles by the canonical sort key
            // (SimpleTitle — same ordering as Browse, so e.g. Star Trek 1/2/3/4 and Dragon Ball Z/Kai/GT/
            // Super sit together) for a coherent review pass; Title is the fallback.
            items = items
                .OrderBy(i => ConfRank(i.ReviewConfidence))
                .ThenBy(i => ProvRank(i.ReviewProvenance))
                .ThenBy(i => string.IsNullOrEmpty(i.SimpleTitle) ? i.Title : i.SimpleTitle, StringComparer.OrdinalIgnoreCase)
                .ToList();

            // MiscVideos (no own tt: workprints, stage performances, instructional/shorts sets) carry
            // Kind="misc". Their related title resolves through Movie (RelatedMovieId) OR Series (RelatedSeriesId).
            // A misc that's ATTACHED to a title AND has no standalone Description is episodic-extra content
            // (an OP/ED, music video, featurette) — it gets NO card of its own; it's surfaced on its parent's
            // card (relatedMisc) and approved/rejected along with that parent. Only standalone misc (unrelated)
            // or a related misc that carries its own Description earns a review card here.
            var miscRaw = await movieDb.MiscVideos
                .Where(v => v.ReviewBatch != null
                    && ((v.RelatedMovieId == null && v.RelatedSeriesId == null)
                        || (v.Description != null && v.Description != "")))
                .Select(v => new { v.Id, v.PlayableId, v.Title, v.SimpleTitle, v.Year, v.Category, v.CollectionName, v.RelatedMovieId, v.RelatedSeriesId, v.ReviewBatch, v.ReviewProvenance, v.ReviewSourcePath })
                .ToListAsync();
            if (miscRaw.Count > 0)
            {
                var relMovieIds = miscRaw.Where(v => v.RelatedMovieId != null).Select(v => v.RelatedMovieId!.Value).Distinct().ToList();
                var relSeriesIds = miscRaw.Where(v => v.RelatedSeriesId != null).Select(v => v.RelatedSeriesId!.Value).Distinct().ToList();
                var relMovieTitles = await movieDb.Movies.Where(m => relMovieIds.Contains(m.id)).Select(m => new { m.id, m.Title }).ToDictionaryAsync(x => x.id, x => x.Title);
                var relSeriesTitles = await movieDb.Series.Where(s => relSeriesIds.Contains(s.Id)).Select(s => new { s.Id, s.Title }).ToDictionaryAsync(x => x.Id, x => x.Title);
                items.AddRange(miscRaw
                    .Select(v => new IngestReviewItemDto
                    {
                        id = v.Id,
                        Kind = "misc",
                        Title = v.Title,
                        SimpleTitle = v.SimpleTitle,
                        Year = v.Year,
                        TitleType = "MiscVideo",
                        Category = v.Category,
                        CollectionName = v.CollectionName,
                        RelatedTitle = (v.RelatedMovieId != null && relMovieTitles.TryGetValue(v.RelatedMovieId.Value, out var rmt)) ? rmt
                                     : (v.RelatedSeriesId != null && relSeriesTitles.TryGetValue(v.RelatedSeriesId.Value, out var rst)) ? rst : null,
                        ReviewBatch = v.ReviewBatch,
                        ReviewProvenance = v.ReviewProvenance,
                        ReviewSourcePath = v.ReviewSourcePath,
                        IsSeries = false,
                        FileCount = fileByPlayable.TryGetValue(v.PlayableId, out var mfc) ? mfc.n : 0,
                        PlayableCount = fileByPlayable.TryGetValue(v.PlayableId, out var mpc) ? mpc.playable : 0,
                        MissingCount = fileByPlayable.TryGetValue(v.PlayableId, out var mmc) ? mmc.missing : 0,
                    })
                    .OrderBy(i => i.CollectionName ?? "")
                    .ThenBy(i => i.Title, StringComparer.OrdinalIgnoreCase));
            }

            var batches = items.GroupBy(i => i.ReviewBatch).Select(g => new { batch = g.Key, count = g.Count() }).ToList();
            var byType = items.GroupBy(i => i.TitleType).Select(g => new { type = g.Key, count = g.Count() }).OrderByDescending(x => x.count).ToList();
            var byConfidence = items.GroupBy(i => i.ReviewConfidence ?? "?").Select(g => new { confidence = g.Key, count = g.Count() }).ToList();

            return Ok(new { total = items.Count, batches, byType, byConfidence, items });
        }

        public class AcknowledgeOddityRequest
        {
            public int Id { get; set; }
            public string Kind { get; set; } = "movie";   // "movie" | "series"
        }

        // Mark a live title's file oddity as reviewed so it stops surfacing in the "oddities" scope.
        // Does NOT touch files or ReviewBatch — purely "I've seen this, it's fine / I'll handle it".
        [HttpPost("/API/Admin/IngestReview/AcknowledgeOddity")]
        public async Task<IActionResult> AcknowledgeOddity([FromBody] AcknowledgeOddityRequest req)
        {
            if (!await IsCurrentUserEditor()) return Forbid();
            if (req == null) return BadRequest(new { Message = "Invalid request." });
            var now = DateTime.UtcNow;
            if (string.Equals(req.Kind, "series", StringComparison.OrdinalIgnoreCase))
            {
                var s = await movieDb.Series.FirstOrDefaultAsync(x => x.Id == req.Id);
                if (s == null) return NotFound(new { Message = "Series not found" });
                s.OddityAcknowledgedUtc = now;
            }
            else
            {
                var m = await movieDb.Movies.FirstOrDefaultAsync(x => x.id == req.Id);
                if (m == null) return NotFound(new { Message = "Movie not found" });
                m.OddityAcknowledgedUtc = now;
            }
            await movieDb.SaveChangesAsync();
            return Ok(new { Success = true });
        }

        // Per-title detail for the review tool: a movie's media files, or a series' episodes grouped by
        // season with the file mapped to each and the match strategy (MediaFile.Label "match:<strategy>")
        // so the position-based matches (absolute/combined/title) can be scrutinized. Lazy-loaded per card.
        [HttpGet("/API/Admin/IngestReview/Detail")]
        public async Task<IActionResult> IngestReviewDetail(int id, string kind = "movie")
        {
            if (!await IsCurrentUserEditor()) return Forbid();

            if (string.Equals(kind, "misc", StringComparison.OrdinalIgnoreCase))
            {
                var mv = await movieDb.MiscVideos.FirstOrDefaultAsync(v => v.Id == id);
                if (mv == null) return NotFound(new { Message = "Not found" });
                var miscFiles = await movieDb.MediaFiles.Where(f => f.PlayableId == mv.PlayableId)
                    .OrderBy(f => f.Role).ThenBy(f => f.PartNumber).ThenBy(f => f.Id)
                    .Select(f => (object)new { path = f.Path, role = f.Role.ToString(), label = f.Label })
                    .ToListAsync();
                string? relTitle = null, relKind = null;
                if (mv.RelatedMovieId != null)
                {
                    relTitle = await movieDb.Movies.Where(m => m.id == mv.RelatedMovieId).Select(m => m.Title).FirstOrDefaultAsync();
                    relKind = "movie";
                }
                else if (mv.RelatedSeriesId != null)
                {
                    relTitle = await movieDb.Series.Where(s => s.Id == mv.RelatedSeriesId).Select(s => s.Title).FirstOrDefaultAsync();
                    relKind = "series";
                }
                return Ok(new { kind = "misc", category = mv.Category, collectionName = mv.CollectionName, relatedTitle = relTitle, relatedKind = relKind, description = mv.Description, files = miscFiles });
            }

            // ── series (its own table): episodes by SeriesId, grouped by season, with mapped files + strategy ──
            if (string.Equals(kind, "series", StringComparison.OrdinalIgnoreCase))
            {
                var ser = await movieDb.Series.FirstOrDefaultAsync(s => s.Id == id);
                if (ser == null) return NotFound(new { Message = "Not found" });
                var allEps = await movieDb.Episodes.Where(e => e.SeriesId == id)
                    .OrderBy(e => e.SeasonNumber).ThenBy(e => e.EpisodeNumber)
                    .Select(e => new { e.Id, e.SeasonNumber, e.EpisodeNumber, e.Title, e.PlayableId })
                    .ToListAsync();
                // The (Season 0, Ep 0, "Extras") pseudo-episode is the holder for series/season-level Extra
                // files (not a real episode) — pull it out and surface its files separately.
                static bool IsExtrasHolder(int s, int e, string? t) => s == 0 && e == 0 && t == "Extras";
                var extrasHolder = allEps.FirstOrDefault(e => IsExtrasHolder(e.SeasonNumber, e.EpisodeNumber, e.Title));
                var seps = allEps.Where(e => !IsExtrasHolder(e.SeasonNumber, e.EpisodeNumber, e.Title)).ToList();
                var sPlayableIds = allEps.Where(e => e.PlayableId != null).Select(e => e.PlayableId!.Value).ToList();
                var sFilesByPlayable = (await movieDb.MediaFiles.Where(f => sPlayableIds.Contains(f.PlayableId))
                        .Select(f => new { f.Id, f.PlayableId, f.Path, f.Label, f.Role }).ToListAsync())
                    .GroupBy(f => f.PlayableId).ToDictionary(g => g.Key, g => g.ToList());
                var emptyFiles = new List<object>().Select(_ => new { mediaFileId = 0, path = (string)null, role = (string)null, label = (string)null }).ToList();
                var sSeasons = seps.GroupBy(e => e.SeasonNumber).OrderBy(g => g.Key).Select(g => new
                {
                    season = g.Key,
                    episodes = g.Select(e => new
                    {
                        episodeId = e.Id,
                        episode = e.EpisodeNumber,
                        title = e.Title,
                        files = (e.PlayableId != null && sFilesByPlayable.TryGetValue(e.PlayableId.Value, out var fl))
                            ? fl.Select(f => new { mediaFileId = f.Id, path = f.Path, role = f.Role.ToString(), label = f.Label }).ToList()
                            : emptyFiles,
                    }).ToList(),
                }).ToList();
                var seriesExtras = (extrasHolder?.PlayableId != null && sFilesByPlayable.TryGetValue(extrasHolder.PlayableId.Value, out var xf))
                    ? xf.Select(f => new { mediaFileId = f.Id, path = f.Path, role = f.Role.ToString(), label = f.Label }).ToList()
                    : emptyFiles;
                var sGenres = await movieDb.SeriesGenres.Where(g => g.SeriesId == id).OrderBy(g => g.Ordering).Select(g => g.Genre.Name).ToListAsync();
                var sCredits = await movieDb.SeriesCredits.Where(cr => cr.SeriesId == id).OrderBy(cr => cr.Ordering)
                    .Select(cr => new { cr.Role, Name = cr.Person.DisplayName }).ToListAsync();
                var sPlot = await movieDb.SeriesPlotSummaries.Where(p => p.SeriesId == id).OrderBy(p => p.Ordering).Select(p => p.Text).FirstOrDefaultAsync();
                string[] SNames(CreditRole r, int take) => sCredits.Where(x => x.Role == r && x.Name != null).Select(x => x.Name!).Distinct().Take(take).ToArray();
                // Re-mark the cached folder dump against CURRENT mappings: scan-series-folders bakes the
                // [OK]/[??] flags at scan time, so files mapped AFTER the last scan wrongly show [??].
                // Match by filename against the series' live MediaFile names and recompute the header counts.
                string liveFolderListing = ser.FolderListing;
                if (!string.IsNullOrEmpty(liveFolderListing))
                {
                    // Runs on the Linux server, but the stored paths are Windows ("L:\...\file.mkv") — so
                    // System.IO.Path.GetFileName would NOT strip them (backslash isn't a Linux separator).
                    // Split on BOTH separators to get the bare filename regardless of host OS.
                    static string BaseName(string p) => (p ?? "").Replace('\\', '/').TrimEnd('/').Split('/')[^1];
                    // Mark a line [OK] if its filename is captured by ANY title, not just this series.
                    // Co-located series share one folder dump (e.g. the 2003 micro-series and the 2008
                    // series both live under "Star Wars - The Clone Wars (2003-2020)"), so a file mapped
                    // to a SIBLING title would otherwise show a misleading "[??] NOT captured" here.
                    var mappedNames = (await movieDb.MediaFiles.Select(f => f.Path).ToListAsync())
                        .Select(p => BaseName(p).Trim().ToLowerInvariant())
                        .Where(n => !string.IsNullOrEmpty(n)).ToHashSet();
                    var lineRx = new System.Text.RegularExpressions.Regex(@"^(\[OK\]|\[\?\?\]) (.*?)(    \S+ [KMG]B)\s*$");
                    int okN = 0, noN = 0;
                    var outLines = liveFolderListing.Replace("\r", "").Split('\n').Select(line =>
                    {
                        var m = lineRx.Match(line);
                        if (!m.Success) return line;
                        var rel = m.Groups[2].Value;
                        var name = BaseName(rel).Trim().ToLowerInvariant();
                        bool ok = !string.IsNullOrEmpty(name) && mappedNames.Contains(name);
                        if (ok) okN++; else noN++;
                        return (ok ? "[OK]" : "[??]") + " " + rel + m.Groups[3].Value;
                    }).ToList();
                    for (int i = 0; i < outLines.Count; i++)
                        outLines[i] = System.Text.RegularExpressions.Regex.Replace(outLines[i],
                            @"\(\[OK\] mapped \d+ / \[\?\?\] NOT captured \d+\)",
                            $"([OK] mapped {okN} / [??] NOT captured {noN})");
                    liveFolderListing = string.Join("\n", outLines);
                }
                var seriesRelatedMisc = await LoadRelatedMiscAsync(null, id);
                return Ok(new
                {
                    kind = "series",
                    episodeTotal = seps.Count,
                    episodeHave = seps.Count(e => e.PlayableId != null && sFilesByPlayable.ContainsKey(e.PlayableId.Value)),
                    seasons = sSeasons,
                    seriesExtras,
                    relatedMisc = seriesRelatedMisc,
                    folderListing = liveFolderListing,   // re-marked live vs current mappings (scan-time flags go stale)
                    meta = new
                    {
                        plot = sPlot ?? ser.Plot,
                        genres = sGenres,
                        directors = SNames(CreditRole.Director, 5),
                        writers = SNames(CreditRole.Writer, 5),
                        cast = SNames(CreditRole.Actor, 10),
                        runtime = ser.Runtime,
                        runtimeMinutes = ser.RuntimeMinutes,
                        imdbRating = ser.ImdbRatingScraped ?? ser.imdbRating,
                        rtTomatometer = ser.RtTomatometer,
                        rtPopcornmeter = ser.RtPopcornmeter,
                        mpaa = ser.MpaaRating,
                        tagline = ser.Tagline,
                        year = ser.ReleaseDate != null ? ser.ReleaseDate.Value.Year : (ser.ImdbReleaseDate != null ? ser.ImdbReleaseDate.Value.Year : (int?)null),
                    },
                });
            }

            // ── movie ──
            var movie = await movieDb.Movies.FirstOrDefaultAsync(m => m.id == id);
            if (movie == null) return NotFound(new { Message = "Not found" });
            var files = movie.PlayableId == null
                ? new List<object>()
                : await movieDb.MediaFiles.Where(f => f.PlayableId == movie.PlayableId)
                    .OrderBy(f => f.Role).ThenBy(f => f.PartNumber).ThenBy(f => f.Id)
                    .Select(f => (object)new { mediaFileId = f.Id, path = f.Path, role = f.Role.ToString(), label = f.Label, partNumber = f.PartNumber,
                        isPlayable = f.JellyfinItemId != null && f.MissingSinceUtc == null, missing = f.MissingSinceUtc != null })
                    .ToListAsync();
            // Cached IMDb/TMDB metadata (normalized tables) so the review card can show what's being approved
            // — plot / genres / director / cast / ratings — with no live lookup.
            var mGenres = await movieDb.MovieGenres.Where(g => g.MovieID == id).OrderBy(g => g.Ordering).Select(g => g.Genre.Name).ToListAsync();
            var mCredits = await movieDb.MovieCredits.Where(cr => cr.MovieID == id).OrderBy(cr => cr.Ordering)
                .Select(cr => new { cr.Role, Name = cr.Person.DisplayName }).ToListAsync();
            var mPlot = await movieDb.MoviePlotSummaries.Where(p => p.MovieID == id).OrderBy(p => p.Ordering).Select(p => p.Text).FirstOrDefaultAsync();
            string[] MNames(CreditRole r, int take) => mCredits.Where(x => x.Role == r && x.Name != null).Select(x => x.Name!).Distinct().Take(take).ToArray();
            var movieRelatedMisc = await LoadRelatedMiscAsync(id, null);
            return Ok(new
            {
                kind = "movie",
                files,
                relatedMisc = movieRelatedMisc,
                meta = new
                {
                    plot = mPlot ?? movie.Plot,
                    genres = mGenres,
                    directors = MNames(CreditRole.Director, 5),
                    writers = MNames(CreditRole.Writer, 5),
                    cast = MNames(CreditRole.Actor, 10),
                    runtime = movie.Runtime,
                    runtimeMinutes = movie.RuntimeMinutes,
                    imdbRating = movie.ImdbRatingScraped ?? movie.imdbRating,
                    rtTomatometer = movie.RtTomatometer,
                    rtPopcornmeter = movie.RtPopcornmeter,
                    mpaa = movie.MpaaRating,
                    tagline = movie.Tagline,
                    year = movie.ReleaseDate != null ? movie.ReleaseDate.Value.Year : (movie.ImdbReleaseDate != null ? movie.ImdbReleaseDate.Value.Year : (int?)null),
                }
            });
        }

        // Extras (MiscVideos) that point AT a title via RelatedMovieId/RelatedSeriesId — surfaced on the
        // movie/series review card so you can see what's attached without hunting the misc queue. Pass the
        // one relevant id; the other stays null.
        private async Task<List<object>> LoadRelatedMiscAsync(int? relatedMovieId, int? relatedSeriesId)
        {
            var rel = await movieDb.MiscVideos
                .Where(v => (relatedMovieId != null && v.RelatedMovieId == relatedMovieId)
                         || (relatedSeriesId != null && v.RelatedSeriesId == relatedSeriesId))
                .OrderBy(v => v.CollectionName).ThenBy(v => v.SortOrder).ThenBy(v => v.Title)
                .Select(v => new { v.Id, v.PlayableId, v.Title, v.Category, v.Year, v.CollectionName, Pending = v.ReviewBatch != null })
                .ToListAsync();
            if (rel.Count == 0) return new List<object>();
            var pids = rel.Select(v => v.PlayableId).ToList();
            var filesByPid = (await movieDb.MediaFiles.Where(f => pids.Contains(f.PlayableId))
                    .OrderBy(f => f.Role).ThenBy(f => f.PartNumber).ThenBy(f => f.Id)
                    .Select(f => new { f.PlayableId, f.Path, f.Role }).ToListAsync())
                .GroupBy(f => f.PlayableId)
                .ToDictionary(g => g.Key, g => g.Select(f => (object)new { path = f.Path, role = f.Role.ToString() }).ToList());
            return rel.Select(v => (object)new
            {
                id = v.Id,
                title = v.Title,
                category = v.Category,
                year = v.Year,
                collectionName = v.CollectionName,
                pending = v.Pending,
                files = filesByPid.TryGetValue(v.PlayableId, out var ff) ? ff : new List<object>(),
            }).ToList();
        }

        // A hand-mapped path must be the FULL on-disk path: Jellyfin matches by path, so a bare filename (or
        // any non-rooted value) looks "mapped" yet never streams (JellyfinItemId stays null). Accept a rooted
        // Windows/UNC path as-is; otherwise resolve a bare filename against the series' scanned FolderListing
        // snapshot (the prod web app can't read the NAS, but it has that snapshot) by unique filename. Returns
        // false with a reason when it can't be resolved, so the caller rejects rather than stores garbage.
        private static bool TryResolveMappedPath(string? submitted, string? folderListing, out string resolved, out string error)
        {
            resolved = (submitted ?? "").Trim();
            error = "";
            if (resolved.Length == 0) { error = "Path required"; return false; }

            bool rooted = System.Text.RegularExpressions.Regex.IsMatch(resolved, @"^[A-Za-z]:[\\/]") || resolved.StartsWith(@"\\");
            if (rooted) return true;

            var fileName = LastPathSegment(resolved);
            if (string.IsNullOrWhiteSpace(folderListing))
            {
                error = $"'{resolved}' isn't a full path and there's no folder scan to resolve it — paste the full L:\\ path (or run scan-series-folders).";
                return false;
            }
            var matches = ParseFolderListingFullPaths(folderListing)
                .Where(full => string.Equals(LastPathSegment(full), fileName, StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (matches.Count == 1) { resolved = matches[0]; return true; }
            error = matches.Count == 0
                ? $"Couldn't find '{fileName}' in this series' scanned folder — paste the full L:\\ path."
                : $"'{fileName}' matches {matches.Count} files in the folder — paste the full L:\\ path to disambiguate.";
            return false;
        }

        private static string LastPathSegment(string p)
        {
            var s = (p ?? "").Replace('/', '\\');
            var i = s.LastIndexOf('\\');
            return i >= 0 ? s.Substring(i + 1) : s;
        }

        // Reconstruct full paths from a Series.FolderListing snapshot (see ScanSeriesFoldersCommand): line 0 is
        // the folder root, then after a "----" separator each line is "<4-char flag> <relative path>    <size>".
        private static IEnumerable<string> ParseFolderListingFullPaths(string listing)
        {
            var lines = listing.Replace("\r\n", "\n").Split('\n');
            if (lines.Length == 0) yield break;
            var root = lines[0].Trim().TrimEnd('\\', '/');
            if (root.Length == 0) yield break;
            bool past = false;
            foreach (var raw in lines.Skip(1))
            {
                if (!past) { if (raw.StartsWith("----")) past = true; continue; }
                if (raw.Length < 6) continue;
                var rel = raw.Substring(5);              // drop the 4-char flag and the space after it
                var sep = rel.LastIndexOf("    ");        // strip the trailing "    <size>"
                if (sep > 0) rel = rel.Substring(0, sep);
                rel = rel.Trim();
                if (rel.Length == 0) continue;
                yield return root + "\\" + rel.Replace('/', '\\');
            }
        }

        public class SetEpisodeFileRequest { public int EpisodeId { get; set; } public string? Path { get; set; } }

        // Manually point a series episode at the correct on-disk file (chosen from the folder dump). Ensures
        // the episode has a Playable and sets/replaces its Primary MediaFile (Label "match:manual"); an empty
        // path clears it. Editor-gated. The file becomes streamable after the next Jellyfin sync (matched by
        // path). Disk files are untouched — this only records the mapping.
        [HttpPost("/API/Admin/IngestReview/SetEpisodeFile")]
        public async Task<IActionResult> IngestReviewSetEpisodeFile([FromBody] SetEpisodeFileRequest req)
        {
            if (!await IsCurrentUserEditor()) return Forbid();
            if (req == null || req.EpisodeId == 0) return BadRequest(new { Message = "EpisodeId required" });

            var ep = await movieDb.Episodes.FirstOrDefaultAsync(e => e.Id == req.EpisodeId);
            if (ep == null) return NotFound(new { Message = "Episode not found" });

            if (ep.PlayableId == null)
            {
                ep.Playable = new Playable { Kind = PlayableKind.Episode };
                await movieDb.SaveChangesAsync();   // assigns ep.PlayableId
            }
            var playableId = ep.PlayableId!.Value;

            var path = req.Path?.Trim();
            if (string.IsNullOrWhiteSpace(path))
            {
                var existing = await movieDb.MediaFiles.Where(f => f.PlayableId == playableId).ToListAsync();
                movieDb.MediaFiles.RemoveRange(existing);
                await movieDb.SaveChangesAsync();
                return Ok(new { Success = true, cleared = existing.Count });
            }

            // Resolve to a FULL path (reject a bare filename that would map but never stream).
            var listing = ep.SeriesId != null
                ? await movieDb.Series.Where(s => s.Id == ep.SeriesId.Value).Select(s => s.FolderListing).FirstOrDefaultAsync()
                : null;
            if (!TryResolveMappedPath(path, listing, out var fullPath, out var resolveErr))
                return BadRequest(new { Message = resolveErr });
            path = fullPath;

            // Replace any current Primary with the chosen file.
            var prior = await movieDb.MediaFiles.Where(f => f.PlayableId == playableId && f.Role == MovieFileRole.Primary).ToListAsync();
            movieDb.MediaFiles.RemoveRange(prior);
            movieDb.MediaFiles.Add(new MediaFile { PlayableId = playableId, Path = path, Role = MovieFileRole.Primary, Label = "match:manual" });
            await movieDb.SaveChangesAsync();
            return Ok(new { Success = true });
        }

        // Generalized hand-map: assign a file as Primary or Extra, to an episode OR to the series' Extras
        // holder (a Season-0 / Ep-0 "Extras" pseudo-episode that carries series/season-level extras).
        public class SetFileRequest
        {
            public string TargetType { get; set; } = "episode";   // "episode" | "series"
            public int TargetId { get; set; }                      // episodeId, or seriesId for "series"
            public int? SeasonNumber { get; set; }                 // optional: scope a series Extra to a season
            public string Role { get; set; } = "Primary";          // "Primary" | "Extra"
            public string? Path { get; set; }
        }

        [HttpPost("/API/Admin/IngestReview/SetFile")]
        public async Task<IActionResult> IngestReviewSetFile([FromBody] SetFileRequest req)
        {
            if (!await IsCurrentUserEditor()) return Forbid();
            if (req == null) return BadRequest(new { Message = "Body required" });
            var path = req.Path?.Trim();
            if (string.IsNullOrWhiteSpace(path)) return BadRequest(new { Message = "Path required" });

            bool toSeries = string.Equals(req.TargetType, "series", StringComparison.OrdinalIgnoreCase);
            bool toMovie = string.Equals(req.TargetType, "movie", StringComparison.OrdinalIgnoreCase);

            // Resolve to a FULL path before storing (a bare filename maps but never streams via Jellyfin).
            // A movie has no scanned FolderListing, so its paths must be pasted fully rooted (listing = null).
            int? listingSeriesId = toMovie ? (int?)null
                : toSeries ? req.TargetId
                : await movieDb.Episodes.Where(e => e.Id == req.TargetId).Select(e => e.SeriesId).FirstOrDefaultAsync();
            var listing = listingSeriesId != null
                ? await movieDb.Series.Where(s => s.Id == listingSeriesId.Value).Select(s => s.FolderListing).FirstOrDefaultAsync()
                : null;
            if (!TryResolveMappedPath(path, listing, out var fullPath, out var resolveErr))
                return BadRequest(new { Message = resolveErr });
            path = fullPath;
            // A series target is always an Extra (it has no episode of its own); a movie/episode target honors the role.
            var role = (toSeries || string.Equals(req.Role, "Extra", StringComparison.OrdinalIgnoreCase))
                ? MovieFileRole.Extra : MovieFileRole.Primary;

            int playableId;
            if (toMovie)
            {
                var mov = await movieDb.Movies.FirstOrDefaultAsync(m => m.id == req.TargetId);
                if (mov == null) return NotFound(new { Message = "Movie not found" });
                if (mov.PlayableId == null)
                {
                    mov.Playable = new Playable { Kind = PlayableKind.Movie };
                    await movieDb.SaveChangesAsync();
                }
                playableId = mov.PlayableId!.Value;
            }
            else if (toSeries)
            {
                // Find/create the (Season 0, Ep 0, "Extras") holder for this series.
                var holder = await movieDb.Episodes.FirstOrDefaultAsync(e =>
                    e.SeriesId == req.TargetId && e.SeasonNumber == 0 && e.EpisodeNumber == 0 && e.Title == "Extras");
                if (holder == null)
                {
                    holder = new Episode { SeriesId = req.TargetId, SeasonNumber = 0, EpisodeNumber = 0, Title = "Extras" };
                    movieDb.Episodes.Add(holder);
                    await movieDb.SaveChangesAsync();
                }
                if (holder.PlayableId == null)
                {
                    holder.Playable = new Playable { Kind = PlayableKind.Episode };
                    await movieDb.SaveChangesAsync();
                }
                playableId = holder.PlayableId!.Value;
            }
            else
            {
                var ep = await movieDb.Episodes.FirstOrDefaultAsync(e => e.Id == req.TargetId);
                if (ep == null) return NotFound(new { Message = "Episode not found" });
                if (ep.PlayableId == null)
                {
                    ep.Playable = new Playable { Kind = PlayableKind.Episode };
                    await movieDb.SaveChangesAsync();
                }
                playableId = ep.PlayableId!.Value;
            }

            // Primary replaces the existing Primary; an Extra is added alongside (multiple allowed).
            if (role == MovieFileRole.Primary)
                movieDb.MediaFiles.RemoveRange(
                    await movieDb.MediaFiles.Where(f => f.PlayableId == playableId && f.Role == MovieFileRole.Primary).ToListAsync());

            if (!await movieDb.MediaFiles.AnyAsync(f => f.PlayableId == playableId && f.Path == path))
            {
                var label = role == MovieFileRole.Extra
                    ? (req.SeasonNumber != null ? $"manual:extra:s{req.SeasonNumber}" : "manual:extra")
                    : "match:manual";
                movieDb.MediaFiles.Add(new MediaFile { PlayableId = playableId, Path = path, Role = role, Label = label });
                await movieDb.SaveChangesAsync();
            }
            return Ok(new { Success = true });
        }

        public class MoveFileRequest { public int MediaFileId { get; set; } public string Action { get; set; } = "primary"; }

        // Reorder a title's files within its "feature sequence" (the Primary + ordered Parts of one playable).
        //   action "primary" → make this file the Primary (promotes a Part, Variant, or Extra; the old Primary
        //                       becomes the next Part). "up"/"down" → shift a Part/Primary one slot in the order.
        // After any move the sequence is renumbered: first = Primary (Part 1), the rest = Parts 2..N. A lone
        // file keeps PartNumber NULL. Variants/Extras not pulled in stay as they are. Editor-gated.
        [HttpPost("/API/Admin/IngestReview/MoveFile")]
        public async Task<IActionResult> IngestReviewMoveFile([FromBody] MoveFileRequest req)
        {
            if (!await IsCurrentUserEditor()) return Forbid();
            if (req == null || req.MediaFileId == 0) return BadRequest(new { Message = "MediaFileId required" });
            var mf = await movieDb.MediaFiles.FirstOrDefaultAsync(x => x.Id == req.MediaFileId);
            if (mf == null) return NotFound(new { Message = "File not found" });

            var all = await movieDb.MediaFiles.Where(x => x.PlayableId == mf.PlayableId).ToListAsync();
            // The feature sequence in current display order; Variants/Extras live outside it.
            var seq = all.Where(x => x.Role == MovieFileRole.Primary || x.Role == MovieFileRole.Part)
                .OrderBy(x => x.Role).ThenBy(x => x.PartNumber ?? int.MaxValue).ThenBy(x => x.Id).ToList();

            var action = (req.Action ?? "").Trim().ToLowerInvariant();
            if (action == "primary")
            {
                seq.RemoveAll(x => x.Id == mf.Id);   // a Part/Variant/Extra is pulled into the sequence at the front
                seq.Insert(0, mf);
            }
            else if (action == "up" || action == "down")
            {
                var idx = seq.FindIndex(x => x.Id == mf.Id);
                if (idx < 0) return BadRequest(new { Message = "Only a primary or part can be shifted." });
                var swap = action == "up" ? idx - 1 : idx + 1;
                if (swap < 0 || swap >= seq.Count) return Ok(new { Success = true });   // already at the edge
                (seq[idx], seq[swap]) = (seq[swap], seq[idx]);
            }
            else return BadRequest(new { Message = "Action must be primary, up, or down." });

            for (int i = 0; i < seq.Count; i++)
            {
                seq[i].Role = i == 0 ? MovieFileRole.Primary : MovieFileRole.Part;
                seq[i].PartNumber = seq.Count == 1 ? (int?)null : i + 1;
            }
            await movieDb.SaveChangesAsync();
            return Ok(new { Success = true });
        }

        public class RemoveFileRequest { public int MediaFileId { get; set; } }

        [HttpPost("/API/Admin/IngestReview/RemoveFile")]
        public async Task<IActionResult> IngestReviewRemoveFile([FromBody] RemoveFileRequest req)
        {
            if (!await IsCurrentUserEditor()) return Forbid();
            var f = await movieDb.MediaFiles.FirstOrDefaultAsync(x => x.Id == req.MediaFileId);
            if (f == null) return NotFound(new { Message = "File not found" });
            var playableId = f.PlayableId;
            var wasCreatedEp = (f.Label ?? "").StartsWith("match:created-ep", StringComparison.OrdinalIgnoreCase);
            movieDb.MediaFiles.Remove(f);
            await movieDb.SaveChangesAsync();

            var phantomRemoved = await CleanupEmptyCreatedEpPhantomAsync(playableId, wasCreatedEp);
            return Ok(new { Success = true, phantomEpisodeRemoved = phantomRemoved });
        }

        // A "created-ep phantom" is an Episode the bulk mapper fabricated from a filename (no ImdbId, title
        // taken from the file) purely to hold a file it couldn't match to a real episode — its MediaFile
        // carries Label "match:created-ep" (data/_create_missing_eps.py). When that file is later remapped to
        // the correct real episode and removed here, the fabricated episode is left behind as an empty "0/1"
        // gap that can never be filled (no such episode exists — often a typo/duplicate of a real one). If
        // removing a created-ep file empties such a phantom, delete the episode + its now-unreferenced
        // playable so it stops surfacing. Guarded tight: only a created-ep file, only an ImdbId-NULL episode,
        // and only once no files remain — a real (scraped) episode or one that still has files is never touched.
        private async Task<bool> CleanupEmptyCreatedEpPhantomAsync(int playableId, bool removedWasCreatedEp)
        {
            if (!removedWasCreatedEp) return false;
            if (await movieDb.MediaFiles.AnyAsync(m => m.PlayableId == playableId)) return false;
            var ep = await movieDb.Episodes.FirstOrDefaultAsync(e => e.PlayableId == playableId);
            if (ep == null || ep.ImdbId != null) return false;
            movieDb.Episodes.Remove(ep);   // episode first (its FK to Playable is Restrict), then the playable
            var pl = await movieDb.Playables.FirstOrDefaultAsync(p => p.Id == playableId);
            if (pl != null) movieDb.Playables.Remove(pl);
            await movieDb.SaveChangesAsync();
            return true;
        }

        // Movie ids in Ids, series ids in SeriesIds, misc-video ids in MiscIds (separate id sequences — see Kind).
        public class IngestReviewIdsRequest { public List<int> Ids { get; set; } = new(); public List<int> SeriesIds { get; set; } = new(); public List<int> MiscIds { get; set; } = new(); }

        // Apply the library's leading-"The" sort convention at the approve gate — PrepMovieTitle runs only
        // on manual insert, so ingested rows arrive un-inverted ("The Cube" instead of "Cube, The"). Preserve
        // a hand-curated SimpleTitle that isn't itself an article form (e.g. franchise numbering).
        private static void ApplyArticleConvention(Movie m)
        {
            var inv = MovieTheater.Ingest.TitleNorm.InvertLeadingThe(m.Title);
            if (string.Equals(inv, m.Title, StringComparison.Ordinal)) return;
            if (string.IsNullOrEmpty(m.SimpleTitle) || string.Equals(m.SimpleTitle, m.Title, StringComparison.Ordinal))
                m.SimpleTitle = inv;
            else if (m.SimpleTitle.StartsWith("The ", StringComparison.OrdinalIgnoreCase))
                m.SimpleTitle = MovieTheater.Ingest.TitleNorm.InvertLeadingThe(m.SimpleTitle);
            m.Title = inv;
        }
        private static void ApplyArticleConvention(Series s)
        {
            var inv = MovieTheater.Ingest.TitleNorm.InvertLeadingThe(s.Title);
            if (string.Equals(inv, s.Title, StringComparison.Ordinal)) return;
            if (string.IsNullOrEmpty(s.SimpleTitle) || string.Equals(s.SimpleTitle, s.Title, StringComparison.Ordinal))
                s.SimpleTitle = inv;
            else if (s.SimpleTitle.StartsWith("The ", StringComparison.OrdinalIgnoreCase))
                s.SimpleTitle = MovieTheater.Ingest.TitleNorm.InvertLeadingThe(s.SimpleTitle);
            s.Title = inv;
        }

        // Approve = clear the quarantine flag so the row joins the library (idempotent;
        // re-approving an already-cleared id is a no-op). ReviewSourcePath is kept — the
        // file-mapping pass (Phase 5) needs it.
        [HttpPost("/API/Admin/IngestReview/Approve")]
        public async Task<IActionResult> IngestReviewApprove([FromBody] IngestReviewIdsRequest req)
        {
            if (!await IsCurrentUserEditor()) return Forbid();
            if (req == null) return Ok(new { approved = 0 });

            var rows = req.Ids.Count == 0 ? new List<Movie>()
                : await movieDb.Movies.Where(m => req.Ids.Contains(m.id) && m.ReviewBatch != null).ToListAsync();
            foreach (var m in rows)
            {
                // IMDb's scraped year wins over ours when they differ — the scrape is the reliable source
                // (project rule), so persist it onto the canonical ReleaseDate at approve/save time.
                if (m.ImdbReleaseDate.HasValue && (m.ReleaseDate == null || m.ReleaseDate.Value.Year != m.ImdbReleaseDate.Value.Year))
                    m.ReleaseDate = m.ImdbReleaseDate;
                ApplyArticleConvention(m);
                m.ReviewBatch = null; m.ReviewProvenance = null; m.ReviewConfidence = null;
            }

            var seriesRows = req.SeriesIds.Count == 0 ? new List<Series>()
                : await movieDb.Series.Where(s => req.SeriesIds.Contains(s.Id) && s.ReviewBatch != null).ToListAsync();
            foreach (var s in seriesRows)
            {
                if (s.ImdbReleaseDate.HasValue && (s.ReleaseDate == null || s.ReleaseDate.Value.Year != s.ImdbReleaseDate.Value.Year))
                { s.ReleaseDate = s.ImdbReleaseDate; s.StartYear = s.ImdbReleaseDate.Value.Year; }
                ApplyArticleConvention(s);
                s.ReviewBatch = null; s.ReviewProvenance = null; s.ReviewConfidence = null;
            }

            var miscRows = req.MiscIds.Count == 0 ? new List<MiscVideo>()
                : await movieDb.MiscVideos.Where(v => req.MiscIds.Contains(v.Id) && v.ReviewBatch != null).ToListAsync();
            foreach (var v in miscRows) { v.ReviewBatch = null; v.ReviewProvenance = null; }

            // Episodic-extra misc (attached, no standalone Description) have no card of their own — approve
            // them WITH the parent series/movie being approved here, so they go live together.
            var approvedMovieIds = rows.Select(m => m.id).ToList();
            var approvedSeriesIds = seriesRows.Select(s => s.Id).ToList();
            var childMisc = (approvedMovieIds.Count == 0 && approvedSeriesIds.Count == 0) ? new List<MiscVideo>()
                : await movieDb.MiscVideos.Where(v => v.ReviewBatch != null
                    && (v.Description == null || v.Description == "")
                    && ((v.RelatedMovieId != null && approvedMovieIds.Contains(v.RelatedMovieId.Value))
                     || (v.RelatedSeriesId != null && approvedSeriesIds.Contains(v.RelatedSeriesId.Value)))).ToListAsync();
            foreach (var v in childMisc) { v.ReviewBatch = null; v.ReviewProvenance = null; }

            await movieDb.SaveChangesAsync();

            // A newly-approved title should carry a poster — fetch one (from IMDb via OMDB) for any movie /
            // series that lacks it. EnsurePosterAsync no-ops when a poster already exists; bounded
            // parallelism keeps a big "approve all" responsive, and a failed fetch never blocks approval.
            var posterTargets = rows.Select(m => (id: m.id, tt: m.imdbID, series: false))
                .Concat(seriesRows.Select(s => (id: s.Id, tt: s.imdbID, series: true)))
                .ToList();
            if (posterTargets.Count > 0)
                await Parallel.ForEachAsync(posterTargets, new ParallelOptions { MaxDegreeOfParallelism = 6 },
                    async (t, _) => await posterFetchService.EnsurePosterAsync(t.id, t.tt, t.series));

            return Ok(new { approved = rows.Count + seriesRows.Count + miscRows.Count + childMisc.Count });
        }

        // Fetch posters for already-approved movies/series that have none (e.g. the auto-approved series).
        // Runs in the web app so it writes to the live image store — the CLI backfill can't from a dev box.
        // Editor-gated; idempotent (EnsurePosterAsync no-ops where a poster exists).
        [HttpPost("/API/Admin/IngestReview/BackfillPosters")]
        public async Task<IActionResult> IngestReviewBackfillPosters([FromQuery] int minId = 0)
        {
            if (!await IsCurrentUserEditor()) return Forbid();
            // Target titles with no PosterDetails row AND those whose row never got an image downloaded
            // (PosterVersion == 0 — e.g. the scrape recorded a URL but the fetch failed). EnsurePosterAsync
            // no-ops where an on-disk image already exists, so this stays safe for legacy rows.
            // minId scopes the pass to recent ids (the buggy ingest era) so a run need not iterate the
            // whole legacy library — pass e.g. minId=9001 to target only recently-ingested titles.
            // Pending-review rows are INCLUDED, deliberately. They are the ones a person is about to
            // look at, and a review card without art is the hardest kind to judge; excluding them
            // meant the only titles that could be given a poster were the ones already approved.
            var series = await movieDb.Series.Where(s => s.imdbID != null && s.Id >= minId
                    && (s.PosterDetails == null || s.PosterDetails.PosterVersion == 0))
                .Select(s => new { s.Id, s.imdbID }).ToListAsync();
            var movies = await movieDb.Movies.Where(m => m.imdbID != null && m.id >= minId
                    && (m.PosterDetails == null || m.PosterDetails.PosterVersion == 0)
                    && m.TitleType != TitleType.TvSeries && m.TitleType != TitleType.TvMiniSeries)
                .Select(m => new { m.id, m.imdbID }).ToListAsync();
            var targets = series.Select(s => (id: s.Id, tt: s.imdbID, isSeries: true))
                .Concat(movies.Select(m => (id: m.id, tt: m.imdbID, isSeries: false))).ToList();

            int got = 0;
            if (targets.Count > 0)
                await Parallel.ForEachAsync(targets, new ParallelOptions { MaxDegreeOfParallelism = 6 },
                    async (t, _) => { if (await posterFetchService.EnsurePosterAsync(t.id, t.tt, t.isSeries)) System.Threading.Interlocked.Increment(ref got); });

            return Ok(new { attempted = targets.Count, got, minId });
        }
    }
}
