import {
  SUB_SIZE_MIN,
  SUB_SIZE_MAX,
  SUB_LIFT_MAX,
  SUB_FONTS,
  SUB_FONT_OPTIONS,
  SUB_EDGES,
  SUB_EDGE_OPTIONS,
  SUB_COLORS,
  SUB_STYLE_DEFAULTS,
  subBg,
  formatDelay,
  SUBTITLE_NUDGE_MS,
} from "./subtitleStyle";
import "./SubtitleStyleEditor.css";

// Approximate the rendered px of a vh size (cue font-size is vh = relative to the window). Just a
// readout; the live preview is the real guide.
const sizePx = (sizeVh) => Math.round((sizeVh / 100) * (typeof window !== "undefined" ? window.innerHeight : 1080));

/**
 * The caption-appearance controls (size / color / box opacity / font / edge / position + reset).
 * Self-contained styling so it looks the same hosted in either player's menu.
 */
export function SubtitleStyleControls({ subStyle, setStyle, setSubStyle }) {
  const atDefaults =
    subStyle.sizeVh === SUB_STYLE_DEFAULTS.sizeVh &&
    subStyle.color === SUB_STYLE_DEFAULTS.color &&
    subStyle.font === SUB_STYLE_DEFAULTS.font &&
    subStyle.edge === SUB_STYLE_DEFAULTS.edge &&
    subStyle.liftPct === SUB_STYLE_DEFAULTS.liftPct &&
    subStyle.bgOpacity === SUB_STYLE_DEFAULTS.bgOpacity;

  return (
    <div className="substyle">
      <div className="substyle-row">
        <span className="substyle-label">Size</span>
        <input
          className="substyle-slider"
          type="range"
          min={SUB_SIZE_MIN}
          max={SUB_SIZE_MAX}
          step="0.1"
          value={subStyle.sizeVh}
          aria-label="Subtitle size"
          onChange={(e) => setStyle({ sizeVh: parseFloat(e.target.value) })}
        />
        <span className="substyle-val">{sizePx(subStyle.sizeVh)}px</span>
      </div>

      <div className="substyle-row">
        <span className="substyle-label">Color</span>
        <div className="substyle-swatches">
          {SUB_COLORS.map((c) => (
            <button
              key={c.value}
              type="button"
              className={`substyle-swatch${subStyle.color === c.value ? " substyle-swatch--on" : ""}`}
              style={{ background: c.value }}
              onClick={() => setStyle({ color: c.value })}
              aria-label={c.label}
              aria-pressed={subStyle.color === c.value}
              title={c.label}
            />
          ))}
        </div>
      </div>

      <div className="substyle-row">
        <span className="substyle-label">Box</span>
        <input
          className="substyle-slider"
          type="range"
          min="0"
          max="1"
          step="0.05"
          value={subStyle.bgOpacity}
          aria-label="Subtitle background opacity"
          onChange={(e) => setStyle({ bgOpacity: parseFloat(e.target.value) })}
        />
        <span className="substyle-val">{subStyle.bgOpacity <= 0 ? "Off" : `${Math.round(subStyle.bgOpacity * 100)}%`}</span>
      </div>

      <div className="substyle-row">
        <span className="substyle-label">Font</span>
        <div className="substyle-seg">
          {SUB_FONT_OPTIONS.map((f) => (
            <button
              key={f.key}
              type="button"
              className={`substyle-segbtn${subStyle.font === f.key ? " substyle-segbtn--on" : ""}`}
              style={{ fontFamily: SUB_FONTS[f.key] }}
              onClick={() => setStyle({ font: f.key })}
              aria-pressed={subStyle.font === f.key}
            >
              {f.label}
            </button>
          ))}
        </div>
      </div>

      <div className="substyle-row">
        <span className="substyle-label">Edge</span>
        <div className="substyle-seg">
          {SUB_EDGE_OPTIONS.map((ed) => (
            <button
              key={ed.key}
              type="button"
              className={`substyle-segbtn${subStyle.edge === ed.key ? " substyle-segbtn--on" : ""}`}
              onClick={() => setStyle({ edge: ed.key })}
              aria-pressed={subStyle.edge === ed.key}
            >
              {ed.label}
            </button>
          ))}
        </div>
      </div>

      <div className="substyle-row">
        <span className="substyle-label">Position</span>
        <input
          className="substyle-slider"
          type="range"
          min="0"
          max={SUB_LIFT_MAX}
          step="1"
          value={subStyle.liftPct}
          aria-label="Subtitle vertical position"
          onChange={(e) => setStyle({ liftPct: parseInt(e.target.value, 10) })}
        />
        <span className="substyle-val">{subStyle.liftPct === 0 ? "Bottom" : `+${subStyle.liftPct}`}</span>
      </div>

      <button
        type="button"
        className="substyle-reset"
        onClick={() => setSubStyle({ ...SUB_STYLE_DEFAULTS })}
        disabled={atDefaults}
      >
        Reset to defaults
      </button>
    </div>
  );
}

/**
 * Subtitle timing-sync controls for the showing soft track:
 *   • a constant Delay (±) for subs that are uniformly early/late, and
 *   • an A/B two-point sync for subs that *drift* (a constant delay can't fix linear drift).
 * The A/B steps only render while a sync is in progress, so at rest this is just the Delay row plus a
 * one-line "Fix drift" link. Driven entirely by the useSubtitleOffset hook.
 */
export function SubtitleSyncControls({
  offsetMs,
  nudge,
  reset,
  rateScale,
  abStep,
  abError,
  beginSync,
  capturePoint,
  cancelSync,
}) {
  const syncing = abStep !== "idle";
  const corrected = offsetMs !== 0 || Math.abs(rateScale - 1) > 1e-6;

  return (
    <div className="substyle">
      {/* Delay: always available — and the tool you use to line up each A/B point. */}
      <div className="substyle-row">
        <span className="substyle-label">Delay</span>
        <div className="subsync-delay">
          <button type="button" className="subsync-btn" onClick={() => nudge(-SUBTITLE_NUDGE_MS)} aria-label="Subtitles earlier" title="Earlier (g)">
            −
          </button>
          <span className="subsync-val">{formatDelay(offsetMs)}</span>
          <button type="button" className="subsync-btn" onClick={() => nudge(SUBTITLE_NUDGE_MS)} aria-label="Subtitles later" title="Later (h)">
            +
          </button>
        </div>
      </div>

      {/* At rest: a single compact entry point. The guided steps only appear once a sync is started. */}
      {!syncing && (
        <div className="substyle-row">
          <span className="substyle-label">Drift?</span>
          <button type="button" className="subsync-link" onClick={beginSync}>
            Fix with A/B sync →
          </button>
        </div>
      )}

      {syncing && (
        <div className="subsync-ab">
          <div className="subsync-ab-head">Sync · point {abStep === "a" ? "A" : "B"} of 2</div>
          <ol className="subsync-ab-steps">
            <li className={abStep === "a" ? "subsync-ab-on" : "subsync-ab-done"}>
              At an <b>early</b> line, tap −/+ above until the subtitle matches the spoken words, then <b>Set A</b>.
            </li>
            <li className={abStep === "b" ? "subsync-ab-on" : ""}>
              Jump to a <b>later</b> scene you&rsquo;ve already watched, match it the same way, then <b>Set B</b>.
              <span className="subsync-ab-tip"> Further apart = more accurate.</span>
            </li>
          </ol>
          {abError && <div className="subsync-ab-error">{abError}</div>}
          <div className="subsync-ab-actions">
            <button type="button" className="subsync-set" onClick={capturePoint}>
              {abStep === "a" ? "Set A" : "Set B"}
            </button>
            <button type="button" className="subsync-cancel" onClick={cancelSync}>
              Cancel
            </button>
          </div>
        </div>
      )}

      {corrected && !syncing && (
        <button type="button" className="substyle-reset" onClick={reset}>
          Reset sync
          {Math.abs(rateScale - 1) > 1e-6 ? ` · rate ×${rateScale.toFixed(3)}` : ""}
        </button>
      )}
    </div>
  );
}

/**
 * A live on-video sample caption, styled identically to the injected ::cue rule and placed at the
 * exact height the real cue lands (bottom = liftPct%, mirroring line = 100 − liftPct with lineAlign
 * "end"), so it's a faithful guide. Render it inside the player's positioned stage.
 */
export function SubtitleStylePreview({ subStyle }) {
  return (
    <div className="substyle-preview" style={{ bottom: `${subStyle.liftPct}%` }} aria-hidden="true">
      <span
        className="substyle-preview-text"
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
  );
}
