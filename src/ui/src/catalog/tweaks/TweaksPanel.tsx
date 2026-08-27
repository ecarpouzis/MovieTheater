import { useCallback, useEffect, useRef, type ReactNode } from "react";
import { onAnyScroll } from "../../utils/scroll";
import type { TweakExtra, TweakExtraOption, ViewMode } from "../types";
import {
  HOVER_EFFECTS, SCALE_MAX, SCALE_MIN, SCALE_STEP,
  type CatalogTweaks, type HoverEffect, type MetadataMode,
} from "./useTweaks";

/**
 * The floating "Browse Tweaks" panel, ported from the standalone site: a draggable card of the
 * device-scoped controls — cover size (per view, per pointer class), hover effect, rounded corners,
 * the metadata strip, and whatever extras the section registered. It renders nothing section-
 * specific itself; the section's extras arrive as `TweakExtra` rows — a Seg, or (nine backdrops)
 * the Long Box's 4-column swatch grid.
 */
export interface TweaksPanelRows {
  cover?: boolean;
  hover?: boolean;
  rounded?: boolean;
  metadata?: boolean;
}

export interface TweaksPanelProps {
  view: ViewMode;
  tweaks: CatalogTweaks;
  coverScale: number;
  onCoverScale: (v: number) => void;
  onChange: (patch: Partial<CatalogTweaks>) => void;
  onExtra: (key: string, value: string) => void;
  extras?: TweakExtra[];
  onClose: () => void;
  /**
   * Which of the standard card rows apply here. A control that does NOT apply is REMOVED, not
   * disabled (the Long Box rule): a page without the results root — the Books Shelf, which draws
   * its own cards — has a cover size and nothing else.
   */
  rows?: TweaksPanelRows;
  /** The label under the panel; the catalog says which VIEW the settings belong to. */
  footNote?: string;
  /** Extra rows a section wants above the standard ones (e.g. a Directory-only toggle). */
  children?: ReactNode;
}

function Seg({ options, value, onChange }: {
  options: { value: string; label: string }[]; value: string; onChange: (v: string) => void;
}) {
  const n = options.length;
  const idx = Math.max(0, options.findIndex((o) => o.value === value));
  return (
    <div className="twk-seg" role="radiogroup">
      <div className="twk-seg-thumb" style={{ left: `calc(2px + ${idx} * (100% - 4px) / ${n})`, width: `calc((100% - 4px) / ${n})` }} />
      {options.map((o) => (
        <button key={o.value} type="button" role="radio" aria-checked={o.value === value} onClick={() => onChange(o.value)}>
          {o.label}
        </button>
      ))}
    </div>
  );
}

/**
 * The Long Box's background grid: nine colours, four to a row, a tick on the chosen one. A swatch
 * from the OTHER light/dark family is dimmed and says so — clicking it still works, because the
 * host answers a cross-family pick by asking the site to switch theme, so no swatch is inert.
 */
export function SwatchGrid({ options, value, onChange }: {
  options: TweakExtraOption[]; value: string; onChange: (v: string) => void;
}) {
  return (
    <div className="twk-swatches" role="radiogroup">
      {options.map((o) => {
        const on = o.value === value;
        return (
          <button
            key={o.value} type="button" role="radio" aria-checked={on}
            className="twk-swatch" data-on={on ? "1" : "0"} data-inactive={o.inactive ? "1" : undefined}
            data-family={o.family ?? "any"}
            style={{ background: o.color ?? "var(--content-bg)" }}
            onClick={() => onChange(o.value)}
            aria-label={o.inactive ? `${o.label} (${o.family} theme)` : o.label}
            title={o.inactive ? `${o.label} — switches to the ${o.family} theme` : o.label}
          >
            {on && (
              <svg viewBox="0 0 14 14" width="11" height="11" aria-hidden="true">
                <path d="M3 7.2 5.8 10 11 4.2" fill="none" strokeWidth="2.2" strokeLinecap="round" strokeLinejoin="round"
                  stroke={o.family === "dark" ? "rgba(255,255,255,.92)" : o.family === "light" ? "rgba(0,0,0,.78)" : "var(--text-primary)"} />
              </svg>
            )}
          </button>
        );
      })}
    </div>
  );
}

export function TweakRow({ label, value, inline, children }: { label: string; value?: string | number; inline?: boolean; children: ReactNode }) {
  return (
    <div className={`twk-row${inline ? " twk-row-h" : ""}`}>
      <div className="twk-lbl">
        <span>{label}</span>
        {value != null && <span className="twk-val">{value}</span>}
      </div>
      {children}
    </div>
  );
}

export function TweakToggle({ on, onChange, label }: { on: boolean; onChange: (v: boolean) => void; label: string }) {
  return (
    <button type="button" className="twk-toggle" data-on={on ? "1" : "0"} role="switch" aria-checked={on} aria-label={label} onClick={() => onChange(!on)}>
      <i />
    </button>
  );
}

const METADATA_MODES: { value: MetadataMode; label: string }[] = [
  { value: "label", label: "Labels" },
  { value: "minimal", label: "Covers only" },
];

const ALL_ROWS: Required<TweaksPanelRows> = { cover: true, hover: true, rounded: true, metadata: true };

/** How long after the last scroll event the glass comes back (the engine's own settle window). */
const GLASS_SETTLE_MS = 160;

export default function TweaksPanel({ view, tweaks, coverScale, onCoverScale, onChange, onExtra, extras, onClose, rows, footNote, children }: TweaksPanelProps) {
  const dragRef = useRef<HTMLDivElement>(null);
  const posRef = useRef({ x: 16, y: 64 });
  const show = rows ? { ...ALL_ROWS, ...rows } : ALL_ROWS;

  // Drag by the header; the panel is anchored bottom/right so it never leaves the viewport.
  const onDragStart = useCallback((e: React.MouseEvent) => {
    const panel = dragRef.current;
    if (!panel) return;
    e.preventDefault();
    const r = panel.getBoundingClientRect();
    const sx = e.clientX;
    const sy = e.clientY;
    const startRight = window.innerWidth - r.right;
    const startBottom = window.innerHeight - r.bottom;
    const move = (ev: MouseEvent) => {
      const nx = startRight - (ev.clientX - sx);
      const ny = startBottom - (ev.clientY - sy);
      const x = Math.max(8, Math.min(window.innerWidth - panel.offsetWidth - 8, nx));
      const y = Math.max(8, Math.min(window.innerHeight - panel.offsetHeight - 8, ny));
      posRef.current = { x, y };
      panel.style.right = `${x}px`;
      panel.style.bottom = `${y}px`;
    };
    const up = () => {
      window.removeEventListener("mousemove", move);
      window.removeEventListener("mouseup", up);
    };
    window.addEventListener("mousemove", move);
    window.addEventListener("mouseup", up);
  }, []);

  // Escape closes (every floating tool on the site does); the scrim is the phone's tap-away.
  useEffect(() => {
    const onKey = (e: KeyboardEvent) => { if (e.key === "Escape") { e.stopPropagation(); onClose(); } };
    window.addEventListener("keydown", onKey);
    return () => window.removeEventListener("keydown", onKey);
  }, [onClose]);

  /**
   * The glass, and the law it would otherwise break. `.twk-panel` is a `backdrop-filter: blur(24px)
   * saturate(160%)` card that sits OVER the results scroller — and a backdrop-filter over scrolling
   * content is re-composited every frame, which is exactly the "no backdrop-filter over a scroller"
   * law the catalog holds elsewhere. The panel is transient (drag-open, Escape-close), so it keeps
   * its glass while the page is still: `data-scrolling` is set on the first scroll event and
   * cleared 160 ms after the last one (the engine's own settle window), and the CSS swaps the blur
   * for an opaque chrome for exactly that long. The reader never sees the swap — the panel is over
   * moving content while it happens. The listener is `onAnyScroll` (one CAPTURING window listener),
   * not a resolved root: the panel is `position: fixed` over whichever element is scrolling, and
   * resolving a root at mount can miss a scroller that is not yet taller than its box.
   */
  useEffect(() => {
    const panel = dragRef.current;
    if (!panel) return undefined;
    let t: ReturnType<typeof setTimeout> | undefined;
    const off = onAnyScroll(() => {
      panel.dataset.scrolling = "1";
      if (t) clearTimeout(t);
      t = setTimeout(() => { delete panel.dataset.scrolling; }, GLASS_SETTLE_MS);
    });
    return () => { off(); if (t) clearTimeout(t); };
  }, []);

  return (
    <>
    <div className="twk-scrim" onClick={onClose} aria-hidden="true" />
    <div ref={dragRef} className="twk-panel" role="dialog" aria-label="Browse tweaks" style={{ right: posRef.current.x, bottom: posRef.current.y }}>
      <div className="twk-hd" onMouseDown={onDragStart}>
        <b>Browse Tweaks</b>
        <button type="button" className="twk-x" aria-label="Close tweaks" onMouseDown={(e) => e.stopPropagation()} onClick={onClose}>✕</button>
      </div>
      <div className="twk-body">
        <div className="twk-sect">Cards</div>
        {show.cover && (
          <TweakRow label="Cover size" value={`${coverScale.toFixed(2)}×`}>
            <input
              type="range" className="twk-slider" aria-label="Cover size"
              min={SCALE_MIN} max={SCALE_MAX} step={SCALE_STEP} value={coverScale}
              onChange={(e) => onCoverScale(Number(e.target.value))}
            />
          </TweakRow>
        )}
        {show.hover && (
          <TweakRow label="Hover">
            <Seg options={HOVER_EFFECTS} value={tweaks.hover} onChange={(v) => onChange({ hover: v as HoverEffect })} />
          </TweakRow>
        )}
        {show.rounded && (
          <TweakRow label="Rounded corners" inline>
            <TweakToggle on={tweaks.rounded} onChange={(rounded) => onChange({ rounded })} label="Rounded corners" />
          </TweakRow>
        )}
        {show.metadata && (
          <TweakRow label="Under the cover">
            <Seg options={METADATA_MODES} value={tweaks.metadata} onChange={(v) => onChange({ metadata: v as MetadataMode })} />
          </TweakRow>
        )}
        {children}
        {extras && extras.length > 0 && (
          <>
            <div className="twk-sect">This section</div>
            {extras.map((x) => {
              const storeKey = x.perView ? `${x.key}:${view}` : x.key;
              const current = (x.perView ? tweaks.extras[storeKey] : undefined) ?? tweaks.extras[x.key] ?? x.options[0]?.value ?? "";
              return (
                <TweakRow key={x.key} label={x.perView ? `${x.label} (this view)` : x.label}>
                  {x.render === "swatch"
                    ? <SwatchGrid options={x.options} value={current} onChange={(v) => onExtra(storeKey, v)} />
                    : <Seg options={x.options} value={current} onChange={(v) => onExtra(storeKey, v)} />}
                </TweakRow>
              );
            })}
          </>
        )}
        <div className="twk-foot">{footNote ?? `Remembered on this device for the ${view === "shelf" ? "Shelves" : view.charAt(0).toUpperCase() + view.slice(1)} view.`}</div>
      </div>
    </div>
    </>
  );
}
