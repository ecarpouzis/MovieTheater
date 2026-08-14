namespace MovieTheater.Services.Jellyfin
{
    /// <summary>
    /// The two spellings of one Jellyfin item id. Our rows store the API's dashless-lowercase form
    /// (<c>ddd251b8fe23cdac497b110eb82e25b5</c>); Jellyfin's OWN database tables store dashed-UPPERCASE
    /// TEXT (<c>DDD251B8-FE23-CDAC-497B-110EB82E25B5</c>). Anything that joins our rows against
    /// <c>jellyfin.db</c> directly — the keyframe bank — crosses that boundary here.
    /// </summary>
    public static class JellyfinItemIds
    {
        /// <summary>Our dashless-lowercase id → the dashed-UPPERCASE TEXT Jellyfin's tables store.
        /// A string reshaping, deliberately not a <see cref="System.Guid"/> round-trip: Guid byte
        /// order is an endianness question this join must never depend on. Anything that is not
        /// 32 chars passes through untouched rather than being mangled.</summary>
        public static string DashedUpper(string dashless)
        {
            var s = dashless.ToUpperInvariant();
            return s.Length != 32 ? s
                : $"{s[..8]}-{s[8..12]}-{s[12..16]}-{s[16..20]}-{s[20..]}";
        }
    }
}
