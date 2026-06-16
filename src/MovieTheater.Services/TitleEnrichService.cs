using System;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using MovieTheater.Db;
using MovieTheater.Services.ImdbApi;
using MovieTheater.Services.Omdb;
using MovieTheater.Services.Poster;

namespace MovieTheater.Services
{
    /// <summary>
    /// Light, on-demand enrichment of a single Movie or Series from IMDb data (via OMDB, with the IMDb API
    /// as fallback — never TMDB). Fills the scraped/normalized columns the cards + modal read
    /// (ImdbScrapedTitle / ImdbReleaseDate / ImdbRatingScraped / MpaaRating / RuntimeMinutes / Plot* + the
    /// legacy comma-separated fallbacks) and fetches a poster, then stamps ImdbVerifiedDate so it counts as
    /// enriched / resumable. This is the fast path used by the "Re-fetch" button and the bulk enrich command;
    /// the heavyweight Playwright `scrape-imdb` (nm-linked cast, plot summaries, genre FK rows) stays separate.
    /// </summary>
    public class TitleEnrichService
    {
        private readonly IDbContextFactory<MovieDb> dbFactory;
        private readonly OmdbApi omdb;
        private readonly ImdbApiClient imdb;
        private readonly PosterFetchService posters;
        private readonly ILogger<TitleEnrichService> logger;

        public TitleEnrichService(IDbContextFactory<MovieDb> dbFactory, OmdbApi omdb, ImdbApiClient imdb,
            PosterFetchService posters, ILogger<TitleEnrichService> logger)
        {
            this.dbFactory = dbFactory;
            this.omdb = omdb;
            this.imdb = imdb;
            this.posters = posters;
            this.logger = logger;
        }

        public async Task<bool> EnrichAsync(int id, bool isSeries, bool force = false)
        {
            try
            {
                await using var db = await dbFactory.CreateDbContextAsync();
                string tt;
                bool alreadyVerified;
                if (isSeries)
                {
                    var s = await db.Series.FirstOrDefaultAsync(x => x.Id == id);
                    if (s == null || string.IsNullOrWhiteSpace(s.imdbID)) return false;
                    tt = s.imdbID; alreadyVerified = s.ImdbVerifiedDate != null;
                }
                else
                {
                    var m = await db.Movies.FirstOrDefaultAsync(x => x.id == id);
                    if (m == null || string.IsNullOrWhiteSpace(m.imdbID)) return false;
                    tt = m.imdbID; alreadyVerified = m.ImdbVerifiedDate != null;
                }
                if (alreadyVerified && !force) return true;

                var o = await omdb.GetMovieByImdbId(tt);
                if (o == null || string.IsNullOrWhiteSpace(o.Title))
                {
                    try { o = await imdb.ImdbApiLookupImdbID(tt); } catch { }
                }
                if (o == null || string.IsNullOrWhiteSpace(o.Title)) return false;

                if (isSeries)
                {
                    var s = await db.Series.FirstOrDefaultAsync(x => x.Id == id);
                    if (s == null) return false;
                    s.ImdbScrapedTitle = o.Title;
                    if (o.ReleaseDate.HasValue && o.ReleaseDate.Value != default) { s.ImdbReleaseDate = o.ReleaseDate; s.StartYear ??= o.ReleaseDate.Value.Year; }
                    if (o.imdbRating != null) s.ImdbRatingScraped = o.imdbRating;
                    if (!string.IsNullOrWhiteSpace(o.Rating)) s.MpaaRating = o.Rating;
                    var rm = RuntimeToMinutes(o.Runtime); if (rm != null) s.RuntimeMinutes = rm;
                    if (!string.IsNullOrWhiteSpace(o.Plot)) { s.PlotFull = o.Plot; if (string.IsNullOrWhiteSpace(s.Plot)) s.Plot = o.Plot; }
                    if (string.IsNullOrWhiteSpace(s.Genre)) s.Genre = o.Genre;
                    if (string.IsNullOrWhiteSpace(s.Actors)) s.Actors = o.Actors;
                    if (string.IsNullOrWhiteSpace(s.Director)) s.Director = o.Director;
                    if (string.IsNullOrWhiteSpace(s.Writer)) s.Writer = o.Writer;
                    s.ImdbVerifiedDate = DateTime.UtcNow;
                    await db.SaveChangesAsync();
                }
                else
                {
                    var m = await db.Movies.FirstOrDefaultAsync(x => x.id == id);
                    if (m == null) return false;
                    m.ImdbScrapedTitle = o.Title;
                    if (o.ReleaseDate.HasValue && o.ReleaseDate.Value != default) m.ImdbReleaseDate = o.ReleaseDate;
                    if (o.imdbRating != null) m.ImdbRatingScraped = o.imdbRating;
                    if (!string.IsNullOrWhiteSpace(o.Rating)) m.MpaaRating = o.Rating;
                    var rm = RuntimeToMinutes(o.Runtime); if (rm != null) m.RuntimeMinutes = rm;
                    if (!string.IsNullOrWhiteSpace(o.Plot)) { m.PlotFull = o.Plot; if (string.IsNullOrWhiteSpace(m.Plot)) m.Plot = o.Plot; }
                    if (string.IsNullOrWhiteSpace(m.Genre)) m.Genre = o.Genre;
                    if (string.IsNullOrWhiteSpace(m.Actors)) m.Actors = o.Actors;
                    if (string.IsNullOrWhiteSpace(m.Director)) m.Director = o.Director;
                    if (string.IsNullOrWhiteSpace(m.Writer)) m.Writer = o.Writer;
                    m.ImdbVerifiedDate = DateTime.UtcNow;
                    await db.SaveChangesAsync();
                }

                // Poster (no-op if one already exists, unless forced).
                if (!string.IsNullOrWhiteSpace(o.PosterLink) && o.PosterLink.StartsWith("http"))
                    await posters.EnsurePosterAsync(id, tt, isSeries, force);

                return true;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Enrich failed for id {Id} (series={IsSeries})", id, isSeries);
                return false;
            }
        }

        private static int? RuntimeToMinutes(string runtime)
        {
            if (string.IsNullOrWhiteSpace(runtime)) return null;
            var m = Regex.Match(runtime, @"\d+");
            return m.Success && int.TryParse(m.Value, out var n) && n > 0 ? n : (int?)null;
        }
    }
}
