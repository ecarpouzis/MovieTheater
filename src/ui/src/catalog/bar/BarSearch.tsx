/**
 * The bar's search box for a section that has a plain text search (R9 S1d): a compact input in the
 * SectionBar's centre slot, bound to the section's own URL param by the page that mounts it. Enter
 * submits, the clear ✕ (or emptying the box) submits "" so the page drops the filter. Sections with
 * a facet spec portal the rail's SmartSearch into the same slot instead (Books, and every section
 * once S2 lands). On phones the slot does not exist — the search lives in the rail drawer / sheet —
 * so the portal renders nothing.
 */
import { useEffect, useState, type FormEvent } from "react";
import { createPortal } from "react-dom";
import useSlot, { BAR_SEARCH_SLOT } from "./useSlot";
import "../rail/rail.css";

export interface BarSearchProps {
  placeholder: string;
  /** The current search text from the URL (the box follows it; a fresh page shows the live filter). */
  value: string;
  onSubmit: (text: string) => void;
  ariaLabel?: string;
}

export function BarSearch({ placeholder, value, onSubmit, ariaLabel = "Search" }: BarSearchProps) {
  const [text, setText] = useState(value);
  useEffect(() => { setText(value); }, [value]);
  const submit = (e: FormEvent) => { e.preventDefault(); onSubmit(text.trim()); };
  return (
    <form className="bx-search bx-search-bar" role="search" onSubmit={submit}>
      <span className="bx-search-icon" aria-hidden="true">
        <svg viewBox="0 0 16 16" width="14" height="14" fill="none" stroke="currentColor" strokeWidth="1.7" strokeLinecap="round"><circle cx="7" cy="7" r="4.5" /><line x1="10.5" y1="10.5" x2="14" y2="14" /></svg>
      </span>
      <input
        type="search" className="bx-search-input" value={text} placeholder={placeholder} aria-label={ariaLabel}
        enterKeyHint="search" onChange={(e) => setText(e.target.value)}
      />
      {text && (
        <button type="button" className="bx-search-clear" aria-label="Clear search" onClick={() => { setText(""); onSubmit(""); }}>✕</button>
      )}
    </form>
  );
}

/** Mounts `BarSearch` in the bar's search slot; nothing where the slot is absent (phones, tests). */
export default function BarSearchPortal(props: BarSearchProps) {
  const slot = useSlot(BAR_SEARCH_SLOT);
  if (!slot) return null;
  return createPortal(<BarSearch {...props} />, slot);
}

export { BarSearchSlot, BarToolsSlot } from "./SlotPortal";
