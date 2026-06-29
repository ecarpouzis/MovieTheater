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
//   "auto"        — open at the lossless tier and only ever DROP on sustained stalls. Best picture by
//                   default; a wired/fast link rarely needs to back off. (Climbing the ladder from a
//                   low opener forced a transcode restart — a multi-second rebuffer — at every rung,
//                   which on an on-network link is pure churn.)
//   "auto-mobile" — "Mobile Auto": open LOW for an instant first frame and a small data hit on
//                   cellular/flaky links, then climb FAST. Capped at 1080p/8 Mbps (full-quality 1080p
//                   — streaming-service territory; the 12 Mbps rung is wasted cellular data on a phone
//                   screen, and decode was never the limit), and the climb JUMPS straight to the best
//                   rung the link supports, so it reaches its ceiling in ONE restart instead of
//                   stalling at every rung on the way up. The shared 400MB/120s player buffer
//                   (streamEngine.createHls) covers this path too, so the cap is safe from the
//                   bitrate-spike starvation we hit on uncapped streams.
export const ABR_PROFILES = {
  auto: { openBps: DIRECT_BPS, ceilingBps: DIRECT_BPS, climbMode: "step", stableForUpMs: 90_000 },
  "auto-mobile": { openBps: 1_500_000, ceilingBps: 8_000_000, climbMode: "jump", stableForUpMs: 8_000 },
};

// The quality keys that engage adaptation (vs a fixed rung / "Original"). Both Auto modes do.
export const isAutoQuality = (key) => key === "auto" || key === "auto-mobile";

// The ABR profile for an active quality key (defaults to the standard "auto" profile).
export const abrProfileFor = (key) => ABR_PROFILES[key] || ABR_PROFILES.auto;

// One rung down, clamped at the bottom (DIRECT_BPS → the top transcode rung, etc.).
export function rungDown(bps) {
  const next = ABR_LADDER.find((rung) => rung < (bps ?? DIRECT_BPS));
  return next ?? BOTTOM;
}

export const isBottomRung = (bps) => (bps ?? DIRECT_BPS) <= BOTTOM;

// The rung Auto should climb UP to given a fresh throughput estimate, honoring the profile's ceiling
// and climb style — or the current cap unchanged when no climb is warranted. A rung is eligible only
// when the link clears it with headroom (1.5×) so we don't immediately stall back down; the lossless
// tier (infinite) gates on clearly beating the top transcode rung. "jump" returns the HIGHEST eligible
// rung (one restart instead of four); "step" advances a single rung.
export function climbTarget(currentBps, estimateBps, profile) {
  if (!estimateBps || !isFinite(estimateBps)) return currentBps;
  const cur = currentBps ?? BOTTOM;
  const eff = ABR_LADDER.filter((rung) => rung <= profile.ceilingBps); // top→bottom, capped at ceiling
  const supported = (rung) =>
    isFinite(rung) ? estimateBps >= rung * 1.5 : estimateBps >= TOP_FINITE * 1.5;
  const higher = eff.filter((rung) => rung > cur && supported(rung)); // top→bottom
  if (!higher.length) return currentBps;
  return profile.climbMode === "jump" ? higher[0] : higher[higher.length - 1];
}

// Human label for the active adaptive cap, e.g. "Auto · 4 Mbps" — or "Auto · Original" at the
// lossless tier (the original file, copied, no re-encode).
export function autoBpsLabel(bps) {
  if (!bps) return "Auto";
  if (!isFinite(bps)) return "Auto · Original";
  return `Auto · ${Math.round(bps / 1_000_000)} Mbps`;
}
