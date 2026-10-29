import { useEffect, useState } from "react";
import { getScrollParent } from "../utils/scroll";
import { readStored, writeStored } from "../utils/storage";

// Drag-to-scroll for a touchscreen that reaches the browser as a MOUSE.
//
// The Steam Deck in Game Mode hands Chrome its touchscreen as mouse events (2026-10-29: a swipe on
// the arcade lobby selected text, and the 8px pointer-only scrollbar was too thin for a finger). No
// CSS can turn a mouse drag into a scroll, so this does it by hand: a primary-button mouse drag past
// DEAD_ZONE_PX scrolls (vertical → the page's scroll root, horizontal → the nearest strip that
// scrolls sideways), release carries a flick, and the click that ends a drag is swallowed so a
// scroll never opens a card. A press that never leaves the dead zone is an ordinary click.
// Real touch and pen are left to the browser — it already scrolls those natively.

export const DRAG_SCROLL_KEY = "dragScroll"; // "on" | "off"; absent = auto-detect
const DEAD_ZONE_PX = 8;
const FRICTION_PER_MS = 0.0025; // flick decay: ~half the speed gone every ~280 ms
const MIN_FLICK = 0.05;          // px/ms below which a release is a stop, not a flick
const SAMPLE_MS = 80;            // velocity is read over the last ~80 ms of movement
const NO_DRAG = "input, textarea, select, [contenteditable], .ant-select, .ant-slider, .ant-input, [data-no-drag-scroll]";

/** A device with a touchscreen, however the browser classes its primary pointer. */
export function hasTouchscreen() {
  try {
    if (navigator.maxTouchPoints > 0) return true;
    return window.matchMedia?.("(any-pointer: coarse)").matches === true;
  } catch {
    return false;
  }
}

/** The stored choice wins; absent, a touchscreen turns it on. */
export function readDragScrollEnabled() {
  const stored = readStored(DRAG_SCROLL_KEY);
  if (stored === "on") return true;
  if (stored === "off") return false;
  return hasTouchscreen();
}

/** `[enabled, setEnabled]` — setting it is a per-device choice that outlives the auto-detect. */
export function useDragScrollPreference() {
  const [enabled, setEnabledState] = useState(readDragScrollEnabled);
  const setEnabled = (on) => {
    writeStored(DRAG_SCROLL_KEY, on ? "on" : "off");
    setEnabledState(on);
  };
  return [enabled, setEnabled];
}

function horizontalScroller(node, stop) {
  for (let el = node; el && el !== stop.parentElement; el = el.parentElement) {
    const ox = window.getComputedStyle(el).overflowX;
    if ((ox === "auto" || ox === "scroll") && el.scrollWidth > el.clientWidth) return el;
  }
  return null;
}

function scrollBy(target, dx, dy) {
  if (target) {
    target.scrollLeft += dx;
    target.scrollTop += dy;
  } else {
    window.scrollBy(dx, dy);
  }
}

/**
 * Wire drag-to-scroll onto `root` (an element — pass it from a callback ref, so a surface that mounts
 * after the first render is still wired) while `enabled`. Also marks `<html>` with
 * `drag-scroll-on`, which index.css reads to widen the pointer scrollbar to a finger-sized target.
 */
export default function useDragScroll(root, enabled) {
  useEffect(() => {
    if (!enabled || !root) return undefined;

    root.classList.add("drag-scroll-surface");
    document.documentElement.classList.add("drag-scroll-on");

    let press = null;   // { x, y, axis, target, samples }
    let flickRaf = 0;
    let swallowT = 0;

    const stopFlick = () => {
      if (flickRaf) cancelAnimationFrame(flickRaf);
      flickRaf = 0;
    };

    const swallowClick = (e) => {
      e.stopPropagation();
      e.preventDefault();
      disarmSwallow();
    };
    const disarmSwallow = () => {
      window.removeEventListener("click", swallowClick, true);
      if (swallowT) clearTimeout(swallowT);
      swallowT = 0;
    };

    const onMove = (e) => {
      if (!press) return;
      const dx = e.clientX - press.lastX;
      const dy = e.clientY - press.lastY;
      if (!press.axis) {
        const tx = e.clientX - press.x;
        const ty = e.clientY - press.y;
        if (Math.hypot(tx, ty) < DEAD_ZONE_PX) return;
        if (Math.abs(tx) > Math.abs(ty)) {
          const strip = horizontalScroller(press.node, root);
          if (!strip) { press = null; return endListeners(); } // nothing to slide — leave it be
          press.axis = "x";
          press.target = strip;
        } else {
          press.axis = "y";
          press.target = getScrollParent(root);
        }
        window.getSelection?.()?.removeAllRanges();
      }
      e.preventDefault();
      const mx = press.axis === "x" ? -dx : 0;
      const my = press.axis === "y" ? -dy : 0;
      scrollBy(press.target, mx, my);
      press.lastX = e.clientX;
      press.lastY = e.clientY;
      const now = performance.now();
      press.samples.push({ t: now, d: press.axis === "x" ? dx : dy });
      while (press.samples.length && now - press.samples[0].t > SAMPLE_MS) press.samples.shift();
    };

    const onUp = () => {
      const p = press;
      press = null;
      endListeners();
      if (!p?.axis) return; // a click — let it through
      // The click that ends a drag must not open the card under the pointer.
      window.addEventListener("click", swallowClick, true);
      swallowT = setTimeout(disarmSwallow, 0);

      const s = p.samples;
      const span = s.length > 1 ? s[s.length - 1].t - s[0].t : 0;
      let v = span > 0 ? -s.reduce((a, b) => a + b.d, 0) / span : 0; // px/ms in scroll direction
      if (Math.abs(v) < MIN_FLICK) return;
      let last = performance.now();
      const step = (now) => {
        const dt = Math.min(now - last, 50);
        last = now;
        scrollBy(p.target, p.axis === "x" ? v * dt : 0, p.axis === "y" ? v * dt : 0);
        v *= Math.max(0, 1 - FRICTION_PER_MS * dt);
        flickRaf = Math.abs(v) >= MIN_FLICK ? requestAnimationFrame(step) : 0;
      };
      flickRaf = requestAnimationFrame(step);
    };

    const endListeners = () => {
      window.removeEventListener("pointermove", onMove);
      window.removeEventListener("pointerup", onUp);
      window.removeEventListener("pointercancel", onUp);
    };

    const onDown = (e) => {
      stopFlick(); // a press catches a flick in flight, like a finger on a touch list
      if (e.pointerType !== "mouse" || e.button !== 0) return;
      if (e.target instanceof Element && e.target.closest(NO_DRAG)) return;
      disarmSwallow();
      press = { x: e.clientX, y: e.clientY, lastX: e.clientX, lastY: e.clientY, axis: null, target: null, node: e.target, samples: [] };
      window.addEventListener("pointermove", onMove, { passive: false });
      window.addEventListener("pointerup", onUp);
      window.addEventListener("pointercancel", onUp);
    };

    // A native drag (an <img> or <a> picked up) would steal the gesture with pointercancel.
    const onDragStart = (e) => e.preventDefault();

    root.addEventListener("pointerdown", onDown);
    root.addEventListener("dragstart", onDragStart);
    return () => {
      root.removeEventListener("pointerdown", onDown);
      root.removeEventListener("dragstart", onDragStart);
      endListeners();
      stopFlick();
      disarmSwallow();
      root.classList.remove("drag-scroll-surface");
      document.documentElement.classList.remove("drag-scroll-on");
    };
  }, [root, enabled]);
}
