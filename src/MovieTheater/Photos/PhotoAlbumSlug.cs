using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace MovieTheater.Photos
{
    /// <summary>
    /// Album URL keys (docs/photos-plan.md §2.9). Minted SERVER-SIDE from the title and then left
    /// alone: a slug is a link a family member may have sent to another one, so retitling an album
    /// must not break it.
    ///
    /// <para>Accents fold to ASCII rather than being dropped, so two albums whose titles differ only by
    /// an accent still get distinct, readable keys instead of one collapsing into a bare number.</para>
    /// </summary>
    public static class PhotoAlbumSlug
    {
        private const int MaxLength = 180;

        /// <summary>A slug for one title, ignoring collisions. Empty only if the title had no
        /// alphanumeric content at all, which <see cref="Unique"/> handles.</summary>
        public static string Make(string? title)
        {
            if (string.IsNullOrWhiteSpace(title)) return "";

            var folded = title!.Normalize(NormalizationForm.FormD);
            var builder = new StringBuilder(folded.Length);
            foreach (var ch in folded)
            {
                var category = CharUnicodeInfo.GetUnicodeCategory(ch);
                if (category == UnicodeCategory.NonSpacingMark) continue;
                if (char.IsLetterOrDigit(ch)) builder.Append(char.ToLowerInvariant(ch));
                else if (builder.Length > 0 && builder[builder.Length - 1] != '-') builder.Append('-');
            }

            var slug = builder.ToString().Trim('-');
            // Non-ASCII letters survive the fold (a title in a non-Latin script is still a title);
            // they are legal in a modern URL path and the browser encodes them.
            return slug.Length <= MaxLength ? slug : slug.Substring(0, MaxLength).Trim('-');
        }

        /// <summary>
        /// A slug that is not already taken. Falls back to "album" when the title yields nothing
        /// sluggable, and disambiguates with -2, -3, … rather than a random suffix so the second
        /// "Christmas" album still reads like one.
        /// </summary>
        public static string Unique(string? title, IEnumerable<string> existingSlugs)
        {
            var taken = new HashSet<string>(existingSlugs ?? Enumerable.Empty<string>(), StringComparer.OrdinalIgnoreCase);
            var baseSlug = Make(title);
            if (baseSlug.Length == 0) baseSlug = "album";
            if (!taken.Contains(baseSlug)) return baseSlug;

            for (var n = 2; n < 10000; n++)
            {
                var candidate = baseSlug + "-" + n.ToString(CultureInfo.InvariantCulture);
                if (!taken.Contains(candidate)) return candidate;
            }
            // Ten thousand albums of the same name is not a case worth a prettier answer than this.
            return baseSlug + "-" + Guid.NewGuid().ToString("N").Substring(0, 8);
        }
    }
}
