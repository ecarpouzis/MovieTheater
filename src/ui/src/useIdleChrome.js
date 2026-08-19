import { useCallback, useEffect, useRef, useState } from "react";

// The house-lights fade both players share: chrome shows on wake, and drops away after a few
// seconds of stillness — unless playback is paused or the player says to hold it up (a popout, a
// scrub in progress). The hold condition is a callback read at fire time (never stale, never
// re-arms the timer), because that's where the two players genuinely differ: Watch holds for
// pause/scrub/menu, TV holds for pause and its popouts.
//
// Returns { visible, wake, hide, cancel }: `wake` shows + re-arms; `hide` is the tap-to-hide
// affordance (cancels the pending fade and drops the chrome now); `cancel` just clears the timer.
export function useIdleChrome({ videoRef, holdWhile, delayMs = 3000 }) {
  const [visible, setVisible] = useState(true);
  const timerRef = useRef(null);
  const holdRef = useRef(holdWhile);
  holdRef.current = holdWhile;

  const wake = useCallback(() => {
    setVisible(true);
    clearTimeout(timerRef.current);
    timerRef.current = setTimeout(() => {
      const video = videoRef.current;
      // Keep the chrome up while paused (or with no element yet), and while the player holds it.
      if (!video || video.paused || holdRef.current?.()) return;
      setVisible(false);
    }, delayMs);
    // videoRef is a ref (stable identity); delayMs is a constant at every call site.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  const hide = useCallback(() => {
    clearTimeout(timerRef.current);
    setVisible(false);
  }, []);

  const cancel = useCallback(() => clearTimeout(timerRef.current), []);

  // Lights up on mount; whatever is pending is cleared on the way out.
  useEffect(() => {
    wake();
    return () => clearTimeout(timerRef.current);
  }, [wake]);

  return { visible, wake, hide, cancel };
}
