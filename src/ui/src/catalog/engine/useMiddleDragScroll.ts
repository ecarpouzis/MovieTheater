import { useEffect } from "react";

/**
 * Middle-button drag = two-axis autoscroll, app-driven. Native autoscroll latches the nearest
 * scrollable element under the press: over an Extended strip that is the horizontal-only strip,
 * so dragging DOWN scrolls nothing (Firefox is strict about the latched axis). This delegated
 * handler latches BOTH targets at the press point — the nearest horizontally-overflowing element
 * and the nearest vertically-overflowing ancestor (or the page) — and drives them with the same
 * displacement-proportional curve: (|d| − 8) × 0.25 px per frame, capped at 120. The first axis
 * past the dead zone owns the gesture (hand drift during a strip browse must not creep the page).
 * A real drag ends on release; a quick click toggles sticky mode, ended by the next press / wheel /
 * Escape. A press some other engine has already `preventDefault`ed (the Shelves own theirs) is left alone.
 */
export default function useMiddleDragScroll(enabled = true) {
  useEffect(() => {
    if (!enabled) return undefined;
    let raf = 0;
    let endActive: (() => void) | null = null;
    let justEnded = 0;
    const scrollableX = (el: HTMLElement) => el.scrollWidth > el.clientWidth + 1 && /(auto|scroll)/.test(getComputedStyle(el).overflowX);
    const scrollableY = (el: HTMLElement) => el.scrollHeight > el.clientHeight + 1 && /(auto|scroll)/.test(getComputedStyle(el).overflowY);
    const onMidDown = (e: MouseEvent) => {
      if (e.button !== 1 || e.defaultPrevented) return;
      let hT: HTMLElement | null = null;
      let vT: HTMLElement | null = null;
      for (let el = e.target as HTMLElement | null; el && el !== document.body; el = el.parentElement) {
        if (!hT && scrollableX(el)) hT = el;
        if (scrollableY(el)) { vT = el; break; }
      }
      if (!vT) {
        const se = document.scrollingElement as HTMLElement | null;
        if (se && se.scrollHeight > se.clientHeight + 1) vT = se;
      }
      if (!hT && !vT) return;
      e.preventDefault();
      if (endActive || performance.now() - justEnded < 100) return;
      const originX = e.clientX;
      const originY = e.clientY;
      let curX = originX;
      let curY = originY;
      let moved = false;
      let sticky = false;
      let axis: "x" | "y" | null = hT && vT ? null : hT ? "x" : "y";
      const t0 = performance.now();
      const prevCursor = document.body.style.cursor;
      document.body.style.cursor = hT && vT ? "all-scroll" : hT ? "ew-resize" : "ns-resize";
      const step = () => {
        const dy = vT ? curY - originY : 0;
        const dx = hT ? curX - originX : 0;
        if (axis === null && (Math.abs(dy) > 8 || Math.abs(dx) > 8)) {
          axis = Math.abs(dx) > Math.abs(dy) ? "x" : "y";
          document.body.style.cursor = axis === "x" ? "ew-resize" : "ns-resize";
        }
        if (axis === "y" && vT) {
          const mag = Math.abs(dy);
          if (mag > 8) { moved = true; vT.scrollTop += Math.sign(dy) * Math.min(120, (mag - 8) * 0.25); }
        } else if (axis === "x" && hT) {
          const mag = Math.abs(dx);
          if (mag > 8) { moved = true; hT.scrollLeft += Math.sign(dx) * Math.min(120, (mag - 8) * 0.25); }
        }
        raf = requestAnimationFrame(step);
      };
      const onMove = (ev: MouseEvent) => { curX = ev.clientX; curY = ev.clientY; };
      const end = () => {
        cancelAnimationFrame(raf); raf = 0;
        window.removeEventListener("mousemove", onMove);
        window.removeEventListener("mouseup", onUp);
        window.removeEventListener("mousedown", endCapture, true);
        window.removeEventListener("wheel", endCapture, true);
        window.removeEventListener("keydown", endOnKey, true);
        document.body.style.cursor = prevCursor;
        endActive = null;
        justEnded = performance.now();
      };
      const onUp = () => { if (!sticky && !moved && performance.now() - t0 < 300) { sticky = true; return; } end(); };
      const endCapture = () => end();
      const endOnKey = (ev: KeyboardEvent) => { if (ev.key === "Escape") end(); };
      window.addEventListener("mousemove", onMove);
      window.addEventListener("mouseup", onUp);
      window.addEventListener("mousedown", endCapture, true);
      window.addEventListener("wheel", endCapture, true);
      window.addEventListener("keydown", endOnKey, true);
      endActive = end;
      raf = requestAnimationFrame(step);
    };
    document.addEventListener("mousedown", onMidDown);
    return () => { document.removeEventListener("mousedown", onMidDown); endActive?.(); };
  }, [enabled]);
}
