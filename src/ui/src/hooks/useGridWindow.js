import { useCallback, useEffect, useLayoutEffect, useRef, useState } from "react";
import { getScrollParent, nudgeScroll, onAnyScroll, viewportBand } from "../utils/scroll";

const OVERSCAN = 1200; // px of extra content kept mounted above and below the viewport
/** Safety valve on a pending jump: if the target never mounts (a page fetch that fails, a filter
 *  change mid-flight), stop waiting and let the ordinary compensation resume. The Long Box uses
 *  6 s for the same reason — a deep-$skip band fetch genuinely takes seconds. */
const JUMP_DEADLINE_MS = 6000;
/** How far a row may sit ABOVE the reading line and still be called the row at the top.
 *
 *  Without it the readout is decided by the top EDGE, and a half-pixel of the previous row hanging
 *  over it wins — which on a phone (fractional device pixels, momentum scrolling) happens constantly
 *  and reads as "the bar highlights the letter before the one I tapped". The Long Box's spy takes the
 *  same precaution twice over: a `+1` slack on its band test (`y <= scroller.scrollTop + 1`) and a
 *  reading LINE rather than an edge — `rootMargin: '0px 0px -98% 0px'`, i.e. only the top 2% of the
 *  scrollport counts as "at the top". */
const SPY_TOLERANCE = 2;
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
 * @param contentKey changes when the SAME slots get different content — a paged list whose
 *                   placeholders have just been replaced by real cards. Without it a re-measure only
 *                   ever runs when the WINDOW moves, so data arriving in place is never measured and
 *                   every row it silently resized stays wrong in the prefix (found by the
 *                   compensation test, 2026-08-13). The Long Box's engine re-runs its maintain pass
 *                   on `[extra, win.start, win.end, band0]` for exactly this reason — `extra` is its
 *                   fetched-band map.
 */
export default function useGridWindow(count, { resetKey = "", contentKey = "", overscan = OVERSCAN, minItems = MIN_WINDOWED } = {}) {
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
  // { index, deadline } while a jump is waiting for its target's real DOM. Owns scrollTop for as
  // long as it is set — see scrollToIndex.
  const pendingJumpRef = useRef(null);
  const topInsetRef = useRef(0);

  const windowed = count >= minItems;
  const initial = { start: 0, end: Math.min(count, INITIAL_ITEMS), padTop: 0, padBottom: 0, visibleStart: 0, startRow: 0 };
  const [win, setWin] = useState(windowed ? initial : { ...initial, end: count });
  const winRef = useRef(win);

  /**
   * The y at which "the top of the list" actually is, in viewport coordinates.
   *
   * An INNER scroller (desktop's .app-content) already begins below every fixed thing, so its own
   * rect top is the answer. When the WINDOW is the scroller (phones) it slides underneath fixed
   * chrome — `.navbar-topbar`, 48px — and scrolling a row to y=0 parks it behind an opaque bar. The
   * number lives in CSS (`--content-top-inset`, index.css) because it belongs to the layout that owns
   * the bar; this hook only reads it, and only when the scroll root is re-resolved.
   *
   * BOTH the jump landing and the active-row readout are measured from this line, which is the point:
   * when they were measured from different places they disagreed, and the disagreement is what
   * highlighted the wrong letter.
   */
  const readTopInset = useCallback(() => {
    if (rootRef.current) { topInsetRef.current = 0; return; }
    try {
      const raw = window.getComputedStyle(document.documentElement).getPropertyValue("--content-top-inset");
      const px = parseFloat(raw);
      topInsetRef.current = Number.isFinite(px) ? px : 0;
    } catch { topInsetRef.current = 0; }
  }, []);

  const readingLine = useCallback(() => viewportBand(rootRef.current).top + topInsetRef.current, []);

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
    // Expire a jump whose target never mounted (a page fetch that failed, or an estimate so exact
    // that no commit followed to run the landing effect). The Long Box expires its own deadline in
    // the same pass and for the same reason: until it clears, the compensation below stands down.
    if (pendingJumpRef.current && Date.now() > pendingJumpRef.current.deadline) {
      pendingJumpRef.current = null;
    }
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
    // The mounted window keeps measuring from the raw edge — overscan is about what to KEEP IN THE
    // DOM, and a row hidden behind the top bar still has to be mounted.
    const startRow = clamp(rowAtOffset(prefix, band.top - overscan - origin), 0, Math.max(0, totalRows - 1));
    const endRow = clamp(rowAtOffset(prefix, band.bottom + overscan - origin) + 1, startRow + 1, totalRows);
    // The READOUT measures from the reading line — the same line a jump lands on, plus a tolerance so
    // a sliver of the previous row cannot claim the top. See SPY_TOLERANCE and readingLine().
    const visRow = clamp(
      rowAtOffset(prefix, (readingLine() + SPY_TOLERANCE) - origin),
      0, Math.max(0, totalRows - 1),
    );

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
  }, [count, overscan, buildPrefix, readingLine]);

  const schedule = useCallback(() => {
    if (rafRef.current) return;
    rafRef.current = requestAnimationFrame(maintain);
  }, [maintain]);

  /**
   * Put the row containing `index` at the top of the scrollport — WITHOUT truncating the list.
   *
   * This is what an A–Z jump should always have been. The music library used to "jump" by
   * re-anchoring the rendered slice at the letter's offset, which meant everything before it stopped
   * existing: tap J and there was no way to scroll back up into A–I (reported 2026-08-13).
   *
   * ── TWO PHASES, not a settle loop ────────────────────────────────────────────────────────────
   * Ported from The Long Box's `InfiniteScroller.tsx` (features/browse), which is where this whole
   * spacer-window blueprint comes from. Rows that have never been mounted have never been measured,
   * so the prefix that produces a target is part estimate and one scroll lands NEAR the letter, not
   * on it. The Long Box's answer is not to iterate:
   *
   *   1. scroll ONCE to the estimate, purely to pull the target into the window so it mounts (and,
   *      where the data is paged, so it fetches);
   *   2. the instant the target's real DOM is on screen, snap exactly to it ONCE, element-anchored,
   *      and clear the jump.
   *
   * Its comment says why this is not a loop, and it is a bug report: *"A pager jump's precise landing
   * is done ONCE … not re-pinned every frame here (a per-frame correction fed its own scroll back
   * into the window/estimate recompute and oscillated between neighbouring letters)."* An iterating
   * version is fine on a list whose rows are all real and uniform (music), and actively wrong on one
   * where placeholders are becoming real cards underneath it (the arcade) — which is exactly the
   * list this hook now has to serve.
   *
   * Two more things come with it, and both are load-bearing: while a jump is pending the re-measure
   * compensation below stands down (two writers on scrollTop oscillate), and ANY hand scroll —
   * wheel, touch, or a navigation key — abandons the jump, so it can never snap the viewport back
   * out from under someone who has started reading.
   *
   * Native scroll anchoring would fight both of these, so this relies on the `overflow-anchor: none`
   * that index.css already sets on <body> and .app-content (Long Box views-perf #6f, same lesson).
   * Do not remove it on their account.
   */
  const scrollToIndex = useCallback((index) => {
    const host = hostRef.current;
    const grid = gridRef.current;
    if (!host || !grid || !count) return;
    rootRef.current = getScrollParent(host);
    readTopInset();
    const items = Array.from(grid.children);
    if (!items.length) return;

    const cols = colsRef.current || measureCols(grid, items) || 1;
    const totalRows = Math.max(1, Math.ceil(count / cols));
    if (dirtyRef.current || prefixRef.current.length !== totalRows + 1) buildPrefix(totalRows);
    const prefix = prefixRef.current;
    const wanted = clamp(index, 0, count - 1);
    const row = clamp(Math.floor(wanted / cols), 0, totalRows - 1);

    // Phase 1: the estimate. Enough to bring the row into the window; not claimed to be exact.
    const origin = items[0].getBoundingClientRect().top
      - prefix[clamp(winRef.current.startRow, 0, totalRows)];
    const delta = (origin + prefix[row]) - readingLine();
    // The pending jump is armed even when the estimate needs no scroll: on a paged list the target
    // may be a placeholder right now, and phase 2 is what waits for the real card.
    pendingJumpRef.current = { index: wanted, deadline: Date.now() + JUMP_DEADLINE_MS };
    if (Math.abs(delta) > 1) nudgeScroll(rootRef.current, delta);
    dirtyRef.current = true;
    maintain();
  }, [count, buildPrefix, maintain, readTopInset, readingLine]);

  // A new list (new search, or a pager jump): forget the measured rows and go back to the top.
  useLayoutEffect(() => {
    rowHRef.current.clear();
    avgRowRef.current = FALLBACK_ROW_H;
    colsRef.current = 0;
    dirtyRef.current = true;
    anchorRef.current = null;
    pendingJumpRef.current = null; // a jump into the OLD list means nothing in the new one
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
    readTopInset();
    const off = onAnyScroll(schedule);
    // ⚠ A hand scroll ABANDONS a pending jump (Long Box: the same wheel/touchmove/keydown trio).
    // Without it the landing effect can still be armed when the reader has started scrolling for
    // themselves, and it snaps the viewport back — remounting and re-fetching everything they were
    // looking at. Capturing, because these fire on whichever element is actually scrolling.
    const cancelJump = () => { pendingJumpRef.current = null; };
    const onKey = (e) => {
      if (e.key?.startsWith("Arrow") || e.key === "PageUp" || e.key === "PageDown"
          || e.key === "Home" || e.key === "End" || e.key === " ") cancelJump();
    };
    window.addEventListener("wheel", cancelJump, { passive: true, capture: true });
    window.addEventListener("touchmove", cancelJump, { passive: true, capture: true });
    window.addEventListener("keydown", onKey);
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
        readTopInset();  // the bar's height is breakpoint-dependent, so a resize can change it
        schedule();
      });
      ro.observe(grid);
    }
    return () => {
      off();
      window.removeEventListener("wheel", cancelJump, { capture: true });
      window.removeEventListener("touchmove", cancelJump, { capture: true });
      window.removeEventListener("keydown", onKey);
      ro?.disconnect();
      if (rafRef.current) cancelAnimationFrame(rafRef.current);
      rafRef.current = 0;
    };
  }, [windowed, schedule, readTopInset]);

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
  }, [windowed, win.start, win.end, count, contentKey, schedule]);

  // ── Phase 2 of a jump: the one-shot, element-anchored landing ───────────────────────────────────
  // Ported from The Long Box's InfiniteScroller: the estimate scroll only had to bring the target
  // into the window. The moment its REAL DOM is mounted — which on a paged list means the moment its
  // page has arrived — snap exactly to it, once, and clear the jump. Element-anchored because the
  // row is genuinely on screen now, so its top is a fact rather than a prefix sum; single-shot
  // because a per-frame re-pin feeds its own scroll back into the window recompute and oscillates.
  //
  // Runs on every commit (no dep array) rather than on [win] alone: on a paged list the commit that
  // matters is the one where the DATA landed, which the window state does not necessarily change.
  useLayoutEffect(() => {
    const pj = pendingJumpRef.current;
    if (!pj) return;
    if (Date.now() > pj.deadline) { pendingJumpRef.current = null; return; }
    const grid = gridRef.current;
    if (!grid) return;
    // Positional: children[i] IS item win.start + i. A placeholder counts as mounted DOM, so a paged
    // consumer that renders skeletons will land on the skeleton and then, when the real card
    // replaces it, the ordinary re-measure compensation below keeps it put.
    const offset = pj.index - win.start;
    if (offset < 0 || offset >= grid.children.length) return;
    const el = grid.children[offset];
    if (!el) return;
    const delta = el.getBoundingClientRect().top - readingLine();
    pendingJumpRef.current = null;
    if (Math.abs(delta) > 1) nudgeScroll(rootRef.current, delta);
    anchorRef.current = { startRow: win.startRow, padTop: win.padTop };
  });

  // Hold the content still when a re-measure moves the rows above the fold. padTop changing while
  // startRow stays put means we corrected the estimated height of something above us, so everything
  // below just jumped by that delta — cancel it out. (padTop changing *because* startRow changed is
  // normal recycling: the rows we unmounted are replaced by exactly their own height.)
  //
  // ⚠ Stands down while a jump is pending: that jump owns scrollTop until it lands, and two writers
  // on the same value oscillate (Long Box, same guard on its lead-spacer compensation).
  useLayoutEffect(() => {
    const prev = anchorRef.current;
    if (!pendingJumpRef.current && prev && prev.startRow === win.startRow && prev.padTop !== win.padTop) {
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
    /** First MOUNTED row. Exposed for the compensation's own tests, which have to distinguish
     *  "padTop moved because we re-estimated" from "padTop moved because we recycled". */
    startRow: win.startRow,
    /** Seek the scrollport to an item, leaving the list whole. See above. */
    scrollToIndex,
  };
}
