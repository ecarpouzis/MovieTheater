import { useCallback, useRef } from "react";

/**
 * The Wall sizes its fetch pages to the viewport: how many covers fill one screen decides the band
 * size, so the first band is exactly one screenful and every later band recycles as a whole. The
 * math is pure (and tested); the hook only measures the wrap container and reports changes.
 */
export const WALL_RESERVE_PX = 24;

export function wallCapacity(width: number, availableHeight: number, cellH: number, aspect = 0.66): number {
  const coverW = Math.max(1, Math.round(cellH * aspect));
  const cols = Math.max(1, Math.floor(width / coverW));
  const rows = Math.max(1, Math.floor(availableHeight / Math.max(1, cellH)));
  return Math.max(cols * rows, cols * 2);
}

export default function useWallCapacity(cellH: number, onCapacity?: (n: number) => void) {
  const reportedRef = useRef(0);
  return useCallback((el: HTMLDivElement | null) => {
    if (!el || !onCapacity) return;
    const width = el.clientWidth;
    const top = el.getBoundingClientRect().top;
    const avail = window.innerHeight - top - WALL_RESERVE_PX;
    const capacity = wallCapacity(width, avail, cellH);
    if (capacity !== reportedRef.current) {
      reportedRef.current = capacity;
      onCapacity(capacity);
    }
  }, [cellH, onCapacity]);
}
