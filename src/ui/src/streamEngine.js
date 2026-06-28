import Hls from "hls.js";

// ── shared hls.js engine (used by both the Watch player and the TV/channel player) ───────────────
// One home for the streaming engine the two players share, so a fix to buffering or error recovery
// can't land in one and be silently forgotten in the other (which is exactly how they drifted before).

// Jellyfin spawns the transcode and opens the (networked) source file on the first playlist/segment
// request, so a cold start can take ~10s before anything is playable. hls.js's default manifest
// timeout is only 10s — it would give up and show nothing, which a refresh then "fixes" by hitting a
// warm transcode. Be patient instead.
export const HLS_LOAD_CONFIG = {
  manifestLoadingTimeOut: 30_000,
  manifestLoadingMaxRetry: 6,
  manifestLoadingRetryDelay: 1_000,
  levelLoadingTimeOut: 30_000,
  levelLoadingMaxRetry: 6,
  fragLoadingTimeOut: 60_000,
  fragLoadingMaxRetry: 6,
};

/**
 * Build an hls.js instance with the shared buffer config + error recovery. The caller still wires its
 * own MANIFEST_PARSED handler (seek-to-start / play) and any diagnostics, then loadSource + attachMedia.
 *
 * Why these buffers: we serve "Original" by COPYING the source (no re-encode), so the stream carries
 * the file's real, spiky bitrate — a calm scene runs a few Mbps, a high-motion scene peaks 25-45 Mbps.
 * hls.js buffers up to whichever of {seconds, bytes} it hits first, and its DEFAULT maxBufferSize is
 * only 60 MB (~10-15s at a 40 Mbps peak). That byte cap silently collapses the buffer exactly when
 * entering a high-bitrate scene (a scene-change keyframe begins a fat GOP), so a single fat segment
 * outruns the buffer → a deterministic "loading dots" hitch at that timestamp, on every device, even
 * after rewinding into it. A large byte budget + ~2 min of lead lets calm scenes pre-buffer the spike.
 *
 * @param backBufferLength seconds to keep behind the head. Watch keeps 90 (rewindable); TV keeps it
 *   small — a channel is forward-only (a lone-viewer scrub re-tunes a fresh stream, not the back buffer).
 * @param startPosition    optional join offset. TV joins the live channel offset directly here
 *   (avoiding the seek churn that caused join-time A/V desync on mobile); omit to start at 0.
 * @param onStall  called on a non-fatal BUFFER_STALLED_ERROR (the adaptive-downshift signal).
 * @param onFatal  called on a fatal error the standard NETWORK/MEDIA recovery can't handle.
 */
export function createHls({ backBufferLength = 90, startPosition, onStall, onFatal } = {}) {
  const hls = new Hls({
    maxBufferLength: 120,             // ~2 min of lead so calm scenes pre-buffer the next spike
    maxBufferSize: 400 * 1000 * 1000, // 400 MB: don't let the byte cap bind during a 40 Mbps scene
    backBufferLength,
    ...(Number.isFinite(startPosition) ? { startPosition } : {}),
    ...HLS_LOAD_CONFIG,
  });
  hls.on(Hls.Events.ERROR, (_event, data) => {
    // A buffer stall (non-fatal) is the adaptive-downshift signal.
    if (data.details === Hls.ErrorDetails.BUFFER_STALLED_ERROR) onStall?.();
    if (!data.fatal) return;
    // Standard hls.js recovery dance before giving up.
    if (data.type === Hls.ErrorTypes.NETWORK_ERROR) hls.startLoad();
    else if (data.type === Hls.ErrorTypes.MEDIA_ERROR) hls.recoverMediaError();
    else onFatal?.();
  });
  return hls;
}
