/**
 * A portal target owned by the SectionBar (or the phone top bar), by id. The bar mounts before the
 * routed page in the tree, so the element exists by the time a page's layout effect asks for it;
 * the bar re-announces its slots (`section-bar:slots`) whenever its layout changes (phone ⇄ desktop,
 * a section switch), so a mounted consumer re-targets instead of portaling into a detached node.
 */
import { useEffect, useLayoutEffect, useState } from "react";

export const SLOT_EVENT = "section-bar:slots";
export const BAR_TOOLS_SLOT = "section-bar-tools";
export const BAR_SEARCH_SLOT = "section-bar-search";
export const TOPBAR_TOOLS_SLOT = "topbar-tools";

export function announceSlots(): void {
  if (typeof window !== "undefined") window.dispatchEvent(new Event(SLOT_EVENT));
}

export default function useSlot(id: string | null): HTMLElement | null {
  const [el, setEl] = useState<HTMLElement | null>(null);
  useLayoutEffect(() => {
    setEl(id ? document.getElementById(id) : null);
  }, [id]);
  useEffect(() => {
    if (!id) return undefined;
    const resolve = () => setEl(document.getElementById(id));
    window.addEventListener(SLOT_EVENT, resolve);
    return () => window.removeEventListener(SLOT_EVENT, resolve);
  }, [id]);
  return el;
}

/**
 * The phone top bar's search button asks the page first: a section whose search lives in its facet
 * sheet (`useRailSheet`) handles this event (preventDefault) and raises the sheet; otherwise the
 * bar falls back to opening the rail drawer, where the older sections keep their search fields.
 */
export const SEARCH_EVENT = "section-bar:search";

/** True when a page took the search request. */
export function requestSectionSearch(): boolean {
  if (typeof window === "undefined") return false;
  return !window.dispatchEvent(new CustomEvent(SEARCH_EVENT, { cancelable: true }));
}
