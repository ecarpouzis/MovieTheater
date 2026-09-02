import { useEffect, useRef, useState } from "react";
import { DEAD_COOLDOWN_MS, RETRY_LIMIT, RETRY_STEP_MS } from "../catalog/cards/CardImage";

// An <img> that renders `fallback` (default: nothing) once its file fails to load, and heals when
// `src` later changes. This replaces the inline onError={mutate style.display/visibility} pattern:
// a DOM mutation survives React re-renders, so a later, valid src stayed invisible forever.
//
// `retry`: the catalog's image-failure law (docs/catalog.md → Laws) for a card that lives in a
// streamed list — a transient failure (a server restart, a Wi-Fi blip under a fling's burst) is
// RETRIED with backoff before the fallback shows, and the fallback is DORMANT, not final: one fresh
// round after a cooldown. Same numbers as `CardImage`. Off by default: a lone image in a modal has
// no burst to survive, and a known-missing file should not be re-asked for every 15 s.
export default function FallbackImage({ fallback = null, src, onError, retry = false, ...imgProps }) {
  const [failed, setFailed] = useState(false);
  const [attempt, setAttempt] = useState(0);
  const timerRef = useRef(undefined);
  useEffect(() => {
    setFailed(false);
    setAttempt(0);
    return () => { if (timerRef.current) clearTimeout(timerRef.current); };
  }, [src]);
  if (failed || !src) return fallback;
  // A caller's own onError still runs — it used to be swallowed by the spread, so a caller that
  // tracks "the image settled, stop the placeholder" never heard about the failing half.
  const handleError = (e) => {
    onError?.(e);
    if (!retry) { setFailed(true); return; }
    if (timerRef.current) clearTimeout(timerRef.current);
    if (attempt >= RETRY_LIMIT) {
      setFailed(true);
      timerRef.current = setTimeout(() => { setAttempt(0); setFailed(false); }, DEAD_COOLDOWN_MS);
      return;
    }
    timerRef.current = setTimeout(() => setAttempt((a) => a + 1), (attempt + 1) * RETRY_STEP_MS);
  };
  // The retry is React state: a new key remounts the <img>, which is what makes the browser ask again.
  return <img key={attempt} src={src} {...imgProps} onError={handleError} />;
}
