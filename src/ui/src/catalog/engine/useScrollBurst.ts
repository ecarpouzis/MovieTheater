import { useEffect, useState } from "react";
import { onRootScroll, resolveScrollRoot, scrollBurstGate } from "./scroller";

/**
 * The scroll-burst hover gate for a surface that is NOT under `InfiniteBands` (which runs the gate
 * inside its own scroll pass): resolve the surface's scroll root, and while it scrolls keep
 * `SCROLL_BURST_CLASS` on the surface so nothing under the cursor reacts to content sliding past.
 * The Newspaper wears it; the Shelves guard the same storm in their own delegated handlers.
 *
 * Returns a CALLBACK ref. A view mounts its surface only after its loading state, and an effect
 * keyed on a ref object never re-runs when `ref.current` changes — the sentinel-effect trap the
 * Newspaper had. Keying the effect on the element itself re-arms it the moment the surface mounts.
 */
export default function useScrollBurst(): (el: HTMLElement | null) => void {
  const [el, setEl] = useState<HTMLElement | null>(null);
  useEffect(() => {
    if (!el) return undefined;
    const gate = scrollBurstGate(() => el);
    const off = onRootScroll(resolveScrollRoot(el), gate.onScroll);
    return () => { off(); gate.dispose(); };
  }, [el]);
  return setEl;
}
