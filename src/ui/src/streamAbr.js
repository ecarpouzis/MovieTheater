// Lightweight adaptive bitrate for the single-variant HLS the site serves
// (streaming-plan.md §14.4, option b). Jellyfin hands out one rendition per
// session, so hls.js can't auto-switch levels; instead the player measures
// throughput + stalls and the page restarts the session at a new bitrate cap
// (the same restart-at-offset machinery the manual quality menu already uses).
//
// The bias is deliberately asymmetric: drop fast when the connection can't keep
// up (reliability), climb slowly and only with clear headroom (each switch costs
// a ~2s reload and a fresh transcode, so churn is the thing to avoid).

// The adaptive ladder, in bitrate order. These mirror the capped rungs of the
// manual QUALITY_LADDER; "Auto" walks among them. null is never a rung here —
// uncapped is the manual "Original" choice, not an adaptive target.
export const ABR_LADDER = [12_000_000, 8_000_000, 4_000_000, 1_500_000];

const TOP = ABR_LADDER[0];
const BOTTOM = ABR_LADDER[ABR_LADDER.length - 1];

// Pick the opening cap before any segment has loaded. navigator.connection.downlink
// (Mbps) exists on Chromium/Android; elsewhere we can't measure yet, so start at a
// safe middle rung and let the running estimate climb it.
export function initialAutoBps() {
  const downlinkMbps = navigator.connection && navigator.connection.downlink;
  if (!downlinkMbps || !isFinite(downlinkMbps) || downlinkMbps <= 0) {
    return 4_000_000; // unknown: 720p/4M is a reliable opener
  }
  const usableBps = downlinkMbps * 1_000_000 * 0.7; // leave headroom for variability
  return ABR_LADDER.find((bps) => bps <= usableBps) ?? BOTTOM;
}

// One rung down, clamped at the bottom.
export function rungDown(bps) {
  const next = ABR_LADDER.find((rung) => rung < (bps ?? TOP));
  return next ?? BOTTOM;
}

// One rung up, clamped at the top.
export function rungUp(bps) {
  const higher = ABR_LADDER.filter((rung) => rung > (bps ?? BOTTOM));
  return higher.length ? higher[higher.length - 1] : TOP;
}

export const isTopRung = (bps) => (bps ?? TOP) >= TOP;
export const isBottomRung = (bps) => (bps ?? TOP) <= BOTTOM;

// Headroom rule for climbing: only step up when the measured throughput comfortably
// exceeds the *next* rung's bitrate (1.5×), so we don't immediately stall back down.
export function shouldStepUp(bps, estimateBps) {
  if (isTopRung(bps) || !estimateBps || !isFinite(estimateBps)) return false;
  return estimateBps >= rungUp(bps) * 1.5;
}

// Human label for the active adaptive cap, e.g. "Auto · 4 Mbps".
export function autoBpsLabel(bps) {
  if (!bps) return "Auto";
  return `Auto · ${Math.round(bps / 1_000_000)} Mbps`;
}
