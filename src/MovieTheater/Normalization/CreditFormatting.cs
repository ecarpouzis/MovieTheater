using System.Collections.Generic;
using System.Linq;

namespace MovieTheater.Normalization
{
    /// <summary>Formats the top-billed actor names into the <c>Movie.TopCast</c> read cache.</summary>
    public static class CreditFormatting
    {
        public const int TopCastCount = 6;

        public static string TopCast(IEnumerable<string> actorNamesInBillingOrder)
        {
            if (actorNamesInBillingOrder == null) return null;
            var names = actorNamesInBillingOrder
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .Select(n => n.Trim())
                .Take(TopCastCount)
                .ToList();
            return names.Count > 0 ? string.Join(", ", names) : null;
        }
    }
}
