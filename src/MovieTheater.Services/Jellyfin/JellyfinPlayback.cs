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
        public long? Bitrate { get; set; }
        public string? TranscodingUrl { get; set; }
        public bool SupportsDirectStream { get; set; }
        public bool SupportsDirectPlay { get; set; }
        /// <summary>The audio track Jellyfin itself will play when the request pins none — its own
        /// answer, not something to re-derive. On a file that flags several audio tracks default it
        /// disagrees with "the first IsDefault stream", and this is the one that reaches ffmpeg (and
        /// the one direct play is negotiated against). Null when Jellyfin didn't state a choice.</summary>
        public int? DefaultAudioStreamIndex { get; set; }
        public List<string>? TranscodeReasons { get; set; }
        public List<JellyfinPlaybackStream> MediaStreams { get; set; } = new();
    }

    public class JellyfinPlaybackStream
    {
        public int Index { get; set; }
        public string? Type { get; set; }
        public string? Codec { get; set; }
        public string? DisplayTitle { get; set; }
        /// <summary>The raw track title from the container ("English (Commentary with …)"), before
        /// Jellyfin decorates it with codec/channel/default suffixes. DisplayTitle normally embeds it,
        /// but an untagged track can leave DisplayTitle codec-only, so keep the original to read too.</summary>
        public string? Title { get; set; }
        public string? Language { get; set; }
        public int? Channels { get; set; }
        /// <summary>Bits per second of this stream in the source file. Load-bearing for the video
        /// stream: Jellyfin refuses to stream-copy a video into a ceiling below it, so this is what
        /// decides whether a capped rung is a copy or a re-encode.</summary>
        public long? BitRate { get; set; }
        public bool IsDefault { get; set; }
        public bool IsExternal { get; set; }
        public string? DeliveryUrl { get; set; }
        public string? DeliveryMethod { get; set; }
        public bool IsTextSubtitleStream { get; set; }
        // Frames per second of a video stream (e.g. 23.976025). Lets the client offer a frame-rate
        // subtitle-sync fix: an external sub authored for a different fps drifts linearly, which no
        // constant delay can correct. RealFrameRate is the precise value; AverageFrameRate backs it up.
        public double? RealFrameRate { get; set; }
        public double? AverageFrameRate { get; set; }
    }

    public class JellyfinSession
    {
        public string? Id { get; set; }
        public JellyfinSessionTranscodingInfo? TranscodingInfo { get; set; }
        public JellyfinSessionNowPlaying? NowPlayingItem { get; set; }
        /// <summary>Last time the session reported playback progress. Our ~10s Stream/Progress heartbeat
        /// drives this (via ReportPlaybackProgress), so it goes stale within a beat or two of a client
        /// disconnecting — a far tighter liveness signal than Jellyfin's own session timeout.</summary>
        public DateTime? LastPlaybackCheckIn { get; set; }
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
