using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using MovieTheater.Db;

namespace MovieTheater.Normalization
{
    /// <summary>
    /// Parses a movie's legacy comma-separated text fields (Genre, Actors, Director, Writer,
    /// Runtime, Plot, Rating) into the normalized columns and FK tables. This is the PRIMARY
    /// path for inserts/edits (API + manual text); the IMDB scrape is the richer fallback that
    /// later overwrites with nm-keyed cast, characters, and summaries. People parsed here are
    /// name-only (no nm) and get unified with the real IMDB person when the scrape runs.
    /// </summary>
    public static class MovieNormalizer
    {
        private static IEnumerable<string> SplitCsv(string s) =>
            string.IsNullOrWhiteSpace(s)
                ? Enumerable.Empty<string>()
                : s.Split(',').Select(x => x.Trim()).Where(x => x.Length > 0);

        // ── scalar fields ──────────────────────────────────────────────────
        public static void ApplyRuntime(Movie m)
        {
            var min = RuntimeParser.ToMinutes(m.Runtime);
            if (min.HasValue) m.RuntimeMinutes = min;
        }

        public static void ApplyPlot(Movie m)
        {
            if (!string.IsNullOrWhiteSpace(m.Plot)) m.PlotFull = m.Plot.Trim();
        }

        public static void ApplyRating(Movie m)
        {
            if (!string.IsNullOrWhiteSpace(m.Rating)) m.MpaaRating = m.Rating.Trim();
        }

        // ── genre / credit tables ──────────────────────────────────────────
        public static async Task ReplaceGenresAsync(MovieDb db, int movieId, string genreCsv)
        {
            var existing = await db.MovieGenres.Where(g => g.MovieID == movieId).ToListAsync();
            if (existing.Count > 0) { db.MovieGenres.RemoveRange(existing); await db.SaveChangesAsync(); }

            int i = 0;
            foreach (var name in SplitCsv(genreCsv))
            {
                var genre = db.Genres.Local.FirstOrDefault(g => g.Name == name)
                            ?? await db.Genres.FirstOrDefaultAsync(g => g.Name == name);
                if (genre == null) { genre = new Genre { Name = name }; db.Genres.Add(genre); }
                db.MovieGenres.Add(new MovieGenre { MovieID = movieId, Genre = genre, Ordering = i++ });
            }
        }

        /// <summary>Replaces only the given role's credits for the movie from a CSV of names
        /// (other roles' credits are left untouched).</summary>
        public static async Task ReplaceRoleCreditsAsync(MovieDb db, int movieId, CreditRole role, string csv)
        {
            var existing = await db.MovieCredits.Where(c => c.MovieID == movieId && c.Role == role).ToListAsync();
            if (existing.Count > 0) { db.MovieCredits.RemoveRange(existing); await db.SaveChangesAsync(); }

            int i = 0;
            var seen = new HashSet<string>();
            foreach (var name in SplitCsv(csv))
            {
                var key = PersonResolver.ComputeNameKey(name);
                if (key == null || !seen.Add(key)) continue;
                var person = await PersonResolver.ResolveAsync(db, null, name);
                if (person == null) continue;
                db.MovieCredits.Add(new MovieCredit { MovieID = movieId, Person = person, Role = role, Ordering = i++ });
            }
        }

        /// <summary>Full parse of every legacy text field — used for a fresh insert.</summary>
        public static async Task ApplyAllAsync(MovieDb db, Movie m)
        {
            ApplyRuntime(m);
            ApplyPlot(m);
            ApplyRating(m);
            await ReplaceGenresAsync(db, m.id, m.Genre);
            await ReplaceRoleCreditsAsync(db, m.id, CreditRole.Director, m.Director);
            await ReplaceRoleCreditsAsync(db, m.id, CreditRole.Writer, m.Writer);
            await ReplaceRoleCreditsAsync(db, m.id, CreditRole.Actor, m.Actors);
            await db.SaveChangesAsync();
        }
    }
}
