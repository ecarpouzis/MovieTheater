namespace MovieTheater.Services.OpenSubtitles
{
    /// <summary>
    /// Credentials for the OpenSubtitles.com REST API. The Api-Key identifies a registered "consumer"
    /// app (opensubtitles.com/en/consumers) and is required on every request; Username/Password log the
    /// account in for a download token (downloads count against that account's daily quota). Search
    /// needs only the Api-Key; download needs the login too.
    /// </summary>
    public class OpenSubtitlesOptions
    {
        public string? ApiKey { get; set; }
        public string? Username { get; set; }
        public string? Password { get; set; }
    }
}
