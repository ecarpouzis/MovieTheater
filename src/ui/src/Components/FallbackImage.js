import { useEffect, useState } from "react";

// An <img> that renders `fallback` (default: nothing) once its file fails to load, and heals when
// `src` later changes. This replaces the inline onError={mutate style.display/visibility} pattern:
// a DOM mutation survives React re-renders, so a later, valid src stayed invisible forever.
export default function FallbackImage({ fallback = null, src, onError, ...imgProps }) {
  const [failed, setFailed] = useState(false);
  useEffect(() => {
    setFailed(false);
  }, [src]);
  if (failed || !src) return fallback;
  // A caller's own onError still runs — it used to be swallowed by the spread, so a caller that
  // tracks "the image settled, stop the placeholder" never heard about the failing half.
  return <img src={src} {...imgProps} onError={(e) => { setFailed(true); onError?.(e); }} />;
}
