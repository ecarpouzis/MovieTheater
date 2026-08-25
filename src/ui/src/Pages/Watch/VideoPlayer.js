import { useState, useEffect, useRef, useCallback } from "react";
import Hls from "hls.js";
import { createHls, bandwidthSample, canRemotePlay } from "../../streamEngine";
import { formatClock as formatTime } from "../../utils/format";
import {
  QUALITY_LADDER, codecLabel, channelLayout, deliveredLayout, formatPlaying,
  qualityOptions, audioOptions, subtitleOptions, deliveredAudio, tvStatusLine,
} from "../../playerMenuModel";
import { CAST_PROFILES } from "../../castProfiles";
import { useIdleChrome } from "../../useIdleChrome";
import { useVideoIncidents } from "../../videoIncidents";
import { useWakeLock } from "../../useWakeLock";
import { useMediaSession } from "../../useMediaSession";
import { usePictureInPicture } from "../../usePictureInPicture";
import { useAirPlay } from "../../useAirPlay";
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

// The receiver profiles offered in the menu, in the order they're shown: safest first, so a viewer
// who doesn't know what their dongle is keeps the one that works everywhere.
const CAST_PROFILE_OPTIONS = [CAST_PROFILES.baseline, CAST_PROFILES.hevc4k];

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
  cast = null,
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
  // Whether the CURRENT source could be handed to an AirPlay receiver. Set by the source effect
  // because only it knows which of the three attach paths was taken (see canRemotePlay); state, not
  // a ref, because the AirPlay button's existence depends on it.
  const [remotePlayable, setRemotePlayable] = useState(true);

  // ── casting ─────────────────────────────────────────────────────────────────
  // While casting, THE RECEIVER IS THE PLAYER. It holds the clock, the buffer, the volume and the
  // decode; this <video> holds nothing at all, because the source effect below never attaches to it.
  // So every transport call and every readout has to come from the mirrored remote player instead.
  //
  // That switch is deliberately this one flag applied at each site, rather than a transport
  // abstraction both paths implement. The two are not symmetric — the local path has a buffered
  // range, a playback rate, PiP, client-rendered PGS/ASS overlays and a subtitle-timing nudge, and
  // the remote path has none of those — so an abstraction would spend most of its surface
  // documenting which half doesn't apply. Reading `casting ? … : …` at the six places it matters is
  // shorter and truer than that.
  const casting = !!cast?.connected;
  const remote = cast?.remote || null;
  const castTime = remote?.currentTime || 0;
  // A cast is "playing" only once the receiver actually holds the media: between requestSession and
  // the load resolving, isPaused is false but nothing is on screen, and treating that as playing
  // fades the chrome out over a title card.
  const castPlaying = !!(remote?.mediaLoaded && !remote.paused);
  // The 10s progress beat and the native event handlers are not re-bound per render, so the cast
  // position has to reach them through a ref rather than a closure.
  const castStateRef = useRef({ casting: false, time: 0, paused: false });
  castStateRef.current = { casting, time: castTime, paused: !!remote?.paused };

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
  // The clock comes from whoever is actually playing. Everything downstream (the rail, the readout,
  // the scrub target, the keyboard percentage jumps) reads these, so this is the only place the two
  // playback paths have to be told apart for timing.
  const effectiveCurrentTime = casting ? castTime : currentTime;
  const effectivePlaying = casting ? castPlaying : playing;
  const globalCurrent = (combined ? partOffset : 0) + effectiveCurrentTime;
  const shownTime = scrubbing && scrubTime != null ? scrubTime : globalCurrent;

  // ── source lifecycle ────────────────────────────────────────────────────────
  useEffect(() => {
    const video = videoRef.current;
    if (!video || !src) return undefined;
    // Casting: attach nothing. The previous run's cleanup has already destroyed the hls.js instance
    // (or cleared the src), so returning here leaves the element genuinely empty rather than quietly
    // pulling a second copy of the same transcode down this tab's connection while the TV pulls the
    // first — which would double the load on the same ffmpeg and the same uplink for a picture
    // nobody is looking at.
    if (casting) {
      setBuffering(false);
      setFatalError(null);
      return undefined;
    }

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
      setRemotePlayable(true);
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
      setRemotePlayable(canRemotePlay(hls));
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
      setRemotePlayable(true);
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
  }, [src, startAt, isHls, casting]);

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
    // Every one of these reports a POSITION, so each has to stand down while casting: the element is
    // detached and sitting at 0, and detaching it (removeAttribute + load) is itself capable of
    // firing `pause`. One stray report at 0 overwrites the resume point for the whole title.
    const onPlay = () => {
      if (castStateRef.current.casting) return;
      setPlaying(true);
      setNeedsTap(false);
      onProgress?.(contentTimeOf(video), false);
    };
    const onPause = () => {
      if (castStateRef.current.casting) return;
      setPlaying(false);
      onProgress?.(contentTimeOf(video), true);
    };
    const onSeeked = () => {
      if (castStateRef.current.casting) return;
      onProgress?.(contentTimeOf(video), video.paused);
    };
    const onWaiting = () => setBuffering(true);
    const onPlaying = () => setBuffering(false);
    const onBufferProgress = () => {
      try {
        if (video.buffered.length > 0) setBufferedEnd(video.buffered.end(video.buffered.length - 1));
      } catch {
        /* transient invalid ranges while switching sources */
      }
    };
    const onVideoEnded = () => {
      if (castStateRef.current.casting) return; // the receiver's end-of-film is the page's to detect
      onEnded?.(contentTimeOf(video));
    };

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
      // Casting: report where the RECEIVER is. This branch is load-bearing, and its absence would
      // be near-invisible — the detached element sits at currentTime 0, so the beat would keep
      // Jellyfin's ping timeout happy (nothing would stall, nothing would error) while quietly
      // rewriting the viewer's resume point to 0:00 every ten seconds. They'd only find out when
      // they came back to finish the film.
      const cs = castStateRef.current;
      if (cs.casting) {
        onProgress?.(cs.time, cs.paused);
        return;
      }
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
    // While casting the receiver owns its own volume (it's a property of the room, not of this tab)
    // and the controls below drive it directly — pushing this tab's remembered level at it here
    // would fight the viewer's TV remote and the Google Home app for control of the same number.
    if (casting) return;
    const video = videoRef.current;
    if (video) {
      video.volume = volume;
      video.muted = muted;
    }
    writeStored("PlayerVolume", volume);
  }, [volume, muted, casting]);

  // ── text subtitles (sidecar VTT) ────────────────────────────────────────────
  // Only the SELECTED sub is mounted as a <track> at all (see the render). Setting the others'
  // mode to "disabled" was the first attempt at this and it is not enough: Firefox fetches a
  // track element's src on insert regardless of mode, so all of them still loaded. Don't Look Up
  // (33 tracks) fired 33 concurrent Stream.vtt requests on open, which Jellyfin answered with a
  // single ffmpeg demuxing all 33 out of a 12.9 GB 4K MKV: 119 s of NAS reads racing the video copy
  // of that same file, and all 33 dead at the gateway's 100 s timeout (measured 2026-08-17); the
  // burst was still there on Matilda: The Musical's 40 tracks on 2026-08-22. One track element means
  // one request, in every engine. This effect then only has to put that one track into "showing" —
  // a freshly mounted track starts "disabled", which shows nothing.
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
  // Null while casting: all three of the client-side subtitle facilities below (the timing nudge, the
  // libpgs canvas, the libass renderer) paint onto or re-time cues on THIS element, and the picture is
  // on a television. The receiver renders the VTT it was handed and nothing else can reach it.
  const activeTextSub =
    (!casting &&
      subtitleTracks.find(
        (t) => t.index === selectedSubtitleIndex && !!t.deliveryUrl && t.kind !== "image-pgs" && t.kind !== "ass"
      )) ||
    null;

  // Vertical lift for the showing track's cues. Size/color/font/edge/box ride on the injected
  // ::cue rule from useSubtitleStyle; position can't be set via ::cue, so it's applied per-cue here.
  useCueLift(videoRef, selectedSubtitleIndex, src, subStyle.liftPct);

  // Client-rendered PGS (Blu-ray bitmap) subs — drawn over the video by libpgs so the server copies the
  // video instead of burning the bitmap in. Active only while the selected track is a PGS image sub.
  const activePgsSub = subtitleTracks.find((t) => t.index === selectedSubtitleIndex && t.kind === "image-pgs");
  usePgsSubtitle(videoRef, activePgsSub && !casting ? activePgsSub.deliveryUrl : null, timelineOffset);

  // Client-rendered ASS/SSA via libass — full typesetting, also keeps the video copied (no flatten to VTT).
  const activeAssSub = subtitleTracks.find((t) => t.index === selectedSubtitleIndex && t.kind === "ass");
  useAssSubtitle(videoRef, activeAssSub && !casting ? activeAssSub.deliveryUrl : null, timelineOffset);

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
    if (casting) {
      // The receiver's own state decides which way this goes, so the bloom follows it rather than
      // the local element (which is paused-and-empty and would flash "play" every time).
      setFlash(remote?.paused ? "play" : "pause");
      setTimeout(() => setFlash(null), 500);
      cast.playPause();
      return;
    }
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
  }, [casting, cast, remote]);

  const seekTo = useCallback(
    (seconds) => {
      if (casting) {
        cast.seek(Math.min(Math.max(seconds, 0), duration || seconds));
        return;
      }
      const video = videoRef.current;
      if (!video) return;
      const clamped = Math.min(Math.max(seconds, 0), duration || video.duration || seconds);
      video.currentTime = clamped;
      setCurrentTime(clamped);
    },
    [duration, casting, cast]
  );

  // Relative seeks read the PLAYING clock, not the element's: while casting the element sits at 0,
  // so a keyboard "+10s" would jump to 0:10 of the film rather than ten seconds further on.
  const seekBy = useCallback(
    (delta) => seekTo((casting ? castTime : videoRef.current?.currentTime ?? 0) + delta),
    [seekTo, casting, castTime]
  );

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
  // The iOS route to a television. Cast and AirPlay never coexist on one browser — Apple doesn't
  // allow the Cast SDK on iOS, and no Chromium browser has the webkit AirPlay API — so at most one
  // of these two buttons can ever be `supported`, and neither needs to know about the other.
  const airplay = useAirPlay(videoRef, remotePlayable);

  // Volume, mute and speed all go to whichever device is actually making the sound. The buttons, the
  // menu and the keyboard route through these three, so they can't end up disagreeing about which
  // player they're controlling — the keyboard silently moving a detached element's volume while the
  // button moved the receiver's would be a maddening bug to report.
  //
  // These sit BELOW usePlaybackRate on purpose: stepRate names cycleRate in its dependency array,
  // which is evaluated during render, so declaring it any earlier is a temporal-dead-zone crash on
  // mount rather than a lint nit.
  const nudgeVolume = useCallback(
    (delta) => {
      if (casting) {
        cast.setVolume(Math.min(Math.max((remote?.volume ?? 1) + delta, 0), 1));
        return;
      }
      if (delta > 0) setMuted(false);
      setVolume((v) => Math.min(Math.max(v + delta, 0), 1));
    },
    [casting, cast, remote]
  );

  const toggleMuted = useCallback(() => {
    if (casting) {
      cast.toggleMuted();
      return;
    }
    setMuted((m) => !m);
  }, [casting, cast]);

  // While casting, step the RECEIVER's reported rate through the same ladder the menu offers —
  // usePlaybackRate only ever touches the local element.
  const stepRate = useCallback(
    (direction) => {
      if (!casting) {
        cycleRate(direction);
        return;
      }
      const current = remote?.playbackRate ?? 1;
      const from = PLAYBACK_RATES.indexOf(current);
      const start = from >= 0 ? from : PLAYBACK_RATES.indexOf(1);
      const next = PLAYBACK_RATES[Math.min(Math.max(start + direction, 0), PLAYBACK_RATES.length - 1)];
      if (next !== current) cast.setPlaybackRate(next);
    },
    [casting, cast, remote, cycleRate]
  );
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
  const { visible: controlsVisible, wake: wakeControls, hide: hideControls } = useIdleChrome({
    videoRef,
    // Casting holds the chrome up: what's behind it is a status plate, not a picture, so fading the
    // controls away would leave a black rectangle with nothing to reveal. This already happens by
    // accident — the detached element reads as paused, which the hook holds for — and saying it out
    // loud is what keeps it true if that check ever changes.
    holdWhile: () => castStateRef.current.casting,
  });

  const hideChrome = effectivePlaying && !controlsVisible && !openMenu && !scrubbing;

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
          nudgeVolume(0.05);
          break;
        case "ArrowDown":
          nudgeVolume(-0.05);
          break;
        case "m":
          toggleMuted();
          break;
        case "<":
          stepRate(-1);
          break;
        case ">":
          stepRate(1);
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
    [togglePlay, seekBy, seekDisplay, toggleFullscreen, displayDuration, subtitleTracks, selectedSubtitleIndex, onSelectSubtitle, wakeControls, activeTextSub, nudgeSubtitle, nudgeVolume, toggleMuted, stepRate]
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
  // No buffered bar while casting: the receiver's buffer is not something the sender API reports,
  // and a bar frozen at 0% reads as "nothing has loaded". Better to show none than to show a lie.
  const bufferedPct =
    casting || displayDuration <= 0
      ? 0
      : Math.min((((combined ? partOffset : 0) + bufferedEnd) / displayDuration) * 100, 100);

  return (
    /* eslint-disable jsx-a11y/no-noninteractive-element-interactions, jsx-a11y/no-static-element-interactions, jsx-a11y/click-events-have-key-events, jsx-a11y/media-has-caption */
    <div
      ref={containerRef}
      className={`vp-stage${hideChrome ? " vp-stage--idle" : ""}`}
      tabIndex={0}
      role="application"
      aria-label={`Video player: ${title || ""}`}
      onMouseMove={wakeControls}
      onMouseLeave={() => effectivePlaying && hideControls()}
    >
      <video ref={videoRef} className="vp-video" poster={poster} crossOrigin="anonymous" playsInline onClick={onSurfaceClick}>
        {/* The selected sidecar-VTT track only — mounting the whole list fetches the whole list.
            None at all while casting: the receiver was handed its own copies of these urls in the
            load request, and a <track> on a detached element would pull the cue file down a second
            time (through the same ffmpeg) to render it for nobody. */}
        {!casting &&
          subtitleTracks
            .filter((t) => t.deliveryUrl && t.kind !== "image-pgs" && t.kind !== "ass")
            .filter((t) => String(t.index) === String(selectedSubtitleIndex))
            .map((t) => (
              <track key={t.index} id={String(t.index)} kind="subtitles" label={t.label} src={t.deliveryUrl} srcLang={t.language || "en"} />
            ))}
      </video>

      {/* The picture is on the television — say so, and say which one. An empty black stage with a
          working scrub bar reads as a broken player. */}
      {casting && (
        <div className="vp-castplate">
          <div className="vp-castplate-mark" aria-hidden="true">
            <span className="vp-castplate-screen" />
            <span className="vp-castplate-wave" />
          </div>
          <div className="vp-castplate-head">{cast.connecting ? "Connecting…" : "Playing on"}</div>
          <div className="vp-castplate-device">{cast.deviceName || "the TV"}</div>
          {cast.error && <div className="vp-castplate-error">{cast.error}</div>}
          {!cast.videoCapable && (
            <div className="vp-castplate-error">
              This device only plays audio — pick one with a screen to see the picture.
            </div>
          )}
          <button className="vp-castplate-stop" onClick={cast.disconnect}>
            Stop casting
          </button>
        </div>
      )}

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
      {(casting ? !!remote?.buffering : buffering) && !needsTap && !fatalError && (
        <div className="vp-bulbs" aria-label={bufferingLabel || "Buffering"}>
          <div className="vp-bulbs-row"><span /><span /><span /></div>
          {bufferingLabel && <div className="vp-bulbs-label">{bufferingLabel}</div>}
        </div>
      )}

      {/* autoplay was blocked — one gold tap target. Never while casting: nothing is trying to
          autoplay here, and a leftover flag from before the cast would put a play button over the
          "playing on your TV" plate. */}
      {needsTap && !casting && (
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
          <button className="vp-btn" onClick={togglePlay} aria-label={effectivePlaying ? "Pause" : "Play"}>
            {effectivePlaying ? (
              <span className="vp-glyph-pause"><i /><i /></span>
            ) : (
              <span className="vp-glyph-play" />
            )}
          </button>

          {/* Volume drives whichever device is making the sound. While casting that's the receiver's
              own level — the same number the TV remote and the Home app move — so it is read back
              from the mirrored player rather than from this tab's remembered preference. */}
          <div className="vp-volume">
            <button
              className="vp-btn"
              onClick={toggleMuted}
              aria-label={(casting ? remote?.muted : muted) ? "Unmute" : "Mute"}
            >
              <span
                className={`vp-glyph-vol${
                  (casting ? remote?.muted || remote?.volume === 0 : muted || volume === 0) ? " vp-glyph-vol--off" : ""
                }`}
              />
            </button>
            <input
              className="vp-volume-slider"
              type="range"
              min="0"
              max="1"
              step="0.02"
              value={casting ? (remote?.muted ? 0 : remote?.volume ?? 1) : muted ? 0 : volume}
              aria-label="Volume"
              onChange={(e) => {
                const next = parseFloat(e.target.value);
                if (casting) {
                  cast.setVolume(next);
                  return;
                }
                setMuted(false);
                setVolume(next);
              }}
            />
          </div>

          <div className="vp-time">
            <span className="vp-time-now">{formatTime(shownTime)}</span>
            <span className="vp-time-sep">/</span>
            <span className="vp-time-total">{formatTime(displayDuration)}</span>
          </div>

          <div className="vp-spacer" />

          {isDirectStream && qualityKey === "original" && !casting && <span className="vp-direct-badge">Direct</span>}

          {/* The cast button only exists once the SDK has found a receiver on the network — there is
              no honest "cast unavailable" state to render, and a permanently dead button on every
              iPhone (where the Cast SDK cannot exist at all) would be a standing bug report. */}
          {cast?.supported && (
            <button
              className={`vp-btn vp-btn-cast${casting ? " vp-btn-cast--on" : ""}`}
              onClick={() => (casting ? cast.disconnect() : cast.connect())}
              aria-label={casting ? `Stop casting to ${cast.deviceName || "the TV"}` : "Play on a TV"}
              title={casting ? `Casting to ${cast.deviceName || "the TV"}` : "Play on a TV"}
              aria-pressed={casting}
            >
              <span className="vp-glyph-cast" />
            </button>
          )}

          {/* AirPlay. Gated on a receiver having actually been SEEN, not just on the API existing:
              Safari reports availability asynchronously, and an always-present button on a Mac with
              no Apple TV in the house is a control that can only disappoint. */}
          {airplay.supported && airplay.available && !casting && (
            <button
              className={`vp-btn vp-btn-airplay${airplay.active ? " vp-btn-airplay--on" : ""}`}
              onClick={airplay.show}
              aria-label="Play on a TV (AirPlay)"
              title="Play on a TV (AirPlay)"
              aria-pressed={airplay.active}
            >
              <span className="vp-glyph-airplay" />
            </button>
          )}

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

                {/* Which receiver profile the session was negotiated from. This is a real control,
                    not a preference: it changes the DeviceProfile the server builds, so a wrong
                    setting here is the difference between a picture and a black screen. It is only
                    offered while connected, because the answer is per-TV. */}
                {casting && (
                  <>
                    <div className="vp-menu-section">TV</div>
                    {CAST_PROFILE_OPTIONS.map((p) => (
                      <button
                        key={p.key}
                        role="menuitemradio"
                        aria-checked={cast.profileKey === p.key}
                        className={`vp-menu-item${cast.profileKey === p.key ? " vp-menu-item--on" : ""}`}
                        onClick={() => {
                          setOpenMenu(null);
                          if (cast.profileKey !== p.key) cast.onSelectProfile?.(p.key);
                        }}
                      >
                        <span className="vp-menu-dot" />
                        {p.label}
                        <span className="vp-menu-hint">{p.hint}</span>
                      </button>
                    ))}
                    <button
                      role="menuitemcheckbox"
                      aria-checked={!!cast.dolbyPassthrough}
                      className={`vp-menu-item${cast.dolbyPassthrough ? " vp-menu-item--on" : ""}`}
                      onClick={() => {
                        setOpenMenu(null);
                        cast.onToggleDolby?.();
                      }}
                    >
                      <span className="vp-menu-dot" />
                      Dolby pass-through
                      <span className="vp-menu-hint">only with a receiver</span>
                    </button>
                    {cast.subtitleNote && <div className="vp-menu-readout">{cast.subtitleNote}</div>}
                  </>
                )}

                {/* Speed. While casting the tick follows the receiver's REPORTED rate, not the one
                    we asked for — SET_PLAYBACK_RATE is a message a receiver may ignore, and a menu
                    that ticks 1.5× over a film playing at normal speed is worse than one that
                    visibly refuses to move. */}
                <div className="vp-menu-section">Speed</div>
                {PLAYBACK_RATES.map((r) => {
                  const on = (casting ? remote?.playbackRate ?? 1 : playbackRate) === r;
                  return (
                    <button
                      key={r}
                      role="menuitemradio"
                      aria-checked={on}
                      className={`vp-menu-item${on ? " vp-menu-item--on" : ""}`}
                      onClick={() => {
                        setOpenMenu(null);
                        if (casting) cast.setPlaybackRate?.(r);
                        else setPlaybackRate(r);
                      }}
                    >
                      <span className="vp-menu-dot" />
                      {r === 1 ? "Normal" : `${r}×`}
                    </button>
                  );
                })}

                {/* Why there is no cast/AirPlay button, when there isn't one. The buttons only
                    exist once a receiver is found, so their absence can't say whether the browser
                    can't, the SDK was blocked, or the TV is on the other Wi-Fi. This line can. */}
                {cast && !casting && (
                  <>
                    <div className="vp-menu-section">TV</div>
                    <div className="vp-menu-readout">
                      {tvStatusLine({ cast, airplay, userAgent: navigator.userAgent })}
                    </div>
                  </>
                )}

                <div className="vp-menu-section">Playing</div>
                {casting && (
                  <div className="vp-menu-readout">On {cast.deviceName || "the TV"}</div>
                )}
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

          {pip.supported && !casting && (
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
