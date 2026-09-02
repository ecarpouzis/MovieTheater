import { useLayoutEffect, useState, type RefObject } from "react";

/**
 * The horizontal virtualiser every strip-shaped surface rides — the Shelves' planks
 * (`views/shelves/Shelf.tsx`) and the Extended strips (`views/ExtendedView.tsx` → `Strip`).
 * Lifted out of the Shelves' geometry (the standalone's views-perf law #9) so the mechanism is
 * ONE: a run of units with known widths reserves its full width up front, only the slice near the
 * scrollport is mounted, and exact-width spacers stand in for the rest — the scroll width never
 * changes, so nothing reflows and a remembered `scrollLeft` still means the same place.
 *
 * Pure geometry in `horizontalWindow`; the hook wires it to a scroller's `scroll` + resize and
 * re-renders only when the window moves by more than `hysteresis` units.
 */
export interface HorizontalWindow { start: number; end: number }

/** prefix[i] = px of units before unit i (gaps excluded); prefix[n] = the packed width of n units. */
export function prefixSums(widths: ArrayLike<number>): number[] {
  const p = new Array<number>(widths.length + 1);
  p[0] = 0;
  for (let i = 0; i < widths.length; i += 1) p[i + 1] = p[i] + widths[i];
  return p;
}

export interface WindowGeometry {
  prefix: number[];
  n: number;
  /** The gap between consecutive units (a flex `gap`, or one baked into each unit's width). */
  gap: number;
  /** The run's left inset inside the scroller (its padding-left). */
  padLeft: number;
  /** Px kept mounted either side of the scrollport. */
  keepPx: number;
  /** Units mounted beyond the kept region on each side — the headroom the hysteresis spends. */
  slack: number;
}

/** The mounted slice: units whose slots fall within scrollLeft ± keepPx (two binary searches over the prefix), widened by `slack`. */
export function horizontalWindow(g: WindowGeometry, scrollLeft: number, clientWidth: number): HorizontalWindow {
  const { prefix, n, gap, padLeft, keepPx, slack } = g;
  const left = scrollLeft - keepPx;
  const right = scrollLeft + clientWidth + keepPx;
  const pos = (i: number) => padLeft + prefix[i] + i * gap;
  let lo = 0;
  let hi = n;
  while (lo < hi) { const mid = (lo + hi) >> 1; if (pos(mid + 1) <= left) lo = mid + 1; else hi = mid; }
  const start = lo;
  lo = start; hi = n;
  while (lo < hi) { const mid = (lo + hi) >> 1; if (pos(mid) <= right) lo = mid + 1; else hi = mid; }
  return { start: Math.max(0, start - slack), end: Math.min(n, lo + slack) };
}

/**
 * The width a lead spacer must take so unit `start` lands exactly where it would have unwindowed,
 * and the width a tail spacer must take after unit `end - 1` so the run's scroll width is preserved
 * — under a flex `gap` (each spacer is itself preceded/followed by one gap). With `gap: 0` these
 * reduce to the plain prefix differences.
 */
export function spacerWidths(prefix: number[], n: number, gap: number, start: number, end: number): { lead: number; tail: number } {
  const lead = start > 0 ? prefix[start] + (start - 1) * gap : 0;
  const tail = end < n ? prefix[n] - prefix[end] + (n - end - 1) * gap : 0;
  return { lead: Math.max(0, lead), tail: Math.max(0, tail) };
}

export interface UseHorizontalWindowOpts {
  prefix: number[];
  n: number;
  /** A number, or a getter for a gap that is itself derived from layout (the Shelves' adaptive gap). Keep the identity stable. */
  gap: number | (() => number);
  padLeft?: number;
  /** Px kept either side, or a function of the scrollport width for a viewport-relative margin. Keep the identity stable. */
  keepPx: number | ((clientWidth: number) => number);
  slack: number;
  /** A run at or under this length mounts whole — the window only pays for itself past it. */
  threshold: number;
  /** The window re-renders only when an edge moves by more than this many units (≤ `slack`, or a gap opens). */
  hysteresis?: number;
}

/**
 * `null` = mount the whole run (short runs, an unmeasured scroller). Before the first measure a long
 * run mounts its first `threshold` units, so even the initial commit is bounded; the layout effect
 * then reads the real `scrollLeft` (a restored one included — the caller's restore effect must be
 * declared BEFORE this hook) and re-renders the exact window before paint.
 */
export function useHorizontalWindow(ref: RefObject<HTMLElement | null>, o: UseHorizontalWindowOpts): HorizontalWindow | null {
  const { prefix, n, gap, padLeft = 0, keepPx, slack, threshold, hysteresis = slack } = o;
  const [win, setWin] = useState<HorizontalWindow | null>(() => (n > threshold ? { start: 0, end: Math.min(n, threshold) } : null));
  useLayoutEffect(() => {
    const el = ref.current;
    if (!el || n <= threshold) { setWin((p) => (p === null ? p : null)); return undefined; }
    let raf = 0;
    const compute = () => {
      raf = 0;
      const cw = el.clientWidth;
      // Unmeasured (display:none, a layout-free test DOM): the whole run, never a sliver of it.
      if (cw === 0) { setWin((p) => (p === null ? p : null)); return; }
      const g = typeof gap === "function" ? gap() : gap;
      const k = typeof keepPx === "function" ? keepPx(cw) : keepPx;
      const next = horizontalWindow({ prefix, n, gap: g, padLeft, keepPx: k, slack }, el.scrollLeft, cw);
      setWin((prev) => (prev && Math.abs(prev.start - next.start) <= hysteresis && Math.abs(prev.end - next.end) <= hysteresis ? prev : next));
    };
    const onScroll = () => { if (!raf) raf = requestAnimationFrame(compute); };
    compute();
    el.addEventListener("scroll", onScroll, { passive: true });
    const ro = typeof ResizeObserver !== "undefined" ? new ResizeObserver(onScroll) : null;
    ro?.observe(el);
    return () => { el.removeEventListener("scroll", onScroll); ro?.disconnect(); if (raf) cancelAnimationFrame(raf); };
  }, [ref, prefix, n, gap, padLeft, keepPx, slack, threshold, hysteresis]);
  return win;
}
