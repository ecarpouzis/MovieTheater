import { useState, useEffect, useRef, useCallback } from "react";
import Hls from "hls.js";
import { detectStreamCapabilities } from "../../streamCapabilities";
import "./VideoPlayer.css";

// The delivered layout is capped at what this client can actually emit, so a stereo machine reads
// "2.0" for a 5.1 source (which it gets downmixed) instead of falsely claiming surround.
export function deliveredLayout(channels) {
  if (!channels) return null;
  const max = detectStreamCapabilities().maxAudioChannels || 2;
  return channelLayout(Math.min(channels, max));
}

const TICKS_PER_SECOND = 10_000_000;

// Jellyfin spawns the transcode and opens the (networked) source file on the first
// playlist/segment request, so a cold start can take ~10s before anything is playable.
// hls.js's default manifest timeout is only 10s — it would give up and show nothing,
// which a refresh then "fixes" by hitting a warm transcode. Be patient instead.
export const HLS_LOAD_CONFIG = {
  manifestLoadingTimeOut: 30_000,
  manifestLoadingMaxRetry: 6,
  manifestLoadingRetryDelay: 1_000,
  levelLoadingTimeOut: 30_000,
  levelLoadingMaxRetry: 6,
  fragLoadingTimeOut: 60_000,
  fragLoadingMaxRetry: 6,
};

// The quality ladder from streaming-plan.md §7. "Auto" (§14.4) adapts the cap to
// measured bandwidth; "Original" omits the cap entirely, letting compatible sources
// direct-stream with no re-encode. The numbered rungs pin a fixed cap.
export const QUALITY_LADDER = [
  { key: "auto", label: "Auto", bps: null, hint: "adapts to your connection" },
  { key: "original", label: "Original", bps: null, hint: "direct stream when possible" },
  { key: "1080-12", label: "1080p", bps: 12_000_000, hint: "12 Mbps" },
  { key: "1080-8", label: "1080p", bps: 8_000_000, hint: "8 Mbps" },
  { key: "720-4", label: "720p", bps: 4_000_000, hint: "4 Mbps" },
  { key: "480-15", label: "480p", bps: 1_500_000, hint: "1.5 Mbps" },
];

// Pretty-print the negotiated output codec for the player readout (§14.1).
export function codecLabel(codec) {
  const map = { hevc: "HEVC", h265: "HEVC", h264: "H.264", avc: "H.264", av1: "AV1", vp9: "VP9" };
  return map[String(codec).toLowerCase()] || String(codec).toUpperCase();
}

// The "Playing" readout, shared by the Watch player and the TV/channel menu so both report delivery
// quality identically and truthfully: the active quality, the output codec, and — the part the viewer
// actually cares about — whether the video is the original copied bit-for-bit ("no re-encode") or a
// transcode. `autoLabel` is the live adaptive-cap label (e.g. "Auto · Original" / "Auto · 8 Mbps").
export function formatPlaying({ qualityKey, autoLabel, videoCodec, isDirectStream, audio }) {
  const rung = QUALITY_LADDER.find((q) => q.key === qualityKey);
  // Lead with the unambiguous live verdict — the question is always "original, or a transcode?" — then
  // the supporting detail. The option's "…when possible" marketing hint stays in the Quality menu, not
  // here, so this line never hedges about what's actually being delivered right now.
  const parts = [isDirectStream ? "Original · no re-encode" : "Transcoded"];
  if (!isDirectStream) {
    parts.push(
      qualityKey === "auto"
        ? (autoLabel || "Auto").replace(/^Auto · /, "")
        : [rung?.label, rung?.hint].filter(Boolean).join(" ")
    );
  }
  if (videoCodec) parts.push(codecLabel(videoCodec));
  if (audio) parts.push(audio);
  return parts.join(" · ");
}

// Speaker layout from a channel count, for the "Playing" readout (so 5.1 surround is visible, not
// just assumed). The server now preserves the source channel count up to the client's output, so
// this reflects what's actually delivered.
export function channelLayout(channels) {
  if (!channels) return null;
  if (channels >= 8) return "7.1";
  if (channels === 7) return "6.1";
  if (channels === 6) return "5.1";
  if (channels === 2) return "2.0";
  if (channels === 1) return "Mono";
  return `${channels}ch`;
}

function formatTime(totalSeconds) {
  if (!isFinite(totalSeconds) || totalSeconds < 0) totalSeconds = 0;
  const s = Math.floor(totalSeconds % 60);
  const m = Math.floor((totalSeconds / 60) % 60);
  const h = Math.floor(totalSeconds / 3600);
  const mm = h > 0 ? String(m).padStart(2, "0") : String(m);
  const ss = String(s).padStart(2, "0");
  return h > 0 ? `${h}:${mm}:${ss}` : `${mm}:${ss}`;
}

// Subtitle-delay readout. Positive = subtitles show later than the audio; negative = earlier.
// Uses a real minus glyph so the sign reads cleanly in the player chrome.
function formatDelay(ms) {
  const sign = ms > 0 ? "+" : ms < 0 ? "−" : "";
  return `${sign}${Math.abs(ms)} ms`;
}

// Step per nudge (keyboard or ± buttons) and the clamp on total offset.
const SUBTITLE_NUDGE_MS = 100;
const SUBTITLE_OFFSET_LIMIT_MS = 30_000;

// ── subtitle appearance ──────────────────────────────────────────────────────
// The viewer's caption look, persisted across sessions. Native WebVTT cues can only be styled
// via a stylesheet (no per-element inline style), so the size/color/font/edge here are written
// into a single injected `::cue` rule; vertical lift is applied per-cue via cue.line. Size is in
// vh so it scales with the player (and reads consistently across browsers — the Firefox fix).
// liftPct is "% raised off the very bottom": 0 = flush at the bottom edge (the slider's low end),
// higher = raised. Default to a small inset so captions don't sit flush by default, while the slider
// can still be dragged all the way down to the true bottom.
const SUB_STYLE_DEFAULTS = { sizeVh: 3.0, color: "#f2ecdd", font: "sans", edge: "shadow", liftPct: 5, bgOpacity: 0.78 };
const SUB_SIZE_MIN = 1.8;
const SUB_SIZE_MAX = 5.5;
const SUB_LIFT_MAX = 40;

const SUB_FONTS = {
  sans: '"Segoe UI", system-ui, Arial, sans-serif',
  serif: 'Georgia, "Times New Roman", serif',
  cinema: '"Marcellus", Georgia, serif',
};
const SUB_FONT_OPTIONS = [
  { key: "sans", label: "Sans" },
  { key: "serif", label: "Serif" },
  { key: "cinema", label: "Cinema" },
];

// text-shadow strings: a soft drop shadow, or a 4-way faux outline that holds the glyph against
// bright scenes. ::cue honors text-shadow (one of its few allowed properties).
const SUB_EDGES = {
  none: "none",
  shadow: "0 1px 3px rgba(0,0,0,0.95), 0 2px 8px rgba(0,0,0,0.7)",
  outline:
    "-1px -1px 0 #000, 1px -1px 0 #000, -1px 1px 0 #000, 1px 1px 0 #000, 0 0 4px rgba(0,0,0,0.9)",
};
const SUB_EDGE_OPTIONS = [
  { key: "none", label: "None" },
  { key: "shadow", label: "Shadow" },
  { key: "outline", label: "Outline" },
];

const SUB_COLORS = [
  { label: "White", value: "#ffffff" },
  { label: "Cream", value: "#f2ecdd" },
  { label: "Yellow", value: "#f2e36b" },
  { label: "Gold", value: "#f5cf72" },
];

// The dark box behind the text. Opacity 0 renders no box at all (fully transparent).
const subBg = (opacity) => (opacity <= 0 ? "transparent" : `rgba(8, 7, 5, ${opacity})`);

/**
 * The screening-room player (streaming-plan.md §7). Self-contained dark UI —
 * deliberately no AntD in here. Shared later by the TV channel page.
 *
 * The parent owns the streaming session: this component plays an HLS url and
 * reports intent (progress beats, quality/audio/subtitle choices, ended) upward.
 */
function VideoPlayer({
  src,
  poster,
  title,
  metaLine,
  durationSeconds,
  startAt = 0,
  isHls = true,
  isDirectStream = false,
  videoCodec = null,
  qualityKey = "original",
  qualityDetail = null,
  audioTracks = [],
  subtitleTracks = [],
  selectedAudioIndex = null,
  selectedSubtitleIndex = null,
  onSelectQuality,
  onSelectAudio,
  onSelectSubtitle,
  onProgress,
  onBandwidth,
  onStall,
  onEnded,
  combinedDuration = 0,
  partOffset = 0,
  partBoundaries = [],
  onSeekGlobal,
  onBack,
}) {
  const containerRef = useRef(null);
  const videoRef = useRef(null);
  const hlsRef = useRef(null);
  const idleTimerRef = useRef(null);
  const clickTimerRef = useRef(null);
  const scrubRef = useRef(null);

  const [playing, setPlaying] = useState(false);
  const [needsTap, setNeedsTap] = useState(false); // autoplay was blocked
  const [buffering, setBuffering] = useState(true);
  const [currentTime, setCurrentTime] = useState(startAt);
  const [bufferedEnd, setBufferedEnd] = useState(0);
  const [volume, setVolume] = useState(() => {
    const stored = parseFloat(window.localStorage.getItem("PlayerVolume"));
    return isFinite(stored) ? Math.min(Math.max(stored, 0), 1) : 1;
  });
  const [muted, setMuted] = useState(false);
  const [fullscreen, setFullscreen] = useState(false);
  const [controlsVisible, setControlsVisible] = useState(true);
  const [openMenu, setOpenMenu] = useState(null); // 'settings' | null
  const [scrubbing, setScrubbing] = useState(false);
  const [scrubTime, setScrubTime] = useState(null);
  const [hoverTime, setHoverTime] = useState(null);
  const [hoverX, setHoverX] = useState(0);
  const [flash, setFlash] = useState(null); // transient center icon: 'play' | 'pause'
  const [fatalError, setFatalError] = useState(null);

  // Subtitle timing nudge: shift the active text track's cues by this many ms so the viewer can
  // fix small sync drift live. Only meaningful for soft (sidecar VTT) tracks — burned-in image
  // subs are baked into the picture server-side and can't be moved client-side.
  const [subtitleOffsetMs, setSubtitleOffsetMs] = useState(0);
  const [offsetToast, setOffsetToast] = useState(false); // transient delay readout on nudge
  const offsetToastTimer = useRef(null);
  const cueOriginalsRef = useRef(new WeakMap()); // cue → its un-shifted {start,end}, so nudges don't compound

  // Caption appearance (size/color/font/edge/lift), restored from localStorage and persisted on change.
  // `styleOpen` reveals the editor sub-panel and turns on the on-video sample preview.
  const [subStyle, setSubStyle] = useState(() => {
    try {
      return { ...SUB_STYLE_DEFAULTS, ...JSON.parse(window.localStorage.getItem("SubtitleStyle") || "{}") };
    } catch {
      return { ...SUB_STYLE_DEFAULTS };
    }
  });
  const [styleOpen, setStyleOpen] = useState(false);
  const setStyle = useCallback((patch) => setSubStyle((s) => ({ ...s, ...patch })), []);

  // ── backward-seek watchdog (diagnostic) ──────────────────────────────────────
  // Hunts the rare "video pops backward ~5s on its own" report: logs any spontaneous backward jump in
  // currentTime with the context that separates the likely causes (a recent hls.js playlist reload /
  // manifest re-parse, a source-effect reload, or neither). Ignores the viewer's own scrubs.
  const lastSteadyTimeRef = useRef(0); // last currentTime seen while playing & not seeking
  const lastHlsEventRef = useRef(null); // { name, at } of the most recent recorded hls.js event
  const sourceReloadRef = useRef(null); // { at, startAt } when the source lifecycle effect (re)ran
  const scrubbingRef = useRef(false); // mirror of `scrubbing` for use inside native event handlers
  const lastScrubEndRef = useRef(0); // performance.now() when a scrub last ended (grace window)
  useEffect(() => {
    scrubbingRef.current = scrubbing;
    if (!scrubbing) lastScrubEndRef.current = performance.now();
  }, [scrubbing]);

  const duration = durationSeconds || videoRef.current?.duration || 0; // current part/stream
  // Combined-timeline mode (multi-part movie): the rail, time readout and scrub all run on the
  // whole-movie clock; `partOffset` maps this part's local time onto it. combinedDuration is 0 for
  // single-file titles/episodes, collapsing every expression below back to plain local time.
  const combined = combinedDuration > 0;
  const displayDuration = combined ? combinedDuration : duration;
  const globalCurrent = (combined ? partOffset : 0) + currentTime;
  const shownTime = scrubbing && scrubTime != null ? scrubTime : globalCurrent;

  // ── source lifecycle ────────────────────────────────────────────────────────
  useEffect(() => {
    const video = videoRef.current;
    if (!video || !src) return undefined;

    setBuffering(true);
    setFatalError(null);
    sourceReloadRef.current = { at: performance.now(), startAt }; // watchdog: note (re)loads of the source

    const seekToStart = () => {
      if (startAt > 0.5) video.currentTime = startAt;
      const attempt = video.play();
      if (attempt) attempt.then(() => setNeedsTap(false)).catch(() => setNeedsTap(true));
    };

    let hls = null;
    if (!isHls) {
      // Direct play: the original file, downloaded progressively via range requests — no
      // transcode, near-instant start. Seeking/startAt are plain currentTime (range fetches).
      video.src = src;
      video.addEventListener("loadedmetadata", seekToStart, { once: true });
    } else if (Hls.isSupported()) {
      hls = new Hls({ maxBufferLength: 60, backBufferLength: 90, ...HLS_LOAD_CONFIG });
      hlsRef.current = hls;
      // watchdog: remember the most recent of the hls.js events that can move/flush the playhead, so a
      // backward jump can be correlated with (e.g.) a manifest re-parse or buffer flush that just fired.
      const recordHls = (name) => () => { lastHlsEventRef.current = { name, at: performance.now() }; };
      [Hls.Events.MANIFEST_PARSED, Hls.Events.LEVEL_LOADED, Hls.Events.LEVEL_SWITCHED,
        Hls.Events.FRAG_CHANGED, Hls.Events.BUFFER_FLUSHED].forEach((ev) => hls.on(ev, recordHls(ev)));
      hls.on(Hls.Events.MANIFEST_PARSED, seekToStart);
      hls.on(Hls.Events.ERROR, (_event, data) => {
        // A buffer stall (non-fatal) is the adaptive-downshift signal (§14.4).
        if (data.details === Hls.ErrorDetails.BUFFER_STALLED_ERROR) onStall?.();
        if (!data.fatal) return;
        // Standard hls.js recovery dance before giving up.
        if (data.type === Hls.ErrorTypes.NETWORK_ERROR) hls.startLoad();
        else if (data.type === Hls.ErrorTypes.MEDIA_ERROR) hls.recoverMediaError();
        else setFatalError("Playback failed — the stream could not be decoded.");
      });
      hls.loadSource(src);
      hls.attachMedia(video);
    } else if (video.canPlayType("application/vnd.apple.mpegurl")) {
      // Safari: native HLS.
      video.src = src;
      video.addEventListener("loadedmetadata", seekToStart, { once: true });
    } else {
      setFatalError("This browser can't play HLS video.");
    }

    return () => {
      if (hls) {
        hls.destroy();
        hlsRef.current = null;
      } else {
        video.removeAttribute("src");
        video.load();
      }
    };
  }, [src, startAt, isHls]);

  // ── element events ──────────────────────────────────────────────────────────
  useEffect(() => {
    const video = videoRef.current;
    if (!video) return undefined;

    const onTime = () => {
      setCurrentTime(video.currentTime);
      // Time advancing is ground truth that we're not stalled. Some browsers
      // resume after a `waiting` without firing `playing`, which would leave
      // the buffering bulbs stuck over a running video.
      if (!video.paused) setBuffering(false);
      // watchdog: remember the last good (playing, not-seeking) position to compare against on a seek.
      if (!video.paused && !video.seeking) lastSteadyTimeRef.current = video.currentTime;
    };

    // watchdog: a `seeking` we didn't initiate (no active/recent scrub) that moves the playhead
    // meaningfully BACKWARD is the "pops backwards on its own" bug — log it with the surrounding context.
    const onSeekingWatch = () => {
      const from = lastSteadyTimeRef.current;
      const delta = video.currentTime - from;
      const recentlyScrubbed = scrubbingRef.current || performance.now() - lastScrubEndRef.current < 700;
      if (delta < -1.5 && !recentlyScrubbed) {
        const now = performance.now();
        const hev = lastHlsEventRef.current;
        const src = sourceReloadRef.current;
        let bufEnd = NaN;
        try { if (video.buffered.length) bufEnd = video.buffered.end(video.buffered.length - 1); } catch { /* transient */ }
        // eslint-disable-next-line no-console
        console.warn(
          `[backseek] ${from.toFixed(1)}s → ${video.currentTime.toFixed(1)}s (Δ${delta.toFixed(1)}s) ` +
            `${video.paused ? "paused" : "playing"} | ` +
            `lastHls: ${hev ? `${hev.name} ${Math.round(now - hev.at)}ms ago` : "none"} | ` +
            `srcReload: ${src ? `${Math.round(now - src.at)}ms ago` : "none"} | ` +
            `bufferedEnd: ${isFinite(bufEnd) ? bufEnd.toFixed(1) : "?"}`
        );
      }
    };
    const onPlay = () => {
      setPlaying(true);
      setNeedsTap(false);
      onProgress?.(video.currentTime, false);
    };
    const onPause = () => {
      setPlaying(false);
      onProgress?.(video.currentTime, true);
    };
    const onSeeked = () => onProgress?.(video.currentTime, video.paused);
    const onWaiting = () => setBuffering(true);
    const onPlaying = () => setBuffering(false);
    const onBufferProgress = () => {
      try {
        if (video.buffered.length > 0) setBufferedEnd(video.buffered.end(video.buffered.length - 1));
      } catch {
        /* transient invalid ranges while switching sources */
      }
    };
    const onVideoEnded = () => onEnded?.(video.currentTime);

    video.addEventListener("timeupdate", onTime);
    video.addEventListener("seeking", onSeekingWatch);
    video.addEventListener("play", onPlay);
    video.addEventListener("pause", onPause);
    video.addEventListener("seeked", onSeeked);
    video.addEventListener("waiting", onWaiting);
    video.addEventListener("playing", onPlaying);
    video.addEventListener("progress", onBufferProgress);
    video.addEventListener("ended", onVideoEnded);
    return () => {
      video.removeEventListener("timeupdate", onTime);
      video.removeEventListener("seeking", onSeekingWatch);
      video.removeEventListener("play", onPlay);
      video.removeEventListener("pause", onPause);
      video.removeEventListener("seeked", onSeeked);
      video.removeEventListener("waiting", onWaiting);
      video.removeEventListener("playing", onPlaying);
      video.removeEventListener("progress", onBufferProgress);
      video.removeEventListener("ended", onVideoEnded);
    };
  }, [onProgress, onEnded]);

  // ── steady progress beat (~10s) while playing ───────────────────────────────
  useEffect(() => {
    if (!playing) return undefined;
    const beat = setInterval(() => {
      const video = videoRef.current;
      if (video && !video.paused) onProgress?.(video.currentTime, false);
    }, 10_000);
    return () => clearInterval(beat);
  }, [playing, onProgress]);

  // ── bandwidth telemetry for adaptive bitrate (§14.4) ────────────────────────
  // hls.js refines bandwidthEstimate as segments load; sample it while playing so
  // the page can climb rungs when there's headroom. Safari's native HLS exposes no
  // estimate, so ABR there leans on stalls + the initial connection guess instead.
  useEffect(() => {
    if (!playing || !onBandwidth) return undefined;
    const sample = setInterval(() => {
      const estimate = hlsRef.current?.bandwidthEstimate;
      if (estimate && isFinite(estimate)) onBandwidth(estimate);
    }, 5000);
    return () => clearInterval(sample);
  }, [playing, onBandwidth]);

  // ── volume ──────────────────────────────────────────────────────────────────
  useEffect(() => {
    const video = videoRef.current;
    if (video) {
      video.volume = volume;
      video.muted = muted;
    }
    window.localStorage.setItem("PlayerVolume", String(volume));
  }, [volume, muted]);

  // ── text subtitles (sidecar VTT) ────────────────────────────────────────────
  useEffect(() => {
    const video = videoRef.current;
    if (!video) return;
    for (const track of Array.from(video.textTracks)) {
      const matches = String(track.id) === String(selectedSubtitleIndex);
      track.mode = matches ? "showing" : "hidden";
    }
  }, [selectedSubtitleIndex, src]);

  // The currently-selected SOFT subtitle (sidecar VTT). null when off or when a burned-in image
  // sub is selected — only soft tracks can be re-timed client-side.
  const activeTextSub = subtitleTracks.find((t) => t.index === selectedSubtitleIndex && !!t.deliveryUrl) || null;

  // Picking a different subtitle starts its timing fresh (each file has its own sync); a stream
  // restart (quality/audio change) keeps `selectedSubtitleIndex`, so the nudge survives those.
  useEffect(() => {
    setSubtitleOffsetMs(0);
  }, [selectedSubtitleIndex]);

  // Apply the timing nudge by shifting the showing track's cue times. We stash each cue's original
  // times (keyed by the cue itself) so repeated nudges measure from the source timing, never compound.
  // Cues load asynchronously and a stream restart reloads the VTT with fresh cue objects, so we also
  // re-apply on the <track> 'load' event.
  useEffect(() => {
    const video = videoRef.current;
    if (!video) return undefined;
    const offsetSec = subtitleOffsetMs / 1000;
    const apply = () => {
      for (const track of Array.from(video.textTracks)) {
        if (String(track.id) !== String(selectedSubtitleIndex)) continue;
        const cues = track.cues;
        if (!cues) continue;
        for (const cue of Array.from(cues)) {
          let orig = cueOriginalsRef.current.get(cue);
          if (!orig) {
            orig = { start: cue.startTime, end: cue.endTime };
            cueOriginalsRef.current.set(cue, orig);
          }
          const ns = Math.max(0, orig.start + offsetSec);
          const ne = Math.max(ns + 0.001, orig.end + offsetSec);
          // Set in the order that keeps start <= end at every step, or the assignment throws.
          try {
            if (offsetSec >= 0) {
              if (cue.endTime !== ne) cue.endTime = ne;
              if (cue.startTime !== ns) cue.startTime = ns;
            } else {
              if (cue.startTime !== ns) cue.startTime = ns;
              if (cue.endTime !== ne) cue.endTime = ne;
            }
          } catch {
            /* a browser that disallows mutating cue times — nudge simply won't apply there */
          }
        }
      }
    };
    apply();
    const tracks = Array.from(video.querySelectorAll("track"));
    tracks.forEach((t) => t.addEventListener("load", apply));
    return () => tracks.forEach((t) => t.removeEventListener("load", apply));
  }, [subtitleOffsetMs, selectedSubtitleIndex, src]);

  const nudgeSubtitle = useCallback((deltaMs) => {
    setSubtitleOffsetMs((v) => Math.max(-SUBTITLE_OFFSET_LIMIT_MS, Math.min(SUBTITLE_OFFSET_LIMIT_MS, v + deltaMs)));
    setOffsetToast(true);
    clearTimeout(offsetToastTimer.current);
    offsetToastTimer.current = setTimeout(() => setOffsetToast(false), 1400);
  }, []);

  useEffect(() => () => clearTimeout(offsetToastTimer.current), []);

  // Persist caption appearance across sessions.
  useEffect(() => {
    window.localStorage.setItem("SubtitleStyle", JSON.stringify(subStyle));
  }, [subStyle]);

  // Write the chosen look into a single injected `::cue` rule. Appended to <head> at runtime so it
  // sits after the bundled stylesheet and wins (equal specificity, later in source order). Shared
  // by every player instance via a stable id; reflects whatever the current settings are.
  useEffect(() => {
    const css =
      `.vp-video::cue{` +
      `font-size:${subStyle.sizeVh}vh;` +
      `color:${subStyle.color};` +
      `font-family:${SUB_FONTS[subStyle.font] || SUB_FONTS.sans};` +
      `text-shadow:${SUB_EDGES[subStyle.edge] || "none"};` +
      `background:${subBg(subStyle.bgOpacity)};` +
      `}`;
    let el = document.getElementById("vp-cue-style");
    if (!el) {
      el = document.createElement("style");
      el.id = "vp-cue-style";
      document.head.appendChild(el);
    }
    el.textContent = css;
  }, [subStyle.sizeVh, subStyle.color, subStyle.font, subStyle.edge, subStyle.bgOpacity]);

  // Vertical lift: ::cue can't set position, but cue.line can. With snapToLines off, line is a
  // percentage of the video box from the top (100 = bottom), so 100 − liftPct raises the caption.
  // liftPct 0 restores the browser default (auto), which keeps its tasteful bottom inset.
  useEffect(() => {
    const video = videoRef.current;
    if (!video) return undefined;
    const apply = () => {
      for (const track of Array.from(video.textTracks)) {
        if (String(track.id) !== String(selectedSubtitleIndex)) continue;
        if (!track.cues) continue;
        for (const cue of Array.from(track.cues)) {
          try {
            // snapToLines off lets us position by percentage. lineAlign "end" makes `line` pin the
            // BOTTOM of the caption box, so 100 − liftPct puts liftPct=0 flush at the very bottom
            // (line=100 with the default top-align would hang the box off-screen) and raises from
            // there. (cue.line = "auto" only reached the browser's default inset, never the bottom.)
            cue.snapToLines = false;
            cue.lineAlign = "end";
            cue.line = Math.max(50, 100 - subStyle.liftPct);
          } catch {
            /* a browser that disallows mutating cue.line — lift simply won't apply */
          }
        }
      }
    };
    apply();
    const tracks = Array.from(video.querySelectorAll("track"));
    tracks.forEach((t) => t.addEventListener("load", apply));
    return () => tracks.forEach((t) => t.removeEventListener("load", apply));
  }, [subStyle.liftPct, selectedSubtitleIndex, src]);

  // ── Media Session: title + poster on the OS media overlay ──────────────────
  useEffect(() => {
    if (!("mediaSession" in navigator)) return undefined;
    navigator.mediaSession.metadata = new window.MediaMetadata({
      title: title || "MovieTheater",
      artist: metaLine || "",
      artwork: poster ? [{ src: poster, sizes: "512x512", type: "image/jpeg" }] : [],
    });
    const video = () => videoRef.current;
    const handlers = [
      ["play", () => video()?.play()],
      ["pause", () => video()?.pause()],
      ["seekbackward", () => seekBy(-10)],
      ["seekforward", () => seekBy(10)],
      ["seekto", (e) => seekTo(e.seekTime)],
    ];
    for (const [action, handler] of handlers) {
      try {
        navigator.mediaSession.setActionHandler(action, handler);
      } catch {
        /* action unsupported */
      }
    }
    return () => {
      for (const [action] of handlers) {
        try {
          navigator.mediaSession.setActionHandler(action, null);
        } catch {
          /* ignore */
        }
      }
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [title, metaLine, poster]);

  // ── fullscreen ──────────────────────────────────────────────────────────────
  useEffect(() => {
    const onChange = () => setFullscreen(!!document.fullscreenElement);
    document.addEventListener("fullscreenchange", onChange);
    return () => document.removeEventListener("fullscreenchange", onChange);
  }, []);

  const toggleFullscreen = useCallback(() => {
    if (document.fullscreenElement) document.exitFullscreen();
    else containerRef.current?.requestFullscreen?.();
  }, []);

  // ── transport ───────────────────────────────────────────────────────────────
  const togglePlay = useCallback(() => {
    const video = videoRef.current;
    if (!video) return;
    if (video.paused) {
      video.play().catch(() => setNeedsTap(true));
      setFlash("play");
    } else {
      video.pause();
      setFlash("pause");
    }
    setTimeout(() => setFlash(null), 500);
  }, []);

  const seekTo = useCallback(
    (seconds) => {
      const video = videoRef.current;
      if (!video) return;
      const clamped = Math.min(Math.max(seconds, 0), duration || video.duration || seconds);
      video.currentTime = clamped;
      setCurrentTime(clamped);
    },
    [duration]
  );

  const seekBy = useCallback((delta) => seekTo((videoRef.current?.currentTime ?? 0) + delta), [seekTo]);

  // Seek to a point on the (possibly combined) timeline: inside the current part it's a plain local
  // seek (no reload); a position in another part is handed up so the page loads that part at the
  // right offset. Defined before the keyboard handler that depends on it (no temporal-dead-zone).
  const seekDisplay = useCallback(
    (displaySeconds) => {
      if (combined) {
        const local = displaySeconds - partOffset;
        if (local >= 0 && local <= duration) seekTo(local);
        else onSeekGlobal?.(displaySeconds);
      } else {
        seekTo(displaySeconds);
      }
    },
    [combined, partOffset, duration, seekTo, onSeekGlobal]
  );

  // ── controls visibility: fade like house lights ─────────────────────────────
  const wakeControls = useCallback(() => {
    setControlsVisible(true);
    clearTimeout(idleTimerRef.current);
    idleTimerRef.current = setTimeout(() => {
      // Keep controls up while paused, scrubbing, or inside a menu.
      setControlsVisible((visible) => {
        const video = videoRef.current;
        if (!video || video.paused) return true;
        return false;
      });
    }, 3000);
  }, []);

  useEffect(() => {
    wakeControls();
    return () => clearTimeout(idleTimerRef.current);
  }, [wakeControls]);

  const hideChrome = playing && !controlsVisible && !openMenu && !scrubbing;

  // ── keyboard ────────────────────────────────────────────────────────────────
  const onKeyDown = useCallback(
    (e) => {
      if (e.target.tagName === "INPUT" || e.target.tagName === "TEXTAREA") return;
      const video = videoRef.current;
      let handled = true;
      switch (e.key) {
        case " ":
        case "k":
          togglePlay();
          break;
        case "ArrowLeft":
          seekBy(-10);
          break;
        case "ArrowRight":
          seekBy(10);
          break;
        case "j":
          seekBy(-10);
          break;
        case "l":
          seekBy(10);
          break;
        case "ArrowUp":
          setMuted(false);
          setVolume((v) => Math.min(1, v + 0.05));
          break;
        case "ArrowDown":
          setVolume((v) => Math.max(0, v - 0.05));
          break;
        case "m":
          setMuted((m) => !m);
          break;
        case "f":
          toggleFullscreen();
          break;
        case "c":
          if (subtitleTracks.length > 0)
            onSelectSubtitle?.(selectedSubtitleIndex == null ? subtitleTracks[0].index : null);
          break;
        // g/h nudge subtitle timing earlier/later (VLC-style), only when a soft text sub is showing.
        case "g":
          if (activeTextSub) nudgeSubtitle(-SUBTITLE_NUDGE_MS);
          else handled = false;
          break;
        case "h":
          if (activeTextSub) nudgeSubtitle(SUBTITLE_NUDGE_MS);
          else handled = false;
          break;
        default:
          if (/^[0-9]$/.test(e.key) && displayDuration > 0 && video) {
            seekDisplay((displayDuration * parseInt(e.key, 10)) / 10);
          } else {
            handled = false;
          }
      }
      if (handled) {
        e.preventDefault();
        wakeControls();
      }
    },
    [togglePlay, seekBy, seekDisplay, toggleFullscreen, displayDuration, subtitleTracks, selectedSubtitleIndex, onSelectSubtitle, wakeControls, activeTextSub, nudgeSubtitle]
  );

  // ── scrubber pointer logic ──────────────────────────────────────────────────
  const timeFromPointer = useCallback(
    (clientX) => {
      const rail = scrubRef.current;
      if (!rail || !displayDuration) return 0;
      const rect = rail.getBoundingClientRect();
      const ratio = Math.min(Math.max((clientX - rect.left) / rect.width, 0), 1);
      return ratio * displayDuration;
    },
    [displayDuration]
  );

  const onScrubPointerDown = (e) => {
    e.currentTarget.setPointerCapture(e.pointerId);
    setScrubbing(true);
    setScrubTime(timeFromPointer(e.clientX));
  };
  const onScrubPointerMove = (e) => {
    const t = timeFromPointer(e.clientX);
    setHoverTime(t);
    setHoverX(e.clientX - scrubRef.current.getBoundingClientRect().left);
    if (scrubbing) setScrubTime(t);
  };
  const onScrubPointerUp = (e) => {
    if (!scrubbing) return;
    setScrubbing(false);
    const t = timeFromPointer(e.clientX);
    setScrubTime(null);
    seekDisplay(t);
  };

  // Click = play/pause, double-click = fullscreen, without the double-fire.
  // But when the chrome has faded out, the first tap only brings it back — the
  // user is reaching for the controls, not trying to pause.
  const onSurfaceClick = () => {
    if (hideChrome) {
      wakeControls();
      return;
    }
    if (clickTimerRef.current) {
      clearTimeout(clickTimerRef.current);
      clickTimerRef.current = null;
      toggleFullscreen();
    } else {
      clickTimerRef.current = setTimeout(() => {
        clickTimerRef.current = null;
        togglePlay();
      }, 220);
    }
  };

  const playedPct = displayDuration > 0 ? (shownTime / displayDuration) * 100 : 0;
  const bufferedPct =
    displayDuration > 0 ? Math.min((((combined ? partOffset : 0) + bufferedEnd) / displayDuration) * 100, 100) : 0;

  return (
    /* eslint-disable jsx-a11y/no-noninteractive-element-interactions, jsx-a11y/no-static-element-interactions, jsx-a11y/click-events-have-key-events, jsx-a11y/media-has-caption */
    <div
      ref={containerRef}
      className={`vp-stage${hideChrome ? " vp-stage--idle" : ""}`}
      tabIndex={0}
      role="application"
      aria-label={`Video player: ${title || ""}`}
      onKeyDown={onKeyDown}
      onMouseMove={wakeControls}
      onMouseLeave={() => playing && setControlsVisible(false)}
    >
      <video ref={videoRef} className="vp-video" poster={poster} crossOrigin="anonymous" playsInline onClick={onSurfaceClick}>
        {subtitleTracks
          .filter((t) => t.deliveryUrl)
          .map((t) => (
            <track key={t.index} id={String(t.index)} kind="subtitles" label={t.label} src={t.deliveryUrl} srcLang={t.language || "en"} />
          ))}
      </video>

      {/* transient center play/pause bloom */}
      {flash && (
        <div className="vp-flash" aria-hidden="true">
          <span className={`vp-flash-glyph vp-flash-glyph--${flash}`} />
        </div>
      )}

      {/* transient subtitle-delay readout while nudging */}
      {offsetToast && activeTextSub && (
        <div className="vp-sub-toast" aria-live="polite">
          Subtitle delay {formatDelay(subtitleOffsetMs)}
        </div>
      )}

      {/* live caption-style preview: a real sample line, styled identically to the injected ::cue rule
          and placed at the same height the real cue lands (a small base inset + liftPct), so it's a
          faithful guide. Raised above the controls (z-index in CSS) so the bottom-most positions
          aren't hidden behind them. */}
      {styleOpen && (
        <div className="vp-sub-preview" style={{ bottom: `${subStyle.liftPct}%` }} aria-hidden="true">
          <span
            className="vp-sub-preview-text"
            style={{
              fontSize: `${subStyle.sizeVh}vh`,
              color: subStyle.color,
              fontFamily: SUB_FONTS[subStyle.font] || SUB_FONTS.sans,
              textShadow: SUB_EDGES[subStyle.edge] || "none",
              background: subBg(subStyle.bgOpacity),
            }}
          >
            Sample subtitle — how this looks
          </span>
        </div>
      )}

      {/* buffering: three marquee bulbs */}
      {buffering && !needsTap && !fatalError && (
        <div className="vp-bulbs" aria-label="Buffering">
          <span /><span /><span />
        </div>
      )}

      {/* autoplay was blocked — one gold tap target */}
      {needsTap && (
        <button className="vp-bigplay" onClick={togglePlay} aria-label="Play">
          <span className="vp-bigplay-tri" />
        </button>
      )}

      {fatalError && (
        <div className="vp-fatal">
          <div className="vp-fatal-rule" />
          <p>{fatalError}</p>
          <div className="vp-fatal-rule" />
        </div>
      )}

      {/* top bill: back + title */}
      <div className={`vp-topbar${hideChrome ? " vp-hidden" : ""}`}>
        <button className="vp-back" onClick={onBack} aria-label="Back">
          <span className="vp-back-arrow">←</span>
        </button>
        <div className="vp-bill">
          <div className="vp-bill-title">{title}</div>
          {metaLine && <div className="vp-bill-meta">{metaLine}</div>}
        </div>
      </div>

      {/* bottom controls */}
      <div className={`vp-controls${hideChrome ? " vp-hidden" : ""}`}>
        <div
          ref={scrubRef}
          className="vp-rail"
          onPointerDown={onScrubPointerDown}
          onPointerMove={onScrubPointerMove}
          onPointerUp={onScrubPointerUp}
          onPointerLeave={() => setHoverTime(null)}
        >
          {hoverTime != null && displayDuration > 0 && (
            <div className="vp-rail-tip" style={{ left: hoverX }}>
              {formatTime(hoverTime)}
            </div>
          )}
          <div className="vp-rail-track">
            <div className="vp-rail-buffered" style={{ width: `${bufferedPct}%` }} />
            <div className="vp-rail-played" style={{ width: `${playedPct}%` }} />
            {/* faint part seams on the combined timeline (where one CD/disc ends and the next begins) */}
            {combined &&
              partBoundaries.map((b, i) => (
                <div key={i} className="vp-rail-mark" style={{ left: `${(b / displayDuration) * 100}%` }} aria-hidden="true" />
              ))}
            <div className="vp-rail-head" style={{ left: `${playedPct}%` }} />
          </div>
        </div>

        <div className="vp-buttons">
          <button className="vp-btn" onClick={togglePlay} aria-label={playing ? "Pause" : "Play"}>
            {playing ? (
              <span className="vp-glyph-pause"><i /><i /></span>
            ) : (
              <span className="vp-glyph-play" />
            )}
          </button>

          <div className="vp-volume">
            <button className="vp-btn" onClick={() => setMuted((m) => !m)} aria-label={muted ? "Unmute" : "Mute"}>
              <span className={`vp-glyph-vol${muted || volume === 0 ? " vp-glyph-vol--off" : ""}`} />
            </button>
            <input
              className="vp-volume-slider"
              type="range"
              min="0"
              max="1"
              step="0.02"
              value={muted ? 0 : volume}
              aria-label="Volume"
              onChange={(e) => {
                setMuted(false);
                setVolume(parseFloat(e.target.value));
              }}
            />
          </div>

          <div className="vp-time">
            <span className="vp-time-now">{formatTime(shownTime)}</span>
            <span className="vp-time-sep">/</span>
            <span className="vp-time-total">{formatTime(displayDuration)}</span>
          </div>

          <div className="vp-spacer" />

          {isDirectStream && qualityKey === "original" && <span className="vp-direct-badge">Direct</span>}

          {subtitleTracks.length > 0 && (
            <button
              className={`vp-btn vp-btn-cc${selectedSubtitleIndex != null ? " vp-btn-cc--on" : ""}`}
              onClick={() => onSelectSubtitle?.(selectedSubtitleIndex == null ? subtitleTracks[0].index : null)}
              aria-label="Subtitles"
            >
              CC
            </button>
          )}

          <div className="vp-menu-anchor">
            <button
              className={`vp-btn vp-btn-gear${openMenu ? " vp-btn-gear--open" : ""}`}
              onClick={() => setOpenMenu((m) => (m ? null : "settings"))}
              aria-label="Settings"
              aria-expanded={!!openMenu}
            >
              ✦
            </button>

            {openMenu && (
              <div className="vp-menu" role="menu">
                <div className="vp-menu-section">Quality</div>
                {QUALITY_LADDER.map((q) => (
                  <button
                    key={q.key}
                    role="menuitemradio"
                    aria-checked={qualityKey === q.key}
                    className={`vp-menu-item${qualityKey === q.key ? " vp-menu-item--on" : ""}`}
                    onClick={() => {
                      setOpenMenu(null);
                      if (q.key !== qualityKey) onSelectQuality?.(q);
                    }}
                  >
                    <span className="vp-menu-dot" />
                    {q.label}
                    <span className="vp-menu-hint">{q.hint}</span>
                  </button>
                ))}

                {audioTracks.length > 1 && (
                  <>
                    <div className="vp-menu-section">Audio</div>
                    {audioTracks.map((t) => (
                      <button
                        key={t.index}
                        role="menuitemradio"
                        aria-checked={selectedAudioIndex === t.index}
                        className={`vp-menu-item${selectedAudioIndex === t.index ? " vp-menu-item--on" : ""}`}
                        onClick={() => {
                          setOpenMenu(null);
                          if (t.index !== selectedAudioIndex) onSelectAudio?.(t);
                        }}
                      >
                        <span className="vp-menu-dot" />
                        {t.label}
                      </button>
                    ))}
                  </>
                )}

                {subtitleTracks.length > 0 && (
                  <>
                    <div className="vp-menu-section">Subtitles</div>
                    <button
                      role="menuitemradio"
                      aria-checked={selectedSubtitleIndex == null}
                      className={`vp-menu-item${selectedSubtitleIndex == null ? " vp-menu-item--on" : ""}`}
                      onClick={() => {
                        setOpenMenu(null);
                        onSelectSubtitle?.(null);
                      }}
                    >
                      <span className="vp-menu-dot" />
                      Off
                    </button>
                    {subtitleTracks.map((t) => (
                      <button
                        key={t.index}
                        role="menuitemradio"
                        aria-checked={selectedSubtitleIndex === t.index}
                        className={`vp-menu-item${selectedSubtitleIndex === t.index ? " vp-menu-item--on" : ""}`}
                        onClick={() => {
                          setOpenMenu(null);
                          onSelectSubtitle?.(t.index);
                        }}
                      >
                        <span className="vp-menu-dot" />
                        {t.label}
                        {!t.deliveryUrl && <span className="vp-menu-hint">burned in</span>}
                      </button>
                    ))}
                    {activeTextSub && (
                      <div className="vp-menu-delay">
                        <span className="vp-menu-delay-label">Delay</span>
                        <button
                          className="vp-menu-delay-btn"
                          onClick={() => nudgeSubtitle(-SUBTITLE_NUDGE_MS)}
                          aria-label="Subtitles earlier"
                          title="Earlier (g)"
                        >
                          −
                        </button>
                        <span className="vp-menu-delay-val">{formatDelay(subtitleOffsetMs)}</span>
                        <button
                          className="vp-menu-delay-btn"
                          onClick={() => nudgeSubtitle(SUBTITLE_NUDGE_MS)}
                          aria-label="Subtitles later"
                          title="Later (h)"
                        >
                          +
                        </button>
                        <button
                          className="vp-menu-delay-reset"
                          onClick={() => setSubtitleOffsetMs(0)}
                          disabled={subtitleOffsetMs === 0}
                        >
                          Reset
                        </button>
                      </div>
                    )}

                    {/* caption appearance editor — toggling it reveals the live on-video sample */}
                    <button
                      className={`vp-menu-item${styleOpen ? " vp-menu-item--on" : ""}`}
                      onClick={() => setStyleOpen((o) => !o)}
                      aria-expanded={styleOpen}
                    >
                      <span className="vp-menu-dot" />
                      Subtitle style
                      <span className="vp-menu-hint">{styleOpen ? "▾" : "▸"}</span>
                    </button>
                    {styleOpen && (
                      <div className="vp-substyle">
                        <div className="vp-substyle-row">
                          <span className="vp-substyle-label">Size</span>
                          <input
                            className="vp-substyle-slider"
                            type="range"
                            min={SUB_SIZE_MIN}
                            max={SUB_SIZE_MAX}
                            step="0.1"
                            value={subStyle.sizeVh}
                            aria-label="Subtitle size"
                            onChange={(e) => setStyle({ sizeVh: parseFloat(e.target.value) })}
                          />
                          <span className="vp-substyle-val">
                            {Math.round((subStyle.sizeVh / 100) * (typeof window !== "undefined" ? window.innerHeight : 1080))}px
                          </span>
                        </div>

                        <div className="vp-substyle-row">
                          <span className="vp-substyle-label">Color</span>
                          <div className="vp-substyle-swatches">
                            {SUB_COLORS.map((c) => (
                              <button
                                key={c.value}
                                type="button"
                                className={`vp-substyle-swatch${subStyle.color === c.value ? " vp-substyle-swatch--on" : ""}`}
                                style={{ background: c.value }}
                                onClick={() => setStyle({ color: c.value })}
                                aria-label={c.label}
                                aria-pressed={subStyle.color === c.value}
                                title={c.label}
                              />
                            ))}
                          </div>
                        </div>

                        <div className="vp-substyle-row">
                          <span className="vp-substyle-label">Box</span>
                          <input
                            className="vp-substyle-slider"
                            type="range"
                            min="0"
                            max="1"
                            step="0.05"
                            value={subStyle.bgOpacity}
                            aria-label="Subtitle background opacity"
                            onChange={(e) => setStyle({ bgOpacity: parseFloat(e.target.value) })}
                          />
                          <span className="vp-substyle-val">
                            {subStyle.bgOpacity <= 0 ? "Off" : `${Math.round(subStyle.bgOpacity * 100)}%`}
                          </span>
                        </div>

                        <div className="vp-substyle-row">
                          <span className="vp-substyle-label">Font</span>
                          <div className="vp-substyle-seg">
                            {SUB_FONT_OPTIONS.map((f) => (
                              <button
                                key={f.key}
                                type="button"
                                className={`vp-substyle-segbtn${subStyle.font === f.key ? " vp-substyle-segbtn--on" : ""}`}
                                style={{ fontFamily: SUB_FONTS[f.key] }}
                                onClick={() => setStyle({ font: f.key })}
                                aria-pressed={subStyle.font === f.key}
                              >
                                {f.label}
                              </button>
                            ))}
                          </div>
                        </div>

                        <div className="vp-substyle-row">
                          <span className="vp-substyle-label">Edge</span>
                          <div className="vp-substyle-seg">
                            {SUB_EDGE_OPTIONS.map((ed) => (
                              <button
                                key={ed.key}
                                type="button"
                                className={`vp-substyle-segbtn${subStyle.edge === ed.key ? " vp-substyle-segbtn--on" : ""}`}
                                onClick={() => setStyle({ edge: ed.key })}
                                aria-pressed={subStyle.edge === ed.key}
                              >
                                {ed.label}
                              </button>
                            ))}
                          </div>
                        </div>

                        <div className="vp-substyle-row">
                          <span className="vp-substyle-label">Position</span>
                          <input
                            className="vp-substyle-slider"
                            type="range"
                            min="0"
                            max={SUB_LIFT_MAX}
                            step="1"
                            value={subStyle.liftPct}
                            aria-label="Subtitle vertical position"
                            onChange={(e) => setStyle({ liftPct: parseInt(e.target.value, 10) })}
                          />
                          <span className="vp-substyle-val">
                            {subStyle.liftPct === 0 ? "Bottom" : `+${subStyle.liftPct}`}
                          </span>
                        </div>

                        <button
                          type="button"
                          className="vp-substyle-reset"
                          onClick={() => setSubStyle({ ...SUB_STYLE_DEFAULTS })}
                          disabled={
                            subStyle.sizeVh === SUB_STYLE_DEFAULTS.sizeVh &&
                            subStyle.color === SUB_STYLE_DEFAULTS.color &&
                            subStyle.font === SUB_STYLE_DEFAULTS.font &&
                            subStyle.edge === SUB_STYLE_DEFAULTS.edge &&
                            subStyle.liftPct === SUB_STYLE_DEFAULTS.liftPct &&
                            subStyle.bgOpacity === SUB_STYLE_DEFAULTS.bgOpacity
                          }
                        >
                          Reset to defaults
                        </button>
                      </div>
                    )}
                  </>
                )}

                <div className="vp-menu-section">Playing</div>
                <div className="vp-menu-readout">
                  {formatPlaying({
                    qualityKey,
                    autoLabel: qualityDetail,
                    videoCodec,
                    isDirectStream,
                    audio: deliveredLayout((audioTracks.find((t) => t.index === selectedAudioIndex) || audioTracks[0])?.channels),
                  })}
                </div>
              </div>
            )}
          </div>

          <button className="vp-btn" onClick={toggleFullscreen} aria-label={fullscreen ? "Exit fullscreen" : "Fullscreen"}>
            <span className={`vp-glyph-fs${fullscreen ? " vp-glyph-fs--exit" : ""}`} />
          </button>
        </div>
      </div>
    </div>
  );
}

export { formatTime, TICKS_PER_SECOND };
export default VideoPlayer;
