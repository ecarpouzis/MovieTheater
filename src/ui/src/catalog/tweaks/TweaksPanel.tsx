import { useCallback, useRef, type ReactNode } from "react";
import type { TweakExtra, ViewMode } from "../types";
import {
  HOVER_EFFECTS, SCALE_MAX, SCALE_MIN, SCALE_STEP,
  type CatalogTweaks, type HoverEffect, type MetadataMode,
} from "./useTweaks";

/**
 * The floating "Browse Tweaks" panel, ported from the standalone site: a draggable card of the
 * device-scoped controls — cover size (per view, per pointer class), hover effect, rounded corners,
 * the metadata strip, and whatever extras the section registered. It renders nothing section-
 * specific itself; the section's extras arrive as `TweakExtra` rows.
 */
export interface TweaksPanelProps {
  view: ViewMode;
  tweaks: CatalogTweaks;
  coverScale: number;
  onCoverScale: (v: number) => void;
  onChange: (patch: Partial<CatalogTweaks>) => void;
  onExtra: (key: string, value: string) => void;
  extras?: TweakExtra[];
  onClose: () => void;
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

export default function TweaksPanel({ view, tweaks, coverScale, onCoverScale, onChange, onExtra, extras, onClose, children }: TweaksPanelProps) {
  const dragRef = useRef<HTMLDivElement>(null);
  const posRef = useRef({ x: 16, y: 64 });

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

  return (
    <div ref={dragRef} className="twk-panel" role="dialog" aria-label="Browse tweaks" style={{ right: posRef.current.x, bottom: posRef.current.y }}>
      <div className="twk-hd" onMouseDown={onDragStart}>
        <b>Browse Tweaks</b>
        <button type="button" className="twk-x" aria-label="Close tweaks" onMouseDown={(e) => e.stopPropagation()} onClick={onClose}>✕</button>
      </div>
      <div className="twk-body">
        <div className="twk-sect">Cards</div>
        <TweakRow label="Cover size" value={`${coverScale.toFixed(2)}×`}>
          <input
            type="range" className="twk-slider" aria-label="Cover size"
            min={SCALE_MIN} max={SCALE_MAX} step={SCALE_STEP} value={coverScale}
            onChange={(e) => onCoverScale(Number(e.target.value))}
          />
        </TweakRow>
        <TweakRow label="Hover">
          <Seg options={HOVER_EFFECTS} value={tweaks.hover} onChange={(v) => onChange({ hover: v as HoverEffect })} />
        </TweakRow>
        <TweakRow label="Rounded corners" inline>
          <TweakToggle on={tweaks.rounded} onChange={(rounded) => onChange({ rounded })} label="Rounded corners" />
        </TweakRow>
        <TweakRow label="Under the cover">
          <Seg options={METADATA_MODES} value={tweaks.metadata} onChange={(v) => onChange({ metadata: v as MetadataMode })} />
        </TweakRow>
        {children}
        {extras && extras.length > 0 && (
          <>
            <div className="twk-sect">This section</div>
            {extras.map((x) => {
              const storeKey = x.perView ? `${x.key}:${view}` : x.key;
              const current = (x.perView ? tweaks.extras[storeKey] : undefined) ?? tweaks.extras[x.key] ?? x.options[0]?.value ?? "";
              return (
                <TweakRow key={x.key} label={x.perView ? `${x.label} (this view)` : x.label}>
                  <Seg options={x.options} value={current} onChange={(v) => onExtra(storeKey, v)} />
                </TweakRow>
              );
            })}
          </>
        )}
        <div className="twk-foot">Remembered on this device for the {view === "shelf" ? "Shelves" : view.charAt(0).toUpperCase() + view.slice(1)} view.</div>
      </div>
    </div>
  );
}
