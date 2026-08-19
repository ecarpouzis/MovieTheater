import { useCallback, useEffect, useRef } from "react";

// A press held this long, without wandering more than a thumb's wobble, is a long press rather than
// a tap. 420ms is the platform long-press everywhere else on a phone; the slop is what separates a
// held finger from the start of a scroll, and getting it wrong makes a grid feel like it grabs
// things while you are trying to move past them.
const LONG_PRESS_MS = 420;
const LONG_PRESS_SLOP = 10;

/**
 * The press-and-hold gesture, as handlers to spread onto an element.
 *
 * Pointer events rather than touch events, so one implementation covers a thumb and a held mouse
 * button. Nothing is preventDefault-ed on the way down — an element must never swallow a scroll that
 * merely started on top of it — so the gesture is cancelled by MOVEMENT instead.
 *
 * `consumeClick` exists because a long press is followed by a click the browser still delivers, and
 * that click would immediately undo whatever the hold just did. Call it FIRST in the click handler;
 * it returns true (and re-arms) when the click belongs to a hold that already fired.
 *
 * A caller that wants the gesture on touch only spreads `handlers` conditionally — with nothing
 * attached, nothing fires and consumeClick always returns false.
 */
export default function useLongPress(onLongPress) {
  const timerRef = useRef(0);
  const originRef = useRef(null);
  const firedRef = useRef(false);
  const handlerRef = useRef(onLongPress);
  handlerRef.current = onLongPress;

  const clear = useCallback(() => {
    if (timerRef.current) clearTimeout(timerRef.current);
    timerRef.current = 0;
    originRef.current = null;
  }, []);

  useEffect(() => clear, [clear]);

  const handlers = {
    onPointerDown: (event) => {
      if (event.button != null && event.button !== 0) return;
      firedRef.current = false;
      originRef.current = { x: event.clientX ?? 0, y: event.clientY ?? 0 };
      clear();
      timerRef.current = setTimeout(() => {
        timerRef.current = 0;
        firedRef.current = true;
        handlerRef.current?.();
      }, LONG_PRESS_MS);
    },
    onPointerMove: (event) => {
      const origin = originRef.current;
      if (!origin || !timerRef.current) return;
      const dx = Math.abs((event.clientX ?? 0) - origin.x);
      const dy = Math.abs((event.clientY ?? 0) - origin.y);
      if (dx > LONG_PRESS_SLOP || dy > LONG_PRESS_SLOP) clear();
    },
    onPointerUp: clear,
    onPointerCancel: clear,
    onPointerLeave: clear,
    // The platform's own long-press menu ("save image") would otherwise land on top of whatever the
    // hold just did.
    onContextMenu: (event) => {
      if (firedRef.current) event.preventDefault();
    },
  };

  return {
    handlers,
    consumeClick: () => {
      const fired = firedRef.current;
      firedRef.current = false;
      return fired;
    },
  };
}
