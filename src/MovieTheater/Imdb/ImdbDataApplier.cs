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
    /// are never overwritten; on a clear id mismatch the row is flagged for review instead.
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

            if (!TitlesPlausiblyMatch(movie, result))
            {
                movie.ImdbNeedsReview = true;
                movie.ImdbReviewReason =
                    $"Title mismatch: ours='{movie.Title}' imdb='{result.Title}' " +
                    $"(year ours={movie.ReleaseDate?.Year}, imdb={result.Year}).";
                await db.SaveChangesAsync();
                return ImdbApplyStatus.Flagged;
            }

            movie.ImdbNeedsReview = false;
            movie.ImdbReviewReason = null;
            if (result.RuntimeMinutes.HasValue) movie.RuntimeMinutes = result.RuntimeMinutes;
            if (!string.IsNullOrWhiteSpace(result.Plot)) movie.PlotFull = result.Plot;
            if (!string.IsNullOrWhiteSpace(result.Synopsis)) movie.PlotSynopsis = result.Synopsis;
            if (!string.IsNullOrWhiteSpace(result.MpaaRating)) movie.MpaaRating = result.MpaaRating;
            if (result.ReleaseDate.HasValue) movie.ImdbReleaseDate = result.ReleaseDate;
            if (result.ImdbRating.HasValue) movie.ImdbRatingScraped = result.ImdbRating;

            await ReplaceGenresAsync(db, movie.id, result.Genres);
            await ReplaceCreditsAsync(db, movie.id, result);
            await ReplacePlotSummariesAsync(db, movie.id, result.Summaries);

            await db.SaveChangesAsync();
            return ImdbApplyStatus.Updated;
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

        // Lenient match: normalized (article/punctuation-insensitive) titles must agree, OR the
        // release years must be within one. Avoids false flags from our odd "Title, The"
        // formatting while still catching genuinely wrong ids.
        private static bool TitlesPlausiblyMatch(Movie movie, ImdbScrapeResult result)
        {
            var ours = Normalize(movie.Title);
            var theirs = Normalize(result.Title);
            if (!string.IsNullOrEmpty(ours) && !string.IsNullOrEmpty(theirs) &&
                (ours == theirs || ours.Contains(theirs) || theirs.Contains(ours)))
                return true;

            int? ourYear = movie.ReleaseDate?.Year;
            if (ourYear.HasValue && result.Year.HasValue && Math.Abs(ourYear.Value - result.Year.Value) <= 1)
                return true;

            return false;
        }

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
