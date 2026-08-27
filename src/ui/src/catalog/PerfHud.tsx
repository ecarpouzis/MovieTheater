import { useEffect, useRef, useState, type CSSProperties } from "react";
import { readStored } from "../utils/storage";

/**
 * The catalog's perf HUD — the Long Box `lb_perf_hud` port (R9 S9).
 *
 * The CDP profiler and the smoke scripts measure the page from OUTSIDE; this is the instrument for
 * a real browser on real hardware, where the GPU slice headless cannot reproduce lives. It reports
 * the ENGINE's facts, read from the DOM the engine already writes, so it never becomes a second
 * source of truth the engine has to keep updated:
 *
 *   fps + the worst frame seen DURING a scroll burst · mounted bands ([data-iband]) and the cards
 *   in them · band placeholders · in-flight fetches · long tasks (PerformanceObserver "longtask")
 *   · JS heap where the browser offers one (Chromium only — "heap n/a" is how you know you are in
 *   Firefox) · covers still loading and covers gone dormant (`data-fallback`, CardImage's cooldown).
 *
 * ZERO COST WHEN OFF, and that is a hard requirement, not an aspiration: with the flag unset the
 * component installs no listener, starts no rAF, patches no fetch and renders nothing at all. The
 * flag is read through `utils/storage` (a bare `localStorage` read is banned — it throws in private
 * mode) exactly once per page load, so toggling it needs a reload, which is what you want anyway:
 * a HUD that could appear mid-session would be measuring a page it changed.
 *
 * Enable:  localStorage.setItem("catalog.perfhud.v1", "1")  then reload.
 * Disable: localStorage.removeItem("catalog.perfhud.v1")    then reload.
 */
export const PERF_HUD_KEY = "catalog.perfhud.v1";

let cached: boolean | undefined;

/** Whether the HUD is armed for this page load (read once; a reload re-reads). */
export function perfHudEnabled(): boolean {
  if (cached === undefined) cached = readStored(PERF_HUD_KEY) === "1";
  return cached;
}

/** Tests only: forget the cached flag so the next read sees the current storage. */
export function resetPerfHudFlag(): void {
  cached = undefined;
}

export interface PerfFacts {
  fps: number;
  /** The worst rAF interval seen inside the last scroll burst (0 = no scroll yet). */
  scrollFrameMs: number;
  bands: number;
  placeholders: number;
  cards: number;
  fetches: number;
  longTasks: number;
  longTaskMaxMs: number;
  /** MB, or null where the browser exposes no heap reading (every engine but Chromium). */
  heapMb: number | null;
  imgsPending: number;
  imgsDead: number;
}

const SAMPLE_MS = 250;
const SCROLL_WINDOW_MS = 200;

/** The DOM half of the reading — the engine's own markers, counted where they stand. */
export function readDomFacts(root: ParentNode = document): Pick<PerfFacts, "bands" | "placeholders" | "cards" | "imgsPending" | "imgsDead"> {
  const imgs = Array.from(root.querySelectorAll<HTMLImageElement>(".bx-results img"));
  return {
    bands: root.querySelectorAll("[data-iband]").length,
    placeholders: root.querySelectorAll(".bx-band-placeholder").length,
    cards: root.querySelectorAll(".bx-results .bx-card").length,
    imgsPending: imgs.filter((i) => !i.dataset.fallback && !i.complete).length,
    imgsDead: imgs.filter((i) => i.dataset.fallback === "1").length,
  };
}

const BOX: CSSProperties = {
  position: "fixed",
  right: 8,
  bottom: 8,
  zIndex: 2000,
  padding: "6px 8px",
  borderRadius: 6,
  background: "rgba(12,12,16,0.86)",
  color: "#c9f5c9",
  font: "500 10.5px/1.45 ui-monospace, SFMono-Regular, Menlo, monospace",
  letterSpacing: "0.04em",
  textTransform: "uppercase",
  pointerEvents: "none",
  whiteSpace: "pre",
};

const ZERO: PerfFacts = {
  fps: 0, scrollFrameMs: 0, bands: 0, placeholders: 0, cards: 0, fetches: 0,
  longTasks: 0, longTaskMaxMs: 0, heapMb: null, imgsPending: 0, imgsDead: 0,
};

export function formatFacts(f: PerfFacts): string {
  return [
    `fps ${f.fps}  scrollframe ${f.scrollFrameMs}ms`,
    `bands ${f.bands}+${f.placeholders}ph  cards ${f.cards}`,
    `fetch ${f.fetches}  longtask ${f.longTasks}${f.longTaskMaxMs ? ` (max ${f.longTaskMaxMs}ms)` : ""}`,
    `covers ${f.imgsPending} loading  ${f.imgsDead} dead`,
    f.heapMb == null ? "heap n/a" : `heap ${f.heapMb} MB`,
  ].join("\n");
}

export default function PerfHud() {
  const on = perfHudEnabled();
  const [facts, setFacts] = useState<PerfFacts>(ZERO);
  const stateRef = useRef({ longTasks: 0, longTaskMaxMs: 0, fetches: 0 });

  useEffect(() => {
    if (!on) return undefined;
    const st = stateRef.current;
    let raf = 0;
    let last = performance.now();
    let lastSample = last;
    let frames = 0;
    let lastScrollAt = -Infinity;
    let burstWorst = 0;

    // A burst's worst frame, not the session's: a new burst (nothing scrolled for SCROLL_WINDOW_MS)
    // starts the reading over, so the number always describes the scroll you just did.
    const onScroll = () => {
      const now = performance.now();
      if (now - lastScrollAt > SCROLL_WINDOW_MS) burstWorst = 0;
      lastScrollAt = now;
    };
    window.addEventListener("scroll", onScroll, { passive: true, capture: true });

    let obs: PerformanceObserver | undefined;
    try {
      obs = new PerformanceObserver((list) => {
        for (const e of list.getEntries()) {
          st.longTasks += 1;
          st.longTaskMaxMs = Math.max(st.longTaskMaxMs, Math.round(e.duration));
        }
      });
      obs.observe({ entryTypes: ["longtask"] });
    } catch {
      obs = undefined; // Firefox has no Long Tasks API — frame deltas are the stall signal there.
    }

    // In-flight fetch count. Patched only while the HUD lives, and restored on unmount UNLESS
    // something else patched over us in the meantime (then ours stays in the chain, harmless).
    const originalFetch = window.fetch;
    const patched: typeof window.fetch = (...args) => {
      st.fetches += 1;
      return originalFetch.apply(window, args).finally(() => { st.fetches = Math.max(0, st.fetches - 1); });
    };
    window.fetch = patched;

    const tick = () => {
      raf = requestAnimationFrame(tick);
      const now = performance.now();
      const dt = now - last;
      last = now;
      frames += 1;
      if (now - lastScrollAt < SCROLL_WINDOW_MS) burstWorst = Math.max(burstWorst, dt);
      if (now - lastSample < SAMPLE_MS) return;
      const fps = Math.round((frames * 1000) / (now - lastSample));
      frames = 0;
      lastSample = now;
      const mem = (performance as unknown as { memory?: { usedJSHeapSize: number } }).memory;
      setFacts({
        fps,
        scrollFrameMs: Math.round(burstWorst),
        fetches: st.fetches,
        longTasks: st.longTasks,
        longTaskMaxMs: st.longTaskMaxMs,
        heapMb: mem ? Math.round(mem.usedJSHeapSize / 1048576) : null,
        ...readDomFacts(),
      });
    };
    raf = requestAnimationFrame(tick);

    return () => {
      cancelAnimationFrame(raf);
      window.removeEventListener("scroll", onScroll, { capture: true } as EventListenerOptions);
      obs?.disconnect();
      if (window.fetch === patched) window.fetch = originalFetch;
    };
  }, [on]);

  if (!on) return null;
  return <div className="bx-perfhud" data-testid="catalog-perfhud" style={BOX}>{formatFacts(facts)}</div>;
}
