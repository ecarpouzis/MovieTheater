/**
 * The Command Deck's shared chrome, used by both readers: the icon set, the scrim + scroll + panel
 * shell with its three tiers (phone = full page, tablet = capped card, desktop = centred panel),
 * the header, the scrubber, and the keyboard-shortcuts footer. Everything is `.rmx-*` (the
 * standalone's classes, restyled in books-reader.css onto the section's tokens).
 */
import type { CSSProperties, PointerEvent as ReactPointerEvent, ReactNode, RefObject } from "react";
import { useEffect, useState } from "react";
import type { KidStyle } from "../KidsHome";

export function RmIcon({ d, fill = false, className = "ic" }: { d: string; fill?: boolean; className?: string }) {
  return (
    <svg className={className} viewBox="0 0 24 24" fill={fill ? "currentColor" : "none"} stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round" aria-hidden="true">
      <path d={d} />
    </svg>
  );
}

export const RM = {
  close: "M6 6l12 12M18 6L6 18",
  left: "M15 5l-7 7 7 7",
  right: "M9 5l7 7-7 7",
  plus: "M12 5v14M5 12h14",
  check: "M5 12.5l4.5 4.5L19 7",
  expand: "M4 9V4h5M20 9V4h-5M4 15v5h5M20 15v5h-5",
  target: "M12 4v3M12 17v3M4 12h3M17 12h3M12 12m-4 0a4 4 0 108 0 4 4 0 10-8 0",
  door: "M14 3H6a1 1 0 00-1 1v16a1 1 0 001 1h8M14 12h7M21 12l-3-3M21 12l-3 3",
  fit: "M3 3h18v18H3zM8 8h8v8H8z",
  rotcw: "M21 12a9 9 0 11-2.64-6.36M21 4v4h-4",
  rotccw: "M3 12a9 9 0 102.64-6.36M3 4v4h4",
  mirror: "M12 3v18M8 7l-4 5 4 5M16 7l4 5-4 5",
  width: "M2 12h20M2 12l4-4M2 12l4 4M22 12l-4-4M22 12l-4 4",
  height: "M12 2v20M12 2L8 6M12 2l4 4M12 22l-4-4M12 22l4-4",
  zoom: "M11 4a7 7 0 105.3 11.6L21 20M11 8v6M8 11h6",
  single: "M7 4h10v16H7z",
  spread: "M4 5h7v14H4zM13 5h7v14h-7",
  minus: "M5 12h14",
  webtoon: "M6 3h12v18H6zM9 9l3 3 3-3M9 14l3 3 3-3",
  list: "M8 6h13M8 12h13M8 18h13M3.5 6h.01M3.5 12h.01M3.5 18h.01",
};

export type MenuTier = "compact" | "tablet" | "desktop";

/** Phone < 680 px (full-page sheet), tablet 680–1023 (capped card), else desktop. Re-evaluated on resize. */
export function useMenuTier(): MenuTier {
  const compute = (): MenuTier => {
    if (typeof window === "undefined") return "desktop";
    const w = window.innerWidth;
    return w < 680 ? "compact" : w < 1024 ? "tablet" : "desktop";
  };
  const [tier, setTier] = useState<MenuTier>(compute);
  useEffect(() => {
    const on = () => setTier(compute());
    window.addEventListener("resize", on);
    return () => window.removeEventListener("resize", on);
  }, []);
  return tier;
}

export interface MenuShellProps {
  tier: MenuTier;
  kidsStyle?: KidStyle;
  onClose: () => void;
  maxWidth?: number;
  zIndex?: number;
  children: ReactNode;
}

/** The scrim, the scroll wrapper and the panel; a click on the scrim closes, inside does not. */
export function MenuShell({ tier, kidsStyle, onClose, maxWidth = 860, zIndex = 20, children }: MenuShellProps) {
  const compact = tier === "compact";
  const tablet = tier === "tablet";
  return (
    <div className={`rmx rdr-fill${kidsStyle ? ` kids-${kidsStyle}` : ""}`} style={{ zIndex }} onClick={onClose} role="dialog" aria-label="Reader controls">
      <div className="rmx-scrim" />
      <div className={`rmx-scroll${compact ? " compact" : tablet ? " tablet" : ""}`} onClick={(e) => e.stopPropagation()}>
        <div className="rmx-panel" style={{ maxWidth, padding: compact ? "calc(env(safe-area-inset-top, 0px) + 14px) 18px 24px" : tablet ? "12px 20px 22px" : 28 }}>
          <div className="rmx-grab" />
          {children}
        </div>
      </div>
    </div>
  );
}

export function MenuHead({ eyebrow, title, now, total, pct, compact, onClose }: { eyebrow: string; title: string; now: number; total: number; pct: number; compact: boolean; onClose: () => void }) {
  return (
    <div className="rmx-head">
      <div style={{ minWidth: 0 }}>
        <div className="rmx-eyebrow">{eyebrow}</div>
        <h1 className="rmx-title" style={{ fontSize: compact ? 22 : 28 }}>{title}</h1>
      </div>
      <div className="rmx-pageflag">
        <span><b>{now}</b> / {total}</span>
        <span className="pct">{pct}% read</span>
      </div>
      <button type="button" className="rmx-close" aria-label="Close controls" title="Close controls (Esc)" onClick={onClose}>
        <RmIcon d={RM.close} />
      </button>
    </div>
  );
}

export interface ScrubberProps {
  label: string;
  totalLabel: string;
  /** 0–100. */
  progress: number;
  trackRef: RefObject<HTMLDivElement>;
  onPointerDown: (e: ReactPointerEvent<HTMLDivElement>) => void;
  onPrev: () => void;
  onNext: () => void;
  preview?: ReactNode;
}

export function Scrubber({ label, totalLabel, progress, trackRef, onPointerDown, onPrev, onNext, preview }: ScrubberProps) {
  return (
    <div className="rmx-scrub">
      <div className="rmx-scrub-meta">
        <span className="now">{label}</span>
        <span className="tot">{totalLabel}</span>
      </div>
      <div className="rmx-scrub-row">
        <button type="button" className="rmx-step" aria-label="Previous page" onClick={onPrev} data-reader-control><RmIcon d={RM.left} /></button>
        <div className="rmx-scrub-track" ref={trackRef} onPointerDown={onPointerDown} style={{ flex: 1 }} role="slider" aria-label="Position" aria-valuenow={Math.round(progress)} aria-valuemin={0} aria-valuemax={100}>
          <div className="rmx-scrub-fill" style={{ width: `${progress}%` }} />
          <div className="rmx-scrub-knob" style={{ left: `${progress}%` }}>{preview}</div>
        </div>
        <button type="button" className="rmx-step" aria-label="Next page" onClick={onNext} data-reader-control><RmIcon d={RM.right} /></button>
      </div>
    </div>
  );
}

export function MenuFooter({ keys, compact }: { keys: [string, string][]; compact: boolean }) {
  const [show, setShow] = useState(false);
  return (
    <>
      <div style={{ height: compact ? 10 : 18 }} />
      <div className="rmx-foot">
        <button type="button" className="rmx-shortcut-toggle" onClick={() => setShow((k) => !k)} data-reader-control>
          <svg className="ic" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round" aria-hidden="true" style={{ width: 13, height: 13 }}>
            <path d={show ? RM.minus : RM.plus} />
          </svg>
          Keyboard shortcuts
        </button>
        <span className="rmx-foot-hint">esc to close</span>
      </div>
      {show && (
        <div className="rmx-keys">
          {keys.map(([k, v]) => <div className="rmx-key" key={v}><kbd>{k}</kbd>{v}</div>)}
        </div>
      )}
    </>
  );
}

export const gapStyle = (h: number): CSSProperties => ({ height: h });
