import { useCallback, useEffect, useRef, useState } from "react";

// ── subtitle appearance (shared by the Watch player and the TV/channel player) ──────────────────
// The viewer's caption look, persisted across sessions in ONE localStorage key so both players agree.
// Native WebVTT cues can only be styled via a stylesheet (no per-element inline style), so
// size/color/font/edge/box are written into a single injected `::cue` rule that targets BOTH players'
// video elements; vertical lift is applied per-cue via cue.line. Size is in vh so it scales with the
// player and reads consistently across browsers (the Firefox "tiny captions" fix).
export const SUB_STYLE_DEFAULTS = { sizeVh: 3.0, color: "#f2ecdd", font: "sans", edge: "shadow", liftPct: 5, bgOpacity: 0.78 };
export const SUB_SIZE_MIN = 1.8;
export const SUB_SIZE_MAX = 5.5;
export const SUB_LIFT_MAX = 40;

export const SUB_FONTS = {
  sans: '"Segoe UI", system-ui, Arial, sans-serif',
  serif: 'Georgia, "Times New Roman", serif',
  cinema: '"Marcellus", Georgia, serif',
};
export const SUB_FONT_OPTIONS = [
  { key: "sans", label: "Sans" },
  { key: "serif", label: "Serif" },
  { key: "cinema", label: "Cinema" },
];

// text-shadow strings: a soft drop shadow, or a 4-way faux outline that holds the glyph against
// bright scenes. ::cue honors text-shadow (one of its few allowed properties).
export const SUB_EDGES = {
  none: "none",
  shadow: "0 1px 3px rgba(0,0,0,0.95), 0 2px 8px rgba(0,0,0,0.7)",
  outline:
    "-1px -1px 0 #000, 1px -1px 0 #000, -1px 1px 0 #000, 1px 1px 0 #000, 0 0 4px rgba(0,0,0,0.9)",
};
export const SUB_EDGE_OPTIONS = [
  { key: "none", label: "None" },
  { key: "shadow", label: "Shadow" },
  { key: "outline", label: "Outline" },
];

export const SUB_COLORS = [
  { label: "White", value: "#ffffff" },
  { label: "Cream", value: "#f2ecdd" },
  { label: "Yellow", value: "#f2e36b" },
  { label: "Gold", value: "#f5cf72" },
];

// The dark box behind the text. Opacity 0 renders no box at all (fully transparent).
export const subBg = (opacity) => (opacity <= 0 ? "transparent" : `rgba(8, 7, 5, ${opacity})`);

// Both players' video classes — the injected rule targets both so one shared stylesheet styles each.
const CUE_SELECTOR = ".vp-video::cue, .tv-video::cue";

/**
 * Owns the persisted caption look and injects the shared `::cue` rule. Returns the style plus
 * `styleOpen` (reveals the editor panel + on-video preview). Used by both players so a change in
 * one is honored by the other.
 */
export function useSubtitleStyle() {
  const [subStyle, setSubStyle] = useState(() => {
    try {
      return { ...SUB_STYLE_DEFAULTS, ...JSON.parse(window.localStorage.getItem("SubtitleStyle") || "{}") };
    } catch {
      return { ...SUB_STYLE_DEFAULTS };
    }
  });
  const [styleOpen, setStyleOpen] = useState(false);
  const setStyle = useCallback((patch) => setSubStyle((s) => ({ ...s, ...patch })), []);

  useEffect(() => {
    window.localStorage.setItem("SubtitleStyle", JSON.stringify(subStyle));
  }, [subStyle]);

  // Write the chosen look into a single injected rule appended to <head> at runtime (so it sits after
  // the bundled stylesheets and wins on equal specificity). A stable id keeps it a singleton.
  useEffect(() => {
    const css =
      `${CUE_SELECTOR}{` +
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

  return { subStyle, setSubStyle, setStyle, styleOpen, setStyleOpen };
}

/**
 * Apply the vertical lift to the showing track's cues. ::cue can't set position, but cue.line can.
 * With snapToLines off and lineAlign "end", `line` pins the BOTTOM of the caption box, so
 * 100 − liftPct puts liftPct=0 flush at the very bottom and raises from there. Cues load async and a
 * source change reloads the VTT with fresh cue objects, so we re-apply on the <track> 'load' too;
 * pass a `reloadKey` that changes whenever the track set is replaced (e.g. the stream src / track list).
 */
export function useCueLift(videoRef, selectedSubtitleIndex, reloadKey, liftPct) {
  useEffect(() => {
    const video = videoRef.current;
    if (!video) return undefined;
    const apply = () => {
      for (const track of Array.from(video.textTracks)) {
        if (String(track.id) !== String(selectedSubtitleIndex)) continue;
        if (!track.cues) continue;
        for (const cue of Array.from(track.cues)) {
          try {
            cue.snapToLines = false;
            cue.lineAlign = "end";
            cue.line = Math.max(50, 100 - liftPct);
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
  }, [videoRef, selectedSubtitleIndex, reloadKey, liftPct]);
}

// ── subtitle timing nudge (shared) ──────────────────────────────────────────────────────────────
// Step per nudge (keyboard / ± buttons) and the clamp on total offset.
export const SUBTITLE_NUDGE_MS = 100;
const SUBTITLE_OFFSET_LIMIT_MS = 30_000;

// Subtitle-delay readout. Positive = subtitles show later than the audio; negative = earlier.
// Uses a real minus glyph so the sign reads cleanly in the player chrome.
export function formatDelay(ms) {
  const sign = ms > 0 ? "+" : ms < 0 ? "−" : "";
  return `${sign}${Math.abs(ms)} ms`;
}

// Common subtitle authoring frame rates. When a sub drifts *linearly* (no constant delay fixes it),
// it was almost always authored for one of these against a video running at a different rate — most
// often a 25 fps (PAL) sub on a 23.976 fps film. The fix is to rescale the cue times, not shift them.
export const SUB_FPS_OPTIONS = [23.976, 24, 25, 29.97];

// Short fps label: 25 → "25", 23.976 → "23.98", 29.97 → "29.97".
export function formatFps(fps) {
  if (!fps) return "";
  const r = Math.round(fps * 100) / 100;
  return Number.isInteger(r) ? String(r) : r.toFixed(2);
}

/**
 * Live subtitle timing correction for the showing SOFT (sidecar VTT) track. Two knobs, both applied
 * from each cue's ORIGINAL times so they never compound:
 *   • a constant delay (±ms) for subs uniformly early/late, and
 *   • a frame-rate rescale for subs that *drift* (a constant delay can't fix linear drift). When the
 *     sub was authored for `subFps` but the video runs at `videoFrameRate`, multiplying every cue time
 *     by subFps/videoFrameRate realigns it. new = orig × scale + offset.
 * Purely client-side, so it works in BOTH the Watch player and the (synchronized) TV/channel player
 * without disturbing anyone else. Burned-in image subs can't be moved — gate the UI on a soft track.
 *
 * Cues load async and a source change reloads the VTT with fresh cue objects, so we re-apply on the
 * <track> 'load' too; `reloadKey` should change when the track set is replaced. Picking a different
 * subtitle resets both knobs. `videoFrameRate` (from Stream/Start) enables the fps presets.
 *
 * Returns { offsetMs, nudge, reset, toast, subFps, setSubFps, fpsOptions, videoFrameRate }.
 */
export function useSubtitleOffset(videoRef, selectedSubtitleIndex, reloadKey, videoFrameRate = null) {
  const [offsetMs, setOffsetMs] = useState(0);
  const [subFps, setSubFps] = useState(null); // assumed source fps of the sub; null = matched (scale 1)
  const [toast, setToast] = useState(false);
  const toastTimer = useRef(null);
  const cueOriginalsRef = useRef(new WeakMap());

  // Picking a different subtitle starts its timing fresh (each file has its own sync + rate).
  useEffect(() => {
    setOffsetMs(0);
    setSubFps(null);
  }, [selectedSubtitleIndex]);

  // subs authored for subFps played on a videoFrameRate video need every timestamp × subFps/videoFps.
  const scale = subFps && videoFrameRate ? subFps / videoFrameRate : 1;

  useEffect(() => {
    const video = videoRef.current;
    if (!video) return undefined;
    const offsetSec = offsetMs / 1000;
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
          const ns = Math.max(0, orig.start * scale + offsetSec);
          const ne = Math.max(ns + 0.001, orig.end * scale + offsetSec);
          // Assign in whichever order keeps start <= end at every step (a transient start > end can throw).
          try {
            if (ne >= cue.startTime) {
              if (cue.endTime !== ne) cue.endTime = ne;
              if (cue.startTime !== ns) cue.startTime = ns;
            } else {
              if (cue.startTime !== ns) cue.startTime = ns;
              if (cue.endTime !== ne) cue.endTime = ne;
            }
          } catch {
            /* a browser that disallows mutating cue times — correction simply won't apply there */
          }
        }
      }
    };
    apply();
    const tracks = Array.from(video.querySelectorAll("track"));
    tracks.forEach((t) => t.addEventListener("load", apply));
    return () => tracks.forEach((t) => t.removeEventListener("load", apply));
  }, [videoRef, offsetMs, scale, selectedSubtitleIndex, reloadKey]);

  const flashToast = useCallback(() => {
    setToast(true);
    clearTimeout(toastTimer.current);
    toastTimer.current = setTimeout(() => setToast(false), 1400);
  }, []);

  const nudge = useCallback((deltaMs) => {
    setOffsetMs((v) => Math.max(-SUBTITLE_OFFSET_LIMIT_MS, Math.min(SUBTITLE_OFFSET_LIMIT_MS, v + deltaMs)));
    flashToast();
  }, [flashToast]);

  const chooseFps = useCallback((fps) => {
    setSubFps(fps);
    flashToast();
  }, [flashToast]);

  const reset = useCallback(() => {
    setOffsetMs(0);
    setSubFps(null);
  }, []);

  useEffect(() => () => clearTimeout(toastTimer.current), []);

  // Offer every common authoring rate except the one matching the video (only when we know the video fps).
  const fpsOptions = videoFrameRate
    ? SUB_FPS_OPTIONS.filter((f) => Math.abs(f - videoFrameRate) > 0.05)
    : [];

  return { offsetMs, nudge, reset, toast, subFps, setSubFps: chooseFps, fpsOptions, videoFrameRate };
}
