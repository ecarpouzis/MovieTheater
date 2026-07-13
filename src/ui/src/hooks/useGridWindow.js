import { useCallback, useEffect, useLayoutEffect, useRef, useState } from "react";
import { getScrollParent, nudgeScroll, onAnyScroll, viewportBand } from "../utils/scroll";

const OVERSCAN = 1200; // px of extra content kept mounted above and below the viewport
const MIN_WINDOWED = 120; // below this, mounting the whole list is cheaper than windowing it
const INITIAL_ITEMS = 48; // mounted on the first frame, before anything has been measured
const FALLBACK_ROW_H = 260; // only used until the first row has actually been measured

/** How many columns the grid is currently laying out. */
function measureCols(grid, items) {
  const cs = window.getComputedStyle(grid);
  if (cs.display.includes("grid")) {
    const tracks = cs.gridTemplateColumns;
    // The COMPUTED value is a resolved track list ("312px 312px 312px"), even for `auto-fill`.
    if (tracks && tracks !== "none") return tracks.trim().split(/\s+/).length;
  }
  if (!items.length) return 1;
  const top = items[0].offsetTop;
  let cols = 0;
  for (const el of items) {
    if (el.offsetTop !== top) break;
    cols += 1;
  }
  return Math.max(1, cols);
}

/** Last row whose top is at or above `offset` (binary search over the prefix sums). */
function rowAtOffset(prefix, offset) {
  let lo = 0;
  let hi = prefix.length - 1; // prefix has totalRows + 1 entries
  while (lo < hi) {
    const mid = (lo + hi + 1) >> 1;
    if (prefix[mid] <= offset) lo = mid;
    else hi = mid - 1;
  }
  return lo;
}

const clamp = (v, lo, hi) => Math.min(hi, Math.max(lo, v));

/**
 * DOM recycling for a card grid: only the rows near the viewport stay mounted, and the rest of the
 * list's height is reserved by two spacers. This is the single biggest lever from MyBooks'
 * views-perf catalog — `content-visibility` lets an off-screen card skip layout and paint, but the
 * node still exists and React still reconciles it, so a fully-mounted 600-card grid still pays for
 * all 600 on every render, and the heap grows without bound as you scroll.
 *
 * Row heights are MEASURED, not assumed. Browse's cards are a fixed height, but an arcade card's is
 * set by its box art and whether it has a version/cheats row, so rows genuinely differ. Measured
 * rows are cached by index; rows never yet mounted are estimated from the running average, which is
 * what keeps the scrollbar honest without loading anything. When a measurement corrects an estimate
 * for a row ABOVE the fold, everything below it shifts — so the anchor effect below scrolls by the
 * same delta to hold the content still (and `overflow-anchor: none` on the scrollers keeps the
 * browser from applying its own correction on top of ours, which oscillates).
 *
 * Usage — the spacers live OUTSIDE the grid element, so the grid's own layout is untouched:
 *
 *   const { hostRef, gridRef, start, end, padTop, padBottom } = useGridWindow(items.length, { resetKey });
 *   <div ref={hostRef}>
 *     <div style={{ height: padTop }} />
 *     <div className="card-list" ref={gridRef}>{items.slice(start, end).map(...)}</div>
 *     <div style={{ height: padBottom }} />
 *   </div>
 *
 * @param count      total number of items in the list (loaded ones — not the server's total)
 * @param resetKey   changes when the list becomes a *different* list (new search / jump): drops the
 *                   measured heights and returns the window to the top
 */
export default function useGridWindow(count, { resetKey = "", overscan = OVERSCAN, minItems = MIN_WINDOWED } = {}) {
  const hostRef = useRef(null);
  const gridRef = useRef(null);
  const rootRef = useRef(null); // resolved scroll root (null = the window)
  const rowHRef = useRef(new Map()); // row index → measured height, gap included
  const avgRowRef = useRef(FALLBACK_ROW_H);
  const colsRef = useRef(0);
  const prefixRef = useRef([0]); // prefix[r] = virtual top of row r
  const dirtyRef = useRef(true);
  const rafRef = useRef(0);
  const anchorRef = useRef(null); // { startRow, padTop } as last committed
  const widthRef = useRef(0);

  const windowed = count >= minItems;
  const initial = { start: 0, end: Math.min(count, INITIAL_ITEMS), padTop: 0, padBottom: 0, visibleStart: 0, startRow: 0 };
  const [win, setWin] = useState(windowed ? initial : { ...initial, end: count });
  const winRef = useRef(win);

  const buildPrefix = useCallback((totalRows) => {
    const avg = avgRowRef.current || FALLBACK_ROW_H;
    const heights = rowHRef.current;
    const prefix = new Array(totalRows + 1);
    prefix[0] = 0;
    for (let r = 0; r < totalRows; r += 1) prefix[r + 1] = prefix[r] + (heights.get(r) ?? avg);
    prefixRef.current = prefix;
    dirtyRef.current = false;
  }, []);

  // One rAF-coalesced pass: derive the mounted window from the scroll position. Runs on scroll, on
  // resize, and whenever the data or our height estimates change. Reads geometry only — never writes
  // to the DOM — so it can't thrash layout.
  const maintain = useCallback(() => {
    rafRef.current = 0;
    const grid = gridRef.current;
    const host = hostRef.current;
    if (!grid || !host || !count) return;

    const items = Array.from(grid.children);
    const cols = measureCols(grid, items);
    const colsChanged = cols !== colsRef.current;
    if (colsChanged) {
      // A different column count means every cached row height describes a row that no longer exists.
      colsRef.current = cols;
      rowHRef.current.clear();
      avgRowRef.current = FALLBACK_ROW_H;
      dirtyRef.current = true;
    }

    const totalRows = Math.ceil(count / cols);
    if (dirtyRef.current || prefixRef.current.length !== totalRows + 1) buildPrefix(totalRows);
    const prefix = prefixRef.current;

    // The virtual origin: where row 0 *would* sit, in viewport coordinates. Taken from a mounted
    // card (whose real position we can trust) rather than from our own estimates, so a wrong
    // estimate can never compound — each pass re-derives the origin from what's actually on screen.
    let origin;
    const first = items[0];
    if (first && !colsChanged) {
      origin = first.getBoundingClientRect().top - prefix[clamp(winRef.current.startRow, 0, totalRows)];
    } else {
      const gridTop = grid.getBoundingClientRect().top;
      origin = gridTop - prefix[clamp(winRef.current.startRow, 0, totalRows)];
    }

    const band = viewportBand(rootRef.current);
    const startRow = clamp(rowAtOffset(prefix, band.top - overscan - origin), 0, Math.max(0, totalRows - 1));
    const endRow = clamp(rowAtOffset(prefix, band.bottom + overscan - origin) + 1, startRow + 1, totalRows);
    const visRow = clamp(rowAtOffset(prefix, band.top - origin), 0, Math.max(0, totalRows - 1));

    const next = {
      start: startRow * cols,
      end: Math.min(count, endRow * cols),
      padTop: prefix[startRow],
      padBottom: Math.max(0, prefix[totalRows] - prefix[endRow]),
      visibleStart: Math.min(Math.max(0, count - 1), visRow * cols),
      startRow,
    };
    const cur = winRef.current;
    if (
      next.start !== cur.start || next.end !== cur.end || next.visibleStart !== cur.visibleStart ||
      Math.abs(next.padTop - cur.padTop) > 1 || Math.abs(next.padBottom - cur.padBottom) > 1
    ) {
      winRef.current = next;
      setWin(next);
    }
  }, [count, overscan, buildPrefix]);

  const schedule = useCallback(() => {
    if (rafRef.current) return;
    rafRef.current = requestAnimationFrame(maintain);
  }, [maintain]);

  // A new list (new search, or a pager jump): forget the measured rows and go back to the top.
  useLayoutEffect(() => {
    rowHRef.current.clear();
    avgRowRef.current = FALLBACK_ROW_H;
    colsRef.current = 0;
    dirtyRef.current = true;
    anchorRef.current = null;
    const fresh = { start: 0, end: Math.min(count, windowed ? INITIAL_ITEMS : count), padTop: 0, padBottom: 0, visibleStart: 0, startRow: 0 };
    winRef.current = fresh;
    setWin(fresh);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [resetKey]);

  // Not windowed (short list): mount everything and keep out of the way.
  useEffect(() => {
    if (windowed) {
      dirtyRef.current = true;
      schedule();
      return;
    }
    const full = { start: 0, end: count, padTop: 0, padBottom: 0, visibleStart: 0, startRow: 0 };
    if (winRef.current.end === count && winRef.current.start === 0 && !winRef.current.padTop) return;
    winRef.current = full;
    setWin(full);
  }, [windowed, count, schedule]);

  // Scroll + resize. One capturing window listener catches whichever element is actually scrolling.
  useEffect(() => {
    if (!windowed) return undefined;
    rootRef.current = getScrollParent(hostRef.current);
    const off = onAnyScroll(schedule);
    const grid = gridRef.current;
    let ro;
    if (grid && typeof ResizeObserver !== "undefined") {
      ro = new ResizeObserver(() => {
        // WIDTH only. Reacting to height would fire on our own window updates and loop forever.
        const w = grid.clientWidth;
        if (w === widthRef.current) return;
        widthRef.current = w;
        rowHRef.current.clear();
        avgRowRef.current = FALLBACK_ROW_H;
        dirtyRef.current = true;
        rootRef.current = getScrollParent(hostRef.current);
        schedule();
      });
      ro.observe(grid);
    }
    return () => {
      off();
      ro?.disconnect();
      if (rafRef.current) cancelAnimationFrame(rafRef.current);
      rafRef.current = 0;
    };
  }, [windowed, schedule]);

  // Measure the rows we just mounted, then re-derive the window if any estimate was wrong.
  useLayoutEffect(() => {
    if (!windowed) return;
    const grid = gridRef.current;
    if (!grid) return;
    const items = Array.from(grid.children);
    if (!items.length) return;
    const cols = colsRef.current || measureCols(grid, items);
    colsRef.current = cols;
    const gap = parseFloat(window.getComputedStyle(grid).rowGap) || 0;
    const startRow = winRef.current.startRow;
    const heights = rowHRef.current;
    let changed = false;

    for (let i = 0; i < items.length; i += cols) {
      const row = startRow + i / cols;
      const top = items[i].offsetTop;
      let h;
      if (i + cols < items.length) {
        h = items[i + cols].offsetTop - top; // row pitch: includes the gap
      } else {
        let tallest = 0;
        for (let j = i; j < items.length; j += 1) tallest = Math.max(tallest, items[j].offsetHeight);
        h = tallest + gap;
      }
      if (h > 0 && Math.abs((heights.get(row) ?? -1) - h) > 1) {
        heights.set(row, h);
        changed = true;
      }
    }

    if (changed) {
      let sum = 0;
      heights.forEach((v) => { sum += v; });
      avgRowRef.current = Math.round(sum / heights.size) || FALLBACK_ROW_H;
      dirtyRef.current = true;
      schedule();
    }
  }, [windowed, win.start, win.end, count, schedule]);

  // Hold the content still when a re-measure moves the rows above the fold. padTop changing while
  // startRow stays put means we corrected the estimated height of something above us, so everything
  // below just jumped by that delta — cancel it out. (padTop changing *because* startRow changed is
  // normal recycling: the rows we unmounted are replaced by exactly their own height.)
  useLayoutEffect(() => {
    const prev = anchorRef.current;
    if (prev && prev.startRow === win.startRow && prev.padTop !== win.padTop) {
      nudgeScroll(rootRef.current, win.padTop - prev.padTop);
    }
    anchorRef.current = { startRow: win.startRow, padTop: win.padTop };
  }, [win.startRow, win.padTop]);

  return {
    hostRef,
    gridRef,
    windowed,
    start: win.start,
    end: win.end,
    padTop: win.padTop,
    padBottom: win.padBottom,
    /** Index of the first item actually on screen (not counting overscan) — drives the arcade pager. */
    visibleStart: win.visibleStart,
  };
}
