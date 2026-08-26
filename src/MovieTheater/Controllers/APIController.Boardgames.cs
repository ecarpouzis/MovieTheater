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
        [HttpPost("/API/SyncBoardgameFromBgg")]
        public async Task<IActionResult> SyncBoardgameFromBgg(int bggThingId)
        {
            if (!await IsCurrentUserEditor()) return Forbid();
            if (bggThingId <= 0)
                return BadRequest(new { Success = false, Message = "bggThingId must be a positive integer" });

            try
            {
                var fromBgg = await boardGameGeekApi.GetBoardgame(bggThingId);
                if (fromBgg == null)
                    return NotFound(new { Success = false, Message = "Boardgame not found from BoardGameGeek" });

                return await SyncBoardgameInternal(fromBgg);
            }
            catch (HttpRequestException ex)
            {
                return StatusCode(502, new { Success = false, Message = "BoardGameGeek request failed", Error = ex.Message });
            }
        }

        [HttpPost("/API/SyncBoardgameFromBggByTitle")]
        public async Task<IActionResult> SyncBoardgameFromBggByTitle(string title)
        {
            if (!await IsCurrentUserEditor()) return Forbid();
            if (string.IsNullOrWhiteSpace(title))
                return BadRequest(new { Success = false, Message = "title is required" });

            try
            {
                var fromBgg = await boardGameGeekApi.GetBoardgameByTitle(title);
                if (fromBgg == null)
                    return NotFound(new { Success = false, Message = $"Boardgame '{title}' not found from BoardGameGeek" });

                return await SyncBoardgameInternal(fromBgg);
            }
            catch (HttpRequestException ex)
            {
                return StatusCode(502, new { Success = false, Message = "BoardGameGeek request failed", Error = ex.Message });
            }
        }

        private async Task<IActionResult> SyncBoardgameInternal(BoardgameBggResult fromBgg)
        {
            var fromBggBoardgame = fromBgg.Boardgame;
            var existing = await movieDb.Boardgames
                .Include(x => x.ImageDetails)
                .Include(x => x.ExtraDetails)
                .SingleOrDefaultAsync(x => x.BggThingId == fromBggBoardgame.BggThingId);

            if (existing == null)
            {
                movieDb.Boardgames.Add(fromBggBoardgame);
                await movieDb.SaveChangesAsync();
                fromBggBoardgame.BaseGameId = await ResolveBaseGameId(fromBggBoardgame.ExtraDetails?.LinksJson);
                if (fromBggBoardgame.BaseGameId.HasValue) await movieDb.SaveChangesAsync();
                await LinkOrphanedExpansionsAsync(fromBggBoardgame.id, fromBggBoardgame.BggThingId);
                await UpsertBoardgameImageUrls(fromBggBoardgame.id, fromBgg.ImageUrl, fromBgg.ThumbnailUrl);
                var newImageError = await TryDownloadBoardgameImages(fromBggBoardgame);
                await movieDb.Entry(fromBggBoardgame).Reference(x => x.ImageDetails).LoadAsync();
                await boardgameSimilarityService.RebuildAsync(movieDb);
                return Ok(new { Success = true, Message = WithImageError("Boardgame captured", newImageError), data = fromBggBoardgame });
            }

            var imageUrlsChanged = !string.Equals(existing.ImageDetails?.ImageUrl, fromBgg.ImageUrl, StringComparison.Ordinal)
                || !string.Equals(existing.ImageDetails?.ThumbnailUrl, fromBgg.ThumbnailUrl, StringComparison.Ordinal);

            ApplyBoardgameSnapshot(existing, fromBggBoardgame);
            await movieDb.SaveChangesAsync();
            // ?? preserves hand-set groupings (standalones parked under a base game) when BGG has no
            // inbound expansion link for this thing - a bare re-sync must not wipe them.
            existing.BaseGameId = await ResolveBaseGameId(existing.ExtraDetails?.LinksJson) ?? existing.BaseGameId;
            await movieDb.SaveChangesAsync();
            await LinkOrphanedExpansionsAsync(existing.id, existing.BggThingId);
            await UpsertBoardgameImageUrls(existing.id, fromBgg.ImageUrl, fromBgg.ThumbnailUrl);

            string? imageError = null;
            if (imageUrlsChanged)
                imageError = await TryDownloadBoardgameImages(existing, force: true);

            if (existing.ImageDetails == null)
                await movieDb.Entry(existing).Reference(x => x.ImageDetails).LoadAsync();

            await boardgameSimilarityService.RebuildAsync(movieDb);
            return Ok(new { Success = true, Message = WithImageError("Boardgame updated", imageError), data = existing });
        }

        public class UpdateBoardgameRequest
        {
            public int Id { get; set; }
            public string? Name { get; set; }
            public string? Description { get; set; }
            public int? YearPublished { get; set; }
            public int? MinPlayers { get; set; }
            public int? MaxPlayers { get; set; }
            public int? PlayingTime { get; set; }
            public int? MinAge { get; set; }
            public string? ImageUrl { get; set; }
            public int? BaseGameId { get; set; }
        }

        [HttpPost("/API/UpdateBoardgame")]
        public async Task<IActionResult> UpdateBoardgame([FromBody] UpdateBoardgameRequest req)
        {
            if (!await IsCurrentUserEditor()) return Forbid();
            if (req == null)
                return BadRequest(new { Success = false, Message = "No data provided." });

            var game = await movieDb.Boardgames.Include(b => b.ImageDetails).FirstOrDefaultAsync(x => x.id == req.Id);
            if (game == null)
                return NotFound(new { Success = false, Message = "Boardgame not found." });

            var imageUrlChanged = !string.Equals(game.ImageDetails?.ImageUrl, req.ImageUrl?.Trim(), StringComparison.Ordinal)
                                  && !string.IsNullOrWhiteSpace(req.ImageUrl);

            // Full-replace on purpose: the modal always sends the complete edit state, and blanking a
            // field is how it gets cleared. Partial API calls will null out whatever they omit.
            game.Name = req.Name;
            game.Description = req.Description;
            game.YearPublished = req.YearPublished;
            game.MinPlayers = req.MinPlayers;
            game.MaxPlayers = req.MaxPlayers;
            game.PlayingTime = req.PlayingTime;
            game.MinAge = req.MinAge;
            game.BaseGameId = req.BaseGameId;

            await movieDb.SaveChangesAsync();

            string? imageError = null;
            if (imageUrlChanged)
            {
                await UpsertBoardgameImageUrls(game.id, req.ImageUrl!.Trim(), null);
                try
                {
                    await DownloadAndSaveBoardgameImages(game, force: true);
                }
                catch (Exception ex)
                {
                    imageError = ex.Message;
                }
            }

            // Name/rating/image fields edited here surface in other games' similar-game
            // entries, so refresh the (persisted) similarity cache.
            await boardgameSimilarityService.RebuildAsync(movieDb);

            var msg = imageError != null ? $"Boardgame updated, but image download failed: {imageError}" : "Boardgame updated";
            return Ok(new { Success = true, Message = msg, data = game });
        }

        public class RematchBoardgameRequest
        {
            public int Id { get; set; }
            public int NewBggThingId { get; set; }
        }

        [HttpPost("/API/RematchBoardgame")]
        public async Task<IActionResult> RematchBoardgame([FromBody] RematchBoardgameRequest req)
        {
            if (!await IsCurrentUserEditor()) return Forbid();
            if (req == null || req.Id <= 0 || req.NewBggThingId <= 0)
                return BadRequest(new { Success = false, Message = "id and newBggThingId must be positive integers." });

            var game = await movieDb.Boardgames
                .Include(x => x.ImageDetails)
                .Include(x => x.ExtraDetails)
                .FirstOrDefaultAsync(x => x.id == req.Id);
            if (game == null)
                return NotFound(new { Success = false, Message = "Boardgame not found." });

            var conflict = await movieDb.Boardgames.FirstOrDefaultAsync(x => x.BggThingId == req.NewBggThingId && x.id != req.Id);
            if (conflict != null)
                return Conflict(new { Success = false, Message = $"BGG ID {req.NewBggThingId} is already used by '{conflict.Name}' (id #{conflict.id})." });

            try
            {
                var fromBgg = await boardGameGeekApi.GetBoardgame(req.NewBggThingId);
                if (fromBgg == null)
                    return NotFound(new { Success = false, Message = "Boardgame not found on BoardGameGeek." });

                var fromBggBoardgame = fromBgg.Boardgame;

                await boardgameImageRepo.DeleteImage(game.id, BoardgameImageVariant.Main);
                await boardgameImageRepo.DeleteImage(game.id, BoardgameImageVariant.Thumbnail);

                ApplyBoardgameSnapshot(game, fromBggBoardgame);
                game.BggThingId = req.NewBggThingId;

                await movieDb.SaveChangesAsync();
                game.BaseGameId = await ResolveBaseGameId(game.ExtraDetails?.LinksJson) ?? game.BaseGameId;
                await movieDb.SaveChangesAsync();
                await LinkOrphanedExpansionsAsync(game.id, game.BggThingId);
                await UpsertBoardgameImageUrls(game.id, fromBgg.ImageUrl, fromBgg.ThumbnailUrl);
                var imageError = await TryDownloadBoardgameImages(game, force: true);

                // ImageDetails is set by DownloadAndSaveBoardgameImages; load it if not already populated
                if (game.ImageDetails == null)
                    await movieDb.Entry(game).Reference(g => g.ImageDetails).LoadAsync();

                await boardgameSimilarityService.RebuildAsync(movieDb);
                return Ok(new { Success = true, Message = WithImageError("Boardgame re-matched", imageError), data = game });
            }
            catch (HttpRequestException ex)
            {
                return StatusCode(502, new { Success = false, Message = "BoardGameGeek request failed", Error = ex.Message });
            }
        }

        [HttpGet("/API/GetBoardgame")]
        public async Task<IActionResult> GetBoardgame(int bggThingId)
        {
            if (bggThingId <= 0)
            {
                return BadRequest(new { Success = false, Message = "bggThingId must be a positive integer" });
            }

            var boardgame = await movieDb.Boardgames
                .Include(x => x.ImageDetails)
                .SingleOrDefaultAsync(x => x.BggThingId == bggThingId);
            if (boardgame == null)
            {
                return NotFound(new { Success = false, Message = "Boardgame not found" });
            }

            return Ok(new { Success = true, data = boardgame });
        }

        [EnableQuery]
        [HttpGet("/odata/Boardgames")]
        public IQueryable<Boardgame> GetBoardgames()
        {
            return movieDb.Boardgames.Include(b => b.ImageDetails);
        }

        /// <summary>
        /// Publisher / family / designer / category / mechanic per game, read out of the stored BGG links
        /// (Web.BoardgameLinkFacets) — the group axes the catalog package's grouped views need and that
        /// are not columns. One slim payload for the whole catalog, cached, so the client-side grouping
        /// never has to $expand every game's links array.
        /// </summary>
        [HttpGet("/API/Boardgames/Facets")]
        public async Task<IActionResult> BoardgameFacets()
        {
            var items = await memoryCache.GetOrCreateAsync("boardgames:facets", async entry =>
            {
                entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromHours(6);
                var rows = await movieDb.BoardgameExtraDetails.AsNoTracking().Select(d => new { d.BoardgameId, d.LinksJson }).ToListAsync();
                var list = rows.Select(r =>
                {
                    var f = Web.BoardgameLinkFacets.Parse(r.LinksJson);
                    return new { id = r.BoardgameId, publishers = f.Publishers, families = f.Families, designers = f.Designers, categories = f.Categories, mechanics = f.Mechanics };
                }).ToList();
                entry.Size = 256 + list.Count * 400L; // the site's cache is byte-budgeted
                return list;
            });
            return Ok(new { items });
        }

        [HttpGet("/API/SimilarBoardgames")]
        public IActionResult SimilarBoardgames(int id)
        {
            var similar = boardgameSimilarityService.GetSimilar(id);
            return Ok(new { success = true, data = similar });
        }

        [HttpPost("/API/BatchImportBoardgames")]
        [HttpPost("/API/BatchInsertBoardgames")]
        public async Task<IActionResult> BatchImportBoardgames([FromBody] List<string> gameNames, int delayMs = 2000)
        {
            if (!await IsCurrentUserEditor()) return Forbid();
            if (gameNames == null || gameNames.Count == 0)
            {
                return BadRequest(new { Success = false, Message = "gameNames array is required" });
            }

            var results = new List<object>();
            int successCount = 0;
            int failureCount = 0;
            int skippedCount = 0;

            for (int i = 0; i < gameNames.Count; i++)
            {
                var rawInput = gameNames[i]?.Trim();
                if (string.IsNullOrWhiteSpace(rawInput))
                {
                    results.Add(new { Index = i, Input = rawInput, Status = "Skipped", Reason = "Empty input" });
                    skippedCount++;
                    continue;
                }

                bool madeApiCall = false;
                try
                {
                    var isBggId = TryParseBggThingId(rawInput, out var bggThingId) && bggThingId > 0;

                    if (isBggId)
                    {
                        var existingById = await movieDb.Boardgames.SingleOrDefaultAsync(x => x.BggThingId == bggThingId);
                        if (existingById != null)
                        {
                            results.Add(new { Index = i, Input = rawInput, BggThingId = existingById.BggThingId, Status = "AlreadyExists", Name = existingById.Name });
                            skippedCount++;
                            continue;
                        }
                    }
                    else
                    {
                        var existingByName = await movieDb.Boardgames.FirstOrDefaultAsync(x => x.Name == rawInput);
                        if (existingByName != null)
                        {
                            results.Add(new { Index = i, Input = rawInput, BggThingId = existingByName.BggThingId, Status = "AlreadyExists", Name = existingByName.Name });
                            skippedCount++;
                            continue;
                        }
                    }

                    var fromBgg = isBggId
                        ? await boardGameGeekApi.GetBoardgame(bggThingId)
                        : await boardGameGeekApi.GetBoardgameByTitle(rawInput);
                    madeApiCall = true;

                    if (fromBgg == null)
                    {
                        results.Add(new { Index = i, Input = rawInput, Status = "NotFound", Message = "Not found on BGG" });
                        failureCount++;
                        continue;
                    }

                    var fromBggBoardgame = fromBgg.Boardgame;
                    var existing = await movieDb.Boardgames.SingleOrDefaultAsync(x => x.BggThingId == fromBggBoardgame.BggThingId);
                    if (existing == null)
                    {
                        movieDb.Boardgames.Add(fromBggBoardgame);
                        await movieDb.SaveChangesAsync();
                        fromBggBoardgame.BaseGameId = await ResolveBaseGameId(fromBggBoardgame.ExtraDetails?.LinksJson);
                        if (fromBggBoardgame.BaseGameId.HasValue) await movieDb.SaveChangesAsync();
                        await LinkOrphanedExpansionsAsync(fromBggBoardgame.id, fromBggBoardgame.BggThingId);
                        await UpsertBoardgameImageUrls(fromBggBoardgame.id, fromBgg.ImageUrl, fromBgg.ThumbnailUrl);

                        var imageError = await TryDownloadBoardgameImages(fromBggBoardgame);
                        results.Add(new { Index = i, Input = rawInput, BggThingId = fromBggBoardgame.BggThingId, Status = "Created", Name = fromBggBoardgame.Name, ImageError = imageError });
                        successCount++;
                    }
                    else
                    {
                        results.Add(new { Index = i, Input = rawInput, BggThingId = fromBggBoardgame.BggThingId, Status = "AlreadyExists", Name = existing.Name });
                        skippedCount++;
                    }
                }
                catch (HttpRequestException ex)
                {
                    results.Add(new { Index = i, Input = rawInput, Status = "Failed", Error = ex.Message });
                    failureCount++;
                }
                catch (Exception ex)
                {
                    results.Add(new { Index = i, Input = rawInput, Status = "Failed", Error = ex.Message });
                    failureCount++;
                }

                // Rate limiting: wait between BGG requests (default 2 seconds)
                if (madeApiCall && i < gameNames.Count - 1)
                {
                    await Task.Delay(delayMs);
                }
            }

            if (successCount > 0)
                await boardgameSimilarityService.RebuildAsync(movieDb);

            return Ok(new
            {
                Success = true,
                Summary = new { Total = gameNames.Count, Success = successCount, Failed = failureCount, Skipped = skippedCount },
                Results = results
            });
        }

        private async Task UpsertBoardgameImageUrls(int boardgameId, string? imageUrl, string? thumbnailUrl)
        {
            var details = await movieDb.BoardgameImageDetails.FindAsync(boardgameId);
            if (details == null)
                movieDb.BoardgameImageDetails.Add(new BoardgameImageDetails { BoardgameId = boardgameId, ImageVersion = 0, ImageUrl = imageUrl, ThumbnailUrl = thumbnailUrl });
            else
            {
                details.ImageUrl = imageUrl;
                details.ThumbnailUrl = thumbnailUrl;
            }
            await movieDb.SaveChangesAsync();
        }

        private async Task DownloadAndSaveBoardgameImages(Boardgame boardgame, bool force = false)
        {
            var details = boardgame.ImageDetails ?? await movieDb.BoardgameImageDetails.FindAsync(boardgame.id);
            var imageUrl = details?.ImageUrl;
            var thumbnailUrl = details?.ThumbnailUrl;

            bool hasMain = await boardgameImageRepo.HasImage(boardgame.id, BoardgameImageVariant.Main);
            bool hasThumb = await boardgameImageRepo.HasImage(boardgame.id, BoardgameImageVariant.Thumbnail);
            bool savedAny = false;

            // Defense-in-depth SSRF guard: these URLs are editor/BGG-supplied, but validate before
            // fetching so a stored internal URL can't turn this into a server-side proxy.
            if (!string.IsNullOrWhiteSpace(imageUrl) && !(await MovieTheater.Web.ServerSideUrlGuard.ValidateAsync(imageUrl)).ok)
                imageUrl = null;
            if (!string.IsNullOrWhiteSpace(thumbnailUrl) && !(await MovieTheater.Web.ServerSideUrlGuard.ValidateAsync(thumbnailUrl)).ok)
                thumbnailUrl = null;

            // Fire both HTTP requests before awaiting either so they download in parallel
            var mainFetchTask = (force || !hasMain) && !string.IsNullOrWhiteSpace(imageUrl)
                ? httpClient.GetAsync(imageUrl)
                : null;
            var thumbFetchTask = (force || !hasThumb) && !string.IsNullOrWhiteSpace(thumbnailUrl)
                ? httpClient.GetAsync(thumbnailUrl)
                : null;

            byte[]? mainBytes = null;
            if (mainFetchTask != null)
            {
                var imageResponse = await mainFetchTask;
                imageResponse.EnsureSuccessStatusCode();
                mainBytes = await imageResponse.Content.ReadAsByteArrayAsync();
                await boardgameImageRepo.SaveImage(boardgame.id, BoardgameImageVariant.Main, mainBytes);
                savedAny = true;
            }

            byte[]? thumbBytes = null;
            if (thumbFetchTask != null)
            {
                var thumbResponse = await thumbFetchTask;
                if (thumbResponse.IsSuccessStatusCode)
                    thumbBytes = await thumbResponse.Content.ReadAsByteArrayAsync();
            }

            if (thumbBytes == null && (force || !hasThumb))
            {
                mainBytes ??= await boardgameImageRepo.GetImage(boardgame.id, BoardgameImageVariant.Main);
                if (mainBytes != null)
                    thumbBytes = BuildBoardgameThumbnail(mainBytes);
            }

            if (thumbBytes != null)
            {
                await boardgameImageRepo.SaveImage(boardgame.id, BoardgameImageVariant.Thumbnail, thumbBytes);
                savedAny = true;
            }

            if (savedAny)
            {
                if (details == null)
                {
                    details = new BoardgameImageDetails { BoardgameId = boardgame.id, ImageVersion = 1, ImageUrl = imageUrl, ThumbnailUrl = thumbnailUrl };
                    movieDb.BoardgameImageDetails.Add(details);
                    boardgame.ImageDetails = details;
                }
                else
                {
                    details.ImageVersion++;
                    details.ImageUrl = imageUrl;
                    details.ThumbnailUrl = thumbnailUrl;
                }
                await movieDb.SaveChangesAsync();
            }
        }

        private static byte[] BuildBoardgameThumbnail(byte[] sourceImage)
        {
            // The shared recipe (ImageShrinkService) with the boardgame's single sharpen pass -
            // this used to be a hand-kept copy of the poster geometry/encoder.
            return MovieTheater.Services.Poster.ImageShrinkService.ShrinkToThumbnailPng(sourceImage, new[] { .5f });
        }

        private static void ApplyBoardgameSnapshot(Boardgame existing, Boardgame fromBgg)
        {
            existing.ThingType = fromBgg.ThingType;
            existing.Name = fromBgg.Name;
            existing.YearPublished = fromBgg.YearPublished;
            existing.MinPlayers = fromBgg.MinPlayers;
            existing.MaxPlayers = fromBgg.MaxPlayers;
            existing.PlayingTime = fromBgg.PlayingTime;
            existing.MinPlayTime = fromBgg.MinPlayTime;
            existing.MaxPlayTime = fromBgg.MaxPlayTime;
            existing.MinAge = fromBgg.MinAge;
            existing.Description = fromBgg.Description;
            existing.UsersRated = fromBgg.UsersRated;
            existing.AverageRating = fromBgg.AverageRating;
            existing.BayesAverageRating = fromBgg.BayesAverageRating;
            existing.StdDev = fromBgg.StdDev;
            existing.Median = fromBgg.Median;
            existing.Owned = fromBgg.Owned;
            existing.Trading = fromBgg.Trading;
            existing.Wanting = fromBgg.Wanting;
            existing.Wishing = fromBgg.Wishing;
            existing.NumComments = fromBgg.NumComments;
            existing.NumWeights = fromBgg.NumWeights;
            existing.AverageWeight = fromBgg.AverageWeight;
            existing.LastSyncedUtc = fromBgg.LastSyncedUtc;

            var src = fromBgg.ExtraDetails;
            if (src != null)
            {
                existing.ExtraDetails ??= new BoardgameExtraDetails { BoardgameId = existing.id };
                existing.ExtraDetails.AlternateNamesJson = src.AlternateNamesJson;
                existing.ExtraDetails.RanksJson = src.RanksJson;
                existing.ExtraDetails.LinksJson = src.LinksJson;
                existing.ExtraDetails.PollsJson = src.PollsJson;
                existing.ExtraDetails.VersionsXml = src.VersionsXml;
                existing.ExtraDetails.VideosJson = src.VideosJson;
                existing.ExtraDetails.MarketplaceXml = src.MarketplaceXml;
                existing.ExtraDetails.RawXml = src.RawXml;
            }
        }


        // boardgameexpansion inbound:true = this game requires the linked game to play
        // boardgameimplementation inbound:true = design lineage only; still a standalone game, not an expansion
        private static List<int> GetInboundExpansionBaseBggIds(string? linksJson)
        {
            var result = new List<int>();
            if (string.IsNullOrWhiteSpace(linksJson)) return result;
            try
            {
                using var doc = JsonDocument.Parse(linksJson);
                if (doc.RootElement.ValueKind != JsonValueKind.Array) return result;
                foreach (var link in doc.RootElement.EnumerateArray())
                {
                    if (!link.TryGetProperty("type", out var typeProp) || typeProp.GetString() != "boardgameexpansion") continue;
                    if (!link.TryGetProperty("inbound", out var inboundProp) || inboundProp.ValueKind != JsonValueKind.True) continue;
                    if (!link.TryGetProperty("id", out var idProp) || !idProp.TryGetInt32(out var bggBaseId)) continue;
                    result.Add(bggBaseId);
                }
            }
            catch { /* malformed JSON */ }
            return result;
        }

        private async Task<int?> ResolveBaseGameId(string? linksJson)
        {
            foreach (var bggBaseId in GetInboundExpansionBaseBggIds(linksJson))
            {
                var baseGame = await movieDb.Boardgames
                    .AsNoTracking()
                    .Where(b => b.BggThingId == bggBaseId)
                    .Select(b => new { b.id })
                    .FirstOrDefaultAsync();
                if (baseGame != null) return baseGame.id;
            }
            return null;
        }

        // Inverse of ResolveBaseGameId: when a base game arrives AFTER its expansions, those
        // expansions resolved to nothing at their own insert time and stayed unlinked forever.
        private async Task LinkOrphanedExpansionsAsync(int baseBoardgameId, int baseBggThingId)
        {
            var marker = "\"id\":" + baseBggThingId;
            var candidates = await movieDb.Boardgames
                .Where(b => b.id != baseBoardgameId && b.BaseGameId == null
                    && b.ExtraDetails != null && b.ExtraDetails.LinksJson != null
                    && b.ExtraDetails.LinksJson.Contains(marker))
                .Include(b => b.ExtraDetails)
                .ToListAsync();

            bool changed = false;
            foreach (var candidate in candidates)
            {
                if (GetInboundExpansionBaseBggIds(candidate.ExtraDetails!.LinksJson).Contains(baseBggThingId))
                {
                    candidate.BaseGameId = baseBoardgameId;
                    changed = true;
                }
            }
            if (changed) await movieDb.SaveChangesAsync();
        }

        // Image failures must not fail the whole capture: by the time images download, the row is
        // already saved, so throwing here would report failure for a game that now exists.
        private async Task<string?> TryDownloadBoardgameImages(Boardgame boardgame, bool force = false)
        {
            try
            {
                await DownloadAndSaveBoardgameImages(boardgame, force);
                return null;
            }
            catch (Exception ex)
            {
                return ex.Message;
            }
        }

        private static string WithImageError(string message, string? imageError)
            => imageError == null ? message : message + ", but image download failed: " + imageError;

        private static string GetMimeType(MosaicOutputFormat format) => format switch
        {
            MosaicOutputFormat.Jpeg => "image/jpeg",
            MosaicOutputFormat.WebP => "image/webp",
            _ => "image/png"
        };

        [HttpPost("/API/InsertBoardgameFromBgg")]
        public async Task<IActionResult> InsertBoardgameFromBgg(int bggThingId)
        {
            if (!await IsCurrentUserEditor()) return Forbid();
            if (bggThingId <= 0)
                return BadRequest(new { Success = false, Message = "bggThingId must be a positive integer" });

            var existing = await movieDb.Boardgames.SingleOrDefaultAsync(x => x.BggThingId == bggThingId);
            if (existing != null)
                return Conflict(new { Success = false, Message = $"Boardgame with BGG ID {bggThingId} already exists.", data = existing });

            try
            {
                var fromBgg = await boardGameGeekApi.GetBoardgame(bggThingId);
                if (fromBgg == null)
                    return NotFound(new { Success = false, Message = "Boardgame not found from BoardGameGeek" });

                var fromBggBoardgame = fromBgg.Boardgame;
                movieDb.Boardgames.Add(fromBggBoardgame);
                await movieDb.SaveChangesAsync();
                fromBggBoardgame.BaseGameId = await ResolveBaseGameId(fromBggBoardgame.ExtraDetails?.LinksJson);
                if (fromBggBoardgame.BaseGameId.HasValue) await movieDb.SaveChangesAsync();

                await UpsertBoardgameImageUrls(fromBggBoardgame.id, fromBgg.ImageUrl, fromBgg.ThumbnailUrl);
                await DownloadAndSaveBoardgameImages(fromBggBoardgame);
                await movieDb.Entry(fromBggBoardgame).Reference(x => x.ImageDetails).LoadAsync();
                await boardgameSimilarityService.RebuildAsync(movieDb);

                return Ok(new { Success = true, Message = "Boardgame inserted", data = fromBggBoardgame });
            }
            catch (HttpRequestException ex)
            {
                return StatusCode(502, new { Success = false, Message = "BoardGameGeek request failed", Error = ex.Message });
            }
        }

        [HttpPost("/API/GetBoardgamesFromInputs")]
        public async Task<IActionResult> GetBoardgamesFromInputs([FromBody] string[] inputs)
        {
            if (!await IsCurrentUserEditor()) return Forbid();
            if (inputs == null || inputs.Length == 0)
                return Ok(new List<object>());

            var results = new List<object>();

            foreach (var raw in inputs)
            {
                var input = raw?.Trim();
                if (string.IsNullOrWhiteSpace(input))
                {
                    results.Add(new { input = raw, found = false, message = "Empty input" });
                    continue;
                }

                try
                {
                    var isBggId = TryParseBggThingId(input, out var bggThingId) && bggThingId > 0;
                    var fromBgg = isBggId
                        ? await boardGameGeekApi.GetBoardgame(bggThingId)
                        : await boardGameGeekApi.GetBoardgameByTitle(input);

                    if (fromBgg == null)
                    {
                        results.Add(new { input, found = false, message = "Not found on BGG" });
                        continue;
                    }

                    var existing = await movieDb.Boardgames
                        .AsNoTracking()
                        .Include(x => x.ImageDetails)
                        .SingleOrDefaultAsync(x => x.BggThingId == fromBgg.Boardgame.BggThingId);

                    results.Add(new
                    {
                        input,
                        found = true,
                        exists = existing != null,
                        id = existing?.id,
                        bggThingId = fromBgg.Boardgame.BggThingId,
                        name = fromBgg.Boardgame.Name,
                        yearPublished = fromBgg.Boardgame.YearPublished,
                        minPlayers = fromBgg.Boardgame.MinPlayers,
                        maxPlayers = fromBgg.Boardgame.MaxPlayers,
                        playingTime = fromBgg.Boardgame.PlayingTime,
                        minAge = fromBgg.Boardgame.MinAge,
                        description = fromBgg.Boardgame.Description,
                        imageUrl = fromBgg.ImageUrl,
                        thumbnailUrl = fromBgg.ThumbnailUrl,
                        imageVersion = existing?.ImageDetails?.ImageVersion ?? 0
                    });
                }
                catch (HttpRequestException ex)
                {
                    results.Add(new { input, found = false, message = $"BGG request failed: {ex.Message}" });
                }
                catch (Exception ex)
                {
                    results.Add(new { input, found = false, message = ex.Message });
                }
            }

            return Ok(results);
        }

        // ─── Rules & Video Endpoints ─────────────────────────────────────────────

        [HttpPost("/API/DiscoverBoardgameRules")]
        public async Task<IActionResult> DiscoverBoardgameRules(int id)
        {
            if (!await IsCurrentUserEditor()) return Forbid();

            var game = await movieDb.Boardgames
                .Include(x => x.ExtraDetails)
                .FirstOrDefaultAsync(x => x.id == id);
            if (game == null) return NotFound(new { Success = false, Message = "Boardgame not found." });

            var (pdfCandidateUrls, videoUrls) = await boardgameRulesService.DiscoverAsync(game);

            if (pdfCandidateUrls.Count > 0)
                game.RulesPdfCandidateUrls = game.RulesPdfCandidateUrls.Union(pdfCandidateUrls).Distinct().ToList();
            if (videoUrls.Count > 0)
                game.HowToPlayVideoUrls = game.HowToPlayVideoUrls.Union(videoUrls).Distinct().ToList();

            await movieDb.SaveChangesAsync();
            return Ok(new { Success = true, data = new { rulesPdfCandidateUrls = game.RulesPdfCandidateUrls, howToPlayVideoUrls = game.HowToPlayVideoUrls } });
        }

        [HttpPost("/API/ApproveBoardgameRulesPdf")]
        public async Task<IActionResult> ApproveBoardgameRulesPdf(int id, [FromBody] ApprovePdfRequest req)
        {
            if (!await IsCurrentUserEditor()) return Forbid();
            if (string.IsNullOrWhiteSpace(req?.Url))
                return BadRequest(new { Success = false, Message = "No URL provided." });

            var game = await movieDb.Boardgames.FirstOrDefaultAsync(x => x.id == id);
            if (game == null) return NotFound(new { Success = false, Message = "Boardgame not found." });

            var pdfUrl = req.Url.Trim();
            var slot = game.RulesPdfUrls.Count;

            try
            {
                var response = await httpClient.GetAsync(pdfUrl);
                response.EnsureSuccessStatusCode();
                var bytes = await response.Content.ReadAsByteArrayAsync();
                await boardgamePdfRepository.SavePdfAsync(game.id, slot, bytes);
            }
            catch (Exception ex)
            {
                return StatusCode(502, new { Success = false, Message = $"Failed to download PDF: {ex.Message}" });
            }

            var approved = game.RulesPdfUrls;
            approved.Add(new RulesPdfEntry { Url = pdfUrl });
            game.RulesPdfUrls = approved;
            game.RulesPdfCandidateUrls = game.RulesPdfCandidateUrls.Where(u => u != pdfUrl).ToList();

            await movieDb.SaveChangesAsync();
            return Ok(new { Success = true, data = new { rulesPdfUrls = game.RulesPdfUrls.Select(e => new { url = e.Url, name = e.Name }), rulesPdfCandidateUrls = game.RulesPdfCandidateUrls, slot } });
        }

        [HttpPost("/API/RemoveBoardgameRulesPdf")]
        public async Task<IActionResult> RemoveBoardgameRulesPdf(int id, int slot)
        {
            if (!await IsCurrentUserEditor()) return Forbid();

            var game = await movieDb.Boardgames.FirstOrDefaultAsync(x => x.id == id);
            if (game == null) return NotFound(new { Success = false, Message = "Boardgame not found." });

            var urls = game.RulesPdfUrls;
            if (slot < 0 || slot >= urls.Count)
                return BadRequest(new { Success = false, Message = "Invalid slot." });

            boardgamePdfRepository.DeleteAndCompact(game.id, slot, urls.Count);
            urls.RemoveAt(slot);
            game.RulesPdfUrls = urls;

            await movieDb.SaveChangesAsync();
            return Ok(new { Success = true, data = new { rulesPdfUrls = game.RulesPdfUrls.Select(e => new { url = e.Url, name = e.Name }) } });
        }

        [HttpPost("/API/RemoveBoardgameRulesPdfCandidate")]
        public async Task<IActionResult> RemoveBoardgameRulesPdfCandidate(int id, [FromBody] ApprovePdfRequest req)
        {
            if (!await IsCurrentUserEditor()) return Forbid();
            if (string.IsNullOrWhiteSpace(req?.Url))
                return BadRequest(new { Success = false, Message = "No URL provided." });

            var game = await movieDb.Boardgames.FirstOrDefaultAsync(x => x.id == id);
            if (game == null) return NotFound(new { Success = false, Message = "Boardgame not found." });

            game.RulesPdfCandidateUrls = game.RulesPdfCandidateUrls.Where(u => u != req.Url.Trim()).ToList();
            await movieDb.SaveChangesAsync();
            return Ok(new { Success = true, data = new { rulesPdfCandidateUrls = game.RulesPdfCandidateUrls } });
        }

        public class ApprovePdfRequest { public string? Url { get; set; } }

        [HttpPost("/API/UploadBoardgameRulesPdf")]
        public async Task<IActionResult> UploadBoardgameRulesPdf(int id, IFormFile file)
        {
            if (!await IsCurrentUserEditor()) return Forbid();
            if (file == null || file.Length == 0)
                return BadRequest(new { Success = false, Message = "No file provided." });
            if (!file.ContentType.Contains("pdf", StringComparison.OrdinalIgnoreCase) &&
                !file.FileName.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
                return BadRequest(new { Success = false, Message = "Only PDF files are allowed." });

            var game = await movieDb.Boardgames.FirstOrDefaultAsync(x => x.id == id);
            if (game == null) return NotFound(new { Success = false, Message = "Boardgame not found." });

            var slot = game.RulesPdfUrls.Count;
            using var ms = new MemoryStream();
            await file.CopyToAsync(ms);
            await boardgamePdfRepository.SavePdfAsync(game.id, slot, ms.ToArray());

            var approved = game.RulesPdfUrls;
            var name = Path.GetFileNameWithoutExtension(file.FileName);
            approved.Add(new RulesPdfEntry { Url = $"/BoardgamePdf/{game.id}/{slot}", Name = name });
            game.RulesPdfUrls = approved;

            await movieDb.SaveChangesAsync();
            return Ok(new { Success = true, data = new { rulesPdfUrls = game.RulesPdfUrls.Select(e => new { url = e.Url, name = e.Name }), slot } });
        }

        [HttpPost("/API/BatchDiscoverBoardgameRules")]
        public async Task<IActionResult> BatchDiscoverBoardgameRules([FromBody] int[] ids)
        {
            if (!await IsCurrentUserEditor()) return Forbid();
            if (ids == null || ids.Length == 0) return BadRequest(new { Success = false, Message = "No ids provided." });

            var results = new List<object>();
            foreach (var gameId in ids)
            {
                var game = await movieDb.Boardgames
                    .Include(x => x.ExtraDetails)
                    .FirstOrDefaultAsync(x => x.id == gameId);
                if (game == null) { results.Add(new { id = gameId, success = false, message = "Not found" }); continue; }

                try
                {
                    var (pdfCandidateUrls, videoUrls) = await boardgameRulesService.DiscoverAsync(game);
                    if (pdfCandidateUrls.Count > 0)
                        game.RulesPdfCandidateUrls = game.RulesPdfCandidateUrls.Union(pdfCandidateUrls).Distinct().ToList();
                    if (videoUrls.Count > 0)
                        game.HowToPlayVideoUrls = game.HowToPlayVideoUrls.Union(videoUrls).Distinct().ToList();
                    var entries = game.HowToPlayVideoEntries;
                    if (await youTubeService.RefreshEntriesAsync(entries))
                        game.HowToPlayVideoEntries = entries;
                    await movieDb.SaveChangesAsync();
                    results.Add(new { id = gameId, success = true, rulesPdfCandidateUrls = game.RulesPdfCandidateUrls, howToPlayVideoUrls = game.HowToPlayVideoUrls });
                }
                catch (Exception ex)
                {
                    results.Add(new { id = gameId, success = false, message = ex.Message });
                }

                await Task.Delay(1000);
            }

            return Ok(new { Success = true, results });
        }

        [HttpPut("/API/UpdateBoardgameRules")]
        public async Task<IActionResult> UpdateBoardgameRules([FromBody] UpdateBoardgameRulesRequest req)
        {
            if (!await IsCurrentUserEditor()) return Forbid();
            if (req == null) return BadRequest(new { Success = false, Message = "No data provided." });

            var game = await movieDb.Boardgames.FirstOrDefaultAsync(x => x.id == req.Id);
            if (game == null) return NotFound(new { Success = false, Message = "Boardgame not found." });

            if (req.HowToPlayVideoUrls != null) game.HowToPlayVideoUrls = req.HowToPlayVideoUrls;
            if (req.RulesPdfUrls != null)
            {
                // The list's order IS the on-disk slot mapping ({id}_{slot}.pdf). Membership and order
                // only change through approve/upload (append) and remove (delete + compact); this
                // endpoint may only rename entries, or names and files silently desync.
                var current = game.RulesPdfUrls;
                if (req.RulesPdfUrls.Count != current.Count ||
                    !req.RulesPdfUrls.Select(e => e.Url).SequenceEqual(current.Select(e => e.Url), StringComparer.Ordinal))
                {
                    return BadRequest(new { Success = false, Message = "RulesPdfUrls may only change display names here; use the approve/upload/remove endpoints to change which PDFs exist." });
                }
                game.RulesPdfUrls = req.RulesPdfUrls;
            }

            if (req.HowToPlayVideoUrls != null)
            {
                var entries = game.HowToPlayVideoEntries;
                if (await youTubeService.RefreshEntriesAsync(entries))
                    game.HowToPlayVideoEntries = entries;
            }

            await movieDb.SaveChangesAsync();

            return Ok(new { Success = true, data = new {
                rulesPdfUrls = game.RulesPdfUrls.Select(e => new { url = e.Url, name = e.Name }),
                howToPlayVideoUrls = game.HowToPlayVideoUrls,
                howToPlayVideoUrlsJson = game.HowToPlayVideoUrlsJson,
            }});
        }

        public class UpdateBoardgameRulesRequest
        {
            public int Id { get; set; }
            public List<string>? HowToPlayVideoUrls { get; set; }
            public List<RulesPdfEntry>? RulesPdfUrls { get; set; }
        }

        private async Task<bool> IsCurrentUserEditor()
        {
            var userId = GetCurrentUserId();
            if (!userId.HasValue) return false;
            var settings = await movieDb.UserSettings.FirstOrDefaultAsync(s => s.UserID == userId.Value && s.SettingKey == "CanEditMovies");
            return settings != null && string.Equals(settings.SettingValue, "true", StringComparison.OrdinalIgnoreCase);
        }

        // Scrapes YouTube video metadata for boardgame videos that are missing or stale (>30 days,
        // per YouTube Developer Policies §4.D). Stores results directly in HowToPlayVideoUrlsJson.
        // Bounded per call: at most `max` games are refreshed; the caller re-runs until remaining=0.
        // Already-fresh games are skipped for free, so repeated calls converge deterministically.
        [HttpPost("/API/ScrapeYouTubeVideoDetails")]
        public async Task<IActionResult> ScrapeYouTubeVideoDetails(int max = 25)
        {
            if (!await IsCurrentUserEditor()) return Forbid();

            var games = await movieDb.Boardgames
                .Where(b => b.HowToPlayVideoUrlsJson != null)
                .OrderBy(b => b.id)
                .ToListAsync();

            int scraped = 0, total = 0, visited = 0;
            foreach (var game in games)
            {
                if (scraped >= max) break;
                visited++;
                var entries = game.HowToPlayVideoEntries;
                if (entries.Count == 0) continue;
                total += entries.Count;
                if (await youTubeService.RefreshEntriesAsync(entries))
                {
                    game.HowToPlayVideoEntries = entries;
                    scraped++;
                }
            }

            if (scraped > 0) await movieDb.SaveChangesAsync();
            var remaining = games.Count - visited;
            return Ok(new { message = $"Updated {scraped} boardgame(s); {remaining} not yet visited.", scraped, total, remaining });
        }
    }
}
