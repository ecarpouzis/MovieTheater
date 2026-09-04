import { useCallback, useRef, useState } from "react";
import { rungDown, isBottomRung, climbTarget, climbHoldBar, isAutoQuality } from "./streamAbr";
import { noteStreamSwitch, noteBandwidthEstimate, reportAbrDowngrade } from "./videoIncidents";

// Adaptive-bitrate state machine, shared by the Watch player and the TV/channel player so the two
// can't drift (they used to carry byte-identical copies of this). Jellyfin hands out one rendition
// per session, so hls.js can't switch levels itself; instead the player measures throughput + stalls
// and the page restarts/re-tunes the session at a new bitrate cap. The bias is deliberately
// asymmetric: drop fast when the connection can't keep up, climb slowly and only with clear headroom
// (each switch costs a reload, so churn is the thing to avoid).
//
// The page supplies what an adapt *does* (`onAdapt` — Watch restarts at the live position; TV
// re-tunes the channel) and a `profile` (ABR_PROFILES.desktop / .phone) that sets the opening cap,
// the ceiling, the climb style (jump vs step) and how long a good streak must hold before climbing.
// Everything else — the math, the cooldown, the stall debounce — lives here, once.
const ABR_COOLDOWN_MS = 20_000;       // at most one switch per this window

// Stall debounce: a single buffer stall is usually a transient bitrate spike the buffer rides out on
// its own — only a repeated pattern means the rung is genuinely too high. Require a few DISTINCT
// stall episodes within a window before dropping a rung (a downshift is a visible reload/re-tune, so
// one hiccup must not trigger it). hls.js re-fires the stalled error every few hundred ms while
// stalled, so collapse fires closer than the episode gap into one episode rather than counting them all.
const ABR_STALL_WINDOW_MS = 30_000;
const ABR_STALL_EPISODE_GAP_MS = 6_000;
const ABR_STALLS_TO_DOWNSHIFT = 2;

// Post-switch grace: every switch restarts the session, and the restart's own rebuffer fires the same
// stall event as a genuine underrun. Counting it made a downshift self-perpetuating — the restart's
// stall became episode #1 of the NEXT drop, so one real stall after a switch completed the pair and
// the ladder cascaded down (observed 2026-08-16: two drops 30s apart on one dip). A stall this soon
// after a switch is the switch, not the link.
const ABR_POST_SWITCH_GRACE_MS = 10_000;
// A copied stream keeps its rung when the fresh estimate clears the source's bitrate by this much —
// the same headroom the climb demands, so "safe to climb into" and "safe to stay on" agree.
const ABR_COPY_STALL_HEADROOM = 1.5;

// A throughput estimate older than this says nothing about the link that is stalling RIGHT NOW —
// fall back to the blind one-rung walk rather than trust it.
const ABR_ESTIMATE_FRESH_MS = 15_000;

// Demotion memory: each stall-driven drop doubles how long the link must stay provably clean before
// a climb (capped), and reseed() forgives it. A link that keeps knocking the session off a rung has
// told us its "clean streaks" don't last — re-climbing on the same evidence just schedules the next
// stall (the climb-back is itself a visible restart, so being wrong twice costs four rebuffers).
const ABR_DEMOTION_MULT_MAX = 8;

/**
 * @param qualityKeyRef ref whose `.current` is the active quality key; adaptation only runs on "auto".
 * @param profile       device ABR profile (ABR_PROFILES.desktop / .phone): openBps, ceilingBps,
 *                      climbMode, stableForUpMs. openBps is read once at mount as the opening cap.
 * @param onAdapt       (nextBps) => void — apply the new cap (restart/re-tune at the live position).
 * @param videoCopiedRef  optional ref; truthy while the server is COPYING the video (`isDirectStream`).
 *                      It freezes the CLIMB only — a copy is already lossless, so every rung above it
 *                      delivers the same bytes and climbing is a reload for nothing. The DROP stays
 *                      live: a copy that keeps stalling is a stream the link cannot carry, and falling
 *                      back to a transcode is the whole point of "auto" ("start best, fall back"). A
 *                      viewer who never wants that picks a fixed rung. Omitted → never copied.
 * @param sourceVideoBpsRef optional ref holding the source video's own bitrate, so a drop skips the
 *                      rungs whose cap sits above it (they re-deliver the same copy — see rungDown)
 *                      and a climb into the lossless tier is gated on what that tier really costs
 *                      (the file's bitrate, not a fixed bar — see climbTarget).
 * @returns { autoBps, autoBpsRef, handleStall, handleBandwidth, adaptTo, reseed }
 */
export function useAdaptiveBitrate({ qualityKeyRef, profile, onAdapt, videoCopiedRef, sourceVideoBpsRef }) {
  const [autoBps, setAutoBps] = useState(profile.openBps);
  const autoBpsRef = useRef(autoBps);
  autoBpsRef.current = autoBps;

  // profile is read through a ref so the climb math always sees the live profile (e.g. if device
  // class ever changes) without re-binding handleBandwidth's identity.
  const profileRef = useRef(profile);
  profileRef.current = profile;

  // onAdapt is read through a ref so adaptTo (and thus handleStall/handleBandwidth) stay stable
  // identities across renders — the page's restart/re-tune callback is defined after this hook runs
  // (it depends on autoBpsRef), so binding it late via the ref also breaks that ordering cycle.
  const onAdaptRef = useRef(onAdapt);
  onAdaptRef.current = onAdapt;

  const lastSwitchAtRef = useRef(0);
  const stableSinceRef = useRef(Date.now());
  const stallEpisodesRef = useRef([]); // timestamps of recent DISTINCT stall episodes
  const lastStallSeenRef = useRef(0);  // last stall fire, to collapse a burst of fires into one episode
  const lastEstimateRef = useRef(null); // { bps, at } — freshest throughput sample, for the informed drop
  const demotionMultRef = useRef(1);   // stableForUpMs multiplier; doubles per stall-driven drop

  // Move the adaptive cap and apply it. Updates the ref first so the restart/re-tune picks up the new
  // cap synchronously, ahead of the state re-render.
  const adaptTo = useCallback((nextBps) => {
    if (nextBps === autoBpsRef.current) return;
    autoBpsRef.current = nextBps;
    setAutoBps(nextBps);
    lastSwitchAtRef.current = Date.now();
    stableSinceRef.current = Date.now();
    // Tell the incident recorder a session restart is beginning. This is the load-bearing half of
    // "an ABR restart is not a stall": what follows is several seconds of frozen picture by design
    // (a new ffmpeg, a fresh manifest), and without this mark the element's `waiting` would be
    // filed as a playback failure every single time the ladder moved.
    noteStreamSwitch("abr");
    onAdaptRef.current(nextBps);
  }, []);

  // Stall (debounced): only drop a rung once a repeated pattern confirms the rung is too high. Runs
  // on a copied stream too — that is the case where the fall-back matters most, since the copy carries
  // the file's full bitrate and nothing else will ever back it off (the channel player has always
  // dropped off copies this way). The drop is throughput-INFORMED when a fresh estimate exists: it
  // lands on the highest rung the measured link actually clears instead of blindly stepping onto one
  // that may still sit above it (see rungDown).
  const handleStall = useCallback(() => {
    if (!isAutoQuality(qualityKeyRef.current) || isBottomRung(autoBpsRef.current)) return;
    const now = Date.now();
    if (now - lastSwitchAtRef.current < ABR_POST_SWITCH_GRACE_MS) return; // the switch's own rebuffer
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
    const est = lastEstimateRef.current;
    const freshBps = est && now - est.at <= ABR_ESTIMATE_FRESH_MS ? est.bps : undefined;
    // A COPIED stream stalling on a link that measurably carries the source with headroom is not a
    // bandwidth problem, and the only rungs below the source are re-encodes — dropping onto one
    // trades a server-side hiccup (a spawn, a cold SMB open, a seek storm) for a permanently worse
    // picture that the same server now has to encode. Ballerina, 2026-09-03: 24.8 Mbps 4K HEVC,
    // estimate 1.36 Gbps, dropped to a 20 Mbps encode after a stall that had nothing to do with the
    // wire. Without a fresh estimate the drop still runs (a thin remote link measures low or not
    // at all), and a non-copied stream is unaffected.
    const sourceBps = sourceVideoBpsRef?.current;
    if (videoCopiedRef?.current && freshBps && sourceBps && freshBps >= sourceBps * ABR_COPY_STALL_HEADROOM) {
      stallEpisodesRef.current = []; // the episode is explained — don't let it accumulate into a drop
      return;
    }
    stallEpisodesRef.current = []; // consumed — start the count fresh after a downshift
    demotionMultRef.current = Math.min(demotionMultRef.current * 2, ABR_DEMOTION_MULT_MAX);
    const from = autoBpsRef.current;
    const to = rungDown(from, sourceVideoBpsRef?.current, freshBps);
    // The emergency downgrade: the viewer's picture is being taken away because the stream kept
    // stalling. Reported BEFORE the adapt so the incident's ring still ends with the stalls that
    // caused it rather than with the restart that answered them. (A climb is not reported — see
    // videoIncidents.reportAbrDowngrade.)
    reportAbrDowngrade({ fromBps: from, toBps: to, estimateBps: freshBps });
    adaptTo(to);
  }, [qualityKeyRef, adaptTo, sourceVideoBpsRef]);

  // Throughput telemetry: climb only after a sustained streak with clear headroom. The streak resets
  // on WEAK evidence (a sample below the next rung's hold bar — see climbHoldBar), not on every sample
  // short of the full climb bar: estimate jitter around that bar used to restart the clock endlessly
  // and starve the climb for half an hour at a time. The target (a jump straight to the best supported
  // rung, or one rung up) and the base streak length come from the device profile; the streak length
  // stretches by the demotion multiplier — a link that already knocked us down must stay clean longer.
  const handleBandwidth = useCallback((estimateBps) => {
    if (estimateBps && isFinite(estimateBps)) {
      lastEstimateRef.current = { bps: estimateBps, at: Date.now() }; // feed the informed drop, always
      // ...and the incident payload: "was the link actually gone?" is the first question anyone asks
      // of a stall row, and this is the only place the answer is measured.
      noteBandwidthEstimate(estimateBps);
    }
    if (videoCopiedRef?.current) return; // video is being copied — already lossless, nothing to climb to
    if (!isAutoQuality(qualityKeyRef.current)) return;
    const sourceBps = sourceVideoBpsRef?.current;
    const target = climbTarget(autoBpsRef.current, estimateBps, profileRef.current, sourceBps);
    const now = Date.now();
    if (target === autoBpsRef.current) {
      const holdBar = climbHoldBar(autoBpsRef.current, profileRef.current, sourceBps);
      const weak = !estimateBps || !isFinite(estimateBps) || (holdBar !== null && estimateBps < holdBar);
      if (weak) stableSinceRef.current = now;
      return;
    }
    if (now - lastSwitchAtRef.current < ABR_COOLDOWN_MS) return;
    const requiredMs = profileRef.current.stableForUpMs * demotionMultRef.current;
    if (now - stableSinceRef.current >= requiredMs) adaptTo(target);
  }, [qualityKeyRef, adaptTo, videoCopiedRef, sourceVideoBpsRef]);

  // Re-seed when re-entering Auto: reset the cap and arm a fresh cooldown + streak (so a manual
  // re-select doesn't immediately flip). The demotion multiplier is forgiven too — a manual re-select
  // is the viewer saying "try again from the top". The caller still triggers its own restart/re-tune.
  const reseed = useCallback((seed) => {
    autoBpsRef.current = seed;
    setAutoBps(seed);
    lastSwitchAtRef.current = Date.now();
    stableSinceRef.current = Date.now();
    stallEpisodesRef.current = [];
    demotionMultRef.current = 1;
  }, []);

  return { autoBps, autoBpsRef, handleStall, handleBandwidth, adaptTo, reseed };
}
