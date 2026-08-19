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
        // One-shot repair for the Movie/Series poster-namespace collision. Posters are on-disk files keyed
        // by id, and Movie & Series ids are NOT disjoint, so before series got their own ("series") bucket a
        // same-id Movie and Series shared "{id}.png" — a series showed the movie's poster.
        //
        // CHUNKED so it can never time out: each call handles the next `limit` series after `afterId`,
        // in parallel, and returns the cursor + whether more remain — the UI drives it to completion.
        // For each series it puts a poster in the series bucket (copying the existing "{id}.png" when the id
        // is the series' alone, else re-fetching the series' real poster from its tt).
        //
        // STRICTLY NON-DESTRUCTIVE to movie posters: it only READS the default ("{id}.png") namespace and
        // WRITES the series bucket; it NEVER deletes or overwrites a movie poster. As a courtesy it also
        // restores a colliding movie's poster *only when that movie has no poster file at all* (e.g. one an
        // earlier buggy run removed) by fetching the movie's OWN poster — again, never overwriting an
        // existing file. Runs in the web app so it writes the live image store (a dev box can't).
        // Editor-gated; idempotent (a series already in the bucket is skipped unless force=true).
        [HttpPost("/API/Admin/IngestReview/MigrateSeriesPosters")]
        public async Task<IActionResult> MigrateSeriesPosters(int afterId = 0, int limit = 40, bool force = false)
        {
            if (!await IsCurrentUserEditor()) return Forbid();
            limit = Math.Clamp(limit, 1, 200);

            var batch = await movieDb.Series.Where(s => s.Id > afterId)
                .OrderBy(s => s.Id).Take(limit)
                .Select(s => new { s.Id, s.imdbID }).ToListAsync();

            if (batch.Count == 0)
                return Ok(new { done = true, processed = 0, nextAfterId = afterId, copied = 0, refetched = 0, skipped = 0, movieRestored = 0, failed = 0, remaining = 0 });

            var batchIds = batch.Select(b => b.Id).ToList();
            // Precompute (no DbContext use inside the parallel body — MovieDb isn't thread-safe): which ids in
            // this chunk are also movies, and those movies' tts (to restore a movie that lost its poster).
            var collidingMovieTt = await movieDb.Movies.Where(m => batchIds.Contains(m.id))
                .Select(m => new { m.id, m.imdbID }).ToDictionaryAsync(x => x.id, x => x.imdbID);

            int copied = 0, refetched = 0, skipped = 0, movieRestored = 0, failed = 0;

            await Parallel.ForEachAsync(batch, new ParallelOptions { MaxDegreeOfParallelism = 6 }, async (s, _) =>
            {
                bool colliding = collidingMovieTt.ContainsKey(s.Id);
                try
                {
                    if (!force && await imageRepo.HasImage(s.Id, PosterImageVariant.Main, PosterBucket.Series))
                    {
                        Interlocked.Increment(ref skipped);
                    }
                    else if (!colliding && await imageRepo.HasImage(s.Id, PosterImageVariant.Main))
                    {
                        // The id is the series' alone, so the existing "{id}.png" is genuinely the series'
                        // poster — carry it (both variants) into the bucket without a network round-trip.
                        await CopyPosterImagesAsync(s.Id, null, s.Id, PosterBucket.Series);
                        Interlocked.Increment(ref copied);
                    }
                    else if (await posterFetchService.EnsurePosterAsync(s.Id, s.imdbID, isSeries: true, force: true))
                    {
                        // Colliding (can't trust "{id}.png" — it may be the movie's), or no source file:
                        // fetch the series' own poster straight into the series bucket.
                        Interlocked.Increment(ref refetched);
                    }
                    else
                    {
                        Interlocked.Increment(ref failed);
                    }
                }
                catch { Interlocked.Increment(ref failed); }

                // Courtesy movie restore: ONLY when the colliding movie has no poster file at all (absent),
                // fetch the movie's own poster. force:false guarantees we never overwrite an existing file.
                if (colliding && !string.IsNullOrWhiteSpace(collidingMovieTt[s.Id]))
                {
                    try
                    {
                        if (!await imageRepo.HasImage(s.Id, PosterImageVariant.Main)
                            && await posterFetchService.EnsurePosterAsync(s.Id, collidingMovieTt[s.Id], isSeries: false, force: false))
                            Interlocked.Increment(ref movieRestored);
                    }
                    catch { /* movie restore is best-effort; never fails the chunk */ }
                }
            });

            var nextAfterId = batchIds.Max();
            var remaining = await movieDb.Series.CountAsync(s => s.Id > nextAfterId);
            return Ok(new { done = remaining == 0, processed = batch.Count, nextAfterId, copied, refetched, skipped, movieRestored, failed, remaining });
        }

        // Backfill missing poster THUMBNAILS for movies: legacy rows whose main "{id}.png" exists on disk
        // but the "{id}_s.png" thumbnail was never generated — so /ImageThumb 404s and the card shows no
        // thumbnail even though the modal's /Image works. EnsurePosterThumnailExists shrinks the existing
        // on-disk main poster (no network fetch); we also (re)compute the dominant color while we hold the
        // bytes, since these legacy rows typically lack it too. CHUNKED by movie-id cursor so it can't time
        // out — the caller drives it to completion. Editor-gated; runs in the web app (writes the live image
        // store; a dev box can't). Idempotent: a movie that already has a thumbnail (or no main poster) is
        // skipped, so re-running only fills the gaps.
        [HttpPost("/API/Admin/IngestReview/BackfillThumbnails")]
        public async Task<IActionResult> BackfillThumbnails(int afterId = 0, int limit = 200)
        {
            if (!await IsCurrentUserEditor()) return Forbid();
            limit = Math.Clamp(limit, 1, 1000);

            var batch = await movieDb.Movies.Where(m => m.id > afterId)
                .OrderBy(m => m.id).Take(limit)
                .Select(m => m.id).ToListAsync();

            if (batch.Count == 0)
                return Ok(new { done = true, processed = 0, nextAfterId = afterId, generated = 0, coloured = 0, failed = 0, remaining = 0 });

            int generated = 0, coloured = 0;
            var failedIds = new List<int>();
            foreach (var id in batch)
            {
                try
                {
                    if (!await imageRepo.HasImage(id, PosterImageVariant.Main)) continue;        // no poster at all
                    if (await imageRepo.HasImage(id, PosterImageVariant.Thumbnail)) continue;     // already has a thumb
                    await shrinkService.EnsurePosterThumnailExists(id);
                    generated++;

                    var pd = await movieDb.MoviePosterDetails.FindAsync(id);
                    if (pd != null && pd.DominantColor == null)
                    {
                        var thumb = await imageRepo.GetImage(id, PosterImageVariant.Thumbnail);
                        if (thumb != null) { pd.DominantColor = ComputeAverageColor(thumb); coloured++; }
                    }
                }
                catch (Exception ex)
                {
                    // Don't let one bad poster sink the batch, but make the skip visible — a silently
                    // swallowed failure here is exactly how a title ends up stuck with a main image and
                    // no thumb (a blank card). Log it and report the ids back to the caller.
                    failedIds.Add(id);
                    logger.LogWarning(ex, "BackfillThumbnails: thumbnail generation failed for movie {Id}", id);
                }
            }
            await movieDb.SaveChangesAsync();

            var nextAfterId = batch.Max();
            var remaining = await movieDb.Movies.CountAsync(m => m.id > nextAfterId);
            return Ok(new { done = remaining == 0, processed = batch.Count, nextAfterId, generated, coloured, failed = failedIds.Count, failedIds, remaining });
        }

        // Generate (or refresh) the thumbnail for a SINGLE title from its existing on-disk main poster.
        // Used by the movie/series edit modal's "Generate thumbnail" button, which appears when a title has
        // a full poster but no "{id}_s.png" thumbnail (card shows a broken placeholder). No network fetch —
        // it shrinks the on-disk main poster — and refreshes the dominant color. Editor-gated; prod-only
        // (a dev box's image repo can't write).
        [HttpPost("/API/GenerateThumbnail")]
        public async Task<IActionResult> GenerateThumbnail(int id, bool isSeries = false)
        {
            if (!await IsCurrentUserEditor()) return Forbid();
            var bucket = PosterBucket.ForTitle(isSeries);
            if (!await imageRepo.HasImage(id, PosterImageVariant.Main, bucket))
                return BadRequest(new { success = false, message = "This title has no poster to make a thumbnail from." });

            await shrinkService.EnsurePosterThumnailExists(id, force: true, bucket);

            var thumb = await imageRepo.GetImage(id, PosterImageVariant.Thumbnail, bucket);
            if (thumb != null)
            {
                if (isSeries)
                {
                    var pd = await movieDb.SeriesPosterDetails.FindAsync(id);
                    if (pd != null) { pd.DominantColor = ComputeAverageColor(thumb); await movieDb.SaveChangesAsync(); }
                }
                else
                {
                    var pd = await movieDb.MoviePosterDetails.FindAsync(id);
                    if (pd != null) { pd.DominantColor = ComputeAverageColor(thumb); await movieDb.SaveChangesAsync(); }
                }
            }
            return Ok(new { success = true });
        }

        // Reject = delete the ingested row entirely. Guarded to pending-review rows so this can never
        // remove an established library entry. A series takes its episodes (+ their Playables/files) and
        // satellite graph with it; a misc video takes its Playable + files.
        [HttpPost("/API/Admin/IngestReview/Reject")]
        public async Task<IActionResult> IngestReviewReject([FromBody] IngestReviewIdsRequest req)
        {
            if (!await IsCurrentUserEditor()) return Forbid();
            if (req == null) return Ok(new { rejected = 0 });

            var rows = req.Ids.Count == 0 ? new List<Movie>()
                : await movieDb.Movies.Where(m => req.Ids.Contains(m.id) && m.ReviewBatch != null).ToListAsync();

            // Related misc must be cleared off a parent before it can be deleted (MiscVideo->Movie/Series FKs
            // are NO_ACTION). An episodic-extra misc (attached, no standalone Description) has no card and no
            // independent existence — delete it (+ its Playable/files) with the parent. A related misc that DOES
            // carry a Description is substantive — DETACH it (it lives on as a standalone pending misc with its
            // own card) rather than destroy it.
            var rejMovieIds = rows.Select(m => m.id).ToList();
            var rejSeriesIds = req.SeriesIds.Count == 0 ? new List<int>()
                : await movieDb.Series.Where(s => req.SeriesIds.Contains(s.Id) && s.ReviewBatch != null).Select(s => s.Id).ToListAsync();
            if (rejMovieIds.Count > 0 || rejSeriesIds.Count > 0)
            {
                var related = await movieDb.MiscVideos.Where(v =>
                    (v.RelatedMovieId != null && rejMovieIds.Contains(v.RelatedMovieId.Value))
                 || (v.RelatedSeriesId != null && rejSeriesIds.Contains(v.RelatedSeriesId.Value))).ToListAsync();
                var extra = related.Where(v => string.IsNullOrEmpty(v.Description)).ToList();
                if (extra.Count > 0)
                {
                    var cpids = extra.Select(v => v.PlayableId).ToList();
                    movieDb.MediaFiles.RemoveRange(await movieDb.MediaFiles.Where(f => cpids.Contains(f.PlayableId)).ToListAsync());
                    movieDb.MiscVideos.RemoveRange(extra);
                    movieDb.Playables.RemoveRange(await movieDb.Playables.Where(p => cpids.Contains(p.Id)).ToListAsync());
                }
                foreach (var v in related.Where(v => !string.IsNullOrEmpty(v.Description)))
                    { v.RelatedMovieId = null; v.RelatedSeriesId = null; }
                await movieDb.SaveChangesAsync();   // release the NO_ACTION FK before deleting the parents
            }

            // Use the full subtree delete: a plain Movies.RemoveRange leaves the movie's Playable+files
            // orphaned and — fatally — trips the NO_ACTION MoviePosterDetails FK when the row got a poster
            // during enrichment (that's the "Reject Failed" on enriched rows). DeleteMovieSubtreeAsync drops
            // poster details + playable/files + credit/genre/plot, then the Movie.
            foreach (var m in rows)
                await DeleteMovieSubtreeAsync(m);

            int seriesCount = 0;
            if (req.SeriesIds.Count > 0)
            {
                var seriesRows = await movieDb.Series.Where(s => req.SeriesIds.Contains(s.Id) && s.ReviewBatch != null).ToListAsync();
                var sids = seriesRows.Select(s => s.Id).ToList();
                var eps = await movieDb.Episodes.Where(e => e.SeriesId != null && sids.Contains(e.SeriesId.Value)).ToListAsync();
                var epPids = eps.Where(e => e.PlayableId != null).Select(e => e.PlayableId!.Value).ToList();
                var epFiles = await movieDb.MediaFiles.Where(f => epPids.Contains(f.PlayableId)).ToListAsync();
                var epPlayables = await movieDb.Playables.Where(p => epPids.Contains(p.Id)).ToListAsync();
                movieDb.MediaFiles.RemoveRange(epFiles);     // episode files…
                movieDb.Episodes.RemoveRange(eps);           // …episodes (releases Episode→Playable Restrict)…
                movieDb.Playables.RemoveRange(epPlayables);  // …their Playables…
                movieDb.Series.RemoveRange(seriesRows);      // …the series (cascades its genre/credit/plot/poster).
                seriesCount = seriesRows.Count;
            }

            int miscCount = 0;
            if (req.MiscIds.Count > 0)
            {
                var miscRows = await movieDb.MiscVideos.Where(v => req.MiscIds.Contains(v.Id) && v.ReviewBatch != null).ToListAsync();
                var pids = miscRows.Select(v => v.PlayableId).ToList();
                var files = await movieDb.MediaFiles.Where(f => pids.Contains(f.PlayableId)).ToListAsync();
                var playables = await movieDb.Playables.Where(p => pids.Contains(p.Id)).ToListAsync();
                movieDb.MediaFiles.RemoveRange(files);
                movieDb.MiscVideos.RemoveRange(miscRows);
                movieDb.Playables.RemoveRange(playables);
                miscCount = miscRows.Count;
            }

            await movieDb.SaveChangesAsync();
            return Ok(new { rejected = rows.Count + seriesCount + miscCount });
        }

        public class IngestReviewUpdateRequest
        {
            public int id { get; set; }
            public string Kind { get; set; } = "movie";   // "movie" | "series"
            public string? Title { get; set; }
            public string? SimpleTitle { get; set; }
            public int? Year { get; set; }
            public string? imdbID { get; set; }
            public string? TitleType { get; set; }
            /// <summary>A poster URL to fetch + persist for this row (so the approved title carries it).</summary>
            public string? PosterLink { get; set; }
        }

        // Correct a pending row in place before approval — title / simple title / year / imdbID / type, and
        // the poster (a provided PosterLink that differs from what's stored is downloaded + saved by id).
        // These are the exact values that go live on Approve. The row stays pending. A corrected imdbID is
        // validated and must not collide. Returns the new posterVersion when a poster was fetched.
        [HttpPost("/API/Admin/IngestReview/Update")]
        public async Task<IActionResult> IngestReviewUpdate([FromBody] IngestReviewUpdateRequest req)
        {
            if (!await IsCurrentUserEditor()) return Forbid();
            if (req == null || req.id == 0) return BadRequest(new { Message = "id required" });

            if (string.Equals(req.Kind, "series", StringComparison.OrdinalIgnoreCase))
            {
                var s = await movieDb.Series.FirstOrDefaultAsync(x => x.Id == req.id && x.ReviewBatch != null);
                if (s == null) return NotFound(new { Message = "Not a pending-review series" });
                if (!string.IsNullOrWhiteSpace(req.Title)) s.Title = req.Title.Trim();
                if (req.SimpleTitle != null) s.SimpleTitle = req.SimpleTitle.Trim();
                if (req.Year != null && (s.ReleaseDate == null || s.ReleaseDate.Value.Year != req.Year.Value))
                {
                    s.ReleaseDate = new DateTime(req.Year.Value, 1, 1);
                    s.StartYear = req.Year.Value;
                }
                if (req.imdbID != null)
                {
                    var newId = req.imdbID.Trim();
                    if (newId.Length > 0 && !string.Equals(newId, s.imdbID, StringComparison.OrdinalIgnoreCase))
                    {
                        if (!IsValidImdbId(newId)) return BadRequest(new { Message = $"'{newId}' is not a valid IMDb id" });
                        if (await movieDb.Series.AnyAsync(x => x.Id != s.Id && x.imdbID == newId))
                            return Conflict(new { Message = $"Another series already has {newId}" });
                        s.imdbID = newId; s.ReviewProvenance = "manual"; s.ReviewConfidence = "HIGH";
                    }
                }
                if (!string.IsNullOrWhiteSpace(req.TitleType) && Enum.TryParse<TitleType>(req.TitleType, true, out var stt)
                    && (stt == TitleType.TvSeries || stt == TitleType.TvMiniSeries))
                    s.TitleType = stt;
                int? sVer = await ApplyReviewPosterAsync(s.Id, req.PosterLink, isSeries: true);
                if (sVer == -1) return BadRequest(new { Success = false, Message = "Poster download failed." });
                await movieDb.SaveChangesAsync();
                return Ok(new { Success = true, posterVersion = sVer });
            }

            var m = await movieDb.Movies.FirstOrDefaultAsync(x => x.id == req.id && x.ReviewBatch != null);
            if (m == null) return NotFound(new { Message = "Not a pending-review movie" });

            if (!string.IsNullOrWhiteSpace(req.Title)) m.Title = req.Title.Trim();
            if (req.SimpleTitle != null) m.SimpleTitle = req.SimpleTitle.Trim();
            if (req.Year != null && (m.ReleaseDate == null || m.ReleaseDate.Value.Year != req.Year.Value))
                m.ReleaseDate = new DateTime(req.Year.Value, 1, 1);

            if (req.imdbID != null)
            {
                var newId = req.imdbID.Trim();
                if (newId.Length > 0 && !string.Equals(newId, m.imdbID, StringComparison.OrdinalIgnoreCase))
                {
                    if (!IsValidImdbId(newId))
                        return BadRequest(new { Message = $"'{newId}' is not a valid IMDb id" });
                    if (await movieDb.Movies.AnyAsync(x => x.id != m.id && x.imdbID == newId))
                        return Conflict(new { Message = $"Another movie already has {newId}" });
                    m.imdbID = newId;
                    m.ReviewProvenance = "manual";
                    m.ReviewConfidence = "HIGH";
                }
            }

            if (!string.IsNullOrWhiteSpace(req.TitleType) && Enum.TryParse<TitleType>(req.TitleType, true, out var tt))
                m.TitleType = tt;

            int? ver = await ApplyReviewPosterAsync(m.id, req.PosterLink, isSeries: false);
            if (ver == -1) return BadRequest(new { Success = false, Message = "Poster download failed." });
            await movieDb.SaveChangesAsync();
            return Ok(new { Success = true, posterVersion = ver });
        }

        // Fetch + persist a poster for a review row when a new/changed link is supplied. Returns the new
        // PosterVersion, null when no fetch was needed, or -1 on download failure (caller surfaces it).
        private async Task<int?> ApplyReviewPosterAsync(int id, string? posterLink, bool isSeries)
        {
            if (string.IsNullOrWhiteSpace(posterLink)) return null;
            var link = posterLink.Trim();
            var existing = isSeries
                ? await movieDb.SeriesPosterDetails.Where(p => p.SeriesId == id).Select(p => p.PosterLink).FirstOrDefaultAsync()
                : await movieDb.MoviePosterDetails.Where(p => p.MovieId == id).Select(p => p.PosterLink).FirstOrDefaultAsync();
            if (string.Equals(existing, link, StringComparison.OrdinalIgnoreCase)) return null;  // already have this exact poster
            try { return await DownloadAndSavePosterByIdAsync(id, link, isSeries); }
            catch { return -1; }   // bad URL / unreachable — caller surfaces a friendly message
        }

        public class IngestReviewReclassifyRequest
        {
            public int id { get; set; }
            // Both are "movie" | "series" | "misc". A bare id is ambiguous (separate id sequences),
            // so the caller states where the row lives now and where it should go.
            public string FromKind { get; set; } = "movie";
            public string ToKind { get; set; } = "misc";
            public string? Category { get; set; }
            public string? CollectionName { get; set; }
            public int? RelatedMovieId { get; set; }
            public int? RelatedSeriesId { get; set; }
        }

        // Reclassify a pending-review row among movie / series / misc. Movies and series each have their
        // own table now, so every direction (bar the no-op) is a real cross-table move: the title's own
        // metadata — and, for movie↔series, its genre / credit / plot / poster graph — is carried to the
        // destination table, the poster image is copied by id (PosterLink also carries, for re-download),
        // and structural children that don't fit the new shape are dropped cleanly. Dropping touches only
        // DB rows (mappings/episodes), never the files on disk; the reviewer re-scrapes / re-maps for the
        // corrected kind. The row stays review-pending so it can be Approved afterward. Pending-only.
        [HttpPost("/API/Admin/IngestReview/Reclassify")]
        public async Task<IActionResult> IngestReviewReclassify([FromBody] IngestReviewReclassifyRequest req)
        {
            if (!await IsCurrentUserEditor()) return Forbid();
            if (req == null || req.id == 0) return BadRequest(new { Message = "id required" });
            var from = (req.FromKind ?? "").Trim().ToLowerInvariant();
            var to = (req.ToKind ?? "").Trim().ToLowerInvariant();
            if (from == to) return Ok(new { Success = true, kind = to });

            string? cat = string.IsNullOrWhiteSpace(req.Category) ? null : req.Category.Trim();
            string? coll = string.IsNullOrWhiteSpace(req.CollectionName) ? null : req.CollectionName.Trim();

            // (A) Movie -> Series : carry metadata + the genre/credit/plot/poster graph to the Series
            // table. The movie's own Playable/files are dropped — a series streams through its episodes,
            // which a re-scrape will create and map (no episodes exist yet).
            if (from == "movie" && to == "series")
            {
                var m = await movieDb.Movies.FirstOrDefaultAsync(x => x.id == req.id && x.ReviewBatch != null);
                if (m == null) return NotFound(new { Message = "Not a pending-review movie" });

                var s = new Series();
                CopyTitleScalars(m, s);
                if (s.TitleType != TitleType.TvSeries && s.TitleType != TitleType.TvMiniSeries) s.TitleType = TitleType.TvSeries;
                s.ReviewProvenance = "reclassified in review";
                movieDb.Series.Add(s);
                await movieDb.SaveChangesAsync();   // assigns s.Id

                movieDb.SeriesGenres.AddRange((await movieDb.MovieGenres.Where(g => g.MovieID == m.id).ToListAsync())
                    .Select(g => new SeriesGenre { SeriesId = s.Id, GenreId = g.GenreId, Ordering = g.Ordering }));
                movieDb.SeriesCredits.AddRange((await movieDb.MovieCredits.Where(c => c.MovieID == m.id).ToListAsync())
                    .Select(c => new SeriesCredit { SeriesId = s.Id, PersonId = c.PersonId, Role = c.Role, Ordering = c.Ordering, Character = c.Character }));
                movieDb.SeriesPlotSummaries.AddRange((await movieDb.MoviePlotSummaries.Where(p => p.MovieID == m.id).ToListAsync())
                    .Select(p => new SeriesPlotSummary { SeriesId = s.Id, Ordering = p.Ordering, Author = p.Author, Text = p.Text }));
                var pd = await movieDb.MoviePosterDetails.FirstOrDefaultAsync(x => x.MovieId == m.id);
                if (pd != null) movieDb.SeriesPosterDetails.Add(new SeriesPosterDetails { SeriesId = s.Id, PosterLink = pd.PosterLink, PosterVersion = pd.PosterVersion, DominantColor = pd.DominantColor });

                await CopyPosterImagesAsync(m.id, null, s.Id, PosterBucket.Series);
                await DeleteMovieSubtreeAsync(m);
                await movieDb.SaveChangesAsync();
                return Ok(new { Success = true, kind = "series", id = s.Id });
            }

            // (A') Series -> Movie : the reverse — metadata + graph back into Movie (with a fresh
            // Playable); the series' episodes + their Playables/files are dropped (a movie has none).
            if (from == "series" && to == "movie")
            {
                var s = await movieDb.Series.FirstOrDefaultAsync(x => x.Id == req.id && x.ReviewBatch != null);
                if (s == null) return NotFound(new { Message = "Not a pending-review series" });

                var m = new Movie { Playable = new Playable { Kind = PlayableKind.Movie } };
                CopyTitleScalars(s, m);
                if (m.TitleType == TitleType.TvSeries || m.TitleType == TitleType.TvMiniSeries) m.TitleType = TitleType.Movie;
                m.ReviewProvenance = "reclassified in review";
                movieDb.Movies.Add(m);
                await movieDb.SaveChangesAsync();   // assigns m.id

                movieDb.MovieGenres.AddRange((await movieDb.SeriesGenres.Where(g => g.SeriesId == s.Id).ToListAsync())
                    .Select(g => new MovieGenre { MovieID = m.id, GenreId = g.GenreId, Ordering = g.Ordering }));
                movieDb.MovieCredits.AddRange((await movieDb.SeriesCredits.Where(c => c.SeriesId == s.Id).ToListAsync())
                    .Select(c => new MovieCredit { MovieID = m.id, PersonId = c.PersonId, Role = c.Role, Ordering = c.Ordering, Character = c.Character }));
                movieDb.MoviePlotSummaries.AddRange((await movieDb.SeriesPlotSummaries.Where(p => p.SeriesId == s.Id).ToListAsync())
                    .Select(p => new MoviePlotSummary { MovieID = m.id, Ordering = p.Ordering, Author = p.Author, Text = p.Text }));
                var pd = await movieDb.SeriesPosterDetails.FirstOrDefaultAsync(x => x.SeriesId == s.Id);
                if (pd != null) movieDb.MoviePosterDetails.Add(new MoviePosterDetails { MovieId = m.id, PosterLink = pd.PosterLink, PosterVersion = pd.PosterVersion, DominantColor = pd.DominantColor });

                await CopyPosterImagesAsync(s.Id, PosterBucket.Series, m.id, null);
                await DeleteSeriesSubtreeAsync(s);
                await movieDb.SaveChangesAsync();
                return Ok(new { Success = true, kind = "movie", id = m.id });
            }

            // (A'') Series -> MiscVideo : keep the title as a misc collection (fresh Playable); drop the
            // series' episodes + their Playables/files.
            if (from == "series" && to == "misc")
            {
                var s = await movieDb.Series.FirstOrDefaultAsync(x => x.Id == req.id && x.ReviewBatch != null);
                if (s == null) return NotFound(new { Message = "Not a pending-review series" });

                var p = new Playable { Kind = PlayableKind.MiscVideo };
                movieDb.Playables.Add(p);
                await movieDb.SaveChangesAsync();
                movieDb.MiscVideos.Add(new MiscVideo
                {
                    PlayableId = p.Id,
                    Title = s.Title ?? "(untitled)",
                    SimpleTitle = s.SimpleTitle,
                    Year = s.ReleaseDate?.Year ?? s.StartYear,
                    Category = cat,
                    CollectionName = coll,
                    RelatedMovieId = req.RelatedMovieId,
                    RelatedSeriesId = req.RelatedSeriesId,
                    ReviewBatch = s.ReviewBatch,
                    ReviewProvenance = "reclassified in review",
                    ReviewSourcePath = s.ReviewSourcePath,
                });
                await DeleteSeriesSubtreeAsync(s);
                await movieDb.SaveChangesAsync();
                return Ok(new { Success = true, kind = "misc" });
            }

            // (A''') MiscVideo -> Series : create the Series shell (reviewer fills tt + re-scrapes
            // episodes); drop the misc's Playable + files.
            if (from == "misc" && to == "series")
            {
                var mv = await movieDb.MiscVideos.FirstOrDefaultAsync(v => v.Id == req.id && v.ReviewBatch != null);
                if (mv == null) return NotFound(new { Message = "Not a pending-review misc video" });

                var s = new Series
                {
                    Title = mv.Title,
                    SimpleTitle = string.IsNullOrEmpty(mv.SimpleTitle) ? mv.Title : mv.SimpleTitle,
                    ReleaseDate = mv.Year != null ? new DateTime(mv.Year.Value, 1, 1) : null,
                    StartYear = mv.Year,
                    TitleType = TitleType.TvSeries,
                    ReviewBatch = mv.ReviewBatch,
                    ReviewProvenance = "reclassified in review",
                    ReviewConfidence = "NONE",
                    ReviewSourcePath = mv.ReviewSourcePath,
                };
                movieDb.Series.Add(s);
                await DeleteMiscSubtreeAsync(mv);
                await movieDb.SaveChangesAsync();
                return Ok(new { Success = true, kind = "series", id = s.Id });
            }

            // (B) Movie -> MiscVideo  (cross-table move; Playable + files come along)
            if (from == "movie" && to == "misc")
            {
                var m = await movieDb.Movies.FirstOrDefaultAsync(x => x.id == req.id && x.ReviewBatch != null);
                if (m == null) return NotFound(new { Message = "Not a pending-review movie" });

                int playableId;
                if (m.PlayableId != null) playableId = m.PlayableId.Value;
                else
                {
                    var p = new Playable { Kind = PlayableKind.MiscVideo };
                    movieDb.Playables.Add(p);
                    await movieDb.SaveChangesAsync();
                    playableId = p.Id;
                }

                movieDb.MiscVideos.Add(new MiscVideo
                {
                    PlayableId = playableId,
                    Title = m.Title ?? "(untitled)",
                    SimpleTitle = m.SimpleTitle,
                    Year = m.ReleaseDate?.Year ?? m.ImdbReleaseDate?.Year,
                    Category = cat,
                    CollectionName = coll,
                    RelatedMovieId = req.RelatedMovieId,
                    RelatedSeriesId = req.RelatedSeriesId,
                    ReviewBatch = m.ReviewBatch,
                    ReviewProvenance = "reclassified in review",
                    ReviewSourcePath = m.ReviewSourcePath,
                });

                var pl = await movieDb.Playables.FirstOrDefaultAsync(p => p.Id == playableId);
                if (pl != null) pl.Kind = PlayableKind.MiscVideo;

                // Drop the (often wrong-tt) credit/genre/plot graph explicitly — the live FKs can't be
                // assumed to cascade — then the Movie row. Files stay on the Playable.
                movieDb.MovieCredits.RemoveRange(await movieDb.MovieCredits.Where(c => c.MovieID == m.id).ToListAsync());
                movieDb.MovieGenres.RemoveRange(await movieDb.MovieGenres.Where(g => g.MovieID == m.id).ToListAsync());
                movieDb.MoviePlotSummaries.RemoveRange(await movieDb.MoviePlotSummaries.Where(s => s.MovieID == m.id).ToListAsync());
                m.PlayableId = null;
                await ClearSyncCandidateRefsAsync(m);   // NO ACTION FKs — see the helper
                movieDb.Movies.Remove(m);
                await movieDb.SaveChangesAsync();
                return Ok(new { Success = true, kind = "misc" });
            }

            // (C) MiscVideo -> Movie  (cross-table move back; reviewer adds the tt via Update)
            if (from == "misc" && to == "movie")
            {
                var mv = await movieDb.MiscVideos.FirstOrDefaultAsync(v => v.Id == req.id && v.ReviewBatch != null);
                if (mv == null) return NotFound(new { Message = "Not a pending-review misc video" });

                var movie = new Movie
                {
                    Title = mv.Title,
                    SimpleTitle = string.IsNullOrEmpty(mv.SimpleTitle) ? mv.Title : mv.SimpleTitle,
                    ReleaseDate = mv.Year != null ? new DateTime(mv.Year.Value, 1, 1) : null,
                    TitleType = TitleType.Movie,
                    PlayableId = mv.PlayableId,
                    ReviewBatch = mv.ReviewBatch,
                    ReviewProvenance = "reclassified in review",
                    ReviewConfidence = "NONE",
                    ReviewSourcePath = mv.ReviewSourcePath,
                };
                movieDb.Movies.Add(movie);

                var pl = await movieDb.Playables.FirstOrDefaultAsync(p => p.Id == mv.PlayableId);
                if (pl != null) pl.Kind = PlayableKind.Movie;

                movieDb.MiscVideos.Remove(mv);
                await movieDb.SaveChangesAsync();
                return Ok(new { Success = true, kind = "movie", id = movie.id });
            }

            return BadRequest(new { Message = $"Unsupported reclassify {req.FromKind} -> {req.ToKind}" });
        }

        // Copy every shared scalar column (string / value-type) from one title entity to another
        // (Movie ⇄ Series). Skips keys, the NotMapped PosterLink passthrough, and any nav/collection —
        // so it auto-carries new metadata columns as the schema grows ("no data left behind").
        private static readonly HashSet<string> TitleScalarSkip = new(StringComparer.Ordinal) { "id", "Id", "PosterLink" };
        private static void CopyTitleScalars(object src, object dst)
        {
            var srcType = src.GetType();
            foreach (var dp in dst.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (!dp.CanWrite || TitleScalarSkip.Contains(dp.Name)) continue;
                if (!(dp.PropertyType == typeof(string) || dp.PropertyType.IsValueType)) continue;  // scalars only — skips navs/collections
                var sp = srcType.GetProperty(dp.Name, BindingFlags.Public | BindingFlags.Instance);
                if (sp == null || !sp.CanRead || sp.PropertyType != dp.PropertyType) continue;
                dp.SetValue(dst, sp.GetValue(src));
            }
        }

        // Carry the poster image to a new id on a cross-table move (posters are on-disk files keyed by id,
        // served with no DB lookup). Best-effort: a missing source or write failure is fine — the copied
        // PosterLink lets enrichment re-download for the new id.
        private async Task CopyPosterImagesAsync(int fromId, string? fromBucket, int toId, string? toBucket)
        {
            if (fromId == toId && string.Equals(fromBucket, toBucket, StringComparison.Ordinal)) return;
            foreach (var variant in new[] { PosterImageVariant.Main, PosterImageVariant.Thumbnail })
            {
                try
                {
                    var bytes = await imageRepo.GetImage(fromId, variant, fromBucket);
                    if (bytes != null && bytes.Length > 0) await imageRepo.SaveImage(toId, variant, bytes, toBucket);
                }
                catch { /* best-effort */ }
            }
        }

        // Delete a movie and everything that hangs off it (Playable + files, credit/genre/plot/poster) —
        // the live FKs can't be assumed to cascade, so each is removed explicitly. Used when its metadata
        // has already been carried elsewhere (movie → series).
        private async Task DeleteMovieSubtreeAsync(Movie m)
        {
            if (m.PlayableId != null)
            {
                var pid = m.PlayableId.Value;
                movieDb.MediaFiles.RemoveRange(await movieDb.MediaFiles.Where(f => f.PlayableId == pid).ToListAsync());
                var pl = await movieDb.Playables.FirstOrDefaultAsync(p => p.Id == pid);
                m.PlayableId = null;
                if (pl != null) movieDb.Playables.Remove(pl);
            }
            movieDb.MovieCredits.RemoveRange(await movieDb.MovieCredits.Where(c => c.MovieID == m.id).ToListAsync());
            movieDb.MovieGenres.RemoveRange(await movieDb.MovieGenres.Where(g => g.MovieID == m.id).ToListAsync());
            movieDb.MoviePlotSummaries.RemoveRange(await movieDb.MoviePlotSummaries.Where(s => s.MovieID == m.id).ToListAsync());
            var pd = await movieDb.MoviePosterDetails.FirstOrDefaultAsync(x => x.MovieId == m.id);
            if (pd != null) movieDb.MoviePosterDetails.Remove(pd);
            await ClearSyncCandidateRefsAsync(m);
            movieDb.Movies.Remove(m);
        }

        /// <summary>
        /// Clears <see cref="SyncCandidate"/> references before a Movie row is removed — the FKs are
        /// NO ACTION in the DB (SQL Server refuses two SET NULL paths into Movie from one table), so
        /// EVERY path that deletes a movie must call this or the delete throws. A candidate whose
        /// CREATED movie is going away reverts to Pending with the reason visible AND its pinned tt
        /// cleared — the pin produced a movie the reviewer just rejected, and keeping it would make a
        /// re-resolve deterministically recreate the same wrong row. A candidate that merely TARGETED
        /// the movie loses its pairing and drops to Unclassified.
        /// </summary>
        private async Task ClearSyncCandidateRefsAsync(Movie m)
        {
            foreach (var c in await movieDb.SyncCandidates
                .Where(c => c.TargetMovieId == m.id || c.CreatedMovieId == m.id).ToListAsync())
            {
                if (c.CreatedMovieId == m.id && c.Status == SyncCandidateStatus.Ingested)
                {
                    c.Status = SyncCandidateStatus.Pending;
                    c.ResolvedImdbId = null;
                    c.ResolutionError = TruncCol($"The resolved movie '{m.Title}' was rejected/deleted — re-resolve or dismiss.", 512);
                    c.ResolvedUtc = null;
                }
                if (c.TargetMovieId == m.id)
                {
                    var wasUpgrade = c.Kind == SyncCandidateKind.Upgrade;
                    c.TargetMovieId = null; c.Signal = null; c.OldPath = null;
                    if (c.Status == SyncCandidateStatus.Pending)
                    {
                        // Only an UPGRADE loses its identity with its target — the pairing was the whole
                        // row. A NewTitle merely carried the movie as an advisory ("that tt is taken, so
                        // this isn't an upgrade — attach it as an alt version instead"); deleting the owner
                        // makes the tt free, so the row keeps its kind and its error is cleared so the next
                        // resolve retries what will now succeed.
                        if (wasUpgrade) c.Kind = SyncCandidateKind.Unclassified;
                        else c.ResolutionError = null;
                    }
                }
                if (c.CreatedMovieId == m.id) c.CreatedMovieId = null;
            }
        }

        /// <summary>Bound a string to a column's MaxLength — the write that records a failure must
        /// never itself fail on 'string or binary data would be truncated'.</summary>
        private static string? TruncCol(string? s, int max) => s != null && s.Length > max ? s.Substring(0, max) : s;

        // Delete a series subtree: episodes + their Playables/files, then the Series row (which cascades
        // its genre/credit/plot/poster). Mirrors the Reject path. Used when the title moves to movie/misc.
        private async Task DeleteSeriesSubtreeAsync(Series s)
        {
            var eps = await movieDb.Episodes.Where(e => e.SeriesId == s.Id).ToListAsync();
            var epPids = eps.Where(e => e.PlayableId != null).Select(e => e.PlayableId!.Value).ToList();
            movieDb.MediaFiles.RemoveRange(await movieDb.MediaFiles.Where(f => epPids.Contains(f.PlayableId)).ToListAsync());
            movieDb.Episodes.RemoveRange(eps);
            movieDb.Playables.RemoveRange(await movieDb.Playables.Where(p => epPids.Contains(p.Id)).ToListAsync());
            await ClearSyncCandidateSeriesRefsAsync(s);
            movieDb.Series.Remove(s);
        }

        /// <summary>
        /// The <see cref="ClearSyncCandidateRefsAsync"/> counterpart for series: <c>TargetSeriesId</c> is
        /// NO ACTION in the DB, so every path that removes a Series must clear it or the delete throws.
        /// Episode candidates that were mapped into the show being deleted come BACK as Pending — their
        /// files exist and are untracked again the moment the episodes go away, and silently losing them
        /// would make a rejected series erase the evidence that its files are on disk. The pinned tt is
        /// cleared too: it produced a series the reviewer just rejected, so a re-resolve must not
        /// deterministically recreate it.
        /// </summary>
        private async Task ClearSyncCandidateSeriesRefsAsync(Series s)
        {
            foreach (var c in await movieDb.SyncCandidates.Where(c => c.TargetSeriesId == s.Id).ToListAsync())
            {
                c.TargetSeriesId = null;
                c.ResolvedImdbId = null;
                // The episode list went with the series, so the next resolve is free to build a new
                // one — the ownership marker must not outlive the list it described.
                c.SeriesListOwned = false;
                c.ResolutionError = TruncCol($"The resolved series '{s.Title}' was rejected/deleted — re-resolve or dismiss.", 512);
                if (c.Status == SyncCandidateStatus.Ingested)
                {
                    c.Status = SyncCandidateStatus.Pending;
                    c.ResolvedUtc = null;
                    c.ResolvedBy = null;
                }
            }
        }

        // Delete a misc video + its Playable/files. Used when the title moves to series.
        private async Task DeleteMiscSubtreeAsync(MiscVideo mv)
        {
            movieDb.MediaFiles.RemoveRange(await movieDb.MediaFiles.Where(f => f.PlayableId == mv.PlayableId).ToListAsync());
            var pl = await movieDb.Playables.FirstOrDefaultAsync(p => p.Id == mv.PlayableId);
            movieDb.MiscVideos.Remove(mv);
            if (pl != null) movieDb.Playables.Remove(pl);
        }
    }
}
