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
        // Series detail (mirror of GetMovie): the series + its normalized graph + seasons/episodes.
        [HttpGet("/API/GetSeries")]
        public async Task<IActionResult> GetSeries(int id)
        {
            int ageRestriction = await GetAgeRestrictionAsync();
            var series = await movieDb.Series.AsNoTracking().Include(s => s.PosterDetails).SingleOrDefaultAsync(s => s.Id == id);
            if (series == null) return BadRequest(new { Success = false, Message = "Series ID not found" });
            if (Web.RatingGate.EffectiveMpaRatingId(movieDb, series.MpaaRating, series.Rating, series.MpaaRatingInferred) > ageRestriction)
                return BadRequest(new { Success = false, Message = "Series ID not found" });
            var normalized = await GetNormalizedSeriesData(id, series);
            return Ok(new { Success = true, data = series, normalized });
        }

        public class SeriesUpdateDto
        {
            public int id { get; set; }
            public string? Title { get; set; }
            public string? SimpleTitle { get; set; }
            public string? Rating { get; set; }
            public DateTime? ReleaseDate { get; set; }
            public string? Runtime { get; set; }
            public string? Genre { get; set; }
            public string? Director { get; set; }
            public string? Writer { get; set; }
            public string? Actors { get; set; }
            public string? Plot { get; set; }
            public string? PosterLink { get; set; }
            public decimal? imdbRating { get; set; }
            public string? imdbID { get; set; }
            public int? RtTomatometer { get; set; }
            public int? RtPopcornmeter { get; set; }
            public bool RemoveFromRandom { get; set; }
        }

        // Edit a series in place (the modal's Edit form for series — the peer of UpdateMovie). Editor-gated;
        // a changed imdbID is conflict-checked, and a changed poster link is fetched. The richer normalized
        // graph (cast/genre FK rows) comes from a re-scrape/Re-fetch, not this scalar edit.
        [HttpPost("/API/UpdateSeries")]
        public async Task<IActionResult> UpdateSeries([FromBody] SeriesUpdateDto dto)
        {
            if (!await IsCurrentUserEditor()) return Forbid();
            if (dto == null || dto.id == 0) return BadRequest(new { Message = "Series ID is required", Success = false });

            var s = await movieDb.Series.Include(x => x.PosterDetails).SingleOrDefaultAsync(x => x.Id == dto.id);
            if (s == null) return NotFound(new { Message = "Series not found", Success = false });

            var newImdb = dto.imdbID?.Trim();
            if (!string.IsNullOrEmpty(newImdb) && !string.Equals(s.imdbID, newImdb, StringComparison.Ordinal))
            {
                if (!IsValidImdbId(newImdb)) return BadRequest(new { Message = $"'{newImdb}' is not a valid IMDb id", Success = false });
                if (await movieDb.Series.AnyAsync(x => x.imdbID == newImdb && x.Id != dto.id))
                    return Conflict(new { Message = $"Another series already has imdbID: {newImdb}", Success = false });
            }

            var posterLink = dto.PosterLink?.Trim();
            var posterChanged = !string.IsNullOrEmpty(posterLink) && !string.Equals(s.PosterDetails?.PosterLink, posterLink, StringComparison.Ordinal);

            s.Title = dto.Title?.Trim();
            s.SimpleTitle = dto.SimpleTitle?.Trim();
            s.Rating = dto.Rating?.Trim();
            s.ReleaseDate = dto.ReleaseDate;
            s.Runtime = dto.Runtime?.Trim();
            s.Genre = dto.Genre?.Trim();
            s.Director = dto.Director?.Trim();
            s.Writer = dto.Writer?.Trim();
            s.Actors = dto.Actors?.Trim();
            s.Plot = dto.Plot?.Trim();
            s.imdbRating = dto.imdbRating;
            s.imdbID = newImdb;
            s.RtTomatometer = dto.RtTomatometer;
            s.RtPopcornmeter = dto.RtPopcornmeter;
            s.RemoveFromRandom = dto.RemoveFromRandom;

            try { await movieDb.SaveChangesAsync(); }
            catch (Exception ex) { return Conflict(new { Message = $"Save failed: {ex.InnerException?.Message ?? ex.Message}", Success = false }); }

            if (posterChanged)
            {
                try { await DownloadAndSavePosterByIdAsync(s.Id, posterLink!, isSeries: true); } catch { /* poster best-effort */ }
            }

            var fresh = await movieDb.Series.Include(x => x.PosterDetails).SingleOrDefaultAsync(x => x.Id == dto.id);
            var normalized = await GetNormalizedSeriesData(dto.id, fresh!);
            return Ok(new { Success = true, data = fresh, normalized });
        }

        // Re-fetch IMDb data for a single title (movie or series) on demand — the modal's "Re-fetch from IMDb"
        // button. Re-resolves rating / certificate / year / plot / poster from the stored tt (OMDB → IMDb API,
        // never TMDB) and overwrites. Editor-gated. After a tt correction, this repopulates the metadata.
        [HttpPost("/API/RefetchTitle")]
        public async Task<IActionResult> RefetchTitle(int id, string kind = "movie")
        {
            if (!await IsCurrentUserEditor()) return Forbid();
            if (id == 0) return BadRequest(new { Success = false, Message = "id required" });
            bool isSeries = string.Equals(kind, "series", StringComparison.OrdinalIgnoreCase);
            var ok = await titleEnrichService.EnrichAsync(id, isSeries, force: true);
            if (!ok) return BadRequest(new { Success = false, Message = "Couldn't fetch IMDb data for this title (check the IMDb id)." });
            return Ok(new { Success = true });
        }

        private static string FileBaseName(string p) => (p ?? "").Replace('\\', '/').TrimEnd('/').Split('/')[^1];

        // Related misc videos (workprints, featurettes, shorts, specials) attached to a title via
        // RelatedMovieId/RelatedSeriesId — surfaced in the public modal's "Extras & Specials" section with
        // enough per-file info to play each. Pass the one relevant id; the other stays null.
        private async Task<List<object>> LoadModalMiscAsync(int? relatedMovieId, int? relatedSeriesId)
        {
            var rel = await movieDb.MiscVideos
                .Where(v => (relatedMovieId != null && v.RelatedMovieId == relatedMovieId)
                         || (relatedSeriesId != null && v.RelatedSeriesId == relatedSeriesId))
                .OrderBy(v => v.CollectionName).ThenBy(v => v.SortOrder).ThenBy(v => v.Title)
                .Select(v => new { v.Id, v.PlayableId, v.Title, v.Category, v.Year, v.CollectionName })
                .ToListAsync();
            if (rel.Count == 0) return new List<object>();
            var pids = rel.Select(v => v.PlayableId).ToList();
            var filesByPid = (await movieDb.MediaFiles.Where(f => pids.Contains(f.PlayableId))
                    .OrderBy(f => f.Role).ThenBy(f => f.PartNumber).ThenBy(f => f.Id)
                    .Select(f => new { f.Id, f.PlayableId, f.Path, Streamable = f.JellyfinItemId != null && f.MissingSinceUtc == null }).ToListAsync())
                .GroupBy(f => f.PlayableId)
                .ToDictionary(g => g.Key, g => g.Select(f => (object)new { mediaFileId = f.Id, name = FileBaseName(f.Path), isPlayable = f.Streamable }).ToList());
            return rel.Select(v => (object)new
            {
                title = v.Title, category = v.Category, year = v.Year, collectionName = v.CollectionName,
                files = filesByPid.TryGetValue(v.PlayableId, out var ff) ? ff : new List<object>(),
            }).ToList();
        }

        private async Task<object> GetNormalizedSeriesData(int id, Series series)
        {
            var genres = await movieDb.SeriesGenres.Where(g => g.SeriesId == id).OrderBy(g => g.Ordering).Select(g => g.Genre.Name).ToListAsync();
            var credits = await movieDb.SeriesCredits.Where(c => c.SeriesId == id).OrderBy(c => c.Ordering)
                .Select(c => new { c.Role, Nm = c.Person.ImdbNameId, Name = c.Person.DisplayName, c.Character }).ToListAsync();
            var summaries = await movieDb.SeriesPlotSummaries.Where(s => s.SeriesId == id).OrderBy(s => s.Ordering).Select(s => new { s.Author, s.Text }).ToListAsync();
            object People(CreditRole role) => credits.Where(c => c.Role == role).Select(c => new { nm = c.Nm, name = c.Name, character = c.Character }).ToList();

            var eps = await movieDb.Episodes.Where(e => e.SeriesId == id)
                .OrderBy(e => e.SeasonNumber).ThenBy(e => e.EpisodeNumber)
                .Select(e => new { e.SeasonNumber, e.EpisodeNumber, e.Title, e.ImdbId, e.RuntimeMinutes, e.PlayableId }).ToListAsync();
            var epPids = eps.Where(e => e.PlayableId != null).Select(e => e.PlayableId!.Value).ToList();
            // hasFile = mapping coverage (any MediaFile); isPlayable = Jellyfin-ready right now
            // (item synced + not gone missing) — the play button needs the stricter flag.
            var fileRows = await movieDb.MediaFiles.Where(f => epPids.Contains(f.PlayableId))
                .OrderBy(f => f.Role).ThenBy(f => f.PartNumber).ThenBy(f => f.Id)
                .Select(f => new { f.Id, f.PlayableId, f.Path, f.Role, f.Label, f.PartNumber, Streamable = f.JellyfinItemId != null && f.MissingSinceUtc == null }).ToListAsync();
            var withFile = fileRows.Select(f => f.PlayableId).Distinct().ToHashSet();
            var streamable = fileRows.Where(f => f.Streamable).Select(f => f.PlayableId).Distinct().ToHashSet();
            // Per-episode file list so the modal can surface multi-file episodes (segment Parts / Variants /
            // Extras), not just a single play button. Windows paths on a Linux host: split on both separators.
            static string BaseName(string p) => (p ?? "").Replace('\\', '/').TrimEnd('/').Split('/')[^1];
            var filesByPlayable = fileRows.GroupBy(f => f.PlayableId).ToDictionary(g => g.Key, g => g.Select(f => (object)new
            {
                mediaFileId = f.Id, role = f.Role.ToString(), label = f.Label, partNumber = f.PartNumber,
                isPlayable = f.Streamable, name = BaseName(f.Path),
            }).ToList());
            var noFiles = new List<object>();
            // The (S0,E0,"Extras") pseudo-episode is a holder for series/season-level extras, not a real
            // episode — pull it out of the season list and surface its files in the "Extras & Specials" section.
            var extrasHolder = eps.FirstOrDefault(e => e.SeasonNumber == 0 && e.EpisodeNumber == 0 && e.Title == "Extras");
            var seasonEps = extrasHolder == null ? eps : eps.Where(e => e != extrasHolder).ToList();
            var seriesExtras = (extrasHolder?.PlayableId != null && filesByPlayable.TryGetValue(extrasHolder.PlayableId.Value, out var xfl)) ? xfl : noFiles;
            var relatedMisc = await LoadModalMiscAsync(null, id);
            var seasons = seasonEps.GroupBy(e => e.SeasonNumber).OrderBy(g => g.Key).Select(g => new
            {
                season = g.Key,
                episodes = g.Select(e => new
                {
                    episode = e.EpisodeNumber,
                    title = e.Title,
                    imdbId = e.ImdbId,
                    runtimeMinutes = e.RuntimeMinutes,
                    playableId = e.PlayableId,
                    hasFile = e.PlayableId != null && withFile.Contains(e.PlayableId.Value),
                    isPlayable = e.PlayableId != null && streamable.Contains(e.PlayableId.Value),
                    files = (e.PlayableId != null && filesByPlayable.TryGetValue(e.PlayableId.Value, out var efl)) ? efl : noFiles,
                }).ToList(),
            }).ToList();

            return new
            {
                verified = series.ImdbVerifiedDate != null,
                needsReview = series.ImdbNeedsReview,
                titleType = series.TitleType.ToString(),
                runtimeMinutes = series.RuntimeMinutes,
                plotFull = series.PlotFull,
                plotSynopsis = series.PlotSynopsis,
                mpaaRating = series.MpaaRating,
                imdbReleaseDate = series.ImdbReleaseDate,
                imdbRating = series.ImdbRatingScraped,
                genres,
                cast = People(CreditRole.Actor),
                directors = People(CreditRole.Director),
                writers = People(CreditRole.Writer),
                summaries,
                insight = await LoadInsightAsync(InsightSubjectKind.Series, id),
                isSeries = true,
                seasons,
                seriesExtras,
                relatedMisc,
                seasonCount = series.SeasonCount,
                episodeCount = series.EpisodeCount,
                network = series.Network,
                startYear = series.StartYear,
                endYear = series.EndYear,
            };
        }

        [HttpPost("/API/InsertMovie")]
        public async Task<IActionResult> InsertMovie([FromBody] Movie movie)
        {
            if (!await IsCurrentUserEditor()) return Forbid();
            var checkMovie = await movieDb.Movies.AnyAsync(d => d.imdbID == movie.imdbID);

            if (checkMovie)
            {
                return Conflict(new { Message = $"Movie already Exists: {movie.Title}", Success = false });
            }

            movie.Title = movie.Title?.Trim();
            movie.SimpleTitle = movie.SimpleTitle?.Trim();
            movie.Rating = movie.Rating?.Trim();
            movie.Runtime = movie.Runtime?.Trim();
            movie.Genre = movie.Genre?.Trim();
            movie.Director = movie.Director?.Trim();
            movie.Writer = movie.Writer?.Trim();
            movie.Actors = movie.Actors?.Trim();
            movie.Plot = movie.Plot?.Trim();
            movie.PosterLink = movie.PosterLink?.Trim();
            movie.imdbID = movie.imdbID?.Trim();
            movie.UploadedDate = DateTime.Now;
            // Every movie gets a Playable (Phase-4 cutover) so files / progress / channel slots attach to it.
            movie.Playable = new Playable { Kind = PlayableKind.Movie };

            movieDb.Movies.Add(movie);
            try
            {
                movieDb.SaveChanges();
            }
            catch
            {
                return Conflict(new { Message = "Save failed", Success = false });
            }

            // Parse the submitted text fields into the normalized model (genres, runtime,
            // plot, rating, cast/crew). The movie stays unverified so the IMDB scrape can
            // later enrich it with nm-keyed cast, characters, and summaries.
            try
            {
                await MovieNormalizer.ApplyAllAsync(movieDb, movie);
            }
            catch
            {
                // Normalized parse failed; the movie itself is already saved.
            }

            if (!string.IsNullOrWhiteSpace(movie.PosterLink))
            {
                await DownloadAndSavePoster(movie, movie.PosterLink);
            }

            return Ok(new { Message = "Movie saved", Success = true });
        }

        public class MovieUpdateDto
        {
            public int id { get; set; }
            public string? Title { get; set; }
            public string? SimpleTitle { get; set; }
            public string? Rating { get; set; }
            public DateTime? ReleaseDate { get; set; }
            public string? Runtime { get; set; }
            public string? Genre { get; set; }
            public string? Director { get; set; }
            public string? Writer { get; set; }
            public string? Actors { get; set; }
            public string? Plot { get; set; }
            public string? PosterLink { get; set; }
            public decimal? imdbRating { get; set; }
            public string? imdbID { get; set; }
            public int? tomatoRating { get; set; }
            public int? RtTomatometer { get; set; }
            public int? RtPopcornmeter { get; set; }
            public bool RemoveFromRandom { get; set; }
        }

        [HttpPost("/API/UpdateMovie")]
        public async Task<IActionResult> UpdateMovie([FromBody] MovieUpdateDto dto)
        {
            if (!await IsCurrentUserEditor()) return Forbid();
            if (dto == null)
                return BadRequest(new { Message = "Invalid movie data", Success = false });

            if (dto.id == 0)
                return BadRequest(new { Message = "Movie ID is required", Success = false });

            var existing = await movieDb.Movies.Include(m => m.PosterDetails).SingleOrDefaultAsync(m => m.id == dto.id);
            if (existing == null)
                return NotFound(new { Message = "Movie not found", Success = false });

            dto.Title = dto.Title?.Trim();
            dto.SimpleTitle = dto.SimpleTitle?.Trim();
            dto.Rating = dto.Rating?.Trim();
            dto.Runtime = dto.Runtime?.Trim();
            dto.Genre = dto.Genre?.Trim();
            dto.Director = dto.Director?.Trim();
            dto.Writer = dto.Writer?.Trim();
            dto.Actors = dto.Actors?.Trim();
            dto.Plot = dto.Plot?.Trim();
            dto.PosterLink = dto.PosterLink?.Trim();
            dto.imdbID = dto.imdbID?.Trim();

            var posterLinkChanged = !string.Equals(existing.PosterDetails?.PosterLink, dto.PosterLink, StringComparison.Ordinal);

            if (!string.Equals(existing.imdbID, dto.imdbID, StringComparison.Ordinal) && !string.IsNullOrEmpty(dto.imdbID))
            {
                var imdbConflict = await movieDb.Movies.AnyAsync(m => m.imdbID == dto.imdbID && m.id != dto.id);
                if (imdbConflict)
                    return Conflict(new { Message = $"Another movie already has imdbID: {dto.imdbID}", Success = false });
            }

            // Detect which legacy text fields actually changed, so we re-parse only those into
            // the normalized tables (the user's edit wins for that field; unchanged fields keep
            // any richer scraped data).
            bool genreChanged = !string.Equals(existing.Genre, dto.Genre, StringComparison.Ordinal);
            bool runtimeChanged = !string.Equals(existing.Runtime, dto.Runtime, StringComparison.Ordinal);
            bool plotChanged = !string.Equals(existing.Plot, dto.Plot, StringComparison.Ordinal);
            bool ratingChanged = !string.Equals(existing.Rating, dto.Rating, StringComparison.Ordinal);
            bool directorChanged = !string.Equals(existing.Director, dto.Director, StringComparison.Ordinal);
            bool writerChanged = !string.Equals(existing.Writer, dto.Writer, StringComparison.Ordinal);
            bool actorsChanged = !string.Equals(existing.Actors, dto.Actors, StringComparison.Ordinal);

            existing.Title = dto.Title;
            existing.SimpleTitle = dto.SimpleTitle;
            existing.Rating = dto.Rating;
            existing.ReleaseDate = dto.ReleaseDate;
            existing.Runtime = dto.Runtime;
            existing.Genre = dto.Genre;
            existing.Director = dto.Director;
            existing.Writer = dto.Writer;
            existing.Actors = dto.Actors;
            existing.Plot = dto.Plot;
            existing.imdbRating = dto.imdbRating;
            existing.imdbID = dto.imdbID;
            existing.tomatoRating = dto.tomatoRating;
            existing.RtTomatometer = dto.RtTomatometer;
            existing.RtPopcornmeter = dto.RtPopcornmeter;
            existing.RemoveFromRandom = dto.RemoveFromRandom;

            try
            {
                await movieDb.SaveChangesAsync();
            }
            catch (Exception ex)
            {
                return Conflict(new { Message = $"Save failed: {ex.InnerException?.Message ?? ex.Message}", Success = false });
            }

            // Re-parse only the changed text fields into the normalized model.
            try
            {
                if (runtimeChanged) MovieNormalizer.ApplyRuntime(existing);
                if (plotChanged) MovieNormalizer.ApplyPlot(existing);
                if (ratingChanged) MovieNormalizer.ApplyRating(existing);
                if (genreChanged) await MovieNormalizer.ReplaceGenresAsync(movieDb, existing.id, existing.Genre);
                if (directorChanged) await MovieNormalizer.ReplaceRoleCreditsAsync(movieDb, existing.id, CreditRole.Director, existing.Director);
                if (writerChanged) await MovieNormalizer.ReplaceRoleCreditsAsync(movieDb, existing.id, CreditRole.Writer, existing.Writer);
                if (actorsChanged)
                {
                    await MovieNormalizer.ReplaceRoleCreditsAsync(movieDb, existing.id, CreditRole.Actor, existing.Actors);
                    MovieNormalizer.ApplyTopCast(existing);
                }
                if (genreChanged || runtimeChanged || plotChanged || ratingChanged || directorChanged || writerChanged || actorsChanged)
                    await movieDb.SaveChangesAsync();
            }
            catch
            {
                // Normalized re-parse failed; the legacy update is already saved.
            }

            string posterError = null;
            if (posterLinkChanged && !string.IsNullOrWhiteSpace(dto.PosterLink))
            {
                if (existing.PosterDetails == null)
                {
                    var pd = new MoviePosterDetails { MovieId = existing.id, PosterLink = dto.PosterLink };
                    movieDb.MoviePosterDetails.Add(pd);
                }
                else
                {
                    existing.PosterDetails.PosterLink = dto.PosterLink;
                }
                await movieDb.SaveChangesAsync();

                try
                {
                    await DownloadAndSavePoster(existing, dto.PosterLink, force: true);
                }
                catch (Exception ex)
                {
                    posterError = ex.Message;
                }
            }

            var message = posterError != null
                ? $"Movie updated, but poster download failed: {posterError}"
                : "Movie updated";
            return Ok(new { Message = message, Success = true, data = existing });
        }

        private async Task DownloadAndSavePoster(Movie movie, string posterLink, bool force = false)
        {
            var result = await httpClient.GetAsync(posterLink);
            result.EnsureSuccessStatusCode();
            var content = await result.Content.ReadAsByteArrayAsync();
            await imageRepo.SaveImage(movie.id, PosterImageVariant.Main, content);
            // The main image is already on disk; a thumbnail failure must not abort the save (which would
            // leave the title with a main poster but PosterVersion unbumped and no thumb — a blank card
            // even though /Image works). Isolate it like PosterFetchService does; BackfillThumbnails can
            // regenerate a missed thumb later from the on-disk main.
            try { await shrinkService.EnsurePosterThumnailExists(movie.id, force); }
            catch (Exception ex) { logger.LogWarning(ex, "Thumbnail generation failed for movie {Id}; saving poster without it", movie.id); }

            var thumbnailBytes = await imageRepo.GetImage(movie.id, PosterImageVariant.Thumbnail);
            var dominantColor = ComputeAverageColor(thumbnailBytes ?? content);

            var posterDetails = await movieDb.MoviePosterDetails.FindAsync(movie.id);
            if (posterDetails == null)
            {
                posterDetails = new MoviePosterDetails { MovieId = movie.id, PosterLink = posterLink, PosterVersion = 1, DominantColor = dominantColor };
                movieDb.MoviePosterDetails.Add(posterDetails);
            }
            else
            {
                posterDetails.PosterLink = posterLink;
                posterDetails.PosterVersion++;
                posterDetails.DominantColor = dominantColor;
            }
            await movieDb.SaveChangesAsync();
        }

        // Download a poster from a link and persist it for a title by id — movie or series. Bumps the
        // PosterVersion (cache-bust) and recomputes the dominant color, exactly like DownloadAndSavePoster
        // but addressable by id+table so the review tool can pull a poster for a pending row. Returns the
        // new version.
        private async Task<int> DownloadAndSavePosterByIdAsync(int id, string posterLink, bool isSeries)
        {
            var bucket = PosterBucket.ForTitle(isSeries);
            var result = await httpClient.GetAsync(posterLink);
            result.EnsureSuccessStatusCode();
            var content = await result.Content.ReadAsByteArrayAsync();
            await imageRepo.SaveImage(id, PosterImageVariant.Main, content, bucket);
            await shrinkService.EnsurePosterThumnailExists(id, true, bucket);
            var thumbnailBytes = await imageRepo.GetImage(id, PosterImageVariant.Thumbnail, bucket);
            var dominantColor = ComputeAverageColor(thumbnailBytes ?? content);

            if (isSeries)
            {
                var pd = await movieDb.SeriesPosterDetails.FindAsync(id);
                if (pd == null)
                {
                    pd = new SeriesPosterDetails { SeriesId = id, PosterLink = posterLink, PosterVersion = 1, DominantColor = dominantColor };
                    movieDb.SeriesPosterDetails.Add(pd);
                }
                else { pd.PosterLink = posterLink; pd.PosterVersion++; pd.DominantColor = dominantColor; }
                await movieDb.SaveChangesAsync();
                return pd.PosterVersion;
            }
            else
            {
                var pd = await movieDb.MoviePosterDetails.FindAsync(id);
                if (pd == null)
                {
                    pd = new MoviePosterDetails { MovieId = id, PosterLink = posterLink, PosterVersion = 1, DominantColor = dominantColor };
                    movieDb.MoviePosterDetails.Add(pd);
                }
                else { pd.PosterLink = posterLink; pd.PosterVersion++; pd.DominantColor = dominantColor; }
                await movieDb.SaveChangesAsync();
                return pd.PosterVersion;
            }
        }

        [HttpPost("/API/ScanPosterColors")]
        public async Task<IActionResult> ScanPosterColors(int batchSize = 50)
        {
            batchSize = Math.Clamp(batchSize, 1, 500);

            var batch = await movieDb.MoviePosterDetails
                .Where(pd => pd.DominantColor == null)
                .OrderBy(pd => pd.MovieId)
                .Take(batchSize)
                .ToListAsync();

            if (batch.Count == 0)
            {
                var total = await movieDb.MoviePosterDetails.CountAsync();
                return Ok(new { Processed = 0, Skipped = 0, Remaining = 0, Total = total, Errors = Array.Empty<string>() });
            }

            int processed = 0;
            int skipped = 0;
            var errors = new List<string>();

            foreach (var pd in batch)
            {
                try
                {
                    var hasThumb = await imageRepo.HasImage(pd.MovieId, PosterImageVariant.Thumbnail);
                    var variant = hasThumb ? PosterImageVariant.Thumbnail : PosterImageVariant.Main;
                    if (!hasThumb && !await imageRepo.HasImage(pd.MovieId, PosterImageVariant.Main))
                    {
                        skipped++;
                        continue;
                    }

                    var imageBytes = await imageRepo.GetImage(pd.MovieId, variant);
                    pd.DominantColor = ComputeAverageColor(imageBytes);
                    processed++;
                }
                catch (Exception ex)
                {
                    errors.Add($"Movie {pd.MovieId}: {ex.Message}");
                }
            }

            await movieDb.SaveChangesAsync();

            var remaining = await movieDb.MoviePosterDetails.CountAsync(pd => pd.DominantColor == null);

            return Ok(new { Processed = processed, Skipped = skipped, Remaining = remaining, Errors = errors });
        }

        private static string ComputeAverageColor(byte[] imageBytes)
        {
            using var image = Image.Load<Rgba32>(imageBytes);
            long totalR = 0, totalG = 0, totalB = 0, count = 0;

            image.ProcessPixelRows(accessor =>
            {
                for (int y = 0; y < accessor.Height; y++)
                {
                    var row = accessor.GetRowSpan(y);
                    for (int x = 0; x < row.Length; x++)
                    {
                        var pixel = row[x];
                        if (pixel.A < 128) continue;
                        totalR += pixel.R;
                        totalG += pixel.G;
                        totalB += pixel.B;
                        count++;
                    }
                }
            });

            if (count == 0)
                return "#000000";

            return $"#{totalR / count:X2}{totalG / count:X2}{totalB / count:X2}";
        }

        private static readonly PasswordHasher<User> passwordHasher = new();
    }
}
