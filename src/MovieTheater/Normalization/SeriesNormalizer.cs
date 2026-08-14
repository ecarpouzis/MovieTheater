using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using MovieTheater.Db;

namespace MovieTheater.Normalization
{
    /// <summary>
    /// The <see cref="MovieNormalizer"/> half for <see cref="Series"/>: parses the same legacy
    /// comma-separated text fields (Genre, Actors, Director, Writer, Runtime, Plot, Rating) into the
    /// normalized columns and the Series* FK tables.
    ///
    /// <para>It is a separate type rather than a generic one because the destinations are different
    /// tables with their own entity types (<see cref="SeriesGenre"/>, <see cref="SeriesCredit"/>), and
    /// the movie path is the hot, well-exercised one — making it generic to save fifty lines would put
    /// every insert in the site through a reflection layer to serve the rarer case.</para>
    /// </summary>
    public static class SeriesNormalizer
    {
        private static IEnumerable<string> SplitCsv(string? s) =>
            string.IsNullOrWhiteSpace(s)
                ? Enumerable.Empty<string>()
                : s.Split(',').Select(x => x.Trim()).Where(x => x.Length > 0);

        public static void ApplyScalars(Series s)
        {
            var min = RuntimeParser.ToMinutes(s.Runtime);
            if (min.HasValue) s.RuntimeMinutes = min;
            if (!string.IsNullOrWhiteSpace(s.Plot)) s.PlotFull = s.Plot.Trim();
            if (!string.IsNullOrWhiteSpace(s.Rating)) s.MpaaRating = s.Rating.Trim();
            s.TopCast = CreditFormatting.TopCast(SplitCsv(s.Actors));
        }

        public static async Task ReplaceGenresAsync(MovieDb db, int seriesId, string? genreCsv)
        {
            var existing = await db.SeriesGenres.Where(g => g.SeriesId == seriesId).ToListAsync();
            if (existing.Count > 0) { db.SeriesGenres.RemoveRange(existing); await db.SaveChangesAsync(); }

            int i = 0;
            foreach (var name in SplitCsv(genreCsv))
            {
                var genre = db.Genres.Local.FirstOrDefault(g => g.Name == name)
                            ?? await db.Genres.FirstOrDefaultAsync(g => g.Name == name);
                if (genre == null) { genre = new Genre { Name = name }; db.Genres.Add(genre); }
                db.SeriesGenres.Add(new SeriesGenre { SeriesId = seriesId, Genre = genre, Ordering = i++ });
            }
        }

        public static async Task ReplaceRoleCreditsAsync(MovieDb db, int seriesId, CreditRole role, string? csv)
        {
            var existing = await db.SeriesCredits.Where(c => c.SeriesId == seriesId && c.Role == role).ToListAsync();
            if (existing.Count > 0) { db.SeriesCredits.RemoveRange(existing); await db.SaveChangesAsync(); }

            int i = 0;
            var seen = new HashSet<string>();
            foreach (var name in SplitCsv(csv))
            {
                var key = PersonResolver.ComputeNameKey(name);
                if (key == null || !seen.Add(key)) continue;
                var person = await PersonResolver.ResolveAsync(db, null, name);
                if (person == null) continue;
                db.SeriesCredits.Add(new SeriesCredit { SeriesId = seriesId, Person = person, Role = role, Ordering = i++ });
            }
        }

        /// <summary>Full parse of every legacy text field — used for a fresh insert.</summary>
        public static async Task ApplyAllAsync(MovieDb db, Series s)
        {
            ApplyScalars(s);
            await ReplaceGenresAsync(db, s.Id, s.Genre);
            await ReplaceRoleCreditsAsync(db, s.Id, CreditRole.Director, s.Director);
            await ReplaceRoleCreditsAsync(db, s.Id, CreditRole.Writer, s.Writer);
            await ReplaceRoleCreditsAsync(db, s.Id, CreditRole.Actor, s.Actors);
            await db.SaveChangesAsync();
        }
    }
}
