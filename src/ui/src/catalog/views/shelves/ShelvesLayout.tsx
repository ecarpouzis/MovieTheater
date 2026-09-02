import { useCallback, useEffect, useMemo, useRef, useState, type CSSProperties } from "react";
import { getScrollTop, resolveScrollRoot, scrollportTop, setScrollTop, topInset, type ScrollRoot } from "../../engine/scroller";
import { DEAD_COOLDOWN_MS, RETRY_LIMIT, RETRY_STEP_MS } from "../../cards/CardImage";
import type { CardGroup, CardItem } from "../../types";
import Shelf from "./Shelf";
import { SHELF_BASE_H } from "./geometry";

/**
 * The bookcase's interaction engine, ported from the standalone site (its views-perf lessons are
 * the load-bearing part — see each block). ONE persistent wiring per query identity; DOM added by a
 * band mount or a horizontal load-more is wired incrementally by reconcile(), so scrolling never
 * tears down and rebuilds every listener.
 *
 * Bands: each `.bx-band` is one page of shelves (GROUPS_PAGE_SIZE) rendered as a flex-wrap row
 * set; an un-mounted band is a height-reserving placeholder. A vertical IntersectionObserver
 * (±1200 px) mounts bands entering the warm zone and recycles (unmounts) those leaving it, which
 * is what bounds node count + heap regardless of scroll distance.
 *
 * The scroll root is RESOLVED (desktop: .app-content; phones: the window) — never assumed.
 */
export interface ShelfSlot {
  band: number;
  groups?: CardGroup[];
  /** Placeholder height when `groups` is absent. */
  height?: number;
}

export interface PendingJump { band: number; within: number; nonce: number }

const NARROW_MQ = "(max-width: 480px)";
function useNarrowViewport(): boolean {
  const [narrow, setNarrow] = useState(() => (typeof window !== "undefined" && window.matchMedia(NARROW_MQ).matches));
  useEffect(() => {
    const mq = window.matchMedia(NARROW_MQ);
    const on = () => setNarrow(mq.matches);
    mq.addEventListener("change", on);
    return () => mq.removeEventListener("change", on);
  }, []);
  return narrow;
}

export interface ShelvesLayoutProps {
  slots: ShelfSlot[];
  /** Per-group items beyond the band page (horizontal "more"), by group key. */
  extras: Record<string, CardItem[]>;
  scale: number;
  noun: string;
  onOpen: (i: CardItem) => void;
  onOpenGroup: ((g: CardGroup) => void) | null;
  onLoadMore: ((groupKey: string, skip: number) => void) | null;
  onNeedBand: (band: number) => void;
  onBandFar: (band: number) => void;
  onBandHeight: (band: number, height: number) => void;
  /** The shelf at the top of the viewport: its band and its index within the band. */
  onActiveChange: (band: number, within: number) => void;
  pendingJump: PendingJump | null;
  onJumpHandled: () => void;
}

export default function ShelvesLayout({ slots, extras, scale, noun, onOpen, onOpenGroup, onLoadMore, onNeedBand, onBandFar, onBandHeight, onActiveChange, pendingJump, onJumpHandled }: ShelvesLayoutProps) {
  const rootRef = useRef<HTMLDivElement>(null);
  const narrow = useNarrowViewport();
  const shelfH = Math.round(SHELF_BASE_H * scale * (narrow ? 0.82 : 1));

  const onNeedBandRef = useRef(onNeedBand); onNeedBandRef.current = onNeedBand;
  const onBandFarRef = useRef(onBandFar); onBandFarRef.current = onBandFar;
  const onBandHeightRef = useRef(onBandHeight); onBandHeightRef.current = onBandHeight;
  const onActiveRef = useRef(onActiveChange); onActiveRef.current = onActiveChange;
  const onLoadMoreRef = useRef(onLoadMore); onLoadMoreRef.current = onLoadMore;
  const wiringRef = useRef<{ reconcile: () => void } | null>(null);

  // Band 0's keys are a stable proxy for query identity (the controller swaps band 0 on a query
  // change and appends later bands without touching it). Latched while band 0 is recycled.
  const band0SigRef = useRef("");
  const band0Sig = useMemo(() => {
    const b0 = slots.find((s) => s.band === 0)?.groups;
    if (b0) band0SigRef.current = b0.map((g) => g.key).join("\0");
    return band0SigRef.current;
  }, [slots]);

  const mergedSlots = useMemo(() => slots.map((s) => {
    if (!s.groups) return s;
    return { ...s, groups: s.groups.map((g) => (extras[g.key]?.length ? { ...g, items: [...g.items, ...extras[g.key]] } : g)) };
  }), [slots, extras]);
  const slotSig = mergedSlots.map((s) => (s.groups ? `${s.band}:${s.groups.map((g) => g.items.length).join(".")}` : `${s.band}P`)).join(",");

  // ── Jump to a band / shelf: scroll to the placeholder for immediate feedback, then refine to the
  //    exact shelf once the band has loaded, and clear the jump.
  useEffect(() => {
    if (!pendingJump) return;
    const root = rootRef.current;
    if (!root) return;
    const scroller = resolveScrollRoot(root);
    const bandEl = root.querySelector<HTMLElement>(`[data-band="${pendingJump.band}"]`);
    if (!bandEl) return;
    const loaded = !!slots.find((s) => s.band === pendingJump.band)?.groups;
    let target: HTMLElement = bandEl;
    if (loaded) {
      const shelves = bandEl.querySelectorAll<HTMLElement>(".shelf");
      const w = Math.max(0, Math.min(pendingJump.within, shelves.length - 1));
      if (shelves[w]) target = shelves[w];
    }
    // The engine's one reading line (scroller.ts): the resolved root's own coordinates, and the fixed
    // phone top bar only when the window is the scroller.
    const top = target.getBoundingClientRect().top - scrollportTop(scroller) + getScrollTop(scroller);
    setScrollTop(scroller, Math.max(0, top - (scroller ? 6 : 8) - topInset(scroller)));
    if (loaded) onJumpHandled();
  }, [pendingJump, slots, onJumpHandled]);

  // ── The persistent interaction machinery (rebuilt only on query change) ────────────────────
  useEffect(() => {
    const root = rootRef.current;
    if (!root) return undefined;
    const scroller: ScrollRoot = resolveScrollRoot(root);
    const isTouchPrimary = window.matchMedia("(pointer: coarse)").matches;
    const vpRect = () => (scroller ? scroller.getBoundingClientRect() : ({ top: 0, bottom: window.innerHeight } as DOMRect));

    const observedBands = new WeakSet<HTMLElement>();
    const heightHooked = new WeakSet<HTMLElement>();
    const observedShelf = new WeakSet<HTMLElement>();
    const nearBands = new Set<HTMLElement>();
    // Two-tier load margins: tight while a scroll is in flight (visible covers never queue behind
    // prefetch on a slow device), full prefetch on the settle timers; unload far behind with a
    // wide hysteresis so load/unload never thrash.
    const V_MARGIN = 500; const V_ACTIVE = 120; const H_MARGIN = 600; const H_ACTIVE = 150; const H_UNLOAD = 2400;
    let vScrolling = false;
    let vSettleTimer: ReturnType<typeof setTimeout> | undefined;
    const retryTimers = new Set<ReturnType<typeof setTimeout>>();
    let loadRaf = 0;
    let loadRetries = 0;
    const scrollingScrollers = new Set<Element>();
    let hoverBook: HTMLElement | null = null;
    let hoverShelf: HTMLElement | null = null;
    let lastPX = 0; let lastPY = 0;
    const wheelEngaged = new Set<Element>();
    const scrollTimers = new Map<Element, ReturnType<typeof setTimeout>>();
    const rafIds = new Map<HTMLElement, number>();
    const autoExposed = new Map<HTMLElement, HTMLElement>();

    // .bk-anim gates the WIDTH transition to books actually being revealed/closed — without it a
    // band mount's gap recompute ran ~550 simultaneous width transitions (7 fps on software raster).
    const animTimers = new Map<HTMLElement, ReturnType<typeof setTimeout>>();
    const armAnim = (el: HTMLElement) => { const t = animTimers.get(el); if (t !== undefined) { clearTimeout(t); animTimers.delete(el); } el.classList.add("bk-anim"); };
    const disarmAnim = (el: HTMLElement) => { const prev = animTimers.get(el); if (prev !== undefined) clearTimeout(prev); animTimers.set(el, setTimeout(() => { animTimers.delete(el); el.classList.remove("bk-anim"); }, 260)); };
    const hideBook = (el: HTMLElement) => {
      if (hoverBook === el) hoverBook = null;
      if (!el.style.getPropertyValue("--f") && !el.style.width && !el.style.zIndex) return;
      el.style.removeProperty("--f"); el.style.width = ""; el.style.zIndex = "";
      disarmAnim(el);
    };
    const firstBookIndex = (kids: HTMLCollection) => { let f = 0; while (f < kids.length && !(kids[f] as HTMLElement).classList.contains("bk")) f += 1; return f; };
    const lastBookIndex = (kids: HTMLCollection) => { let l = kids.length - 1; while (l >= 0 && !(kids[l] as HTMLElement).classList.contains("bk")) l -= 1; return l; };

    // O(log n) centre-book lookup by offsetLeft (runs every rAF while a shelf scrolls on touch).
    const findCenterBook = (sb: HTMLElement): HTMLElement | null => {
      const kids = sb.children;
      const first = firstBookIndex(kids); const last = lastBookIndex(kids);
      if (last < first) return null;
      const center = sb.scrollLeft + sb.clientWidth / 2;
      let lo = first; let hi = last;
      while (lo < hi) { const mid = (lo + hi + 1) >> 1; if ((kids[mid] as HTMLElement).offsetLeft <= center) lo = mid; else hi = mid - 1; }
      let best = kids[lo] as HTMLElement;
      if (lo < last) { const next = kids[lo + 1] as HTMLElement; if (Math.abs(next.offsetLeft + next.offsetWidth / 2 - center) < Math.abs(best.offsetLeft + best.offsetWidth / 2 - center)) best = next; }
      if (best.offsetLeft + best.offsetWidth < sb.scrollLeft || best.offsetLeft > sb.scrollLeft + sb.clientWidth) return null;
      return best;
    };
    const exposeCenterLight = (sb: HTMLElement) => {
      const next = findCenterBook(sb); const prev = autoExposed.get(sb);
      if (prev === next) return;
      if (prev) { prev.style.removeProperty("--f"); prev.style.zIndex = ""; }
      if (next) { next.style.setProperty("--f", "1"); next.style.zIndex = "500"; autoExposed.set(sb, next); } else autoExposed.delete(sb);
    };
    const exposeCenter = (sb: HTMLElement) => {
      const next = findCenterBook(sb); const prev = autoExposed.get(sb);
      if (prev && prev !== next) hideBook(prev);
      if (next) { armAnim(next); next.style.setProperty("--f", "1"); next.style.width = `${next.dataset.cw ?? "0"}px`; next.style.zIndex = "500"; autoExposed.set(sb, next); } else autoExposed.delete(sb);
    };
    const enterBook = (el: HTMLElement) => {
      if (vScrolling) return; // Chrome re-dispatches pointerover for content moving under a still cursor
      const sbEl = el.closest(".shelf-books") as HTMLElement | null;
      if (sbEl && scrollingScrollers.has(sbEl)) return;
      if (sbEl) { const prev = autoExposed.get(sbEl); if (prev && prev !== el) hideBook(prev); autoExposed.delete(sbEl); }
      if (hoverBook && hoverBook !== el) hideBook(hoverBook);
      armAnim(el);
      el.style.setProperty("--f", "1"); el.style.width = `${el.dataset.cw ?? "0"}px`; el.style.zIndex = "500";
      hoverBook = el;
    };
    const leaveBook = (el: HTMLElement) => {
      if (hoverBook === el) hoverBook = null;
      const sbEl = el.closest(".shelf-books") as HTMLElement | null;
      if (sbEl && autoExposed.get(sbEl) === el) return;
      hideBook(el);
      if (isTouchPrimary && sbEl) exposeCenter(sbEl);
    };

    // Delegated pointer handling: four listeners on the root, not four per book.
    const onRootOver = (e: PointerEvent) => {
      lastPX = e.clientX; lastPY = e.clientY;
      const sbEl = (e.target as HTMLElement)?.closest?.(".shelf-books") as HTMLElement | null;
      if (sbEl) hoverShelf = sbEl;
      const bk = (e.target as HTMLElement)?.closest?.(".bk") as HTMLElement | null;
      if (!bk) return;
      const from = e.relatedTarget as Node | null;
      if (from && bk.contains(from)) return;
      enterBook(bk);
    };
    const onRootOut = (e: PointerEvent) => {
      const sbEl = (e.target as HTMLElement)?.closest?.(".shelf-books") as HTMLElement | null;
      if (sbEl) { const out = e.relatedTarget as Node | null; if (!out || !sbEl.contains(out)) { if (hoverShelf === sbEl) hoverShelf = null; wheelEngaged.delete(sbEl); } }
      const bk = (e.target as HTMLElement)?.closest?.(".bk") as HTMLElement | null;
      if (!bk) return;
      const to = e.relatedTarget as Node | null;
      if (to && bk.contains(to)) return;
      leaveBook(bk);
    };
    const onRootMove = (e: PointerEvent) => { lastPX = e.clientX; lastPY = e.clientY; };
    const onRootCancel = (e: PointerEvent) => { const bk = (e.target as HTMLElement)?.closest?.(".bk") as HTMLElement | null; if (bk) { const sbEl = bk.closest(".shelf-books") as HTMLElement | null; if (!(sbEl && autoExposed.get(sbEl) === bk)) hideBook(bk); } };
    const onRootContextMenu = (e: Event) => { if ((e.target as HTMLElement)?.closest?.(".bk")) e.preventDefault(); };
    // Delegated cover-load failure (capture: `error` does not bubble): retry with backoff while the
    // hue shows; after RETRY_LIMIT quick failures the book goes DORMANT (timestamped), not dead
    // forever — the same law, and the same numbers, as `CardImage` on every other view.
    const onImgError = (e: Event) => {
      const img = e.target as HTMLImageElement;
      if (!(img instanceof HTMLImageElement) || !img.dataset.src || !img.closest(".bk")) return;
      const tries = parseInt(img.dataset.retry || "0", 10) + 1;
      img.dataset.retry = String(tries);
      img.removeAttribute("src");
      if (tries > RETRY_LIMIT) { img.dataset.dead = String(Date.now()); return; }
      const t = setTimeout(() => { retryTimers.delete(t); scheduleLoad(); }, tries * RETRY_STEP_MS);
      retryTimers.add(t);
    };
    root.addEventListener("error", onImgError, true);
    root.addEventListener("pointerover", onRootOver);
    root.addEventListener("pointerout", onRootOut);
    root.addEventListener("pointermove", onRootMove, { passive: true });
    root.addEventListener("pointercancel", onRootCancel);
    root.addEventListener("contextmenu", onRootContextMenu);

    const maybeLoadMore = (sb: HTMLElement) => {
      const key = sb.dataset.groupKey;
      if (!key || !onLoadMoreRef.current) return;
      const loaded = parseInt(sb.dataset.loaded || "", 10);
      const total = parseInt(sb.dataset.total || "0", 10);
      if (!Number.isFinite(loaded) || total <= loaded) return;
      const loadedRight = parseFloat(sb.dataset.loadedRight || "") || sb.scrollWidth;
      const needFill = loadedRight <= sb.clientWidth + 1;
      const nearEdge = sb.scrollLeft > 0 && sb.scrollLeft + sb.clientWidth * 2 >= loadedRight;
      if (needFill || nearEdge) onLoadMoreRef.current(key, loaded);
    };

    // Cover loading by direct geometry: books whose resting slot falls within the shelf's
    // horizontal viewport (+margin) get their src; O(log n + visible) per call.
    const loadShelfVisible = (sb: HTMLElement, margin = H_MARGIN): boolean => {
      if (sb.clientWidth === 0) return false;
      const kids = sb.children;
      const first = firstBookIndex(kids); const last = lastBookIndex(kids);
      if (last < first) return true;
      const left = sb.scrollLeft - margin; const right = sb.scrollLeft + sb.clientWidth + margin;
      let lo = first; let hi = last;
      while (lo < hi) { const mid = (lo + hi) >> 1; const el = kids[mid] as HTMLElement; if (el.offsetLeft + el.offsetWidth < left) lo = mid + 1; else hi = mid; }
      for (let i = lo; i <= last; i += 1) {
        const bk = kids[i] as HTMLElement;
        if (bk.offsetLeft > right) break;
        const img = bk.querySelector("img[data-src]") as HTMLImageElement | null;
        if (img && !img.getAttribute("src")) {
          if (img.dataset.dead) { if (Date.now() - +img.dataset.dead < DEAD_COOLDOWN_MS) continue; delete img.dataset.dead; img.dataset.retry = "0"; }
          (img as HTMLImageElement & { fetchPriority?: string }).fetchPriority = bk.offsetLeft + bk.offsetWidth >= sb.scrollLeft && bk.offsetLeft <= sb.scrollLeft + sb.clientWidth ? "auto" : "low";
          img.src = img.dataset.src!;
        }
      }
      return true;
    };
    const unloadShelfFar = (sb: HTMLElement) => {
      const left = sb.scrollLeft - H_UNLOAD; const right = sb.scrollLeft + sb.clientWidth + H_UNLOAD;
      const kids = sb.children; const first = firstBookIndex(kids); const last = lastBookIndex(kids);
      for (let i = first; i <= last; i += 1) {
        const bk = kids[i] as HTMLElement;
        if (bk.offsetLeft + bk.offsetWidth >= left && bk.offsetLeft <= right) continue;
        const img = bk.querySelector("img[src]") as HTMLImageElement | null;
        if (img && img.dataset.src) img.removeAttribute("src");
      }
    };
    const unloadBand = (band: HTMLElement) => { band.querySelectorAll<HTMLImageElement>("img[src]").forEach((img) => { if (img.dataset.src) img.removeAttribute("src"); }); };

    let loadRetryTimer: ReturnType<typeof setTimeout> | undefined;
    let disposed = false;
    const scheduleLoad = () => {
      if (loadRaf || disposed) return;
      loadRaf = requestAnimationFrame(() => {
        loadRaf = 0;
        if (disposed) return;
        const scRect = vpRect();
        const vm = vScrolling ? V_ACTIVE : V_MARGIN;
        const hm = vScrolling ? H_ACTIVE : H_MARGIN;
        const vTop = scRect.top - vm; const vBot = scRect.bottom + vm;
        let allRendered = true;
        nearBands.forEach((band) => {
          if (!band.isConnected) return;
          band.querySelectorAll<HTMLElement>(".shelf-books").forEach((sb) => {
            if (sb.clientWidth === 0) { allRendered = false; return; }
            const r = sb.getBoundingClientRect();
            if (r.bottom < vTop || r.top > vBot) return;
            loadShelfVisible(sb, hm);
          });
        });
        // a17: a band whose data has not mounted yet (clientWidth 0) re-runs the load pass shortly.
        // The timer is TRACKED so an unmount (a query change swapping the layout) cannot leave a
        // trailing pass touching disconnected nodes — the cover stall came from exactly that.
        if (!allRendered && loadRetries < 40) {
          loadRetries += 1;
          if (loadRetryTimer) clearTimeout(loadRetryTimer);
          loadRetryTimer = setTimeout(() => { loadRetryTimer = undefined; if (!disposed) scheduleLoad(); }, 64);
        } else loadRetries = 0;
      });
    };

    // Delegated shelf scroll (capture: scroll does not bubble).
    const onAnyScroll = (e: Event) => {
      const sb = e.target as HTMLElement;
      if (!sb || !sb.classList || !sb.classList.contains("shelf-books")) return;
      maybeLoadMore(sb);
      loadShelfVisible(sb, H_ACTIVE);
      scrollingScrollers.add(sb);
      if (isTouchPrimary) { const p = rafIds.get(sb); if (p !== undefined) cancelAnimationFrame(p); rafIds.set(sb, requestAnimationFrame(() => { rafIds.delete(sb); exposeCenterLight(sb); })); }
      const existing = scrollTimers.get(sb);
      if (existing !== undefined) clearTimeout(existing);
      scrollTimers.set(sb, setTimeout(() => {
        scrollingScrollers.delete(sb);
        if (wheelEngaged.has(sb)) {
          if (hoverShelf !== sb) wheelEngaged.delete(sb);
          else if (!isTouchPrimary) { const el = (document.elementFromPoint(lastPX, lastPY) as HTMLElement | null)?.closest?.(".bk") as HTMLElement | null; if (el && el.closest(".shelf-books") === sb) enterBook(el); }
        }
        loadShelfVisible(sb);
        unloadShelfFar(sb);
        if (isTouchPrimary) exposeCenter(sb);
      }, 150));
    };
    root.addEventListener("scroll", onAnyScroll, true);

    // Middle-button drag = autoscroll, owned here (the delegated host handler skips a prevented press):
    // vertical drives the page/scroller, horizontal the shelf under the press; axis lock.
    let asRaf = 0; let asEnd: (() => void) | null = null; let asJustEnded = 0;
    const onMidDown = (e: MouseEvent) => {
      if (e.button !== 1) return;
      e.preventDefault();
      if (asEnd || performance.now() - asJustEnded < 100) return;
      const sc: HTMLElement | null = scroller ?? (document.scrollingElement as HTMLElement | null);
      if (!sc) return;
      const tgt = e.target as HTMLElement | null;
      const sbCand = (tgt?.closest?.(".shelf-books") ?? tgt?.closest?.(".shelf")?.querySelector?.(".shelf-books")) as HTMLElement | null;
      const sbX = sbCand && sbCand.scrollWidth > sbCand.clientWidth + 1 ? sbCand : null;
      const originX = e.clientX; const originY = e.clientY;
      let curX = originX; let curY = originY; let moved = false; let sticky = false;
      let axis: "x" | "y" | null = sbX ? null : "y";
      const t0 = performance.now();
      const prevCursor = root.style.cursor;
      root.style.cursor = sbX ? "all-scroll" : "ns-resize";
      const step = () => {
        const dy = curY - originY; const dx = sbX ? curX - originX : 0;
        if (axis === null && (Math.abs(dy) > 8 || Math.abs(dx) > 8)) { axis = Math.abs(dx) > Math.abs(dy) ? "x" : "y"; root.style.cursor = axis === "x" ? "ew-resize" : "ns-resize"; }
        if (axis === "y") { const mag = Math.abs(dy); if (mag > 8) { moved = true; sc.scrollTop += Math.sign(dy) * Math.min(120, (mag - 8) * 0.25); } }
        else if (axis === "x" && sbX) { const mag = Math.abs(dx); if (mag > 8) { moved = true; sbX.scrollLeft += Math.sign(dx) * Math.min(120, (mag - 8) * 0.25); } }
        asRaf = requestAnimationFrame(step);
      };
      const onMove = (ev: MouseEvent) => { curX = ev.clientX; curY = ev.clientY; };
      const end = () => {
        cancelAnimationFrame(asRaf); asRaf = 0;
        window.removeEventListener("mousemove", onMove); window.removeEventListener("mouseup", onUp);
        window.removeEventListener("mousedown", endCapture, true); window.removeEventListener("wheel", endCapture, true); window.removeEventListener("keydown", endOnKey, true);
        root.style.cursor = prevCursor; asEnd = null; asJustEnded = performance.now();
      };
      const onUp = () => { if (!sticky && !moved && performance.now() - t0 < 300) { sticky = true; return; } end(); };
      const endCapture = () => end();
      const endOnKey = (ev: KeyboardEvent) => { if (ev.key === "Escape") end(); };
      window.addEventListener("mousemove", onMove); window.addEventListener("mouseup", onUp);
      window.addEventListener("mousedown", endCapture, true); window.addEventListener("wheel", endCapture, true); window.addEventListener("keydown", endOnKey, true);
      asEnd = end; asRaf = requestAnimationFrame(step);
    };
    root.addEventListener("mousedown", onMidDown);

    // Wheel over a hover-revealed book = horizontal shelf browse; never fights a page scroll.
    const onAnyWheel = (e: WheelEvent) => {
      const sb = (e.target as HTMLElement)?.closest?.(".shelf-books") as HTMLElement | null;
      if (!sb) return;
      if (Math.abs(e.deltaX) > Math.abs(e.deltaY)) return;
      if (vScrolling) return;
      const engaged = wheelEngaged.has(sb) || (hoverBook !== null && hoverBook.closest(".shelf-books") === sb);
      if (!engaged) return;
      if (sb.scrollWidth <= sb.clientWidth + 1) return;
      const step = e.deltaMode === 1 ? e.deltaY * 16 : e.deltaY;
      const atStart = sb.scrollLeft <= 0; const atEnd = sb.scrollLeft >= sb.scrollWidth - sb.clientWidth - 1;
      if ((step < 0 && atStart) || (step > 0 && atEnd)) { wheelEngaged.delete(sb); return; }
      e.preventDefault();
      wheelEngaged.add(sb);
      sb.scrollLeft += step;
    };
    root.addEventListener("wheel", onAnyWheel, { capture: true, passive: false });

    const onVScroll = () => {
      vScrolling = true;
      wheelEngaged.clear();
      if (vSettleTimer) clearTimeout(vSettleTimer);
      vSettleTimer = setTimeout(() => { vScrolling = false; scheduleLoad(); }, 160);
      scheduleLoad();
    };
    (scroller ?? window).addEventListener("scroll", onVScroll, { passive: true });

    const verticalIO = typeof IntersectionObserver !== "undefined" ? new IntersectionObserver((entries) => entries.forEach((entry) => {
      const band = entry.target as HTMLElement;
      const idx = parseInt(band.dataset.band || "-1", 10);
      if (entry.isIntersecting) { onNeedBandRef.current(idx); nearBands.add(band); scheduleLoad(); }
      else { nearBands.delete(band); if (!band.classList.contains("bx-band-placeholder")) unloadBand(band); onBandFarRef.current(idx); }
    }), { root: scroller, rootMargin: "1200px 0px 1200px 0px", threshold: 0 }) : null;

    const spyState = new Set<HTMLElement>();
    const spyIO = typeof IntersectionObserver !== "undefined" ? new IntersectionObserver((entries) => {
      entries.forEach((e) => { if (e.isIntersecting) spyState.add(e.target as HTMLElement); else spyState.delete(e.target as HTMLElement); });
      let best: HTMLElement | null = null; let bestTop = Infinity;
      spyState.forEach((sh) => { const t = sh.getBoundingClientRect().top; if (t < bestTop) { bestTop = t; best = sh; } });
      if (best) {
        const shelfEl = best as HTMLElement;
        const bandEl = shelfEl.closest(".bx-band") as HTMLElement | null;
        const band = bandEl ? parseInt(bandEl.dataset.band || "0", 10) : 0;
        const within = bandEl ? Array.from(bandEl.querySelectorAll(".shelf")).indexOf(shelfEl) : 0;
        onActiveRef.current(band, Math.max(0, within));
      }
    }, { root: scroller, rootMargin: "0px 0px -98% 0px", threshold: 0 }) : null;

    const bandRO = typeof ResizeObserver !== "undefined" ? new ResizeObserver((entries) => entries.forEach((e) => {
      const el = e.target as HTMLElement; const idx = parseInt(el.dataset.band || "-1", 10);
      if (idx >= 0) onBandHeightRef.current(idx, el.offsetHeight);
    })) : null;

    const reconcile = () => {
      root.querySelectorAll<HTMLElement>(".bx-band").forEach((band) => {
        if (!observedBands.has(band)) { verticalIO?.observe(band); observedBands.add(band); }
        if (!band.classList.contains("bx-band-placeholder") && !heightHooked.has(band)) { bandRO?.observe(band); heightHooked.add(band); }
      });
      root.querySelectorAll<HTMLElement>(".shelf").forEach((sh) => { if (observedShelf.has(sh)) return; spyIO?.observe(sh); observedShelf.add(sh); });
      const scRect = vpRect();
      root.querySelectorAll<HTMLElement>(".bx-band:not(.bx-band-placeholder)").forEach((band) => {
        const r = band.getBoundingClientRect();
        if (r.bottom >= scRect.top - V_MARGIN && r.top <= scRect.bottom + V_MARGIN) nearBands.add(band);
      });
      scheduleLoad();
      root.querySelectorAll<HTMLElement>(".shelf-books").forEach(maybeLoadMore);
    };
    wiringRef.current = { reconcile };
    reconcile();

    let resizeTimer: ReturnType<typeof setTimeout> | undefined;
    const onResize = () => { if (resizeTimer) clearTimeout(resizeTimer); resizeTimer = setTimeout(reconcile, 150); };
    window.addEventListener("resize", onResize);
    window.addEventListener("orientationchange", onResize);

    return () => {
      window.removeEventListener("resize", onResize); window.removeEventListener("orientationchange", onResize);
      if (resizeTimer) clearTimeout(resizeTimer);
      root.removeEventListener("error", onImgError, true);
      root.removeEventListener("pointerover", onRootOver); root.removeEventListener("pointerout", onRootOut);
      root.removeEventListener("pointermove", onRootMove); root.removeEventListener("pointercancel", onRootCancel);
      root.removeEventListener("contextmenu", onRootContextMenu);
      root.removeEventListener("scroll", onAnyScroll, true);
      root.removeEventListener("wheel", onAnyWheel, true);
      root.removeEventListener("mousedown", onMidDown);
      asEnd?.();
      (scroller ?? window).removeEventListener("scroll", onVScroll);
      scrollTimers.forEach((t) => clearTimeout(t)); retryTimers.forEach((t) => clearTimeout(t));
      if (vSettleTimer) clearTimeout(vSettleTimer);
      rafIds.forEach((id) => cancelAnimationFrame(id));
      disposed = true;
      if (loadRetryTimer) clearTimeout(loadRetryTimer);
      if (loadRaf) cancelAnimationFrame(loadRaf);
      verticalIO?.disconnect(); spyIO?.disconnect(); bandRO?.disconnect();
      autoExposed.forEach((bk) => hideBook(bk));
      animTimers.forEach((t) => clearTimeout(t));
      wiringRef.current = null;
    };
  }, [band0Sig]);

  useEffect(() => { wiringRef.current?.reconcile(); }, [slotSig]);
  useEffect(() => { wiringRef.current?.reconcile(); }, [shelfH]);

  const openGroup = useCallback((g: CardGroup) => onOpenGroup?.(g), [onOpenGroup]);

  return (
    <div className="bx-shelvesv" ref={rootRef} style={{ "--shelf-h": `${shelfH}px` } as CSSProperties}>
      {mergedSlots.map((slot) => (slot.groups ? (
        <div className="bx-band" data-band={slot.band} key={slot.band}>
          {slot.groups.map((g) => (
            <Shelf key={g.key} group={g} items={g.items} shelfH={shelfH} noun={noun} onOpen={onOpen} onOpenGroup={onOpenGroup ? openGroup : null} />
          ))}
        </div>
      ) : (
        <div className="bx-band bx-band-placeholder" data-band={slot.band} key={slot.band} style={{ height: `${slot.height ?? 600}px` }} aria-hidden="true" />
      )))}
    </div>
  );
}
