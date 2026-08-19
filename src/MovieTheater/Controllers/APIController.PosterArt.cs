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
        // GET /PosterCollage
        // Optional query params:
        //   postersWide    – number of poster columns (default: 25)
        //   postersHigh    – target row count; all matching posters are shown, distributed evenly
        //                    across this many rows (last row may be shorter). Makes the image
        //                    as wide as needed rather than capping the poster count.
        //   maxPixelsWide  – derive column count from max image width instead of postersWide
        //   actor          – only include movies whose Actors field contains this value
        //   text           – only include movies whose SimpleTitle or Title contains this value
        //   startsWith     – only include movies whose SimpleTitle starts with this letter ('#' for digits)
        //   posterWidth    – width of each poster tile in pixels (default: 75)
        //   posterHeight   – height of each poster tile in pixels (default: 100)
        [HttpGet("/PosterCollage")]
        public async Task<IActionResult> PosterCollage(
            int? postersWide = null, int? postersHigh = null, int? maxPixelsWide = null,
            string actor = null, string text = null, string startsWith = null,
            int posterWidth = 75, int posterHeight = 100)
        {
            // Renders a full-library composite in memory — an editor/creative tool, not on any user path.
            if (!await IsCurrentUserEditor()) return Forbid();

            // Clamp caller-controlled tile size so a request can't demand a gigapixel canvas.
            posterWidth = Math.Clamp(posterWidth, 8, 300);
            posterHeight = Math.Clamp(posterHeight, 8, 400);

            var cacheKey = $"collage:a={actor}:t={text}:sw={startsWith}:pw={postersWide}:ph={postersHigh}:mpw={maxPixelsWide}:w={posterWidth}:h={posterHeight}";
            if (memoryCache.TryGetValue(cacheKey, out byte[] cachedPng))
            {
                HttpContext.Response.ContentType = "image/png";
                await HttpContext.Response.Body.WriteAsync(cachedPng);
                return new EmptyResult();
            }

            IQueryable<Movie> moviesQuery = movieDb.Movies.AsNoTracking().OrderBy(m => m.SimpleTitle);

            if (!string.IsNullOrEmpty(actor))
                moviesQuery = moviesQuery.Where(m =>
                    m.Credits.Any(c => c.Role == CreditRole.Actor && c.Person.DisplayName.Contains(actor))
                    || (m.Actors != null && m.Actors.Contains(actor)));

            if (!string.IsNullOrEmpty(text))
                moviesQuery = moviesQuery.Where(m => m.SimpleTitle.Contains(text) || m.Title.Contains(text));

            if (!string.IsNullOrEmpty(startsWith))
            {
                if (startsWith == "#")
                {
                    moviesQuery = moviesQuery.Where(m => char.IsDigit(m.SimpleTitle[0]));
                }
                else
                {
                    moviesQuery = moviesQuery.Where(m => m.SimpleTitle.StartsWith(startsWith));
                }
            }

            // Only the id is needed to load each poster — never materialize whole Movie entities here.
            const int MaxPosters = 5000;
            var movieIds = await moviesQuery.Select(m => m.id).Take(MaxPosters).ToListAsync();

            // Load posters with bounded concurrency (Task.WhenAll preserves array order, so draw order
            // is still deterministic) rather than firing thousands of simultaneous file reads.
            byte[][] allImageResults;
            using (var gate = new SemaphoreSlim(16))
            {
                allImageResults = await Task.WhenAll(movieIds.Select(async id =>
                {
                    await gate.WaitAsync();
                    try { return await imageRepo.GetImage(id, PosterImageVariant.Thumbnail); }
                    finally { gate.Release(); }
                }));
            }

            var posterImages = allImageResults.Where(b => b != null).ToList();

            int totalPosters = posterImages.Count;

            // postersHigh: distribute all posters into this many rows, making the image as wide as needed.
            // maxPixelsWide / postersWide: directly set column count regardless of poster count.
            int rowLength;
            if (postersHigh.HasValue)
                rowLength = Math.Max(1, (int)Math.Ceiling((double)totalPosters / postersHigh.Value));
            else if (maxPixelsWide.HasValue)
                rowLength = Math.Max(1, maxPixelsWide.Value / posterWidth);
            else
                rowLength = postersWide ?? 25;

            int rowCount = (int)Math.Ceiling((double)totalPosters / rowLength);
            int totalWidth = Math.Min(totalPosters, rowLength) * posterWidth;
            int totalHeight = rowCount * posterHeight;

            using var combinedImage = new Image<Rgba32>(totalWidth, totalHeight);

            int drawingX = 0;
            int drawingY = 0;
            int rowCounter = 0;

            foreach (var bytes in posterImages)
            {
                if (rowCounter == rowLength)
                {
                    rowCounter = 0;
                    drawingX = 0;
                    drawingY += posterHeight;
                }

                using var posterImg = Image.Load(bytes);
                posterImg.Mutate(x => x.Resize(posterWidth, posterHeight));
                combinedImage.Mutate(ctx => ctx.DrawImage(posterImg, new Point(drawingX, drawingY), 1f));

                drawingX += posterWidth;
                rowCounter++;
            }

            using var outputMs = new MemoryStream();
            await combinedImage.SaveAsPngAsync(outputMs);
            var pngBytes = outputMs.ToArray();

            memoryCache.Set(cacheKey, pngBytes, new MemoryCacheEntryOptions
            {
                SlidingExpiration = TimeSpan.FromHours(4),
                Size = pngBytes.Length,
            });

            HttpContext.Response.ContentType = "image/png";
            await HttpContext.Response.Body.WriteAsync(pngBytes);
            return new EmptyResult();
        }

        // POST /PosterMosaic
        // Accepts an uploaded image and creates a photo-mosaic where each tile is one of the stored posters.
        [HttpPost("/PosterMosaic")]
        public async Task<IActionResult> PosterMosaic(
            IFormFile imageFile,
            // Scale
            double tileScale = 1.0,
            double outputScale = 1.0,
            int maxOutputDimension = 0,
            // Color Matching
            int topK = 50,
            int excludeRadius = 2,
            double colorDecayFactor = 100.0,
            double adjacencyPenaltyBase = 0.1,
            // Output Format
            string format = "png",
            int quality = 85,
            int pngCompression = 6)
        {
            if (imageFile == null || imageFile.Length == 0)
                return BadRequest(new { Message = "No image uploaded", Success = false });

            byte[] sourceBytes;
            using (var ms = new MemoryStream())
            {
                await imageFile.CopyToAsync(ms);
                sourceBytes = ms.ToArray();
            }

            var options = BuildMosaicOptions(tileScale, outputScale, maxOutputDimension,
                topK, excludeRadius, colorDecayFactor, adjacencyPenaltyBase, format, quality, pngCompression);

            byte[] mosaicBytes;
            try
            {
                mosaicBytes = await posterMosaicService.BuildPosterMosaicBytes(sourceBytes, options);
            }
            catch (InvalidOperationException ex)
            {
                return BadRequest(new { Message = ex.Message, Success = false });
            }

            return File(mosaicBytes, GetMimeType(options.OutputFormat));
        }

        [HttpGet("/PosterMosaicFromUrl")]
        public async Task<IActionResult> PosterMosaicFromUrl(
            string imageUrl,
            // Scale
            double tileScale = 1.0,
            double outputScale = 1.0,
            int maxOutputDimension = 0,
            // Color Matching
            int topK = 50,
            int excludeRadius = 2,
            double colorDecayFactor = 100.0,
            double adjacencyPenaltyBase = 0.1,
            // Output Format
            string format = "png",
            int quality = 85,
            int pngCompression = 6)
        {
            if (!await IsCurrentUserEditor()) return Forbid();

            if (string.IsNullOrWhiteSpace(imageUrl))
                return BadRequest(new { Message = "imageUrl is required", Success = false });

            var (urlOk, urlError) = await MovieTheater.Web.ServerSideUrlGuard.ValidateAsync(imageUrl);
            if (!urlOk)
                return BadRequest(new { Message = urlError, Success = false });

            var options = BuildMosaicOptions(tileScale, outputScale, maxOutputDimension,
                topK, excludeRadius, colorDecayFactor, adjacencyPenaltyBase, format, quality, pngCompression);

            var cacheKey = $"mosaic:{imageUrl}:ts={tileScale}:os={outputScale}:max={maxOutputDimension}:k={topK}:er={excludeRadius}:cd={colorDecayFactor}:ap={adjacencyPenaltyBase}:fmt={format}:q={quality}:png={pngCompression}";
            if (memoryCache.TryGetValue(cacheKey, out byte[] cached))
                return File(cached, GetMimeType(options.OutputFormat));

            HttpResponseMessage result;
            try
            {
                result = await httpClient.GetAsync(imageUrl);
            }
            catch (Exception ex)
            {
                return BadRequest(new { Message = $"Failed to fetch image: {ex.Message}", Success = false });
            }

            if (!result.IsSuccessStatusCode)
                return BadRequest(new { Message = $"Failed to fetch image: {result.StatusCode}", Success = false });

            var sourceBytes = await result.Content.ReadAsByteArrayAsync();

            byte[] mosaicBytes;
            try
            {
                mosaicBytes = await posterMosaicService.BuildPosterMosaicBytes(sourceBytes, options);
            }
            catch (InvalidOperationException ex)
            {
                return BadRequest(new { Message = ex.Message, Success = false });
            }

            memoryCache.Set(cacheKey, mosaicBytes, new MemoryCacheEntryOptions
            {
                SlidingExpiration = TimeSpan.FromHours(4),
                Size = mosaicBytes.Length,
            });

            return File(mosaicBytes, GetMimeType(options.OutputFormat));
        }

        private static MosaicOptions BuildMosaicOptions(
            double tileScale, double outputScale, int maxOutputDimension,
            int topK, int excludeRadius, double colorDecayFactor, double adjacencyPenaltyBase,
            string format, int quality, int pngCompression)
        {
            return new MosaicOptions
            {
                TileScale = tileScale,
                OutputScale = outputScale,
                MaxOutputDimension = maxOutputDimension,
                TopK = topK,
                ExcludeRadius = excludeRadius,
                ColorDecayFactor = colorDecayFactor,
                AdjacencyPenaltyBase = adjacencyPenaltyBase,
                OutputFormat = format?.ToLowerInvariant() switch
                {
                    "jpeg" or "jpg" => MosaicOutputFormat.Jpeg,
                    "webp" => MosaicOutputFormat.WebP,
                    _ => MosaicOutputFormat.Png
                },
                Quality = quality,
                PngCompressionLevel = pngCompression switch
                {
                    1 => PngCompressionLevel.Level1,
                    2 => PngCompressionLevel.Level2,
                    3 => PngCompressionLevel.Level3,
                    4 => PngCompressionLevel.Level4,
                    5 => PngCompressionLevel.Level5,
                    6 => PngCompressionLevel.Level6,
                    7 => PngCompressionLevel.Level7,
                    8 => PngCompressionLevel.Level8,
                    9 => PngCompressionLevel.Level9,
                    _ => PngCompressionLevel.DefaultCompression
                }
            };
        }
    }
}
