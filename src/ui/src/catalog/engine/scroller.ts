import { getScrollParent } from "../../utils/scroll";

/**
 * The band engine's scroll root, RESOLVED rather than assumed. Desktop scrolls `.app-content`;
 * phones scroll the window (index.css hands scrolling to the window below 768 px). The standalone
 * site's engine did `closest('.bx-results')` because that element WAS its scroller; here the
 * results box is just a box, so everything the engine reads or writes about scrolling goes
 * through these helpers, with `null` meaning "the window".
 */
export type ScrollRoot = HTMLElement | null;

export function resolveScrollRoot(node: Element | null): ScrollRoot {
  return getScrollParent(node) as ScrollRoot;
}

/** Viewport y of the scrollport's top edge. */
export function scrollportTop(root: ScrollRoot): number {
  return root ? root.getBoundingClientRect().top : 0;
}

export function scrollportHeight(root: ScrollRoot): number {
  return root ? root.clientHeight : window.innerHeight;
}

export function getScrollTop(root: ScrollRoot): number {
  return root ? root.scrollTop : window.scrollY;
}

export function setScrollTop(root: ScrollRoot, y: number): void {
  if (root) root.scrollTop = y;
  else window.scrollTo(0, y);
}

/**
 * Fixed chrome the window slides underneath (the 48 px mobile top bar). An inner scroller already
 * starts below everything, so it is 0 there. One reading line for every windowed surface on the site.
 */
export function topInset(root: ScrollRoot): number {
  if (root) return 0;
  try {
    const raw = window.getComputedStyle(document.documentElement).getPropertyValue("--content-top-inset");
    const px = parseFloat(raw);
    return Number.isFinite(px) ? px : 0;
  } catch {
    return 0;
  }
}

/** Subscribe to the root's own scroll events (passive). */
export function onRootScroll(root: ScrollRoot, handler: () => void): () => void {
  const target: HTMLElement | Window = root ?? window;
  target.addEventListener("scroll", handler, { passive: true });
  return () => target.removeEventListener("scroll", handler);
}
