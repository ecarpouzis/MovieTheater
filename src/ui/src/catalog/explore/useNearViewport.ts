/**
 * "Has this element come near the viewport yet?" — one observer per element, disconnected the moment
 * it fires, so nothing survives the reveal (the engine's law: no per-item listeners left running).
 * Explore uses it to keep the rails below the fold unmounted until they are approached, and to gate
 * the tail QUERIES so a landing that is never scrolled never asks for them.
 *
 * `IntersectionObserver` is absent in the test DOM and in very old browsers; there the element is
 * treated as revealed immediately, which is the safe answer (content, just not deferred).
 */
import { useEffect, useRef, useState } from "react";

export function useNearViewport<T extends HTMLElement>(rootMargin = "600px"): [React.RefObject<T>, boolean] {
  const ref = useRef<T>(null);
  const [near, setNear] = useState(false);
  useEffect(() => {
    if (near) return;
    const el = ref.current;
    if (!el) return;
    if (typeof IntersectionObserver === "undefined") { setNear(true); return; }
    const io = new IntersectionObserver((entries) => {
      if (entries.some((e) => e.isIntersecting)) { setNear(true); io.disconnect(); }
    }, { rootMargin });
    io.observe(el);
    return () => io.disconnect();
  }, [near, rootMargin]);
  return [ref, near];
}

/**
 * The page-level twin: false until the reader has actually moved, then true for the rest of the
 * visit. Sections hang their TAIL rails' `enabled` on it so an Explore that is opened and left alone
 * costs its first screen and nothing more. An idle fallback flips it anyway after `idleMs` so a very
 * tall window (or a reader who never scrolls) still fills in — off the critical path, never on it.
 */
export function useExploreDepth(idleMs = 2500): boolean {
  const [deep, setDeep] = useState(false);
  useEffect(() => {
    if (deep) return;
    const go = () => setDeep(true);
    window.addEventListener("scroll", go, { passive: true, capture: true });
    window.addEventListener("wheel", go, { passive: true });
    const t = window.setTimeout(go, idleMs);
    return () => {
      window.removeEventListener("scroll", go, true);
      window.removeEventListener("wheel", go);
      window.clearTimeout(t);
    };
  }, [deep, idleMs]);
  return deep;
}
