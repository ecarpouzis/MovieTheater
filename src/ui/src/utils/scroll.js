// Scroll-root helpers shared by the infinite-scroll + windowing hooks.
//
// A scroll engine's scroll root must be RESOLVED, never assumed: desktop scrolls inside
// `.app-content` (index.css) while mobile scrolls the window, so anything that measures "is this
// near the viewport?" has to ask which element is actually scrolling. Assuming `window` silently
// produces an engine that looks like it works — the sentinel never enters the window's viewport
// because the window never scrolls — so the list simply stops loading.

/** The nearest scrollable ancestor of `node`, or null when the window itself is the scroller. */
export function getScrollParent(node) {
  let el = node?.parentElement;
  while (el) {
    const oy = window.getComputedStyle(el).overflowY;
    if ((oy === "auto" || oy === "scroll") && el.scrollHeight > el.clientHeight) return el;
    el = el.parentElement;
  }
  return null; // null root = the viewport (mobile / window scroll)
}

/** The visible band of the scroll root, in viewport coordinates. */
export function viewportBand(root) {
  if (root) {
    const r = root.getBoundingClientRect();
    return { top: r.top, bottom: r.bottom };
  }
  return { top: 0, bottom: window.innerHeight };
}

/** Nudge the scroll position by `dy` px — used to compensate for content re-measured above the fold. */
export function nudgeScroll(root, dy) {
  if (!dy) return;
  if (root) root.scrollTop += dy;
  else window.scrollBy(0, dy);
}

/**
 * Subscribe to every scroll in the page, whatever element is doing it.
 *
 * Scroll events don't bubble, but they DO propagate in the capture phase, so one capturing listener
 * on `window` catches the window's scroll AND `.app-content`'s — no per-container bookkeeping, and
 * no silent breakage the day a layout refactor moves the scroller.
 */
export function onAnyScroll(handler) {
  window.addEventListener("scroll", handler, { passive: true, capture: true });
  window.addEventListener("resize", handler, { passive: true });
  return () => {
    window.removeEventListener("scroll", handler, { capture: true });
    window.removeEventListener("resize", handler);
  };
}
