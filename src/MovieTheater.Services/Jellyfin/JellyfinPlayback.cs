namespace MovieTheater.Services.Jellyfin
{
    // DTOs for the Phase-2 playback surface (streaming-plan.md §6).

    public class JellyfinPlaybackInfoResult
    {
        public List<JellyfinPlaybackMediaSource> MediaSources { get; set; } = new();
        public string PlaySessionId { get; set; } = default!;
    }

    public class JellyfinPlaybackMediaSource
    {
        public string Id { get; set; } = default!;
        public string? Container { get; set; }
        public long? RunTimeTicks { get; set; }
        public string? TranscodingUrl { get; set; }
        public bool SupportsDirectStream { get; set; }
        public List<string>? TranscodeReasons { get; set; }
        public List<JellyfinPlaybackStream> MediaStreams { get; set; } = new();
    }

    public class JellyfinPlaybackStream
    {
        public int Index { get; set; }
        public string? Type { get; set; }
        public string? Codec { get; set; }
        public string? DisplayTitle { get; set; }
        public string? Language { get; set; }
        public bool IsDefault { get; set; }
        public bool IsExternal { get; set; }
        public string? DeliveryUrl { get; set; }
        public string? DeliveryMethod { get; set; }
        public bool IsTextSubtitleStream { get; set; }
    }

    public class JellyfinSession
    {
        public string? Id { get; set; }
        public JellyfinSessionTranscodingInfo? TranscodingInfo { get; set; }
        public JellyfinSessionNowPlaying? NowPlayingItem { get; set; }
    }

    public class JellyfinSessionTranscodingInfo
    {
        public bool IsVideoDirect { get; set; }
    }

    public class JellyfinSessionNowPlaying
    {
        public string? Id { get; set; }
        public string? Name { get; set; }
    }
}
