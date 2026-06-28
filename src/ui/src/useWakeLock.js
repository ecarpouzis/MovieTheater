import { useEffect, useRef } from "react";

// Hold a screen wake lock while mounted so the display doesn't sleep mid-playback — a two-hour film
// on the Watch player, or a channel left running on the TV player. The lock is auto-released when the
// tab is hidden, so we re-acquire when it returns to the foreground. Shared by both players.
export function useWakeLock() {
  const lockRef = useRef(null);
  useEffect(() => {
    let released = false;
    const acquire = async () => {
      try {
        if (!released) lockRef.current = await navigator.wakeLock?.request("screen");
      } catch {
        /* not supported / denied — fine */
      }
    };
    acquire();
    const onVisibility = () => {
      if (document.visibilityState === "visible") acquire();
    };
    document.addEventListener("visibilitychange", onVisibility);
    return () => {
      released = true;
      document.removeEventListener("visibilitychange", onVisibility);
      lockRef.current?.release?.().catch(() => {});
      lockRef.current = null;
    };
  }, []);
}
