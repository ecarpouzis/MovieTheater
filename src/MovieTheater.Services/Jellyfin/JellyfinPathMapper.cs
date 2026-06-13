namespace MovieTheater.Services.Jellyfin
{
    /// <summary>
    /// Pure path translation between Jellyfin-reported paths and the DB's path form.
    /// All comparison is case-insensitive with separators unified to backslash — the
    /// UNC↔drive-letter normalization is where sync bugs would live, so everything
    /// funnels through these two functions.
    /// </summary>
    public static class JellyfinPathMapper
    {
        /// <summary>Canonical comparison key: backslash separators, lower-case, no trailing separator.</summary>
        public static string NormalizeForCompare(string path)
        {
            var p = path.Replace('/', '\\').TrimEnd('\\');
            return p.ToLowerInvariant();
        }

        /// <summary>
        /// Translates a Jellyfin-reported path into the DB's form using the first mapping whose
        /// JellyfinPrefix matches. Returns false when no mapping applies. The matched mapping's
        /// index doubles as a preference rank: when Jellyfin holds duplicate items for one file
        /// (seen with a leftover drive-letter library folder), the item translated by the
        /// earlier-listed mapping wins.
        /// </summary>
        public static bool TryTranslateToDb(string jellyfinPath, IReadOnlyList<JellyfinPathMapping> mappings, out string dbPath, out int mappingIndex)
        {
            var unified = jellyfinPath.Replace('/', '\\');
            for (int i = 0; i < mappings.Count; i++)
            {
                var prefix = mappings[i].JellyfinPrefix.Replace('/', '\\');
                if (unified.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    dbPath = mappings[i].DbPrefix.Replace('/', '\\') + unified.Substring(prefix.Length);
                    mappingIndex = i;
                    return true;
                }
            }

            dbPath = string.Empty;
            mappingIndex = -1;
            return false;
        }
    }
}
