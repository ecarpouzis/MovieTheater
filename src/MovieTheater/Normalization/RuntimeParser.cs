using System.Text.RegularExpressions;

namespace MovieTheater.Normalization
{
    /// <summary>
    /// Parses the many runtime string shapes we receive into whole minutes:
    /// "1 h 30 min", "1h", "45 min", "136 min", "2h 16m", ISO-8601 "PT2H16M", or a bare "120".
    /// </summary>
    public static class RuntimeParser
    {
        private static readonly Regex Hours = new Regex(@"(\d+)\s*h", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex Minutes = new Regex(@"(\d+)\s*m", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex BareInt = new Regex(@"^\s*(\d+)\s*$", RegexOptions.Compiled);

        public static int? ToMinutes(string runtime)
        {
            if (string.IsNullOrWhiteSpace(runtime)) return null;
            var s = runtime.Trim();

            // A bare number is taken as minutes ("120").
            var bare = BareInt.Match(s);
            if (bare.Success && int.TryParse(bare.Groups[1].Value, out var only))
                return only > 0 ? only : (int?)null;

            int total = 0;
            bool found = false;

            // Matches "1 h", "2H", and the H in "PT2H16M".
            var h = Hours.Match(s);
            if (h.Success && int.TryParse(h.Groups[1].Value, out var hours)) { total += hours * 60; found = true; }

            // Matches "30 min", "16m", and the M in "PT2H16M".
            var m = Minutes.Match(s);
            if (m.Success && int.TryParse(m.Groups[1].Value, out var mins)) { total += mins; found = true; }

            return found && total > 0 ? total : (int?)null;
        }
    }
}
