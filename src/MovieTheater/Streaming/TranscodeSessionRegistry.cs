using System;
using System.Collections.Concurrent;

namespace MovieTheater.Streaming
{
    /// <summary>
    /// In-app ledger of active play sessions, used by the streaming concurrency guard.
    ///
    /// Historically we couldn't derive the count from Jellyfin's <c>/Sessions</c>: every viewer's requests
    /// carried the same <c>DeviceId</c> ("movietheater-site") and Jellyfin keys sessions by
    /// Client+DeviceId, so all viewers collapsed into one session and <c>/Sessions</c> reported at most
    /// one transcode however many ffmpegs were running. Playback now sends a per-viewer device id
    /// (<c>StreamController.DeviceIdFor</c> — it had to, because a shared id also made two viewers of one
    /// title collide on the same ffmpeg output directory), so <c>/Sessions</c> could be believed again.
    /// This ledger is kept anyway: it costs no Jellyfin round trip on the hot path, and it counts the
    /// sessions WE started rather than anything else pointed at the same server.
    ///
    /// Instead we track our own play sessions here, fed by the Stream Start/Progress/Stop beats the
    /// client already sends (~10s). A session that stops checking in for <see cref="StaleAfter"/> is
    /// treated as abandoned (tab closed without a clean Stop, network loss, sleep) and drops out of the
    /// count on the next read. Registered as a singleton.
    /// </summary>
    public sealed class TranscodeSessionRegistry
    {
        private sealed class Entry
        {
            public DateTime LastSeenUtc;
            public bool IsTranscode;
        }

        // A session with no check-in for this long is considered gone. Sized to tolerate a couple of
        // missed ~10s heartbeats, matching the old Jellyfin-side staleness window.
        private static readonly TimeSpan StaleAfter = TimeSpan.FromSeconds(45);

        private readonly ConcurrentDictionary<string, Entry> sessions = new();

        /// <summary>Record (or refresh) a session at start. <paramref name="isTranscode"/> is false for a
        /// direct-play source (no ffmpeg), so it never counts against the transcode cap.</summary>
        public void Register(string playSessionId, bool isTranscode)
        {
            if (string.IsNullOrEmpty(playSessionId)) return;
            sessions[playSessionId] = new Entry { LastSeenUtc = DateTime.UtcNow, IsTranscode = isTranscode };
        }

        /// <summary>Keep-alive from a progress beat — refreshes an existing session's timestamp only.</summary>
        public void Touch(string playSessionId)
        {
            if (string.IsNullOrEmpty(playSessionId)) return;
            if (sessions.TryGetValue(playSessionId, out var e))
                e.LastSeenUtc = DateTime.UtcNow;
        }

        public void Remove(string playSessionId)
        {
            if (!string.IsNullOrEmpty(playSessionId))
                sessions.TryRemove(playSessionId, out _);
        }

        /// <summary>Number of live (non-stale) sessions that are actually transcoding. Prunes stale
        /// entries as a side effect so the dictionary can't grow without bound.</summary>
        public int ActiveTranscodeCount()
        {
            var cutoff = DateTime.UtcNow - StaleAfter;
            int count = 0;
            foreach (var kv in sessions)
            {
                if (kv.Value.LastSeenUtc <= cutoff)
                {
                    sessions.TryRemove(kv.Key, out _);
                    continue;
                }
                if (kv.Value.IsTranscode) count++;
            }
            return count;
        }
    }
}
