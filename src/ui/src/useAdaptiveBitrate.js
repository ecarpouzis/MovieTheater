import { useCallback, useRef, useState } from "react";
import { rungDown, rungUp, shouldStepUp, isBottomRung } from "./streamAbr";

// Adaptive-bitrate state machine, shared by the Watch player and the TV/channel player so the two
// can't drift (they used to carry byte-identical copies of this). Jellyfin hands out one rendition
// per session, so hls.js can't switch levels itself; instead the player measures throughput + stalls
// and the page restarts/re-tunes the session at a new bitrate cap. The bias is deliberately
// asymmetric: drop fast when the connection can't keep up, climb slowly and only with clear headroom
// (each switch costs a reload, so churn is the thing to avoid).
//
// The page supplies what an adapt *does* (`onAdapt` — Watch restarts at the live position; TV
// re-tunes the channel) and the opening cap (`initialBps` — Watch seeds from the connection estimate,
// TV opens optimistically at the lossless tier). Everything else — the math, the cooldown, the
// stall debounce — lives here, once.
const ABR_COOLDOWN_MS = 20_000;       // at most one switch per this window
const ABR_STABLE_FOR_UP_MS = 90_000;  // only climb after a sustained good streak

// Stall debounce: a single buffer stall is usually a transient bitrate spike the buffer rides out on
// its own — only a repeated pattern means the rung is genuinely too high. Require a few DISTINCT
// stall episodes within a window before dropping a rung (a downshift is a visible reload/re-tune, so
// one hiccup must not trigger it). hls.js re-fires the stalled error every few hundred ms while
// stalled, so collapse fires closer than the episode gap into one episode rather than counting them all.
const ABR_STALL_WINDOW_MS = 30_000;
const ABR_STALL_EPISODE_GAP_MS = 6_000;
const ABR_STALLS_TO_DOWNSHIFT = 2;

/**
 * @param qualityKeyRef ref whose `.current` is the active quality key; adaptation only runs on "auto".
 * @param initialBps    opening adaptive cap (read once at mount).
 * @param onAdapt       (nextBps) => void — apply the new cap (restart/re-tune at the live position).
 * @returns { autoBps, autoBpsRef, handleStall, handleBandwidth, adaptTo, reseed }
 */
export function useAdaptiveBitrate({ qualityKeyRef, initialBps, onAdapt }) {
  const [autoBps, setAutoBps] = useState(initialBps);
  const autoBpsRef = useRef(autoBps);
  autoBpsRef.current = autoBps;

  // onAdapt is read through a ref so adaptTo (and thus handleStall/handleBandwidth) stay stable
  // identities across renders — the page's restart/re-tune callback is defined after this hook runs
  // (it depends on autoBpsRef), so binding it late via the ref also breaks that ordering cycle.
  const onAdaptRef = useRef(onAdapt);
  onAdaptRef.current = onAdapt;

  const lastSwitchAtRef = useRef(0);
  const stableSinceRef = useRef(Date.now());
  const stallEpisodesRef = useRef([]); // timestamps of recent DISTINCT stall episodes
  const lastStallSeenRef = useRef(0);  // last stall fire, to collapse a burst of fires into one episode

  // Move the adaptive cap and apply it. Updates the ref first so the restart/re-tune picks up the new
  // cap synchronously, ahead of the state re-render.
  const adaptTo = useCallback((nextBps) => {
    if (nextBps === autoBpsRef.current) return;
    autoBpsRef.current = nextBps;
    setAutoBps(nextBps);
    lastSwitchAtRef.current = Date.now();
    stableSinceRef.current = Date.now();
    onAdaptRef.current(nextBps);
  }, []);

  // Stall (debounced): only drop a rung once a repeated pattern confirms the rung is too high.
  const handleStall = useCallback(() => {
    if (qualityKeyRef.current !== "auto" || isBottomRung(autoBpsRef.current)) return;
    const now = Date.now();
    if (now - lastStallSeenRef.current < ABR_STALL_EPISODE_GAP_MS) {
      lastStallSeenRef.current = now; // same ongoing stall — extend the episode, don't count it twice
      return;
    }
    lastStallSeenRef.current = now;
    const recent = stallEpisodesRef.current.filter((t) => now - t < ABR_STALL_WINDOW_MS);
    recent.push(now);
    stallEpisodesRef.current = recent;
    if (recent.length < ABR_STALLS_TO_DOWNSHIFT) return; // a lone transient stall — let the buffer recover
    if (now - lastSwitchAtRef.current < ABR_COOLDOWN_MS) return;
    stallEpisodesRef.current = []; // consumed — start the count fresh after a downshift
    adaptTo(rungDown(autoBpsRef.current));
  }, [qualityKeyRef, adaptTo]);

  // Throughput telemetry: climb a rung only after a sustained streak with clear headroom; any sample
  // short of that headroom resets the streak.
  const handleBandwidth = useCallback((estimateBps) => {
    if (qualityKeyRef.current !== "auto") return;
    if (!shouldStepUp(autoBpsRef.current, estimateBps)) {
      stableSinceRef.current = Date.now();
      return;
    }
    if (Date.now() - lastSwitchAtRef.current < ABR_COOLDOWN_MS) return;
    if (Date.now() - stableSinceRef.current >= ABR_STABLE_FOR_UP_MS) adaptTo(rungUp(autoBpsRef.current));
  }, [qualityKeyRef, adaptTo]);

  // Re-seed when re-entering Auto: reset the cap and arm a fresh cooldown + streak (so a manual
  // re-select doesn't immediately flip). The caller still triggers its own restart/re-tune afterward.
  const reseed = useCallback((seed) => {
    autoBpsRef.current = seed;
    setAutoBps(seed);
    lastSwitchAtRef.current = Date.now();
    stableSinceRef.current = Date.now();
    stallEpisodesRef.current = [];
  }, []);

  return { autoBps, autoBpsRef, handleStall, handleBandwidth, adaptTo, reseed };
}
