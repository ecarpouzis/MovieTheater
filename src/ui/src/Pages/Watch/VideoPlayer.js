import { useState, useEffect, useRef, useCallback } from "react";
import Hls from "hls.js";
import { createHls, bandwidthSample } from "../../streamEngine";
import { formatClock as formatTime } from "../../utils/format";
import {
  QUALITY_LADDER, codecLabel, channelLayout, deliveredLayout, formatPlaying,
  qualityOptions, audioOptions, subtitleOptions, deliveredAudio,
} from "../../playerMenuModel";
import { useIdleChrome } from "../../useIdleChrome";
import { useVideoIncidents } from "../../videoIncidents";
import { useWakeLock } from "../../useWakeLock";
import { useMediaSession } from "../../useMediaSession";
import { usePictureInPicture } from "../../usePictureInPicture";
import { usePlaybackRate, PLAYBACK_RATES } from "../../usePlaybackRate";
import { usePgsSubtitle } from "../../usePgsSubtitle";
import { useAssSubtitle } from "../../useAssSubtitle";
import { useSubtitleStyle, useCueLift, useSubtitleOffset, formatDelay, SUBTITLE_NUDGE_MS } from "../../subtitleStyle";
import { SubtitleStyleControls, SubtitleStylePreview, SubtitleSyncControls } from "../../SubtitleStyleEditor";
import "./VideoPlayer.css";
import { readStored, writeStored } from "../../utils/storage";

// The menu vocabulary (QUALITY_LADDER, formatPlaying, deliveredLayout, codecLabel, channelLayout)
// moved to playerMenuModel.js — shared with the TV player — and is re-exported here so existing
// import paths keep working.
export { QUALITY_LADDER, codecLabel, channelLayout, deliveredLayout, formatPlaying };

const TICKS_PER_SECOND = 10_000_000;

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
  bufferingLabel = null,
  incident = null,
  combinedDuration = 0,
  partOffset = 0,
  partBoundaries = [],
  onSeekGlobal,
  onBack,
}) {
  const containerRef = useRef(null);
  const videoRef = useRef(null);
  const hlsRef = useRef(null);
  const clickTimerRef = useRef(null);
  const scrubRef = useRef(null);

  const [playing, setPlaying] = useState(false);
  const [needsTap, setNeedsTap] = useState(false); // autoplay was blocked
  const [buffering, setBuffering] = useState(true);
  const [currentTime, setCurrentTime] = useState(startAt);
  const [bufferedEnd, setBufferedEnd] = useState(0);
  const [volume, setVolume] = useState(() => {
    const stored = parseFloat(readStored("PlayerVolume"));
    return isFinite(stored) ? Math.min(Math.max(stored, 0), 1) : 1;
  });
  const [muted, setMuted] = useState(false);
  const [fullscreen, setFullscreen] = useState(false);
  const [openMenu, setOpenMenu] = useState(null); // 'settings' | null
  const [scrubbing, setScrubbing] = useState(false);
  const [scrubTime, setScrubTime] = useState(null);
  const [hoverTime, setHoverTime] = useState(null);
  const [hoverX, setHoverX] = useState(0);
  const [flash, setFlash] = useState(null); // transient center icon: 'play' | 'pause'
  const [fatalError, setFatalError] = useState(null);

  // ── player time vs content time ─────────────────────────────────────────────
  // An HLS session that starts mid-file (a resume, a seek, an ABR/quality restart) lands on the
  // previous SOURCE keyframe, so hls.js's timeline sits up to one GOP ahead of true content time:
  // currentTime = content + timelineOffset (streamEngine's timelineOffsetFromInitPts). Everything the
  // SERVER interprets as a position — progress beats, the resume it stores, the offset a restart
  // re-opens at — must be content time; the scrub bar and seeks stay on the player timeline they
  // address. State for the subtitle renderers (it lands after they mount), ref for the native event
  // handlers and the 10s beat, which aren't re-bound per render.
  const [timelineOffset, setTimelineOffset] = useState(0);
  const timelineOffsetRef = useRef(0);
  const contentTimeOf = useCallback(
    (video) => Math.max(0, (video?.currentTime ?? 0) - timelineOffsetRef.current),
    []
  );

  // Self-reported playback failures — the shared recorder both players use. `src` is the session
  // key: it changes on every restart (a resume, a quality/audio/subtitle pick, an ABR adapt), and
  // the seconds after one of those are an EXPECTED freeze, not a stall. Nothing about detection
  // lives in this file; see videoIncidents.
  useVideoIncidents({
    player: "watch",
    videoRef,
    identity: incident?.identity,
    ladder: {
      qualityKey,
      autoBps: incident?.autoBps ?? null,
      copied: isDirectStream,
      codec: videoCodec,
      sourceVideoBps: incident?.sourceVideoBps ?? null,
    },
    sessionKey: src,
    durationSeconds,
    timelineOffsetRef,
  });

  // Subtitle timing nudge — shift the showing soft (sidecar VTT) track's cues so the viewer can fix
  // small sync drift. Shared with the TV player; only meaningful for soft tracks (burned-in image
  // subs are baked into the picture server-side and can't be moved client-side).
  const {
    offsetMs: subtitleOffsetMs,
    nudge: nudgeSubtitle,
    reset: resetSubtitleOffset,
    toast: offsetToast,
    rateScale: subtitleRateScale,
    abStep: subtitleAbStep,
    abError: subtitleAbError,
    beginSync: beginSubtitleSync,
    capturePoint: captureSubtitleSyncPoint,
    cancelSync: cancelSubtitleSync,
  } = useSubtitleOffset(videoRef, selectedSubtitleIndex, src, timelineOffset);

  // Caption appearance (size/color/font/edge/box/lift), shared with the TV player via a hook that
  // owns persistence and the injected ::cue rule. `styleOpen` reveals the editor + on-video preview.
  const { subStyle, setSubStyle, setStyle, styleOpen, setStyleOpen } = useSubtitleStyle();

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
    // Each session re-rolls its own offset (and direct play has none) — zero it before attaching so a
    // stale offset can't outlive the stream that produced it.
    timelineOffsetRef.current = 0;
    setTimelineOffset(0);
    sourceReloadRef.current = { at: performance.now(), startAt }; // watchdog: note (re)loads of the source
    // Diagnostic: every source (re)load states where it was ASKED to start vs where the element
    // actually sits. The 2026-08-16 stall-restart requested a position 15.0s ahead of where playback
    // settled (a backward segment walk + two ffmpeg respawns) — this line makes the next mismatch
    // self-documenting instead of a cross-server log dig.
    {
      let bufEnd = NaN;
      try { if (video.buffered.length) bufEnd = video.buffered.end(video.buffered.length - 1); } catch { /* transient */ }
      // eslint-disable-next-line no-console
      console.warn(
        `[restart] startAt=${startAt.toFixed(2)}s element=${video.currentTime.toFixed(2)}s ` +
          `bufferedEnd=${isFinite(bufEnd) ? bufEnd.toFixed(2) : "?"}`
      );
    }

    const tryPlay = () => {
      const attempt = video.play();
      if (attempt) attempt.then(() => setNeedsTap(false)).catch(() => setNeedsTap(true));
    };
    // Native branches (direct play, Safari HLS) have no startPosition knob, so they seek the element
    // once metadata is in. The hls.js branch instead opens AT the offset (see startPosition below).
    const seekToStart = () => {
      if (startAt > 0.5) video.currentTime = startAt;
      tryPlay();
    };

    let hls = null;
    if (!isHls) {
      // Direct play: the original file, downloaded progressively via range requests — no
      // transcode, near-instant start. Seeking/startAt are plain currentTime (range fetches).
      video.src = src;
      video.addEventListener("loadedmetadata", seekToStart, { once: true });
    } else if (Hls.isSupported()) {
      // Shared engine: buffer config + error recovery live in createHls (see streamEngine.js) so the
      // Watch and TV players can't drift. Watch keeps a 90s back buffer (it's freely rewindable).
      hls = createHls({
        // 30, not the 90 Watch used to keep: Chrome caps a video SourceBuffer at ~150 MB, and the
        // back buffer competes with the FORWARD buffer for that quota — at a 23 Mbps remux the
        // forward buffer measured ~30s instead of the configured 120 (2026-08-16). Rewind past 30s
        // still works; it just re-fetches segments (they persist server-side for the session).
        backBufferLength: 30,
        // Open AT the resume/restart offset rather than loading from 0 then seeking — load-then-seek
        // wastes a segment-0 fetch (a second cold transcode start) and is the join-time A/V-desync churn
        // our TV player already avoids this way. This effect re-runs with startAt at the live position on
        // every resume / quality-audio-subtitle change / ABR adapt, so each restart re-seeds cleanly.
        startPosition: startAt > 0.5 ? startAt : undefined,
        onStall,
        onFatal: () => setFatalError("Playback failed — the stream could not be decoded."),
        onTimelineOffset: (offset) => {
          timelineOffsetRef.current = offset;
          setTimelineOffset(offset);
        },
      });
      hlsRef.current = hls;
      // watchdog: remember the most recent of the hls.js events that can move/flush the playhead, so a
      // backward jump can be correlated with (e.g.) a manifest re-parse or buffer flush that just fired.
      const recordHls = (name) => () => { lastHlsEventRef.current = { name, at: performance.now() }; };
      [Hls.Events.MANIFEST_PARSED, Hls.Events.LEVEL_LOADED, Hls.Events.LEVEL_SWITCHED,
        Hls.Events.FRAG_CHANGED, Hls.Events.BUFFER_FLUSHED].forEach((ev) => hls.on(ev, recordHls(ev)));
      hls.on(Hls.Events.MANIFEST_PARSED, tryPlay); // position handled by startPosition, not a post-load seek
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
      onProgress?.(contentTimeOf(video), false);
    };
    const onPause = () => {
      setPlaying(false);
      onProgress?.(contentTimeOf(video), true);
    };
    const onSeeked = () => onProgress?.(contentTimeOf(video), video.paused);
    const onWaiting = () => setBuffering(true);
    const onPlaying = () => setBuffering(false);
    const onBufferProgress = () => {
      try {
        if (video.buffered.length > 0) setBufferedEnd(video.buffered.end(video.buffered.length - 1));
      } catch {
        /* transient invalid ranges while switching sources */
      }
    };
    const onVideoEnded = () => onEnded?.(contentTimeOf(video));

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
  }, [onProgress, onEnded, contentTimeOf]);

  // ── steady progress beat (~10s), paused or not ───────────────────────────────
  // Jellyfin's HLS job has a 60s ping timeout kept alive ONLY by these progress reports —
  // segment fetches do NOT reset it. So a pause longer than ~1min would make the server kill
  // ffmpeg and delete its segments; the resume then stalls and pays a cold re-transcode. The
  // beat therefore runs unconditionally, reporting the REAL paused flag (server forwards it as
  // IsPaused), matching jellyfin-web's unconditional 10s report. It's cleaned up on unmount and
  // when playback truly ends — VideoPlayer is only mounted while the page is in its "playing"
  // phase, so leaving/ending unmounts it and clears this interval.
  useEffect(() => {
    const beat = setInterval(() => {
      const video = videoRef.current;
      if (video) onProgress?.(contentTimeOf(video), video.paused);
    }, 10_000);
    return () => clearInterval(beat);
  }, [onProgress, contentTimeOf]);

  // ── bandwidth telemetry for adaptive bitrate (§14.4) ────────────────────────
  // hls.js refines bandwidthEstimate as segments load; sample it while playing so
  // the page can climb rungs when there's headroom. Safari's native HLS exposes no
  // estimate, so ABR there leans on stalls + the initial connection guess instead.
  useEffect(() => {
    if (!playing || !onBandwidth) return undefined;
    const sample = setInterval(() => {
      // bandwidthSample returns null while the estimator is still serving its canned default — the
      // `playing` gate above does NOT cover an ABR restart (the element never fires `pause`), so
      // without this the fresh instance's 500 kbps placeholder would be recorded as a real reading.
      const estimate = bandwidthSample(hlsRef.current);
      if (estimate) onBandwidth(estimate);
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
    writeStored("PlayerVolume", volume);
  }, [volume, muted]);

  // ── text subtitles (sidecar VTT) ────────────────────────────────────────────
  // "disabled", NOT "hidden", for the tracks we aren't showing: a hidden track is still ACTIVE, so the
  // browser fetches and parses its cue file. With one <track> per embedded sub that meant every track
  // loaded at once — Don't Look Up (33 tracks) fired 33 concurrent Stream.vtt requests on open, which
  // Jellyfin answered with a single ffmpeg demuxing all 33 out of a 12.9 GB 4K MKV: 119 s of NAS reads
  // racing the video copy of that same file, and all 33 requests dead at the gateway's 100 s timeout
  // (measured 2026-08-17). Nothing here reads cues off a non-showing track, so disabled costs nothing;
  // the selected track loads on demand and useSubtitleStyle/useCueLift re-apply on its 'load'.
  useEffect(() => {
    const video = videoRef.current;
    if (!video) return;
    for (const track of Array.from(video.textTracks)) {
      const matches = String(track.id) === String(selectedSubtitleIndex);
      track.mode = matches ? "showing" : "disabled";
    }
  }, [selectedSubtitleIndex, src]);

  // The currently-selected SOFT subtitle (sidecar VTT). null when off, a burned-in image sub, or a
  // client-rendered PGS sub — only soft text cues can be re-timed client-side, so the delay UI gates here.
  const activeTextSub =
    subtitleTracks.find(
      (t) => t.index === selectedSubtitleIndex && !!t.deliveryUrl && t.kind !== "image-pgs" && t.kind !== "ass"
    ) || null;

  // Vertical lift for the showing track's cues. Size/color/font/edge/box ride on the injected
  // ::cue rule from useSubtitleStyle; position can't be set via ::cue, so it's applied per-cue here.
  useCueLift(videoRef, selectedSubtitleIndex, src, subStyle.liftPct);

  // Client-rendered PGS (Blu-ray bitmap) subs — drawn over the video by libpgs so the server copies the
  // video instead of burning the bitmap in. Active only while the selected track is a PGS image sub.
  const activePgsSub = subtitleTracks.find((t) => t.index === selectedSubtitleIndex && t.kind === "image-pgs");
  usePgsSubtitle(videoRef, activePgsSub ? activePgsSub.deliveryUrl : null, timelineOffset);

  // Client-rendered ASS/SSA via libass — full typesetting, also keeps the video copied (no flatten to VTT).
  const activeAssSub = subtitleTracks.find((t) => t.index === selectedSubtitleIndex && t.kind === "ass");
  useAssSubtitle(videoRef, activeAssSub ? activeAssSub.deliveryUrl : null, timelineOffset);

  // Keep the screen awake during a film — shared with the TV player.
  useWakeLock();

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

  // ── OS media integration + extra playback controls (shared hooks) ────────────
  const { rate: playbackRate, setRate: setPlaybackRate, cycleRate } = usePlaybackRate(videoRef, src);
  const pip = usePictureInPicture(videoRef);
  // Lock-screen / media-key now-playing + an accurate position scrubber (setPositionState lives in the
  // hook). Defined here so the seek handlers it references are already in scope.
  useMediaSession({
    videoRef,
    title,
    subtitle: metaLine,
    poster,
    actions: {
      play: () => videoRef.current?.play(),
      pause: () => videoRef.current?.pause(),
      seekbackward: () => seekBy(-10),
      seekforward: () => seekBy(10),
      seekto: (d) => { if (d?.seekTime != null) seekTo(d.seekTime); },
    },
  });

  // ── controls visibility: fade like house lights (useIdleChrome — shared with the TV player).
  // Menu/scrub holds stay at the render gate below, exactly as before: closing the menu drops the
  // chrome immediately rather than restarting a countdown.
  const { visible: controlsVisible, wake: wakeControls, hide: hideControls } = useIdleChrome({ videoRef });

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
        case "<":
          cycleRate(-1);
          break;
        case ">":
          cycleRate(1);
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
    [togglePlay, seekBy, seekDisplay, toggleFullscreen, displayDuration, subtitleTracks, selectedSubtitleIndex, onSelectSubtitle, wakeControls, activeTextSub, nudgeSubtitle, cycleRate]
  );

  // Listen on window (not just the focused stage) so the spacebar pauses the moment the player is up —
  // no click-to-focus first — matching the TV player. The INPUT/TEXTAREA guard inside keeps the volume
  // slider and any field typing intact. VideoPlayer is only mounted while a film is playing, so this
  // never steals keys from the resume/ended cards.
  useEffect(() => {
    window.addEventListener("keydown", onKeyDown);
    return () => window.removeEventListener("keydown", onKeyDown);
  }, [onKeyDown]);

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
      onMouseMove={wakeControls}
      onMouseLeave={() => playing && hideControls()}
    >
      <video ref={videoRef} className="vp-video" poster={poster} crossOrigin="anonymous" playsInline onClick={onSurfaceClick}>
        {subtitleTracks
          .filter((t) => t.deliveryUrl && t.kind !== "image-pgs" && t.kind !== "ass")
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

      {/* live caption-style preview (shared component): faithful sample at the real caption height */}
      {styleOpen && <SubtitleStylePreview subStyle={subStyle} />}

      {/* buffering: three marquee bulbs — labeled when the page knows WHY (an ABR quality switch),
          so a deliberate restart doesn't read as a failure and invite a refresh (2026-08-16). */}
      {buffering && !needsTap && !fatalError && (
        <div className="vp-bulbs" aria-label={bufferingLabel || "Buffering"}>
          <div className="vp-bulbs-row"><span /><span /><span /></div>
          {bufferingLabel && <div className="vp-bulbs-label">{bufferingLabel}</div>}
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
                {qualityOptions(qualityKey).map((q) => (
                  <button
                    key={q.key}
                    role="menuitemradio"
                    aria-checked={q.selected}
                    className={`vp-menu-item${q.selected ? " vp-menu-item--on" : ""}`}
                    onClick={() => {
                      setOpenMenu(null);
                      if (!q.selected) onSelectQuality?.(q);
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
                    {audioOptions(audioTracks, selectedAudioIndex).map((t) => (
                      <button
                        key={t.index}
                        role="menuitemradio"
                        aria-checked={t.selected}
                        className={`vp-menu-item${t.selected ? " vp-menu-item--on" : ""}`}
                        onClick={() => {
                          setOpenMenu(null);
                          if (!t.selected) onSelectAudio?.(t.track);
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
                    {subtitleOptions(subtitleTracks, selectedSubtitleIndex).map((t) => (
                      <button
                        key={t.index ?? "off"}
                        role="menuitemradio"
                        aria-checked={t.selected}
                        className={`vp-menu-item${t.selected ? " vp-menu-item--on" : ""}`}
                        onClick={() => {
                          setOpenMenu(null);
                          onSelectSubtitle?.(t.index);
                        }}
                      >
                        <span className="vp-menu-dot" />
                        {t.label}
                        {t.hint && <span className="vp-menu-hint">{t.hint}</span>}
                      </button>
                    ))}
                    {activeTextSub && (
                      <SubtitleSyncControls
                        offsetMs={subtitleOffsetMs}
                        nudge={nudgeSubtitle}
                        reset={resetSubtitleOffset}
                        rateScale={subtitleRateScale}
                        abStep={subtitleAbStep}
                        abError={subtitleAbError}
                        beginSync={beginSubtitleSync}
                        capturePoint={captureSubtitleSyncPoint}
                        cancelSync={cancelSubtitleSync}
                      />
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
                      <SubtitleStyleControls subStyle={subStyle} setStyle={setStyle} setSubStyle={setSubStyle} />
                    )}
                  </>
                )}

                <div className="vp-menu-section">Speed</div>
                {PLAYBACK_RATES.map((r) => (
                  <button
                    key={r}
                    role="menuitemradio"
                    aria-checked={playbackRate === r}
                    className={`vp-menu-item${playbackRate === r ? " vp-menu-item--on" : ""}`}
                    onClick={() => {
                      setOpenMenu(null);
                      setPlaybackRate(r);
                    }}
                  >
                    <span className="vp-menu-dot" />
                    {r === 1 ? "Normal" : `${r}×`}
                  </button>
                ))}

                <div className="vp-menu-section">Playing</div>
                <div className="vp-menu-readout">
                  {formatPlaying({
                    qualityKey,
                    autoLabel: qualityDetail,
                    videoCodec,
                    isHls,
                    isDirectStream,
                    audio: deliveredAudio(audioTracks, selectedAudioIndex),
                  })}
                </div>
              </div>
            )}
          </div>

          {pip.supported && (
            <button
              className={`vp-btn vp-btn-pip${pip.active ? " vp-btn-pip--on" : ""}`}
              onClick={pip.toggle}
              aria-label="Picture in picture"
              title="Picture in picture"
            >
              <span className="vp-glyph-pip" />
            </button>
          )}

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
