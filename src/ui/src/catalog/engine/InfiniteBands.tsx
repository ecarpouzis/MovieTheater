import {
  forwardRef, memo, startTransition, useCallback, useEffect, useImperativeHandle, useLayoutEffect, useRef, useState,
  type CSSProperties, type ForwardedRef, type ReactNode,
} from "react";
import {
  getScrollTop, onRootScroll, resolveScrollRoot, scrollportHeight, scrollportTop, setScrollTop, topInset, type ScrollRoot,
} from "./scroller";

/**
 * InfiniteBands — the sparse-band engine behind every catalog view, ported from the standalone
 * site's InfiniteScroller (its shelves blueprint generalised).
 *
 * The whole result set is `totalBands` fixed slots of `perBand` units (items, or groups). Only the
 * bands within scrollTop ± KEEP_PX render real DOM; everything before/after collapses into ONE lead
 * and ONE tail spacer sized from per-band measured heights (an estimate until measured), so the
 * scrollbar is right immediately and DOM/heap stay bounded however far the user scrolls. Band data
 * is fetched on demand and cached for instant re-mount (recycling drops DOM, never data).
 *
 * The laws it encodes (views-perf, all measured on the standalone site):
 *  - The window is derived from scrollTop arithmetic over a height array — no per-slot
 *    IntersectionObserver, so no same-key placeholder↔real IO gotcha and nothing to observe in an
 *    all-placeholder region.
 *  - Band fetches go through a want-list the maintain pass REPLACES each run, drained by a pump
 *    capped at MAX_INFLIGHT, and a band only fetches once it has stayed wanted for MIN_WANT_AGE.
 *    Without the age gate, aborting swept-past fetches cascades (every abort frees a slot that
 *    starts the next about-to-be-swept band; the server runs each to completion; the landing bands
 *    starve — "no band mounted 8 s after a halfway drag").
 *  - Big band mounts run under startTransition so a fling keeps its frames.
 *  - `overflow-anchor: none` must be on the real scroller (index.css sets it on body and
 *    .app-content) — native anchoring double-compensates the engine's own scrollTop writes.
 *  - The scroll root is RESOLVED (see scroller.ts), never assumed.
 *  - `flow` mode renders bands as display:contents inside one wrap container so card rows wrap
 *    CONTINUOUSLY across band boundaries; spacers are full-width row-breaking blocks.
 */
export const KEEP_PX = 1200;
export const DEFAULT_EST = 800;
export const MAX_INFLIGHT = 4;
export const MIN_WANT_AGE = 150;
export const RETRY_MS = 2500;
export const JUMP_DEADLINE_MS = 6000;
const SETTLE_MS = 160;
const RESIZE_MS = 150;

export interface InfiniteBandsProps<T> {
  /** Total units across the whole result set (items, or groups). */
  total: number;
  /** Units per band (the page size fetchBand uses). */
  perBand: number;
  /** Band 0 when the caller already has it (it learned `total` from the same response). */
  band0?: T[];
  /** Query identity — caches/window/scroll reset when it changes. */
  queryKey: string;
  /**
   * Data identity UNDER a stable query (see `CatalogSource.dataVersion`). A change drops the cached
   * bands so they re-read, and KEEPS the window, the measured heights and the scroll position — the
   * in-place edit of a dense client list, which must not throw the reader back to the top.
   */
  dataVersion?: number;
  /** Fetch one band. HONOUR the signal: the pump aborts fetches for bands the window swept past. */
  fetchBand: (band: number, signal: AbortSignal) => Promise<T[]>;
  renderBand: (units: T[], band: number) => ReactNode;
  /** Continuous-wrap mode: bands are display:contents inside wrapClass. */
  flow?: boolean;
  wrapClass?: string;
  wrapStyle?: CSSProperties;
  estBandHeight?: number;
  /** The wrap container element (for the Wall's capacity measure). */
  onWrapEl?: (el: HTMLDivElement | null) => void;
  /**
   * The unit at the top of the scrollport, for a pager readout. "band" granularity fires only when
   * the band changes (cheap); "unit" interpolates within the band and fires on most scroll frames —
   * ask for it only when a letter rail needs it.
   */
  spy?: "band" | "unit";
  onSpy?: (unit: number, band: number) => void;
  /** Placeholder for a band whose data has not arrived (default: an empty spacer of its estimate). */
  renderPlaceholder?: (band: number, height: number) => ReactNode;
}

export interface InfiniteBandsHandle {
  /** Seek so that `unit` is at the top of the scrollport (estimate first, exact landing once its band mounts). */
  jumpToUnit(unit: number): void;
}

// One mounted band. memo'd so the engine's own scroll-state re-renders do NOT re-invoke renderBand:
// without this every spy setState during a scroll rebuilt the element tree for EVERY mounted card.
function BandSlotInner<T>({ data, band, flow, renderBand }: {
  data: T[]; band: number; flow?: boolean; renderBand: (units: T[], band: number) => ReactNode;
}) {
  return (
    <div data-iband={band} style={flow ? { display: "contents" } : undefined}>
      {renderBand(data, band)}
    </div>
  );
}
const BandSlot = memo(BandSlotInner) as typeof BandSlotInner;

function InfiniteBandsInner<T>(
  {
    total, perBand, band0, queryKey, dataVersion, fetchBand, renderBand,
    flow, wrapClass, wrapStyle, estBandHeight = DEFAULT_EST, onWrapEl,
    spy = "band", onSpy, renderPlaceholder,
  }: InfiniteBandsProps<T>,
  ref: ForwardedRef<InfiniteBandsHandle>,
) {
  const totalBands = Math.max(1, Math.ceil(total / Math.max(1, perBand)));

  const rootRef = useRef<HTMLDivElement | null>(null);
  const scrollRootRef = useRef<ScrollRoot>(null);
  const [extra, setExtra] = useState<Record<number, T[]>>({});
  const [win, setWin] = useState<{ start: number; end: number }>({ start: 0, end: 1 });
  const loadingRef = useRef<Set<number>>(new Set());
  const wantRef = useRef<number[]>([]);
  const inFlightRef = useRef(0);
  const abortersRef = useRef<Map<number, AbortController>>(new Map());
  const wantAgeRef = useRef<Map<number, number>>(new Map());
  const pumpTimerRef = useRef<ReturnType<typeof setTimeout> | undefined>(undefined);
  const fetchRef = useRef(fetchBand); fetchRef.current = fetchBand;
  const extraRef = useRef(extra); extraRef.current = extra;
  const spyRef = useRef(spy); spyRef.current = spy;
  const onSpyRef = useRef(onSpy); onSpyRef.current = onSpy;
  const lastSpyRef = useRef<{ unit: number; band: number }>({ unit: -1, band: -1 });
  const heightsRef = useRef<Map<number, number>>(new Map());
  const avgRef = useRef<number>(estBandHeight);
  // Scroll-anchor compensation: when the lead spacer's height changes while the window's start band
  // is unchanged (estimates refining), shift scrollTop by the delta so the content doesn't jump.
  const leadRef = useRef<{ start: number; lead: number }>({ start: 0, lead: 0 });
  const pendingJumpRef = useRef<{ band: number; frac: number; deadline: number } | null>(null);

  // Reset everything when the query identity changes. `staleRef` covers the render(s) BETWEEN the
  // key change and the reset effect's commit, when `win`/`extra` still hold the previous query's
  // state: rendering stale bands with a new renderBand of a different shape crashed the
  // standalone site once (Wall comics fed to the List's group renderer).
  const keyRef = useRef(queryKey);
  const staleRef = useRef(false);
  if (keyRef.current !== queryKey) {
    keyRef.current = queryKey;
    staleRef.current = true;
    loadingRef.current.clear();
    wantRef.current = [];
    wantAgeRef.current.clear();
    abortersRef.current.forEach((a) => a.abort());
    abortersRef.current.clear();
    // The old query's fetches no longer count against the cap — a fetcher that ignores its signal
    // must not hold the pump's slots forever (their own `finally` checks the key before decrementing).
    inFlightRef.current = 0;
    heightsRef.current.clear();
    avgRef.current = estBandHeight;
    leadRef.current = { start: 0, lead: 0 };
    pendingJumpRef.current = null;
    lastSpyRef.current = { unit: -1, band: -1 };
  }
  useEffect(() => {
    setExtra({});
    setWin({ start: 0, end: 1 });
    staleRef.current = false;
    // Scroll a NEW result set back to its top. Only when we are not already there (a fresh mount
    // during a hand scroll elsewhere on the page must not yank the viewport).
    const root = rootRef.current;
    if (root && getScrollTop(scrollRootRef.current) > 0) {
      const top = root.getBoundingClientRect().top - scrollportTop(scrollRootRef.current) + getScrollTop(scrollRootRef.current) - topInset(scrollRootRef.current);
      if (getScrollTop(scrollRootRef.current) > top) setScrollTop(scrollRootRef.current, Math.max(0, top));
    }
  }, [queryKey]);

  // Data changed under a STABLE query (a dense list edited in place, a background chunk landing).
  // Drop the band cache and the pump's accounting so every mounted band re-reads — but leave the
  // window, the measured heights and the scroll position exactly where the reader left them. The
  // queryKey reset above is the other case: a different list, and it owns all three.
  const dvRef = useRef(dataVersion);
  useEffect(() => {
    if (dvRef.current === dataVersion) return;
    dvRef.current = dataVersion;
    loadingRef.current.clear();
    wantRef.current = [];
    wantAgeRef.current.clear();
    abortersRef.current.forEach((a) => a.abort());
    abortersRef.current.clear();
    inFlightRef.current = 0;
    setExtra({});
  }, [dataVersion]);

  const bandData = useCallback(
    (i: number): T[] | undefined => (i === 0 && band0 ? band0 : (staleRef.current ? undefined : extra[i])),
    [band0, extra],
  );

  const retryTimers = useRef<Set<ReturnType<typeof setTimeout>>>(new Set());
  useEffect(() => () => {
    retryTimers.current.forEach(clearTimeout);
    abortersRef.current.forEach((a) => a.abort());
    if (pumpTimerRef.current) clearTimeout(pumpTimerRef.current);
  }, []);

  // ── The pump: drain the want-list, at most MAX_INFLIGHT at once, each band only once it has
  //    stayed wanted for MIN_WANT_AGE (a drag step outruns the gate, so a sweep fires ZERO fetches).
  const pump = useCallback(() => {
    let soonest = Infinity;
    while (inFlightRef.current < MAX_INFLIGHT) {
      const now = Date.now();
      const i = wantRef.current.find((b) => {
        if (loadingRef.current.has(b) || extraRef.current[b]) return false;
        const age = now - (wantAgeRef.current.get(b) ?? now);
        if (age >= MIN_WANT_AGE) return true;
        soonest = Math.min(soonest, MIN_WANT_AGE - age);
        return false;
      });
      if (i === undefined) break;
      wantRef.current = wantRef.current.filter((b) => b !== i);
      const key = keyRef.current;
      loadingRef.current.add(i);
      inFlightRef.current += 1;
      const aborter = new AbortController();
      abortersRef.current.set(i, aborter);
      fetchRef.current(i, aborter.signal)
        .then((units) => {
          if (keyRef.current !== key) return; // stale query — drop
          // A page of cards is a big commit; keep it interruptible so scroll frames keep flowing.
          startTransition(() => setExtra((prev) => (prev[i] ? prev : { ...prev, [i]: units })));
        })
        .catch(() => {
          if (aborter.signal.aborted) return; // deliberate abort — not a failure, never re-want
          // Transient failure (flaky Wi-Fi): re-want after a beat. Self-limiting — the next maintain
          // replaces the list if the band is no longer in view.
          const t = setTimeout(() => {
            retryTimers.current.delete(t);
            if (keyRef.current === key && !wantRef.current.includes(i)) {
              wantRef.current.push(i);
              wantAgeRef.current.set(i, 0);
              pump();
            }
          }, RETRY_MS);
          retryTimers.current.add(t);
        })
        .finally(() => {
          if (abortersRef.current.get(i) === aborter) abortersRef.current.delete(i);
          if (keyRef.current !== key) return; // a reset already zeroed the accounting for this query
          loadingRef.current.delete(i);
          inFlightRef.current = Math.max(0, inFlightRef.current - 1);
          pump();
        });
    }
    if (soonest !== Infinity) {
      if (pumpTimerRef.current) clearTimeout(pumpTimerRef.current);
      pumpTimerRef.current = setTimeout(() => { pumpTimerRef.current = undefined; pump(); }, soonest + 10);
    }
  }, []);

  const bandH = useCallback((i: number) => heightsRef.current.get(i) ?? avgRef.current, []);

  /** Content-coordinate y of the engine's top edge in the scroll root. */
  const baseY = useCallback((root: HTMLElement, sr: ScrollRoot) =>
    root.getBoundingClientRect().top - scrollportTop(sr) + getScrollTop(sr), []);

  // ── The maintain pass: measure mounted bands, derive the window, want ahead. All geometry reads
  //    happen here; the only write (anchor compensation) is in a layout effect below.
  const maintain = useCallback(() => {
    const root = rootRef.current;
    if (!root) return;
    const sr = scrollRootRef.current;

    // 1. Measure mounted bands. Flow bands are display:contents and SHARE wrap rows with their
    //    neighbours, so a band's true stacked contribution is the delta between consecutive bands'
    //    first-child tops; the last mounted band falls back to its own child-rect extent.
    let measuredChange = false;
    const record = (idx: number, h: number) => {
      if (h > 0 && Math.abs((heightsRef.current.get(idx) ?? -1) - h) > 1) {
        heightsRef.current.set(idx, h);
        measuredChange = true;
      }
    };
    const mounted: { idx: number; el: HTMLElement }[] = [];
    root.querySelectorAll<HTMLElement>("[data-iband]").forEach((el) => {
      const idx = parseInt(el.dataset.iband || "-1", 10);
      if (idx >= 0) mounted.push({ idx, el });
    });
    mounted.sort((a, b) => a.idx - b.idx);
    if (flow) {
      for (let m = 0; m < mounted.length; m += 1) {
        const cur = mounted[m];
        const first = cur.el.firstElementChild as HTMLElement | null;
        if (!first) continue;
        const next = mounted[m + 1];
        if (next && next.idx === cur.idx + 1 && next.el.firstElementChild) {
          record(cur.idx, (next.el.firstElementChild as HTMLElement).getBoundingClientRect().top - first.getBoundingClientRect().top);
        } else {
          const last = cur.el.lastElementChild as HTMLElement | null;
          if (!last) continue;
          record(cur.idx, Math.max(0, last.getBoundingClientRect().bottom - first.getBoundingClientRect().top));
        }
      }
    } else {
      for (const { idx, el } of mounted) record(idx, el.offsetHeight);
    }
    if (measuredChange && heightsRef.current.size > 0) {
      let sum = 0;
      heightsRef.current.forEach((v) => { sum += v; });
      avgRef.current = Math.max(40, Math.round(sum / heightsRef.current.size));
    }

    // 2. Derive the window from scrollTop over the height array.
    const base = baseY(root, sr);
    const scrollTop = getScrollTop(sr);
    const lo = scrollTop - KEEP_PX;
    const hi = scrollTop + scrollportHeight(sr) + KEEP_PX;
    const readingLine = scrollTop + topInset(sr);

    let y = base;
    let start = totalBands;
    let end = 0;
    let spyBand = 0;
    let spyUnit = 0;
    for (let i = 0; i < totalBands; i += 1) {
      const h = bandH(i);
      if (y + h > lo && start === totalBands) start = i;
      if (y <= readingLine + 1) {
        spyBand = i;
        if (spyRef.current === "unit") {
          const frac = h > 0 ? Math.min(0.999, Math.max(0, (readingLine - y) / h)) : 0;
          spyUnit = i * perBand + Math.floor(frac * perBand);
        } else {
          spyUnit = i * perBand;
        }
      }
      if (y < hi) end = i + 1; else break;
      y += h;
    }
    if (start >= end) { start = Math.max(0, Math.min(start, totalBands - 1)); end = start + 1; }

    // Window shifts mount/unmount whole bands — interruptible, so a fling keeps its frames.
    startTransition(() => setWin((prev) => (prev.start === start && prev.end === end ? prev : { start, end })));
    const last = lastSpyRef.current;
    if (spyUnit !== last.unit || spyBand !== last.band) {
      lastSpyRef.current = { unit: spyUnit, band: spyBand };
      onSpyRef.current?.(Math.min(spyUnit, Math.max(0, total - 1)), spyBand);
    }

    // A jump's precise landing happens ONCE, in the layout effect below, when its band mounts.
    // Here we only expire the safety deadline so the anchor compensation can resume.
    if (pendingJumpRef.current && Date.now() > pendingJumpRef.current.deadline) pendingJumpRef.current = null;

    // 3. Want the window's bands + one ahead. REPLACES the want-list: swept-past bands are dropped
    //    unfetched, and anything in flight for a swept-past band is aborted so its slot frees now.
    const wantLo = Math.max(band0 ? 1 : 0, start);
    const wantHi = Math.min(totalBands, end + 1);
    const want: number[] = [];
    const now = Date.now();
    for (let i = wantLo; i < wantHi; i += 1) {
      if (!extraRef.current[i] && !loadingRef.current.has(i)) want.push(i);
    }
    wantRef.current = want;
    wantAgeRef.current.forEach((_, band) => {
      if ((band < wantLo || band >= wantHi) && !loadingRef.current.has(band)) wantAgeRef.current.delete(band);
    });
    for (const b of want) if (!wantAgeRef.current.has(b)) wantAgeRef.current.set(b, now);
    abortersRef.current.forEach((a, band) => { if (band < wantLo || band >= wantHi) a.abort(); });
    pump();
  }, [flow, totalBands, perBand, total, band0, pump, bandH, baseY]);

  // Wire scroll/resize once per query (and re-resolve the scroll root each time).
  useEffect(() => {
    const root = rootRef.current;
    if (!root) return undefined;
    scrollRootRef.current = resolveScrollRoot(root);
    let raf = 0;
    // Hover gate: Chrome re-dispatches pointerover for content moving under a STATIONARY cursor, so
    // every card passing under the mouse during a wheel scroll ran its hover transition. While
    // scrolling, `.bx-inf-scrolling` turns off hit-testing for the stream's children — one class
    // toggle per burst, no React state; wheel events fall through to the scroller.
    let scrolling = false;
    let settleT: ReturnType<typeof setTimeout> | undefined;
    const onScroll = () => {
      if (!raf) raf = requestAnimationFrame(() => { raf = 0; maintain(); });
      if (!scrolling) { scrolling = true; rootRef.current?.classList.add("bx-inf-scrolling"); }
      if (settleT) clearTimeout(settleT);
      settleT = setTimeout(() => { scrolling = false; rootRef.current?.classList.remove("bx-inf-scrolling"); }, SETTLE_MS);
    };
    let rt: ReturnType<typeof setTimeout> | undefined;
    const onResize = () => {
      if (rt) clearTimeout(rt);
      rt = setTimeout(() => { scrollRootRef.current = resolveScrollRoot(rootRef.current); maintain(); }, RESIZE_MS);
    };
    // The moment the user drives the scroll themselves — wheel, touch, or a navigation key —
    // abandon a pending jump so it never snaps the viewport back out from under them.
    const cancelJump = () => { pendingJumpRef.current = null; };
    const onKey = (e: KeyboardEvent) => {
      if (e.key.startsWith("Arrow") || e.key === "PageUp" || e.key === "PageDown" || e.key === "Home" || e.key === "End" || e.key === " ") cancelJump();
    };
    const offScroll = onRootScroll(scrollRootRef.current, onScroll);
    window.addEventListener("wheel", cancelJump, { passive: true, capture: true });
    window.addEventListener("touchmove", cancelJump, { passive: true, capture: true });
    window.addEventListener("keydown", onKey);
    window.addEventListener("resize", onResize);
    maintain();
    return () => {
      offScroll();
      window.removeEventListener("wheel", cancelJump, { capture: true });
      window.removeEventListener("touchmove", cancelJump, { capture: true });
      window.removeEventListener("keydown", onKey);
      window.removeEventListener("resize", onResize);
      if (raf) cancelAnimationFrame(raf);
      if (rt) clearTimeout(rt);
      if (settleT) clearTimeout(settleT);
      rootRef.current?.classList.remove("bx-inf-scrolling");
    };
  }, [maintain, queryKey]);

  // Re-measure + refetch when band data lands or the window moves (new DOM).
  useEffect(() => { maintain(); }, [extra, win.start, win.end, band0, maintain]);

  // ── Render: lead spacer · window bands (real or placeholder) · tail spacer.
  const winStart = staleRef.current ? 0 : win.start;
  const winEnd = staleRef.current ? 1 : Math.min(win.end, Math.max(1, totalBands));
  let leadH = 0;
  for (let i = 0; i < winStart; i += 1) leadH += bandH(i);
  let tailH = 0;
  for (let i = winEnd; i < totalBands; i += 1) tailH += bandH(i);

  // Scroll-anchor compensation (see leadRef). Stands down while a jump is pending (the one-shot
  // landing owns scrollTop then; two writers oscillate) and while stale (the reset owns it).
  useLayoutEffect(() => {
    const prev = leadRef.current;
    if (!pendingJumpRef.current && !staleRef.current && prev.start === winStart && prev.lead !== leadH) {
      const sr = scrollRootRef.current;
      setScrollTop(sr, getScrollTop(sr) + (leadH - prev.lead));
    }
    leadRef.current = { start: winStart, lead: leadH };
  });

  // ── One-shot jump landing: the moment the target band's real DOM is mounted, snap exactly to it
  //    ONCE and clear the jump. Element-anchored and single-shot — a per-frame re-pin feeds its own
  //    scroll back into the window/estimate recompute and oscillates between neighbouring letters.
  useLayoutEffect(() => {
    const pj = pendingJumpRef.current;
    if (!pj) return;
    const root = rootRef.current;
    if (!root) return;
    const el = root.querySelector<HTMLElement>(`[data-iband="${pj.band}"]`);
    if (!el) return; // not mounted yet — wait (the deadline in maintain is the safety valve)
    const sr = scrollRootRef.current;
    const anchor = flow ? ((el.firstElementChild as HTMLElement | null) ?? el) : el;
    const bandPx = (!flow ? el.getBoundingClientRect().height : 0) || bandH(pj.band);
    const within = pj.frac > 0 ? bandPx * pj.frac : 0;
    const top = anchor.getBoundingClientRect().top - scrollportTop(sr) + getScrollTop(sr);
    setScrollTop(sr, Math.max(0, top - 4 + within - topInset(sr)));
    pendingJumpRef.current = null;
  }, [extra, win.start, win.end, band0, flow, bandH]);

  const jumpTo = useCallback((band: number, frac = 0) => {
    const root = rootRef.current;
    if (!root) return;
    const sr = scrollRootRef.current;
    pendingJumpRef.current = { band, frac, deadline: Date.now() + JUMP_DEADLINE_MS };
    // The estimate: enough to pull the target band into the window so it fetches + mounts.
    let y = 0;
    const b = Math.min(band, totalBands);
    for (let i = 0; i < b; i += 1) y += bandH(i);
    if (frac > 0 && b < totalBands) y += bandH(b) * frac;
    setScrollTop(sr, Math.max(0, baseY(root, sr) + y - 4 - topInset(sr)));
    maintain();
  }, [totalBands, bandH, baseY, maintain]);

  useImperativeHandle(ref, () => ({
    jumpToUnit(unit: number) {
      const u = Math.max(0, Math.min(unit, Math.max(0, total - 1)));
      jumpTo(Math.floor(u / perBand), (u % perBand) / perBand);
    },
  }), [jumpTo, perBand, total]);

  const spacer = (h: number, key: string) => (
    <div key={key} aria-hidden="true" className="bx-band-spacer" style={flow ? { flexBasis: "100%", width: "100%", height: h, minHeight: h } : { height: h, minHeight: h }} />
  );

  const bands: ReactNode[] = [];
  if (leadH > 0) bands.push(spacer(leadH, "lead"));
  for (let i = winStart; i < winEnd; i += 1) {
    const data = bandData(i);
    if (data) bands.push(<BandSlot key={`b${i}`} data={data} band={i} flow={flow} renderBand={renderBand} />);
    else if (renderPlaceholder) bands.push(<div key={`p${i}`} aria-hidden="true" className="bx-band-placeholder" style={flow ? { display: "contents" } : undefined}>{renderPlaceholder(i, bandH(i))}</div>);
    else bands.push(spacer(bandH(i), `p${i}`));
  }
  if (tailH > 0) bands.push(spacer(tailH, "tail"));

  const wrapRefCb = useCallback((el: HTMLDivElement | null) => {
    rootRef.current = el;
    onWrapEl?.(el);
  }, [onWrapEl]);

  return <div className={wrapClass} style={wrapStyle} ref={wrapRefCb}>{bands}</div>;
}

const InfiniteBands = forwardRef(InfiniteBandsInner) as <T>(
  props: InfiniteBandsProps<T> & { ref?: ForwardedRef<InfiniteBandsHandle> },
) => ReturnType<typeof InfiniteBandsInner>;

export default InfiniteBands;
