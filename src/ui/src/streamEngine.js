import Hls from "hls.js";
import { noteVideoEvent, notePlaylistError, reportFatal } from "./videoIncidents";

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

// Diagnostic (mirrors the [backseek] watchdog): on a buffer stall, log the playhead and the gap ahead.
// Fires only on the rare stall event, so there's no steady-state cost. A microstutter that still slips
// past maxBufferHole shows up here as a small hole with the timestamp; silence means the buffer glided
// over it. Safe to delete once the microstutter question is settled.
function logStall(hls) {
  const v = hls.media;
  if (!v) return;
  const t = v.currentTime;
  let detail = "buffer empty ahead (underrun)";
  try {
    const b = v.buffered;
    for (let i = 0; i < b.length; i++) {
      if (t >= b.start(i) - 0.05 && t <= b.end(i) + 0.05) {
        detail =
          i + 1 < b.length
            ? `hole ${(b.start(i + 1) - b.end(i)).toFixed(3)}s → resumes ${b.start(i + 1).toFixed(2)}s`
            : "at buffer end (underrun)";
        break;
      }
    }
  } catch { /* buffered can throw transiently */ }
  // maxBufferLength is live config, not the constructor value: hls.js HALVES it when the browser's
  // SourceBuffer quota rejects an append (see the BUFFER_FULL_ERROR log below), so the value at stall
  // time is the buffer the session was really running with.
  console.warn(`[stutter] stalled @ ${t.toFixed(2)}s · ${detail} · maxBufferLength ${hls.config.maxBufferLength}s`);
}

/**
 * The media-timeline shift on a mid-file HLS join, in seconds, from an INIT_PTS_FOUND payload
 * (null when the payload can't be read). Jellyfin starts an encode with an input seek that snaps to
 * the previous SOURCE keyframe, so the first fragment's true PTS can be up to one GOP earlier than
 * the playlist slot it is muxed into; hls.js aligns fragment→slot with initPTS = truePTS −
 * playlistTime and maps every fragment start to decodeTime − initPTS, which shifts the whole media
 * timeline: video.currentTime = true content time + offset.
 *
 *     offset = −(initPTS / timescale)
 *     trueContentTime(T) = T − offset          // T = video.currentTime
 *
 * Sign check with the measured 2026-07-29 join: initPTS = 2702.95 − 2711.71 = −8.76 → offset =
 * +8.76 → a cue authored at content 2705 must render at currentTime 2713.76, where the picture
 * shows 2713.76 − 8.76 = 2705. Correct.
 *
 * 1.6.16 emits a numeric `initPTS` (a baseTime in `timescale` units) alongside `timescale`, but it
 * is copied straight off an internal { baseTime, timescale, trackId } record — accept either shape
 * rather than betting the ride on which one a future version hands us.
 */
export function timelineOffsetFromInitPts(data) {
  const raw = data?.initPTS;
  const wrapped = raw !== null && typeof raw === "object";
  const baseTime = wrapped ? raw.baseTime : raw;
  const timescale = (wrapped ? raw.timescale : undefined) ?? data?.timescale;
  if (!Number.isFinite(baseTime) || !Number.isFinite(timescale) || timescale <= 0) return null;
  const offset = -(baseTime / timescale);
  return offset === 0 ? 0 : offset; // negating an aligned start yields -0; hand back a plain 0
}

/**
 * A TRUSTWORTHY throughput estimate from an hls.js instance, or null while it has none.
 *
 * hls.js's bandwidthEstimate does NOT report "unknown" before it has data — its EWMA returns the
 * configured `abrEwmaDefaultEstimate` (500 kbps) until enough fragments have loaded to estimate
 * (EwmaBandWidthEstimator.canEstimate). Every ABR switch destroys the instance and builds a fresh
 * one, and the players' 5s samplers keep firing across that restart, so the canned 500 kbps lands in
 * the ABR's estimate memory as if it were a measurement of the link. That is not a rounding error:
 * a drop reading it would price the link at 0.5 Mbps and slam a healthy connection to the bottom
 * rung. A cold transcode start can hold the estimator empty for ~10s (see HLS_LOAD_CONFIG), long
 * enough for such a sample to still look fresh when the first post-restart drop is allowed to fire.
 *
 * Comparing against the instance's own `abrEwmaDefaultEstimate` is the exact "has real data yet"
 * test using public API. A link genuinely measured at exactly 500,000 bps is discarded for one
 * sample — harmless, and at that speed the bottom rung is where it belongs anyway.
 */
export function bandwidthSample(hls) {
  if (!hls) return null;
  const estimate = hls.bandwidthEstimate;
  if (!estimate || !isFinite(estimate)) return null;
  return estimate === hls.abrEwmaDefaultEstimate ? null : estimate;
}

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
 * @param onFatal  called on a fatal error the staged NETWORK/MEDIA recovery below can't clear (a dead
 *   session, or a decode error that survives recoverMediaError + swapAudioCodec). The page surfaces it
 *   (Watch's fatal card / TV's "no signal") instead of spinning forever.
 * @param onTimelineOffset  called with the seconds this instance's media timeline sits AHEAD of true
 *   content time (see timelineOffsetFromInitPts). Fires on every INIT_PTS_FOUND — the value re-rolls
 *   across seeks and discontinuities, so the latest wins. A new instance implicitly starts at 0, so
 *   callers re-zero on every re-tune / new session; direct play never fires it and stays at 0.
 */
export function createHls({ backBufferLength = 90, startPosition, onStall, onFatal, onTimelineOffset } = {}) {
  const hls = new Hls({
    maxBufferLength: 120,             // ~2 min of lead so calm scenes pre-buffer the next spike
    // Explicit ceiling. The 400 MB byte budget below otherwise lets the forward buffer grow to hls.js's
    // 600s (~10 min) default at low average bitrate — wasted work the server transcodes ahead and our
    // ABR restart throws away on every switch, plus memory pressure on mobile. 5 min is ample lead.
    maxMaxBufferLength: 300,
    maxBufferSize: 400 * 1000 * 1000, // 400 MB: don't let the byte cap bind during a 40 Mbps scene
    // We COPY video while transcoding audio, so the copied-video keyframe cuts and the AAC frame grid
    // don't line up — leaving sub-frame HOLES at some segment boundaries. hls.js's default maxBufferHole
    // (0.1s) treats a slightly-larger hole as a stall and nudges the playhead across it: a rare
    // one-frame microstutter (and each nudge fires onStall, which can spuriously trip the ABR downshift).
    // Tolerate small holes so the playhead glides over them seamlessly. Harmless when there are no holes.
    maxBufferHole: 0.5,
    backBufferLength,
    ...(Number.isFinite(startPosition) ? { startPosition } : {}),
    ...HLS_LOAD_CONFIG,
  });
  // Staged fatal-error recovery, ported from Jellyfin's htmlMediaHelper. Counters are per-instance, so
  // each new createHls (every stream restart / channel re-tune) starts with a clean slate; the 3s window
  // also auto-resets escalation so an unrelated later error restarts at the gentlest step.
  const RECOVER_WINDOW_MS = 3_000;
  const MAX_NETWORK_RETRIES = 3;
  let lastMediaRecoverAt = null; // last plain recoverMediaError()
  let lastAudioSwapAt = null;    // last swapAudioCodec() + recover
  let networkRetries = 0;        // bounded fatal-NETWORK startLoad() attempts

  if (onTimelineOffset) {
    hls.on(Hls.Events.INIT_PTS_FOUND, (_event, data) => {
      const offset = timelineOffsetFromInitPts(data);
      if (offset !== null) onTimelineOffset(offset);
    });
  }

  hls.on(Hls.Events.ERROR, (_event, data) => {
    // A buffer stall (non-fatal) is the adaptive-downshift signal.
    if (data.details === Hls.ErrorDetails.BUFFER_STALLED_ERROR) {
      logStall(hls); // diagnostic: report the hole size at the stall, to confirm the cause / verify the fix
      noteVideoEvent("hls:stalled"); // ring only — the stall INCIDENT is decided at the element (videoIncidents)
      onStall?.();
    }
    // Playlist/segment fetch failures. Reported from here rather than either player because this is
    // the one place both of them load bytes from — and because hls.js retries them itself, the
    // incident rule ("fatal, or three inside 30 s") lives in videoIncidents, not in this handler.
    if (/load(error|timeout)/i.test(String(data.details))) {
      notePlaylistError({ details: data.details, fatal: !!data.fatal, code: data.response?.code ?? null });
    }
    // The browser's SourceBuffer byte quota (~150 MB of video in Chromium) rejected an append. That
    // quota binds BEFORE our seconds/bytes config on a high-bitrate stream — at a 23 Mbps remux it
    // caps the forward buffer near ~50s, and hls.js reacts by halving maxBufferLength for the rest of
    // the session (measured 2026-08-16: an Original session rode a ~30s buffer, not the configured
    // 120). Log it so the real per-device ceiling is visible instead of inferred from drain math.
    if (data.details === Hls.ErrorDetails.BUFFER_FULL_ERROR) {
      console.warn(`[buffer] SourceBuffer quota hit — maxBufferLength now ${hls.config.maxBufferLength}s`);
    }
    if (!data.fatal) return;

    // Giving up is the one player event that is unambiguously a failure, so it self-reports here —
    // once, in the shared engine, rather than in each player's own onFatal handler (they show
    // different copy: Watch's fatal card, TV's "no signal"). A player with no incident context
    // (the photo-album video) files nothing; see videoIncidents.setVideoIncidentContext.
    const giveUp = () => {
      reportFatal(`hls fatal ${data.type}/${data.details}${data.response?.code ? ` (http ${data.response.code})` : ""}`, {
        type: data.type,
        details: data.details,
        code: data.response?.code ?? null,
      });
      onFatal?.();
    };

    if (data.type === Hls.ErrorTypes.NETWORK_ERROR) {
      // A dead session (transcode gone → 404, expired, gateway 5xx, or CORS → code 0) will never clear
      // by retrying the same URL — surface it so the page can re-establish the session / show the error.
      // Only genuinely transient blips get the bounded startLoad() retry; an unbounded retry was an
      // invisible infinite reload loop.
      const code = data.response?.code;
      if (code >= 400 || code === 0 || networkRetries >= MAX_NETWORK_RETRIES) {
        giveUp();
      } else {
        networkRetries += 1;
        hls.startLoad();
      }
      return;
    }

    if (data.type === Hls.ErrorTypes.MEDIA_ERROR) {
      // Escalate: recoverMediaError → (2nd within 3s) swapAudioCodec + recover → (3rd within 3s) give up.
      // We COPY video but TRANSCODE audio, so a browser audio-decode mismatch is a likely media error and
      // swapAudioCodec is the exact escape hatch our old single recoverMediaError() never reached (it just
      // looped forever → permanent loading bulbs / silently frozen TV).
      const now = performance.now();
      if (lastMediaRecoverAt === null || now - lastMediaRecoverAt > RECOVER_WINDOW_MS) {
        lastMediaRecoverAt = now;
        hls.recoverMediaError();
      } else if (lastAudioSwapAt === null || now - lastAudioSwapAt > RECOVER_WINDOW_MS) {
        lastAudioSwapAt = now;
        hls.swapAudioCodec();
        hls.recoverMediaError();
      } else {
        giveUp();
      }
      return;
    }

    // OTHER_ERROR (e.g. a mux error) — nothing to recover.
    giveUp();
  });
  return hls;
}

/** True when this browser should play HLS natively (Safari, incl. iPad) rather than via MSE —
 *  native does it better there, and hls.js isn't needed at all. */
export function prefersNativeHls(video) {
  return !!video.canPlayType?.("application/vnd.apple.mpegurl");
}

/** Plain-src attach (direct play, or native HLS on Safari) with the matching teardown. The
 *  removeAttribute+load pair is the part everyone kept re-writing: without it the element keeps
 *  the network connection and the old frame alive after unmount. */
export function attachDirect(video, url) {
  video.src = url;
  return () => {
    video.removeAttribute("src");
    video.load();
  };
}
