import { useEffect, useRef } from "react";

/**
 * The site's poll loop, with the two behaviors every hand-rolled setInterval kept missing:
 * an immediate first call, and visibility awareness — browsers throttle hidden tabs' timers
 * anyway, and of the app's eleven data polls only two ever stopped when the tab was hidden
 * (survey 2026-08-19); the rest kept hitting the server from backgrounded tabs.
 *
 * pauseWhenHidden (default true): ticks are skipped while document.hidden, and returning to the
 * tab fires ONE immediate beat (the arcade room heartbeat's pattern) so the surface catches up
 * without waiting out the interval. Set it false for polls that ARE presence — the watch-party
 * roster: pausing those tells the server the user left.
 *
 * Deliberately NOT for: the players' 10 s progress beats (pausing them kills the ffmpeg session —
 * documented in movie-streaming), the 16 ms input pump, or the music engine's watchdogs (they
 * reason about document.hidden themselves).
 */
export default function usePolling(fn, intervalMs, { enabled = true, pauseWhenHidden = true } = {}) {
  const fnRef = useRef(fn);
  fnRef.current = fn;

  useEffect(() => {
    if (!enabled) return undefined;
    const beat = () => {
      if (pauseWhenHidden && document.hidden) return;
      fnRef.current();
    };
    beat();
    const id = setInterval(beat, intervalMs);
    const onVisible = () => {
      if (!document.hidden) beat();
    };
    if (pauseWhenHidden) document.addEventListener("visibilitychange", onVisible);
    return () => {
      clearInterval(id);
      if (pauseWhenHidden) document.removeEventListener("visibilitychange", onVisible);
    };
  }, [enabled, intervalMs, pauseWhenHidden]);
}
