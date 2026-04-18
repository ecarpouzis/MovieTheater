namespace MovieTheater.Services.Bgg
{
    public class BggApiOptions
    {
        /// <summary>
        /// Bearer token for BGG API authentication.
        /// Obtain from https://boardgamegeek.com/applications after registering your application.
        /// </summary>
        public string? ApiToken { get; set; }

        /// <summary>
        /// Minimum delay between API requests in milliseconds.
        /// BGG recommends at least 5 seconds (5000ms) between requests to avoid throttling.
        /// </summary>
        public int RateLimitDelayMs { get; set; } = 5000;

    }
}
