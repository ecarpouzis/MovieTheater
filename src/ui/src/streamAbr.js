// Lightweight adaptive bitrate for the single-variant HLS the site serves
// (streaming-plan.md §14.4, option b). Jellyfin hands out one rendition per
// session, so hls.js can't auto-switch levels; instead the player measures
// throughput + stalls and the page restarts the session at a new bitrate cap
// (the same restart-at-offset machinery the manual quality menu already uses).
//
// The bias is deliberately asymmetric: drop fast when the connection can't keep
// up (reliability), climb slowly and only with clear headroom (each switch costs
// a ~2s reload and a fresh transcode, so churn is the thing to avoid).

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

// Pick the opening cap before any segment has loaded. navigator.connection.downlink
// (Mbps) exists on Chromium/Android; elsewhere we can't measure yet, so start at a
// safe middle rung and let the running estimate climb it.
export function initialAutoBps() {
  const downlinkMbps = navigator.connection && navigator.connection.downlink;
  if (!downlinkMbps || !isFinite(downlinkMbps) || downlinkMbps <= 0) {
    return 4_000_000; // unknown: 720p/4M is a reliable opener
  }
  const usableBps = downlinkMbps * 1_000_000 * 0.7; // leave headroom for variability
  // A genuinely fat link opens straight at the lossless tier; otherwise the highest transcode rung
  // that fits, and the running estimate climbs from there (possibly all the way to lossless).
  if (usableBps >= TOP_FINITE * 1.5) return DIRECT_BPS;
  return ABR_LADDER.find((bps) => isFinite(bps) && bps <= usableBps) ?? BOTTOM;
}

// One rung down, clamped at the bottom (DIRECT_BPS → the top transcode rung, etc.).
export function rungDown(bps) {
  const next = ABR_LADDER.find((rung) => rung < (bps ?? DIRECT_BPS));
  return next ?? BOTTOM;
}

// One rung up, clamped at the top (the lossless tier).
export function rungUp(bps) {
  const higher = ABR_LADDER.filter((rung) => rung > (bps ?? BOTTOM));
  return higher.length ? higher[higher.length - 1] : DIRECT_BPS;
}

export const isTopRung = (bps) => (bps ?? DIRECT_BPS) >= DIRECT_BPS;
export const isBottomRung = (bps) => (bps ?? DIRECT_BPS) <= BOTTOM;

// Headroom rule for climbing: only step up when the measured throughput comfortably exceeds the
// *next* rung's bitrate (1.5×), so we don't immediately stall back down. The next rung up from the
// top transcode cap is the lossless tier (infinite target) — there, gate on clearly beating the top
// transcode rung, since a connection that sustains >18 Mbps will almost always carry our originals.
export function shouldStepUp(bps, estimateBps) {
  if (isTopRung(bps) || !estimateBps || !isFinite(estimateBps)) return false;
  const next = rungUp(bps);
  const target = isFinite(next) ? next : TOP_FINITE;
  return estimateBps >= target * 1.5;
}

// Human label for the active adaptive cap, e.g. "Auto · 4 Mbps" — or "Auto · Original" at the
// lossless tier (the original file, copied, no re-encode).
export function autoBpsLabel(bps) {
  if (!bps) return "Auto";
  if (!isFinite(bps)) return "Auto · Original";
  return `Auto · ${Math.round(bps / 1_000_000)} Mbps`;
}
