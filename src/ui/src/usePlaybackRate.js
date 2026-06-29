import { useCallback, useEffect, useState } from "react";

export const PLAYBACK_RATES = [0.5, 0.75, 1, 1.25, 1.5, 1.75, 2];
const STORAGE_KEY = "PlayerPlaybackRate";

// Persisted playback speed for the on-demand (Watch) player. NOT used by TV — a per-viewer rate would
// drift from the shared channel schedule clock. Speed is a pure client property (no transcode), but our
// player rebuilds the <video>/stream per source, which resets rate to 1.0, so this re-applies the chosen
// rate on mount and on every (re)load of the source.
export function usePlaybackRate(videoRef, src) {
  const [rate, setRateState] = useState(() => {
    const stored = parseFloat(window.localStorage.getItem(STORAGE_KEY));
    return PLAYBACK_RATES.includes(stored) ? stored : 1;
  });

  useEffect(() => {
    const v = videoRef.current;
    if (!v) return undefined;
    const apply = () => {
      v.playbackRate = rate;
    };
    apply(); // immediate (element already loaded on a quality/audio restart)
    v.addEventListener("loadedmetadata", apply); // and after a fresh source resets it to 1.0
    return () => v.removeEventListener("loadedmetadata", apply);
  }, [videoRef, src, rate]);

  const setRate = useCallback(
    (r) => {
      setRateState(r);
      window.localStorage.setItem(STORAGE_KEY, String(r));
      const v = videoRef.current;
      if (v) v.playbackRate = r;
    },
    [videoRef]
  );

  // Step through the rate ladder (keyboard < / >), clamped at the ends.
  const cycleRate = useCallback(
    (dir) => {
      const i = PLAYBACK_RATES.indexOf(rate);
      const from = i < 0 ? PLAYBACK_RATES.indexOf(1) : i;
      setRate(PLAYBACK_RATES[Math.min(Math.max(from + dir, 0), PLAYBACK_RATES.length - 1)]);
    },
    [rate, setRate]
  );

  return { rate, setRate, cycleRate };
}
