using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using MovieTheater.Db;

namespace MovieTheater.Normalization
{
    /// <summary>
    /// Resolves (or creates) the <see cref="Person"/> for a credit, working for both
    /// scrape data (has an IMDB nm id) and API/text data (name only). Dedup rules:
    /// <list type="bullet">
    /// <item>nm known → match by nm; else upgrade a name-only person with the same
    /// <see cref="Person.NameKey"/>; else create a new nm-keyed person.</item>
    /// <item>nm unknown → match any person by NameKey (unifies text people with the
    /// real IMDB person of the same name); else create a name-only person.</item>
    /// </list>
    /// The returned person is tracked by <paramref name="db"/> (added if new).
    /// </summary>
    public static class PersonResolver
    {
        /// <summary>Normalized name used for nm-less dedup. Must match the SQL backfill
        /// (LOWER(LTRIM(RTRIM(DisplayName)))).</summary>
        public static string ComputeNameKey(string displayName)
            => string.IsNullOrWhiteSpace(displayName) ? null : displayName.Trim().ToLowerInvariant();

        public static async Task<Person> ResolveAsync(MovieDb db, string imdbNameId, string displayName)
        {
            imdbNameId = string.IsNullOrWhiteSpace(imdbNameId) ? null : imdbNameId.Trim();
            var nameKey = ComputeNameKey(displayName);

            if (imdbNameId != null)
            {
                var byNm = db.People.Local.FirstOrDefault(p => p.ImdbNameId == imdbNameId)
                           ?? await db.People.FirstOrDefaultAsync(p => p.ImdbNameId == imdbNameId);
                if (byNm != null)
                {
                    if (string.IsNullOrWhiteSpace(byNm.DisplayName) && !string.IsNullOrWhiteSpace(displayName))
                    {
                        byNm.DisplayName = displayName;
                        byNm.NameKey = nameKey;
                    }
                    return byNm;
                }

                // Upgrade a name-only person to this IMDB identity if one exists.
                if (nameKey != null)
                {
                    var byName = db.People.Local.FirstOrDefault(p => p.ImdbNameId == null && p.NameKey == nameKey)
                                 ?? await db.People.FirstOrDefaultAsync(p => p.ImdbNameId == null && p.NameKey == nameKey);
                    if (byName != null)
                    {
                        byName.ImdbNameId = imdbNameId;
                        if (string.IsNullOrWhiteSpace(byName.DisplayName)) byName.DisplayName = displayName;
                        return byName;
                    }
                }

                var createdNm = new Person { ImdbNameId = imdbNameId, DisplayName = displayName, NameKey = nameKey };
                db.People.Add(createdNm);
                return createdNm;
            }

            // No nm: dedup by name across everyone (links text people to real IMDB people).
            if (nameKey == null) return null;
            var existing = db.People.Local.FirstOrDefault(p => p.NameKey == nameKey)
                           ?? await db.People.FirstOrDefaultAsync(p => p.NameKey == nameKey);
            if (existing != null) return existing;

            var created = new Person { DisplayName = displayName, NameKey = nameKey };
            db.People.Add(created);
            return created;
        }
    }
}
