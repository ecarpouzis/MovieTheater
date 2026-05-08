import { useState, useRef, useCallback, useEffect } from "react";

function useLongPress(delay = 600) {
  const [open, setOpen] = useState(false);
  const holdTimer = useRef(null);
  const closeTimer = useRef(null);
  const suppressRef = useRef(false);
  const elementRef = useRef(null);

  useEffect(() => {
    const el = elementRef.current;
    if (!el) return;

    const onTouchStart = () => {
      suppressRef.current = false;
      clearTimeout(closeTimer.current);
      holdTimer.current = setTimeout(() => {
        suppressRef.current = true;
        setOpen(true);
        closeTimer.current = setTimeout(() => setOpen(false), 2500);
      }, delay);
    };

    const onTouchEnd = () => clearTimeout(holdTimer.current);

    const onTouchMove = () => {
      clearTimeout(holdTimer.current);
      suppressRef.current = false;
    };

    // contextmenu is the event the browser fires for long-press image menus.
    // Attaching it as a native non-passive listener lets preventDefault() actually
    // suppress the native sheet, which React synthetic events cannot do.
    const onContextMenu = (e) => e.preventDefault();

    el.addEventListener("touchstart", onTouchStart, { passive: true });
    el.addEventListener("touchend", onTouchEnd);
    el.addEventListener("touchmove", onTouchMove);
    el.addEventListener("contextmenu", onContextMenu);

    return () => {
      el.removeEventListener("touchstart", onTouchStart);
      el.removeEventListener("touchend", onTouchEnd);
      el.removeEventListener("touchmove", onTouchMove);
      el.removeEventListener("contextmenu", onContextMenu);
    };
  }, [delay]);

  useEffect(() => () => {
    clearTimeout(holdTimer.current);
    clearTimeout(closeTimer.current);
  }, []);

  const suppressClick = useCallback((handler) => (e) => {
    if (suppressRef.current) {
      suppressRef.current = false;
      return;
    }
    handler?.(e);
  }, []);

  return { open, ref: elementRef, suppressClick };
}

export default useLongPress;
