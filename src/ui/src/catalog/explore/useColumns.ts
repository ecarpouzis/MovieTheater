/**
 * How many columns of at least `minWidth` (with `gap` between them) fit the element — the clamped
 * modules (wall, grid, doors) render exactly `columns × rows` items, so they always end on a full row
 * and the covers that would have been hidden are never mounted (or fetched) at all.
 *
 * One ResizeObserver per module (never per card). Without one (the test DOM) the answer is
 * `fallback`, which is the safe reading: content, just not width-fitted.
 */
import { useLayoutEffect, useRef, useState } from "react";

export function columnsFor(width: number, minWidth: number, gap: number): number {
  if (!(width > 0)) return 0;
  return Math.max(1, Math.floor((width + gap) / (minWidth + gap)));
}

export function useColumns<T extends HTMLElement>(minWidth: number, gap: number, fallback = 6): [React.RefObject<T>, number] {
  const ref = useRef<T>(null);
  const [cols, setCols] = useState(fallback);
  useLayoutEffect(() => {
    const el = ref.current;
    if (!el) return;
    const measure = () => {
      const n = columnsFor(el.clientWidth, minWidth, gap);
      if (n > 0) setCols(n);
    };
    measure();
    if (typeof ResizeObserver === "undefined") return;
    const ro = new ResizeObserver(measure);
    ro.observe(el);
    return () => ro.disconnect();
  }, [minWidth, gap]);
  return [ref, cols];
}

/** The width-fitted module's numbers for the current viewport (phones get the smaller tiles). */
export function isNarrow(): boolean {
  return typeof window !== "undefined" && typeof window.matchMedia === "function" && window.matchMedia("(max-width: 640px)").matches;
}
