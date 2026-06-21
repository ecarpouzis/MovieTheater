using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using MovieTheater.Db;
using MovieTheater.Normalization;

namespace MovieTheater.Imdb
{
    public enum ImdbApplyStatus { Updated, Flagged, NotFound }

    /// <summary>
    /// Writes an <see cref="ImdbScrapeResult"/> into the normalized columns and FK tables for
    /// a single (already-tracked) <see cref="Movie"/>. Shared by the bulk scrape CLI command
    /// and the movie-insert endpoint so both produce identical normalized data. Legacy columns
    /// are never overwritten; on a title mismatch the scraped detail is still written but the row
    /// is also flagged for review (a populated row to confirm/edit beats a silent blank).
    /// </summary>
    public static class ImdbDataApplier
    {
        public static async Task<ImdbApplyStatus> ApplyAsync(MovieDb db, Movie movie, ImdbScrapeResult result)
        {
            movie.ImdbVerifiedDate = DateTime.Now;
            movie.ImdbScrapedTitle = result.Title;

            if (!result.Found)
            {
                movie.ImdbNeedsReview = true;
                movie.ImdbReviewReason = result.FailureReason ?? "IMDB id did not resolve.";
                await db.SaveChangesAsync();
                return ImdbApplyStatus.NotFound;
            }

            // Classify the title (Phase C1). titleType is a fact about the IMDB id itself, so set it
            // even on a title mismatch below — that keeps the --retype resume from re-pulling flagged
            // rows forever. tvEpisode has no Movie-side enum value (episodes live in their own table),
            // so such rows stay Unknown rather than being mislabeled as movies.
            var titleType = MapTitleType(result.TitleTypeId);
            if (titleType != TitleType.Unknown) movie.TitleType = titleType;

            // A title mismatch flags the row for a human to confirm the id — but it must NOT
            // discard the scraped data. The whole point of caching/scraping IMDb is that the
            // reviewer opens a *populated* row to sanity-check and hand-edit; a genuinely wrong
            // id is rarer than our own odd "Title, The" / foreign / variant-recut titles, and the
            // review flag (not a silent skip) is the safeguard. So we always write the detail
            // below — only the flag differs — and approval never re-fetches, so hand-edits stick.
            bool mismatch = !TitlesPlausiblyMatch(movie.Title, movie.ReleaseDate?.Year, result);
            if (mismatch)
            {
                movie.ImdbNeedsReview = true;
                movie.ImdbReviewReason =
                    $"Title mismatch: ours='{movie.Title}' imdb='{result.Title}' " +
                    $"(year ours={movie.ReleaseDate?.Year}, imdb={result.Year}).";
            }
            else
            {
                movie.ImdbNeedsReview = false;
                movie.ImdbReviewReason = null;
            }

            if (result.RuntimeMinutes.HasValue) movie.RuntimeMinutes = result.RuntimeMinutes;
            if (!string.IsNullOrWhiteSpace(result.Plot)) movie.PlotFull = result.Plot;
            if (!string.IsNullOrWhiteSpace(result.Synopsis)) movie.PlotSynopsis = result.Synopsis;
            if (!string.IsNullOrWhiteSpace(result.MpaaRating)) movie.MpaaRating = result.MpaaRating;
            if (result.ReleaseDate.HasValue) movie.ImdbReleaseDate = result.ReleaseDate;
            if (result.ImdbRating.HasValue) movie.ImdbRatingScraped = result.ImdbRating;

            movie.TopCast = CreditFormatting.TopCast(result.Actors.Select(a => a.DisplayName));

            await ReplaceGenresAsync(db, movie.id, result.Genres);
            await ReplaceCreditsAsync(db, movie.id, result);
            await ReplacePlotSummariesAsync(db, movie.id, result.Summaries);

            await db.SaveChangesAsync();
            return mismatch ? ImdbApplyStatus.Flagged : ImdbApplyStatus.Updated;
        }

        /// <summary>
        /// Series peer of <see cref="ApplyAsync(MovieDb, Movie, ImdbScrapeResult)"/>. Writes the same
        /// normalized columns + FK graph (genres/credits/plot summaries) onto a <see cref="Series"/>,
        /// plus the series-only StartYear/EndYear aggregates. Episode pages are cached/parsed separately
        /// (scrape-episodes); this fills the series' own title-level data.
        /// </summary>
        public static async Task<ImdbApplyStatus> ApplyAsync(MovieDb db, Series series, ImdbScrapeResult result)
        {
            series.ImdbVerifiedDate = DateTime.Now;
            series.ImdbScrapedTitle = result.Title;

            if (!result.Found)
            {
                series.ImdbNeedsReview = true;
                series.ImdbReviewReason = result.FailureReason ?? "IMDB id did not resolve.";
                await db.SaveChangesAsync();
                return ImdbApplyStatus.NotFound;
            }

            // Only adopt a series-shaped titleType; if IMDB classifies the id as a movie/short the
            // ids almost certainly disagree, which the mismatch flag below surfaces for review.
            var titleType = MapTitleType(result.TitleTypeId);
            if (titleType is TitleType.TvSeries or TitleType.TvMiniSeries or TitleType.TvSpecial)
                series.TitleType = titleType;

            bool mismatch = !TitlesPlausiblyMatch(series.Title, series.StartYear ?? series.ReleaseDate?.Year, result);
            if (mismatch)
            {
                series.ImdbNeedsReview = true;
                series.ImdbReviewReason =
                    $"Title mismatch: ours='{series.Title}' imdb='{result.Title}' " +
                    $"(year ours={series.StartYear ?? series.ReleaseDate?.Year}, imdb={result.Year}).";
            }
            else
            {
                series.ImdbNeedsReview = false;
                series.ImdbReviewReason = null;
            }

            if (result.RuntimeMinutes.HasValue) series.RuntimeMinutes = result.RuntimeMinutes;
            if (!string.IsNullOrWhiteSpace(result.Plot)) series.PlotFull = result.Plot;
            if (!string.IsNullOrWhiteSpace(result.Synopsis)) series.PlotSynopsis = result.Synopsis;
            if (!string.IsNullOrWhiteSpace(result.MpaaRating)) series.MpaaRating = result.MpaaRating;
            if (result.ReleaseDate.HasValue) series.ImdbReleaseDate = result.ReleaseDate;
            if (result.Year.HasValue) series.StartYear = result.Year;
            if (result.EndYear.HasValue) series.EndYear = result.EndYear;
            if (result.ImdbRating.HasValue) series.ImdbRatingScraped = result.ImdbRating;

            series.TopCast = CreditFormatting.TopCast(result.Actors.Select(a => a.DisplayName));

            await ReplaceSeriesGenresAsync(db, series.Id, result.Genres);
            await ReplaceSeriesCreditsAsync(db, series.Id, result);
            await ReplaceSeriesPlotSummariesAsync(db, series.Id, result.Summaries);

            await db.SaveChangesAsync();
            return mismatch ? ImdbApplyStatus.Flagged : ImdbApplyStatus.Updated;
        }

        private static async Task ReplaceGenresAsync(MovieDb db, int movieId, List<string> genres)
        {
            var existing = await db.MovieGenres.Where(g => g.MovieID == movieId).ToListAsync();
            if (existing.Count > 0) { db.MovieGenres.RemoveRange(existing); await db.SaveChangesAsync(); }

            for (int i = 0; i < genres.Count; i++)
            {
                var name = genres[i];
                if (string.IsNullOrWhiteSpace(name)) continue;
                var genre = await db.Genres.FirstOrDefaultAsync(g => g.Name == name)
                            ?? db.Genres.Local.FirstOrDefault(g => g.Name == name);
                if (genre == null) { genre = new Genre { Name = name }; db.Genres.Add(genre); }
                db.MovieGenres.Add(new MovieGenre { MovieID = movieId, Genre = genre, Ordering = i });
            }
        }

        private static async Task ReplacePlotSummariesAsync(MovieDb db, int movieId, List<ScrapedSummary> summaries)
        {
            var existing = await db.MoviePlotSummaries.Where(s => s.MovieID == movieId).ToListAsync();
            if (existing.Count > 0) { db.MoviePlotSummaries.RemoveRange(existing); await db.SaveChangesAsync(); }

            for (int i = 0; i < summaries.Count; i++)
            {
                var s = summaries[i];
                if (string.IsNullOrWhiteSpace(s.Text)) continue;
                db.MoviePlotSummaries.Add(new MoviePlotSummary
                {
                    MovieID = movieId,
                    Ordering = i,
                    Author = s.Author,
                    Text = s.Text
                });
            }
        }

        private static async Task ReplaceCreditsAsync(MovieDb db, int movieId, ImdbScrapeResult result)
        {
            var existing = await db.MovieCredits.Where(c => c.MovieID == movieId).ToListAsync();
            if (existing.Count > 0) { db.MovieCredits.RemoveRange(existing); await db.SaveChangesAsync(); }

            var added = new HashSet<(string, CreditRole)>();
            await AddCreditsAsync(db, movieId, result.Directors, CreditRole.Director, added);
            await AddCreditsAsync(db, movieId, result.Writers, CreditRole.Writer, added);
            await AddCreditsAsync(db, movieId, result.Actors, CreditRole.Actor, added);
        }

        private static async Task AddCreditsAsync(MovieDb db, int movieId, List<ScrapedPerson> people,
            CreditRole role, HashSet<(string, CreditRole)> added)
        {
            for (int i = 0; i < people.Count; i++)
            {
                var p = people[i];
                var dedupKey = string.IsNullOrWhiteSpace(p.ImdbNameId)
                    ? PersonResolver.ComputeNameKey(p.DisplayName)
                    : p.ImdbNameId.Trim();
                if (string.IsNullOrWhiteSpace(dedupKey)) continue;
                if (!added.Add((dedupKey, role))) continue;

                var person = await PersonResolver.ResolveAsync(db, p.ImdbNameId, p.DisplayName);
                if (person == null) continue;

                db.MovieCredits.Add(new MovieCredit
                {
                    MovieID = movieId,
                    Person = person,
                    Role = role,
                    Ordering = i,
                    Character = role == CreditRole.Actor ? p.Character : null
                });
            }
        }

        // ── Series FK-table replacers (peers of the Movie* ones above) ──────

        private static async Task ReplaceSeriesGenresAsync(MovieDb db, int seriesId, List<string> genres)
        {
            var existing = await db.SeriesGenres.Where(g => g.SeriesId == seriesId).ToListAsync();
            if (existing.Count > 0) { db.SeriesGenres.RemoveRange(existing); await db.SaveChangesAsync(); }

            for (int i = 0; i < genres.Count; i++)
            {
                var name = genres[i];
                if (string.IsNullOrWhiteSpace(name)) continue;
                var genre = await db.Genres.FirstOrDefaultAsync(g => g.Name == name)
                            ?? db.Genres.Local.FirstOrDefault(g => g.Name == name);
                if (genre == null) { genre = new Genre { Name = name }; db.Genres.Add(genre); }
                db.SeriesGenres.Add(new SeriesGenre { SeriesId = seriesId, Genre = genre, Ordering = i });
            }
        }

        private static async Task ReplaceSeriesPlotSummariesAsync(MovieDb db, int seriesId, List<ScrapedSummary> summaries)
        {
            var existing = await db.SeriesPlotSummaries.Where(s => s.SeriesId == seriesId).ToListAsync();
            if (existing.Count > 0) { db.SeriesPlotSummaries.RemoveRange(existing); await db.SaveChangesAsync(); }

            for (int i = 0; i < summaries.Count; i++)
            {
                var s = summaries[i];
                if (string.IsNullOrWhiteSpace(s.Text)) continue;
                db.SeriesPlotSummaries.Add(new SeriesPlotSummary
                {
                    SeriesId = seriesId,
                    Ordering = i,
                    Author = s.Author,
                    Text = s.Text
                });
            }
        }

        private static async Task ReplaceSeriesCreditsAsync(MovieDb db, int seriesId, ImdbScrapeResult result)
        {
            var existing = await db.SeriesCredits.Where(c => c.SeriesId == seriesId).ToListAsync();
            if (existing.Count > 0) { db.SeriesCredits.RemoveRange(existing); await db.SaveChangesAsync(); }

            var added = new HashSet<(string, CreditRole)>();
            await AddSeriesCreditsAsync(db, seriesId, result.Directors, CreditRole.Director, added);
            await AddSeriesCreditsAsync(db, seriesId, result.Writers, CreditRole.Writer, added);
            await AddSeriesCreditsAsync(db, seriesId, result.Actors, CreditRole.Actor, added);
        }

        private static async Task AddSeriesCreditsAsync(MovieDb db, int seriesId, List<ScrapedPerson> people,
            CreditRole role, HashSet<(string, CreditRole)> added)
        {
            for (int i = 0; i < people.Count; i++)
            {
                var p = people[i];
                var dedupKey = string.IsNullOrWhiteSpace(p.ImdbNameId)
                    ? PersonResolver.ComputeNameKey(p.DisplayName)
                    : p.ImdbNameId.Trim();
                if (string.IsNullOrWhiteSpace(dedupKey)) continue;
                if (!added.Add((dedupKey, role))) continue;

                var person = await PersonResolver.ResolveAsync(db, p.ImdbNameId, p.DisplayName);
                if (person == null) continue;

                db.SeriesCredits.Add(new SeriesCredit
                {
                    SeriesId = seriesId,
                    Person = person,
                    Role = role,
                    Ordering = i,
                    Character = role == CreditRole.Actor ? p.Character : null
                });
            }
        }

        // Lenient match: normalized (article/punctuation-insensitive) titles must agree, OR the
        // release years must be within one. Avoids false flags from our odd "Title, The"
        // formatting while still catching genuinely wrong ids.
        private static bool TitlesPlausiblyMatch(string ourTitle, int? ourYear, ImdbScrapeResult result)
        {
            var ours = Normalize(ourTitle);
            var theirs = Normalize(result.Title);
            if (!string.IsNullOrEmpty(ours) && !string.IsNullOrEmpty(theirs) &&
                (ours == theirs || ours.Contains(theirs) || theirs.Contains(ours)))
                return true;

            if (ourYear.HasValue && result.Year.HasValue && Math.Abs(ourYear.Value - result.Year.Value) <= 1)
                return true;

            return false;
        }

        // Map IMDB's titleType id to our enum. tvEpisode is intentionally absent (→ Unknown):
        // episodes live in their own table, not as Movie rows.
        private static TitleType MapTitleType(string imdbTitleTypeId) => imdbTitleTypeId switch
        {
            "movie" => TitleType.Movie,
            "tvMovie" => TitleType.TvMovie,
            "short" => TitleType.Short,
            "tvShort" => TitleType.TvShort,
            "tvSeries" => TitleType.TvSeries,
            "tvMiniSeries" => TitleType.TvMiniSeries,
            "tvSpecial" => TitleType.TvSpecial,
            "video" => TitleType.Video,
            _ => TitleType.Unknown,
        };

        private static string Normalize(string title)
        {
            if (string.IsNullOrWhiteSpace(title)) return "";
            var t = title.ToLowerInvariant();
            t = Regex.Replace(t, @"\(\s*\d{4}.*?\)", " ");      // drop trailing (year)
            t = Regex.Replace(t, @",\s*the\b", " ");             // "matrix, the" -> "matrix"
            t = Regex.Replace(t, @"^\s*the\b", " ");             // "the matrix" -> "matrix"
            t = Regex.Replace(t, @"[^a-z0-9]", "");              // strip punctuation/space
            return t;
        }
    }
}
