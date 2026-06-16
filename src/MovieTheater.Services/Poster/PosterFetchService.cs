using System;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using MovieTheater.Db;
using MovieTheater.Services.ImdbApi;
using MovieTheater.Services.Omdb;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace MovieTheater.Services.Poster
{
    /// <summary>
    /// Resolves a poster for a title by id and persists it (image file on disk keyed by id, a thumbnail,
    /// the dominant colour, and the PosterDetails row). The URL is resolved from OMDB (which serves the
    /// IMDb poster) then the IMDb API — never TMDB — so it matches what the rest of the title's data came
    /// from. Used at approval time (manual + auto) and by the poster backfill, so an approved title always
    /// has a poster without a manual step.
    /// </summary>
    public class PosterFetchService
    {
        private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

        private readonly IDbContextFactory<MovieDb> dbFactory;
        private readonly IPosterImageRepository imageRepo;
        private readonly ImageShrinkService shrink;
        private readonly OmdbApi omdb;
        private readonly ImdbApiClient imdb;
        private readonly ILogger<PosterFetchService> logger;

        public PosterFetchService(IDbContextFactory<MovieDb> dbFactory, IPosterImageRepository imageRepo,
            ImageShrinkService shrink, OmdbApi omdb, ImdbApiClient imdb, ILogger<PosterFetchService> logger)
        {
            this.dbFactory = dbFactory;
            this.imageRepo = imageRepo;
            this.shrink = shrink;
            this.omdb = omdb;
            this.imdb = imdb;
            this.logger = logger;
        }

        /// <summary>
        /// Ensure a poster exists for a movie/series id. No-op (returns true) if one is already on disk,
        /// unless <paramref name="force"/>. Returns false when no poster could be resolved/fetched.
        /// </summary>
        public async Task<bool> EnsurePosterAsync(int id, string? imdbID, bool isSeries, bool force = false)
        {
            try
            {
                if (!force && await imageRepo.HasImage(id, PosterImageVariant.Main)) return true;
                var url = await ResolvePosterUrlAsync(imdbID);
                if (url == null) return false;
                await SaveFromUrlAsync(id, url, isSeries);
                return true;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Poster fetch failed for id {Id} ({Tt})", id, imdbID);
                return false;
            }
        }

        /// <summary>OMDB's poster (the IMDb image) first, then the IMDb API. Null when neither has one.</summary>
        public async Task<string?> ResolvePosterUrlAsync(string? imdbID)
        {
            if (string.IsNullOrWhiteSpace(imdbID)) return null;
            try { var m = await omdb.GetMovieByImdbId(imdbID); if (IsUsable(m?.PosterLink)) return m!.PosterLink; } catch { }
            try { var m = await imdb.ImdbApiLookupImdbID(imdbID); if (IsUsable(m?.PosterLink)) return m!.PosterLink; } catch { }
            return null;
        }

        private static bool IsUsable(string? url) =>
            !string.IsNullOrWhiteSpace(url) && !url.Equals("N/A", StringComparison.OrdinalIgnoreCase) && url.StartsWith("http");

        private async Task SaveFromUrlAsync(int id, string url, bool isSeries)
        {
            var bytes = await Http.GetByteArrayAsync(url);
            await imageRepo.SaveImage(id, PosterImageVariant.Main, bytes);
            try { await shrink.EnsurePosterThumnailExists(id, force: true); }
            catch (Exception ex) { logger.LogWarning(ex, "Thumbnail generation failed for id {Id}", id); }

            string? color = null;
            try { var thumb = await imageRepo.GetImage(id, PosterImageVariant.Thumbnail); color = ComputeAverageColor(thumb ?? bytes); }
            catch { /* colour is a nice-to-have */ }

            await using var db = await dbFactory.CreateDbContextAsync();
            if (isSeries)
            {
                var pd = await db.SeriesPosterDetails.FindAsync(id);
                if (pd == null) db.SeriesPosterDetails.Add(new SeriesPosterDetails { SeriesId = id, PosterLink = url, PosterVersion = 1, DominantColor = color });
                else { pd.PosterLink = url; pd.PosterVersion++; pd.DominantColor = color ?? pd.DominantColor; }
            }
            else
            {
                var pd = await db.MoviePosterDetails.FindAsync(id);
                if (pd == null) db.MoviePosterDetails.Add(new MoviePosterDetails { MovieId = id, PosterLink = url, PosterVersion = 1, DominantColor = color });
                else { pd.PosterLink = url; pd.PosterVersion++; pd.DominantColor = color ?? pd.DominantColor; }
            }
            await db.SaveChangesAsync();
        }

        private static string? ComputeAverageColor(byte[] imageBytes)
        {
            using var image = Image.Load<Rgba32>(imageBytes);
            long r = 0, g = 0, b = 0, n = 0;
            image.ProcessPixelRows(accessor =>
            {
                for (int y = 0; y < accessor.Height; y++)
                {
                    var row = accessor.GetRowSpan(y);
                    for (int x = 0; x < row.Length; x++)
                    {
                        var p = row[x];
                        if (p.A < 128) continue;
                        r += p.R; g += p.G; b += p.B; n++;
                    }
                }
            });
            return n == 0 ? null : $"#{r / n:X2}{g / n:X2}{b / n:X2}";
        }
    }
}
