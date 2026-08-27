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
 * The phone top bar's magnifier.
 *
 * ONE filter surface on a phone (2026-08-28, Eric: "this filter button seems to present the same
 * options opening the drawer does — why do these buttons still exist?"). The drawer IS the sider and
 * carries the section's own `FacetRail`, so the bar's Filters pill and the page's full-page rail
 * SHEET are gone: the magnifier opens the DRAWER, and the rail's SmartSearch — the first thing under
 * the rail's head line on a phone — takes the caret.
 *
 * A module flag PLUS an event, not a prop, because of the mount order: the rail mounts WITH the
 * drawer (NavBar's `railVisible`), so at the instant the button is pressed there is no input to
 * focus. The flag survives that mount and the SmartSearch claims it; the event covers the other
 * case, a magnifier tap while the drawer is already open. A section with no facet spec (the TV
 * guide) never claims it, so the drawer closing drops it — an unclaimed request must not steal the
 * caret on the next section the reader opens.
 */
export const SEARCH_FOCUS_EVENT = "section-bar:search-focus";
let searchFocusPending = false;

export function requestRailSearchFocus(): void {
  searchFocusPending = true;
  if (typeof window !== "undefined") window.dispatchEvent(new Event(SEARCH_FOCUS_EVENT));
}

/** A rail SmartSearch claims the request ONCE — on mount, or on the event when it is already up. */
export function claimRailSearchFocus(): boolean {
  if (!searchFocusPending) return false;
  searchFocusPending = false;
  return true;
}

/** The drawer closing drops an unclaimed request. */
export function clearRailSearchFocus(): void {
  searchFocusPending = false;
}
