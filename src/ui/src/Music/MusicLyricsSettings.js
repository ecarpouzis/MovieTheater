import { useEffect, useRef, useState } from "react";
import "./MusicLyricsSettings.css";

// ── How the lyrics LOOK, as opposed to whether they're shown ────────────────
// The Lyrics switch (player context) decides IF; these decide HOW: size, typeface, whether the dark
// scrim sits behind them over Butterchurn, and whether the pane follows the song on its own.
//
// They live in one place because three surfaces render the same pane — the play-bar overlay, the
// words over the visualizer, and the Now Playing column — and a setting that only took effect on one
// of them would read as a bug. The values are pure data and the helpers below are pure functions, so
// what's stored and what's legal can be tested without mounting anything.

export const LYRICS_FONTS = [
  { id: "sans", label: "Sans", stack: "var(--font-sans)" },
  { id: "display", label: "Display", stack: "var(--font-display)" },
  { id: "serif", label: "Serif", stack: 'Georgia, "Iowan Old Style", "Times New Roman", serif' },
  { id: "mono", label: "Mono", stack: 'ui-monospace, "Cascadia Code", Consolas, monospace' },
];

// Pixels per second for lyrics with no timestamps. Deliberately slow: this is reading pace, not
// scrolling pace, and anything faster than the top value outruns a singer.
export const LYRICS_CREEP_SPEEDS = [
  { id: "slow", label: "Slow", pxPerSec: 7 },
  { id: "medium", label: "Medium", pxPerSec: 14 },
  { id: "fast", label: "Fast", pxPerSec: 26 },
];

export const LYRICS_SCALE_MIN = 0.7;
export const LYRICS_SCALE_MAX = 2.4;
export const LYRICS_SCALE_STEP = 0.1;

export const LYRICS_DEFAULTS = {
  scale: 1,
  font: "sans",
  scrim: true,
  // "Follow" means two different mechanics depending on the lyrics: timestamped lines are tracked
  // exactly, untimed ones creep at a steady rate. One switch rather than two, because from the
  // reader's side it is one question — do the words move by themselves?
  follow: true,
  creep: "medium",
};

export function clampLyricsScale(n) {
  const v = typeof n === "number" && Number.isFinite(n) ? n : LYRICS_DEFAULTS.scale;
  return Math.min(LYRICS_SCALE_MAX, Math.max(LYRICS_SCALE_MIN, Math.round(v * 100) / 100));
}

export function lyricsFontStack(id) {
  return (LYRICS_FONTS.find((f) => f.id === id) || LYRICS_FONTS[0]).stack;
}

export function lyricsCreepPxPerSec(id) {
  return (LYRICS_CREEP_SPEEDS.find((s) => s.id === id) || LYRICS_CREEP_SPEEDS[1]).pxPerSec;
}

/// Coerce anything that came out of localStorage into a usable settings object. Storage is a hostile
/// input: it holds whatever an older build wrote, whatever a hand-edit left, and sometimes garbage —
/// and a single bad field must not cost the reader every other setting, so each one falls back on
/// its own.
export function normalizeLyricsSettings(raw) {
  const src = raw && typeof raw === "object" ? raw : {};
  return {
    scale: clampLyricsScale(src.scale),
    font: LYRICS_FONTS.some((f) => f.id === src.font) ? src.font : LYRICS_DEFAULTS.font,
    scrim: typeof src.scrim === "boolean" ? src.scrim : LYRICS_DEFAULTS.scrim,
    follow: typeof src.follow === "boolean" ? src.follow : LYRICS_DEFAULTS.follow,
    creep: LYRICS_CREEP_SPEEDS.some((s) => s.id === src.creep) ? src.creep : LYRICS_DEFAULTS.creep,
  };
}

/// The "Aa" button plus its panel, for the visualizer's control strip and the Now Playing lyrics
/// heading. It renders nothing about lyrics itself — it only edits the settings object it's handed.
/// `tone` names the SURFACE this sits on, not a colour scheme: "dark" for the two hosts that are
/// dark in both themes (the visualizer strip over the GL canvas, the lyrics panel in the sidebar
/// palette), "page" for the Now Playing column, which follows the content-area tokens and would
/// otherwise get white-on-white.
export default function MusicLyricsSettingsButton({ settings, onChange, tone = "dark", className = "" }) {
  const [open, setOpen] = useState(false);
  const wrapRef = useRef(null);
  const s = settings || LYRICS_DEFAULTS;
  const set = (key, value) => onChange && onChange(key, value);

  // Dismiss on an outside click or Escape. Pointerdown rather than click so the panel closes on the
  // press that started elsewhere, which is what a tap on the canvas behind it should do.
  useEffect(() => {
    if (!open) return undefined;
    const onDown = (e) => {
      if (wrapRef.current && !wrapRef.current.contains(e.target)) setOpen(false);
    };
    const onKey = (e) => { if (e.key === "Escape") setOpen(false); };
    document.addEventListener("pointerdown", onDown);
    document.addEventListener("keydown", onKey);
    return () => {
      document.removeEventListener("pointerdown", onDown);
      document.removeEventListener("keydown", onKey);
    };
  }, [open]);

  return (
    <span
      className={`mlset mlset--${tone === "page" ? "page" : "dark"}${className ? ` ${className}` : ""}`}
      ref={wrapRef}
    >
      <button
        className={`mlset-btn${open ? " mlset-btn--on" : ""}`}
        onClick={() => setOpen((o) => !o)}
        aria-expanded={open}
        aria-label="Lyrics display options"
        title="Lyrics display options"
        data-testid="music-lyrics-settings-toggle"
      >
        Aa
      </button>

      {open && (
        <div className="mlset-panel" data-testid="music-lyrics-settings-panel">
          <div className="mlset-head">Lyrics</div>

          <label className="mlset-row" htmlFor="mlset-scale">
            <span className="mlset-label">Size</span>
            <input
              id="mlset-scale"
              type="range"
              min={LYRICS_SCALE_MIN}
              max={LYRICS_SCALE_MAX}
              step={LYRICS_SCALE_STEP}
              value={s.scale}
              onChange={(e) => set("scale", clampLyricsScale(parseFloat(e.target.value)))}
            />
            <span className="mlset-value">{Math.round(s.scale * 100)}%</span>
          </label>

          <div className="mlset-row">
            <span className="mlset-label">Font</span>
            <span className="mlset-choices" role="group" aria-label="Lyrics font">
              {LYRICS_FONTS.map((f) => (
                <button
                  key={f.id}
                  className={`mlset-chip${s.font === f.id ? " mlset-chip--on" : ""}`}
                  style={{ fontFamily: f.stack }}
                  onClick={() => set("font", f.id)}
                  aria-pressed={s.font === f.id}
                >
                  {f.label}
                </button>
              ))}
            </span>
          </div>

          <label className="mlset-check">
            <input type="checkbox" checked={!!s.scrim} onChange={(e) => set("scrim", e.target.checked)} />
            <span>
              Dark backdrop
              <em>Dims the visuals behind the words. Off shows the preset in full.</em>
            </span>
          </label>

          <label className="mlset-check">
            <input type="checkbox" checked={!!s.follow} onChange={(e) => set("follow", e.target.checked)} />
            <span>
              Scroll with the song
              <em>Off leaves the words exactly where you put them.</em>
            </span>
          </label>

          <div className={`mlset-row${s.follow ? "" : " mlset-row--off"}`}>
            <span className="mlset-label">Untimed pace</span>
            <span className="mlset-choices" role="group" aria-label="Scroll speed for untimed lyrics">
              {LYRICS_CREEP_SPEEDS.map((sp) => (
                <button
                  key={sp.id}
                  className={`mlset-chip${s.creep === sp.id ? " mlset-chip--on" : ""}`}
                  onClick={() => set("creep", sp.id)}
                  disabled={!s.follow}
                  aria-pressed={s.creep === sp.id}
                >
                  {sp.label}
                </button>
              ))}
            </span>
          </div>
          {/* Most lyrics here have no timestamps, so this is the row that decides whether they move
              at all — worth saying out loud rather than leaving as a mystery third control. */}
          <div className="mlset-note">Only for lyrics with no timestamps — timed ones follow the track.</div>
        </div>
      )}
    </span>
  );
}
