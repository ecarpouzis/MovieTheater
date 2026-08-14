using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using MovieTheater.Services.Omdb;
using MovieTheater.Services.Tmdb;

namespace MovieTheater.Services.Series
{
    /// <summary>
    /// Enumerates a show's seasons and episodes over PLAIN HTTP, so the review tool can build a full
    /// episode list from inside the API pod.
    ///
    /// <para>The existing <c>bootstrap-series-episodes</c> / <c>scrape-episodes</c> commands drive
    /// Playwright against IMDb's episode pages — correct, but they need a browser, which the pod does
    /// not have and should not grow. This reads the same facts from two JSON APIs the site already
    /// authenticates against and merges them: TMDB carries titles, plots, air dates, runtimes and
    /// still images; OMDB (<c>&amp;Season=</c>, i.e. IMDb's own data) carries each episode's IMDb id
    /// and IMDb rating, which TMDB has no equivalent for. Neither alone produces a card as complete as
    /// the hand-run scrape; together they do.</para>
    ///
    /// <para>Nothing here writes to the database or decides anything — it reports what the catalogues
    /// say. Reconciling that against what is on disk (the numbering disagreements) is the caller's
    /// job, deliberately, because a disagreement is a review decision and not a fetch error.</para>
    /// </summary>
    public class SeriesEpisodeCatalog
    {
        private readonly TmdbApi tmdb;
        private readonly OmdbApi omdb;
        private readonly ILogger<SeriesEpisodeCatalog> logger;

        public SeriesEpisodeCatalog(TmdbApi tmdb, OmdbApi omdb, ILogger<SeriesEpisodeCatalog> logger)
        {
            this.tmdb = tmdb;
            this.omdb = omdb;
            this.logger = logger;
        }

        /// <summary>One episode as the catalogues describe it. Every field but the numbers is optional —
        /// a sparse episode is still a real episode and must appear on the card.</summary>
        public sealed record CatalogEpisode(
            int Season, int Episode, string? Title, string? ImdbId, string? Plot,
            DateTime? AirDate, int? RuntimeMinutes, decimal? ImdbRating, string? StillPath);

        /// <summary>What seasons a show has, and the TMDB id to fetch them with (null = TMDB has no
        /// record and the OMDB-only path is in use).</summary>
        public sealed record SeasonPlan(int? TmdbTvId, IReadOnlyList<int> Seasons, string? Note);

        /// <summary>A show found by name in TMDB's TV index.</summary>
        public sealed record SeriesMatch(string ImdbId, int TmdbTvId, string? Name, int? FirstAirYear);

        /// <summary>
        /// Finds a SHOW by title, asking TMDB's TV index rather than a general title search.
        ///
        /// <para>This exists because a show and its films share a name and a shelf — the Muppets are
        /// the standing example — and a general lookup will often answer a series folder with the
        /// movie, which is both wrong and confidently wrong. Searching the TV index cannot make that
        /// mistake. A year narrows it when the folder carries one, but a miss on the year falls back
        /// to the unfiltered search rather than giving up, since folder years and first-air years
        /// disagree often enough (a show filed under its DVD year, say).</para>
        ///
        /// <para>Returns null when TMDB has no TV match or holds no IMDb id for the one it found —
        /// the caller then falls back to the general cascade.</para>
        /// </summary>
        public async Task<SeriesMatch?> FindSeriesByTitleAsync(string title, int? year)
        {
            if (string.IsNullOrWhiteSpace(title)) return null;
            try
            {
                var hits = year != null ? await tmdb.SearchTv(title, year) : new List<TmdbTvResultDto>();
                if (hits.Count == 0) hits = await tmdb.SearchTv(title);
                foreach (var hit in hits.Take(3))
                {
                    var tt = await tmdb.GetTvImdbId(hit.Id);
                    if (string.IsNullOrEmpty(tt)) continue;
                    int? airYear = null;
                    if (!string.IsNullOrEmpty(hit.FirstAirDate) && hit.FirstAirDate.Length >= 4
                        && int.TryParse(hit.FirstAirDate.Substring(0, 4), out var y)) airYear = y;
                    return new SeriesMatch(tt, hit.Id, hit.Name, airYear);
                }
            }
            catch (Exception ex) { logger.LogDebug(ex, "TMDB tv search failed for {Title}", title); }
            return null;
        }

        /// <summary>
        /// Discovers the show's season numbers. TMDB's own season list is the authority when it has the
        /// show; otherwise OMDB's <c>totalSeasons</c> is used. Season 0 (Specials) is included when the
        /// catalogue lists it — the library files those under "Specials" as S00Exx and they must land
        /// somewhere real rather than being dropped as an anomaly.
        /// </summary>
        public async Task<SeasonPlan> PlanAsync(string imdbId)
        {
            int? tvId = null;
            try
            {
                var tv = await tmdb.TryGetTvId(imdbId);
                tvId = tv?.Id;
            }
            catch (Exception ex) { logger.LogDebug(ex, "TMDB tv lookup failed for {Tt}", imdbId); }

            if (tvId != null)
            {
                try
                {
                    var detail = await tmdb.GetTvDetail(tvId.Value);
                    var seasons = (detail?.Seasons ?? new List<TmdbSeasonStub>())
                        .Where(s => s.EpisodeCount > 0 && s.SeasonNumber >= 0 && s.SeasonNumber <= 100)
                        .Select(s => s.SeasonNumber).Distinct().OrderBy(n => n).ToList();
                    if (seasons.Count > 0) return new SeasonPlan(tvId, seasons, null);
                }
                catch (Exception ex) { logger.LogDebug(ex, "TMDB tv detail failed for {Tt}", imdbId); }
            }

            // No TMDB record (or no seasons on it) — ask IMDb via OMDB how many seasons exist.
            try
            {
                var s1 = await omdb.GetSeason(imdbId, 1);
                if (s1 != null && int.TryParse(s1.TotalSeasons, out var total) && total > 0 && total <= 100)
                    return new SeasonPlan(tvId, Enumerable.Range(1, total).ToList(),
                        tvId == null ? "TMDB has no record of this show — episode detail comes from IMDb only." : null);
                if (s1?.Episodes?.Count > 0)
                    return new SeasonPlan(tvId, new List<int> { 1 }, "Only one season could be confirmed.");
            }
            catch (Exception ex) { logger.LogDebug(ex, "OMDB season probe failed for {Tt}", imdbId); }

            return new SeasonPlan(tvId, Array.Empty<int>(), "Neither TMDB nor IMDb returned a season list.");
        }

        /// <summary>
        /// One season's episodes, merged from both catalogues. The union of episode numbers is used, so
        /// an episode only one source knows about is still returned — never the intersection, which
        /// would quietly shorten a season.
        /// </summary>
        public async Task<List<CatalogEpisode>> FetchSeasonAsync(string imdbId, int? tmdbTvId, int season)
        {
            var byTmdb = new Dictionary<int, TmdbEpisodeDto>();
            if (tmdbTvId != null)
            {
                try
                {
                    var detail = await tmdb.GetTvSeason(tmdbTvId.Value, season);
                    foreach (var e in detail?.Episodes ?? new List<TmdbEpisodeDto>())
                        if (e.EpisodeNumber > 0) byTmdb[e.EpisodeNumber] = e;
                }
                catch (Exception ex) { logger.LogDebug(ex, "TMDB season {S} failed for {Tt}", season, imdbId); }
            }

            var byOmdb = new Dictionary<int, OmdbSeasonEpisode>();
            try
            {
                foreach (var e in await omdb.GetSeasonEpisodes(imdbId, season))
                    if (int.TryParse(e.Episode, out var n) && n > 0) byOmdb[n] = e;
            }
            catch (Exception ex) { logger.LogDebug(ex, "OMDB season {S} failed for {Tt}", season, imdbId); }

            var numbers = byTmdb.Keys.Concat(byOmdb.Keys).Distinct().OrderBy(n => n).ToList();
            var result = new List<CatalogEpisode>(numbers.Count);
            foreach (var n in numbers)
            {
                byTmdb.TryGetValue(n, out var t);
                byOmdb.TryGetValue(n, out var o);
                result.Add(new CatalogEpisode(
                    Season: season,
                    Episode: n,
                    // TMDB's titles are cleaner; IMDb's is the fallback. "Episode 7"-style placeholders
                    // are kept — a numbered episode really is how some shows are catalogued.
                    Title: FirstNonEmpty(t?.Name, o?.Title),
                    ImdbId: o?.ImdbID,
                    Plot: FirstNonEmpty(t?.Overview),
                    AirDate: ParseDate(t?.AirDate) ?? ParseDate(o?.Released),
                    RuntimeMinutes: t?.Runtime > 0 ? t.Runtime : null,
                    ImdbRating: ParseRating(o?.ImdbRating),
                    StillPath: FirstNonEmpty(t?.StillPath)));
            }
            return result;
        }

        private static string? FirstNonEmpty(params string?[] values) =>
            values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v) && !v.Trim().Equals("N/A", StringComparison.OrdinalIgnoreCase))?.Trim();

        private static DateTime? ParseDate(string? s)
        {
            var v = FirstNonEmpty(s);
            if (v == null) return null;
            // TMDB gives "1992-09-14"; OMDB gives "14 Sep 1992".
            return DateTime.TryParse(v, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;
        }

        private static decimal? ParseRating(string? s)
        {
            var v = FirstNonEmpty(s);
            return v != null && decimal.TryParse(v, NumberStyles.Any, CultureInfo.InvariantCulture, out var d) && d > 0 ? d : null;
        }
    }
}
