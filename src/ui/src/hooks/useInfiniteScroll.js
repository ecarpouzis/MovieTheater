import { useCallback, useEffect, useRef } from "react";
import { getScrollParent, onAnyScroll, viewportBand } from "../utils/scroll";

// Start loading the next page while the sentinel is still ~one screen below the fold.
const DEFAULT_MARGIN = 800;

/**
 * Bottom-sentinel infinite scroll, shared by Browse and the Arcade lobby.
 *
 * Position check, not an IntersectionObserver: the observer's callback only reports intersection
 * *changes*, and a fresh-per-page observer's first callback races with layout and can report "not
 * intersecting" — once the user is sitting at the bottom there's no further scroll to correct it, so
 * the chain stalls (most visibly on a short list's final partial page). A direct position check on
 * every scroll has no such transition/timing dependency.
 *
 * The listener subscribes ONCE and reads `hasMore`/`onLoadMore` through refs. The earlier per-page
 * effect (deps: [hasMore, loadMore, items.length]) tore down and re-added its listeners on every
 * append, and re-resolved the scroll parent — cheap per event, but a re-subscribe storm exactly while
 * the user is scrolling. Callers nudge it after an append via the returned `recheck` instead, which
 * also auto-fills a list too short to scroll.
 */
export default function useInfiniteScroll({ enabled, hasMore, onLoadMore, margin = DEFAULT_MARGIN }) {
  const sentinelRef = useRef(null);
  const hasMoreRef = useRef(hasMore);
  const loadRef = useRef(onLoadMore);
  const checkRef = useRef(null);
  hasMoreRef.current = hasMore;
  loadRef.current = onLoadMore;

  useEffect(() => {
    if (!enabled) return undefined;
    const node = sentinelRef.current;
    if (!node) return undefined;
    const root = getScrollParent(node);
    let scheduled = false;

    const maybeLoad = () => {
      scheduled = false;
      const sentinel = sentinelRef.current;
      if (!sentinel || !hasMoreRef.current) return;
      const { bottom } = viewportBand(root);
      // loadMore is idempotent (in-flight guard + a page cursor), so repeated calls are harmless.
      if (sentinel.getBoundingClientRect().top <= bottom + margin) loadRef.current?.();
    };
    const onScroll = () => {
      if (scheduled) return;
      scheduled = true;
      requestAnimationFrame(maybeLoad);
    };

    checkRef.current = onScroll;
    const off = onAnyScroll(onScroll);
    onScroll(); // fill immediately when the list is shorter than the viewport
    return () => {
      off();
      checkRef.current = null;
    };
  }, [enabled, margin]);

  /** Re-run the position check without re-subscribing — call after appending a page. */
  const recheck = useCallback(() => checkRef.current?.(), []);

  return { sentinelRef, recheck };
}
