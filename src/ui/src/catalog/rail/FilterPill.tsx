/**
 * The phone's Filters pill — the bar tool that raises a section's full-page facet sheet. One glyph,
 * one badge with the active count; every section with a FacetSpec mounts this same pill through the
 * host's `tools` (or `BarToolsSlot` when there is no host).
 *
 * It is also the site's ANSWER to "does this page have a rail on a phone": the pill is mounted
 * exactly when one exists, so it publishes itself (`publishSectionRail`) for the nav drawer, which
 * lives above the router and cannot see the page's rail state. See `useSlot.ts`.
 */
import { useEffect } from "react";
import { publishSectionRail } from "../bar/useSlot";

export function FilterGlyph() {
  return (
    <svg viewBox="0 0 16 16" width="15" height="15" fill="none" stroke="currentColor" strokeWidth="1.5" aria-hidden="true">
      <line x1="2" y1="4" x2="14" y2="4" /><line x1="2" y1="8" x2="14" y2="8" /><line x1="2" y1="12" x2="14" y2="12" />
      <circle cx="6" cy="4" r="1.7" fill="currentColor" stroke="none" /><circle cx="10" cy="8" r="1.7" fill="currentColor" stroke="none" /><circle cx="5" cy="12" r="1.7" fill="currentColor" stroke="none" />
    </svg>
  );
}

export default function FilterPill({ count, onClick }: { count: number; onClick: () => void }) {
  useEffect(() => {
    publishSectionRail(count);
    return () => publishSectionRail(null);
  }, [count]);
  return (
    <button type="button" className="bx-filter-pill" onClick={onClick} aria-label="Filters" title="Filters">
      <FilterGlyph />
      {count > 0 && <span className="bx-tool-num">{count}</span>}
    </button>
  );
}
