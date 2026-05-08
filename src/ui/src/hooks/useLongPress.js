import { useState, useRef, useCallback, useEffect } from "react";

function useLongPress(delay = 600) {
  const [open, setOpen] = useState(false);
  const holdTimer = useRef(null);
  const closeTimer = useRef(null);
  const suppressRef = useRef(false);

  useEffect(() => () => {
    clearTimeout(holdTimer.current);
    clearTimeout(closeTimer.current);
  }, []);

  const onTouchStart = useCallback(() => {
    suppressRef.current = false;
    clearTimeout(closeTimer.current);
    holdTimer.current = setTimeout(() => {
      suppressRef.current = true;
      setOpen(true);
      closeTimer.current = setTimeout(() => setOpen(false), 2500);
    }, delay);
  }, [delay]);

  const onTouchEnd = useCallback(() => {
    clearTimeout(holdTimer.current);
  }, []);

  const onTouchMove = useCallback(() => {
    clearTimeout(holdTimer.current);
    suppressRef.current = false;
  }, []);

  // Wraps a click handler: suppresses it if triggered by a long press
  const suppressClick = useCallback((handler) => (e) => {
    if (suppressRef.current) {
      suppressRef.current = false;
      return;
    }
    handler?.(e);
  }, []);

  return {
    open,
    handlers: { onTouchStart, onTouchEnd, onTouchMove, onContextMenu: (e) => e.preventDefault() },
    suppressClick,
  };
}

export default useLongPress;
