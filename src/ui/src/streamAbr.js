// Lightweight adaptive bitrate for the single-variant HLS the site serves
// (streaming-plan.md §14.4, option b). Jellyfin hands out one rendition per
// session, so hls.js can't auto-switch levels; instead the player measures
// throughput + stalls and the page restarts the session at a new bitrate cap
// (the same restart-at-offset machinery the manual quality menu already uses).
//
// Each switch costs a ~2s reload and a fresh transcode, so churn is the thing to avoid. Behavior is
// chosen per device via ABR_PROFILES (desktop opens high and only drops; phone opens low and jumps up
// to a 720p cap) — see those profiles for the rationale.

// The uncapped/lossless tier: the server serves the original video copied bit-for-bit (a remux, no
// re-encode), so the picture is identical to the raw file. Represented as Infinity here so the ladder
// math treats it as "above every transcode rung"; the page maps it to a null bitrate cap on the wire.
export const DIRECT_BPS = Number.POSITIVE_INFINITY;

// The adaptive ladder, top to bottom. The top rung is lossless direct-stream (DIRECT_BPS); below it
// are the transcode caps (mirroring the capped rungs of the manual QUALITY_LADDER). "Auto" walks the
// whole ladder, so a fat connection gets the untouched file and a weak one steps down to a transcode.
export const ABR_LADDER = [DIRECT_BPS, 12_000_000, 8_000_000, 4_000_000, 1_500_000];

// Highest *transcode* rung — the climb-to-lossless headroom test and the open-direct test key off it
// (we can't compare a measured throughput against an infinite target).
const TOP_FINITE = 12_000_000;
const BOTTOM = ABR_LADDER[ABR_LADDER.length - 1];

// Two user-selectable Auto modes (each a menu item, alongside the fixed rungs and "Original"). The
// active quality key picks the profile — there is NO device sniffing; the viewer chooses.
//   "auto"        — open at the lossless tier and DROP on sustained stalls; after a drop, climb back
//                   once the link proves itself. Best picture by default; a wired/fast link rarely
//                   needs to back off. Both the drop and the climb-back JUMP straight to the best rung
//                   the measured link supports — every switch is a multi-second restart, so walking
//                   the ladder one rung at a time spends a visible rebuffer per rung for nothing
//                   (measured 2026-08-16: the step climb's freeze read as a failure and got refreshed
//                   away, which was slower than either jumping or waiting).
//   "auto-mobile" — "Mobile Auto": open LOW for an instant first frame and a small data hit on
//                   cellular/flaky links, then climb FAST. Capped at 1080p/8 Mbps (full-quality 1080p
//                   — streaming-service territory; the 12 Mbps rung is wasted cellular data on a phone
//                   screen, and decode was never the limit). The shared player buffer
//                   (streamEngine.createHls) covers this path too, so the cap is safe from the
//                   bitrate-spike starvation we hit on uncapped streams.
export const ABR_PROFILES = {
  auto: { openBps: DIRECT_BPS, ceilingBps: DIRECT_BPS, climbMode: "jump", stableForUpMs: 90_000 },
  "auto-mobile": { openBps: 1_500_000, ceilingBps: 8_000_000, climbMode: "jump", stableForUpMs: 8_000 },
};

// The quality keys that engage adaptation (vs a fixed rung / "Original"). Both Auto modes do.
export const isAutoQuality = (key) => key === "auto" || key === "auto-mobile";

// The ABR profile for an active quality key (defaults to the standard "auto" profile).
export const abrProfileFor = (key) => ABR_PROFILES[key] || ABR_PROFILES.auto;

// A rung must clear the link with this much headroom before we'll climb to it — or trust it as a
// drop target. 1.5× keeps a routine bitrate wobble from immediately stalling the rung we just paid a
// restart to reach.
const CLIMB_HEADROOM = 1.5;
// Hysteresis floor for the climb streak (see climbHoldBar): a sample below the next rung × this
// resets the streak; a sample between the two bars is a dead zone — not strong enough to climb on,
// not weak enough to start the clock over.
const HOLD_HEADROOM = 1.15;

// What the link must actually carry to sustain a rung: a finite rung is its own cap; the lossless
// tier delivers the source file's real bitrate, so it costs `sourceVideoBps` — falling back to the
// top transcode rung as a stand-in when the source bitrate is unknown.
const rungCostBps = (rung, sourceVideoBps) =>
  isFinite(rung) ? rung : sourceVideoBps || TOP_FINITE;

// The next drop target, clamped at the bottom.
//
// `sourceVideoBps` (the source video stream's own bitrate, reported by Stream/Start) skips the rungs
// that would change nothing: the server can only re-encode into a ceiling BELOW the source, so a cap
// above it hands back the identical copied video — dropping onto it costs a reload and delivers the
// same bytes that were already stalling. On a 5.8 Mbps file the 12 and 8 Mbps rungs are both that,
// so a stalling viewer would burn two restarts (~a minute) before the first rung that actually helps.
//
// `estimateBps` (a fresh throughput estimate, when the caller has one) picks the highest candidate
// the measured link clears with headroom, in ONE step. Without it the drop walks one rung at a time —
// which on 2026-08-16 meant DIRECT→12 Mbps on a link delivering 13: still above the link, so it
// stalled again and burned a second restart before reaching the rung that actually fit. When even the
// lowest candidate lacks headroom, take it anyway — it's the least-bad rung on offer.
export function rungDown(bps, sourceVideoBps, estimateBps) {
  const useful = (rung) => rung < (bps ?? DIRECT_BPS) && (!sourceVideoBps || rung <= sourceVideoBps);
  const candidates = ABR_LADDER.filter(useful); // top→bottom
  if (!candidates.length) return BOTTOM;
  if (estimateBps && isFinite(estimateBps)) {
    const sustainable = candidates.find((rung) => rung <= estimateBps / CLIMB_HEADROOM);
    return sustainable ?? candidates[candidates.length - 1];
  }
  return candidates[0];
}

export const isBottomRung = (bps) => (bps ?? DIRECT_BPS) <= BOTTOM;

// The rung Auto should climb UP to given a fresh throughput estimate, honoring the profile's ceiling
// and climb style — or the current cap unchanged when no climb is warranted. A rung is eligible only
// when the link clears its real cost with headroom (CLIMB_HEADROOM) so we don't immediately stall
// back down. The lossless tier's cost is the SOURCE FILE'S bitrate when known (rungCostBps) — a fixed
// gate let auto climb into a 23 Mbps remux over a 30 Mbps link (2026-08-16), almost no headroom at
// the exact tier whose buffer is smallest. "jump" returns the HIGHEST eligible rung (one restart
// instead of four); "step" advances a single rung.
export function climbTarget(currentBps, estimateBps, profile, sourceVideoBps) {
  if (!estimateBps || !isFinite(estimateBps)) return currentBps;
  const cur = currentBps ?? BOTTOM;
  const eff = ABR_LADDER.filter((rung) => rung <= profile.ceilingBps); // top→bottom, capped at ceiling
  const supported = (rung) => estimateBps >= rungCostBps(rung, sourceVideoBps) * CLIMB_HEADROOM;
  const higher = eff.filter((rung) => rung > cur && supported(rung)); // top→bottom
  if (!higher.length) return currentBps;
  return profile.climbMode === "jump" ? higher[0] : higher[higher.length - 1];
}

// The estimate below which a sample is WEAK evidence against climbing — the hysteresis floor for the
// climb streak. The streak used to reset on any sample short of the full climb bar (next rung ×
// CLIMB_HEADROOM), so estimate jitter around that bar restarted the 90s clock endlessly: on
// 2026-08-16 a steady ~25 Mbps link with dips to 16 took 29 MINUTES to climb off the 8 Mbps rung
// (small transcode segments make the hls.js estimate noisy). Only a sample below the next rung's cost
// × HOLD_HEADROOM should reset; between the bars is a dead zone — hold the streak, don't climb yet.
// Null when there is nothing above the current rung to climb to.
export function climbHoldBar(currentBps, profile, sourceVideoBps) {
  const cur = currentBps ?? BOTTOM;
  const above = ABR_LADDER.filter((rung) => rung <= profile.ceilingBps && rung > cur); // top→bottom
  if (!above.length) return null;
  const next = above[above.length - 1]; // the LOWEST rung above current — the first step of any climb
  return rungCostBps(next, sourceVideoBps) * HOLD_HEADROOM;
}

// Human label for the active adaptive cap, e.g. "Auto · 4 Mbps" — or "Auto · Original" at the
// lossless tier (the original file, copied, no re-encode).
export function autoBpsLabel(bps) {
  if (!bps) return "Auto";
  if (!isFinite(bps)) return "Auto · Original";
  return `Auto · ${Math.round(bps / 1_000_000)} Mbps`;
}
