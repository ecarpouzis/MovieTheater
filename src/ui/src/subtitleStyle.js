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

// A/B sync guardrails: the two calibration points must be far enough apart to measure a rate, and the
// solved rate must be sane (real fps mismatches sit within a few % of 1.0; anything wild is a mis-mark).
const AB_MIN_SECONDS_APART = 30;
const AB_MIN_SCALE = 0.6;
const AB_MAX_SCALE = 1.5;

// The total shift applied to a cue: the viewer's nudge PLUS the (invisible) timeline baseline — the
// seconds this HLS session's media timeline runs ahead of true content time (streamEngine's
// timelineOffsetFromInitPts). Cues are authored in content time and fire against currentTime, so a
// cue at content C must be moved to C + baseline to land on its dialogue. The two are separate
// inputs and only ever added here, so the delay readout keeps showing the nudge alone.
export function cueShiftSeconds(offsetMs, baselineSeconds = 0) {
  return offsetMs / 1000 + (Number.isFinite(baselineSeconds) ? baselineSeconds : 0);
}

// New times for one cue, always solved from its ORIGINAL pair so repeated applications can't
// compound. The end is held a hair past the start: a transient start > end throws in some browsers.
export function retimedCue(orig, rateScale, shiftSeconds) {
  const start = Math.max(0, orig.start * rateScale + shiftSeconds);
  return { start, end: Math.max(start + 0.001, orig.end * rateScale + shiftSeconds) };
}

/**
 * Live subtitle timing correction for the showing SOFT (sidecar VTT) track. Two knobs, both applied
 * from each cue's ORIGINAL times so they never compound: new = orig × rateScale + offset.
 *   • a constant Delay (±ms) for subs uniformly early/late, and
 *   • a rate correction for subs that *drift* (a constant delay can't fix linear drift), solved from a
 *     two-point "A/B" calibration — the viewer syncs an early line, then a later line, both already
 *     watched (no spoilers). From the two (videoTime, delay) marks we solve scale + offset exactly.
 * Purely client-side, so it works in BOTH the Watch and (synchronized) TV/channel players without
 * disturbing anyone else. Burned-in image subs can't be moved — gate the UI on a soft track.
 *
 * Cues load async and a source change reloads the VTT with fresh cue objects, so we re-apply on the
 * <track> 'load' too; `reloadKey` should change when the track set is replaced. Picking a different
 * subtitle resets everything.
 *
 * A/B math: while calibrating, rateScale is held at 1, so a synced cue satisfies origStart + delay =
 * videoTime ⇒ its original time O = t − d. With two marks (tA, dA) and (tB, dB): OA = tA−dA, OB = tB−dB,
 * and we want the final mapping to land each at its real time (tA, tB): scale = (tA−tB)/(OA−OB),
 * offset = tA − scale·OA.
 *
 * `baselineOffsetSec` is plumbing, not a knob: the HLS timeline offset of the live session (0 for
 * direct play), added on top of the nudge so cues land on the picture even when a mid-file join
 * shifted the media timeline. It arrives (and re-rolls) AFTER the tracks are mounted, so it's a
 * dependency of the re-time effect. `offsetMs` — the delay readout — stays the viewer's nudge alone.
 *
 * Returns { offsetMs, nudge, reset, toast, rateScale, abStep, abError, beginSync, capturePoint, cancelSync }.
 */
export function useSubtitleOffset(videoRef, selectedSubtitleIndex, reloadKey, baselineOffsetSec = 0) {
  const [offsetMs, setOffsetMs] = useState(0);
  const [rateScale, setRateScale] = useState(1);
  const [toast, setToast] = useState(false);
  const [abStep, setAbStep] = useState("idle"); // 'idle' | 'a' | 'b'
  const [abError, setAbError] = useState(null);
  const toastTimer = useRef(null);
  const cueOriginalsRef = useRef(new WeakMap());
  const pointARef = useRef(null); // first calibration mark: { t, d }
  const priorRef = useRef(null); // correction snapshot to restore if the sync is cancelled

  // Picking a different subtitle starts its timing fresh (each file has its own sync).
  useEffect(() => {
    setOffsetMs(0);
    setRateScale(1);
    setAbStep("idle");
    setAbError(null);
    pointARef.current = null;
    priorRef.current = null;
  }, [selectedSubtitleIndex]);

  useEffect(() => {
    const video = videoRef.current;
    if (!video) return undefined;
    const shiftSec = cueShiftSeconds(offsetMs, baselineOffsetSec);
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
          const { start: ns, end: ne } = retimedCue(orig, rateScale, shiftSec);
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
  }, [videoRef, offsetMs, rateScale, baselineOffsetSec, selectedSubtitleIndex, reloadKey]);

  const flashToast = useCallback(() => {
    setToast(true);
    clearTimeout(toastTimer.current);
    toastTimer.current = setTimeout(() => setToast(false), 1600);
  }, []);

  const nudge = useCallback((deltaMs) => {
    setOffsetMs((v) => Math.max(-SUBTITLE_OFFSET_LIMIT_MS, Math.min(SUBTITLE_OFFSET_LIMIT_MS, v + deltaMs)));
    flashToast();
  }, [flashToast]);

  const reset = useCallback(() => {
    setOffsetMs(0);
    setRateScale(1);
    setAbStep("idle");
    setAbError(null);
    pointARef.current = null;
    priorRef.current = null;
  }, []);

  // ── A/B two-point sync ──
  // Calibrate with pure delay (rate held at 1); snapshot the prior correction so Cancel restores it.
  const beginSync = useCallback(() => {
    priorRef.current = { offsetMs, rateScale };
    pointARef.current = null;
    setAbError(null);
    setRateScale(1);
    setOffsetMs(0);
    setAbStep("a");
  }, [offsetMs, rateScale]);

  const cancelSync = useCallback(() => {
    const prior = priorRef.current;
    if (prior) {
      setOffsetMs(prior.offsetMs);
      setRateScale(prior.rateScale);
    }
    pointARef.current = null;
    priorRef.current = null;
    setAbError(null);
    setAbStep("idle");
  }, []);

  // Mark the current (videoTime, delay) as A, then B; on B, solve scale + offset and apply.
  const capturePoint = useCallback(() => {
    const video = videoRef.current;
    if (!video) return;
    // Mark in CONTENT time: the baseline is plumbing the viewer never sees, and taking it off here
    // keeps the solved offset a pure user correction (it cancels out of the scale either way).
    const mark = { t: video.currentTime - baselineOffsetSec, d: offsetMs / 1000 };
    if (abStep === "a") {
      pointARef.current = mark;
      setAbError(null);
      setAbStep("b");
      return;
    }
    if (abStep === "b") {
      const A = pointARef.current;
      const B = mark;
      const oa = A.t - A.d;
      const ob = B.t - B.d;
      if (Math.abs(B.t - A.t) < AB_MIN_SECONDS_APART || Math.abs(oa - ob) < 1) {
        setAbError("Pick a second point further along — at least a minute from the first.");
        return; // stay on step B for another try
      }
      const scale = (A.t - B.t) / (oa - ob);
      const off = A.t - scale * oa;
      if (!isFinite(scale) || scale < AB_MIN_SCALE || scale > AB_MAX_SCALE) {
        setAbError("That didn't line up — re-mark each point right as the line is spoken.");
        return;
      }
      setRateScale(scale);
      setOffsetMs(Math.round(off * 1000));
      pointARef.current = null;
      priorRef.current = null;
      setAbError(null);
      setAbStep("idle");
      flashToast();
    }
  }, [videoRef, offsetMs, abStep, baselineOffsetSec, flashToast]);

  useEffect(() => () => clearTimeout(toastTimer.current), []);

  return { offsetMs, nudge, reset, toast, rateScale, abStep, abError, beginSync, capturePoint, cancelSync };
}
