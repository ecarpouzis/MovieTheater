/**
 * The Shelves' geometry, pure and shared: a book's resting spine width, a cover's shelf-fitted
 * dimensions, the spine prefix sums a shelf's spacers and windows are computed from. ONE source
 * of truth — ShelfBook, the spacer reservation, the gap relaxation and the virtualization window
 * all use these, so none of them can drift apart.
 */
export const SHELF_BASE_H = 172;
/** The plank board band under each shelf (matches the 28 px plank image). */
export const PLANK = 28;
/** A cover wider than this ratio of the shelf height shrinks (true aspect kept) so it cannot poke past its neighbours. */
export const SHELF_MAX_W_RATIO = 1.0;
export const VIRT_THRESHOLD = 150;
export const VIRT_KEEP = 1600;
export const VIRT_SLACK = 12;
/** The shelf's left inset (the label + books sit in from the stile). */
export const SHELF_PAD_LEFT = 18;

/** Resting spine slot for a cover of width `cw`: a real slice of the cover, not a thin spine. */
export function spineFor(cw: number): number {
  return Math.max(16, Math.min(52, Math.round(cw * 0.34)));
}

export function shelfBookDims(shelfH: number, aspect?: number): { w: number; h: number } {
  const a = aspect && aspect > 0 ? aspect : 0.66;
  const maxW = shelfH * SHELF_MAX_W_RATIO;
  let w = shelfH * a;
  let h = shelfH;
  if (w > maxW) { w = maxW; h = maxW / a; }
  return { w: Math.round(w), h: Math.round(h) };
}

/** prefix[i] = px of resting spines before book i; prefix[n] = the packed width of n books. */
export function spinePrefix(aspects: (number | undefined)[], shelfH: number): number[] {
  const p = new Array<number>(aspects.length + 1);
  p[0] = 0;
  for (let i = 0; i < aspects.length; i += 1) p[i + 1] = p[i] + spineFor(shelfBookDims(shelfH, aspects[i]).w);
  return p;
}

/** The row-filling flex basis of a shelf holding n loaded books. */
export function shelfBasis(n: number): number {
  return Math.max(240, Math.min(820, 70 + n * 28));
}

/** Content-aware growth weight: roughly the width the shelf's FULL run (loaded + unloaded) would take. */
export function shelfGrowWeight(total: number, shelfH: number): number {
  const perSpine = Math.max(8, Math.round(shelfH * 0.22));
  const coverBase = Math.round(18 + shelfH * 0.70);
  return Math.max(240, coverBase + total * perSpine);
}

/** The relaxed gap between books on a fully-loaded shelf with slack: half the slack, capped. */
export function relaxedGap(shelfH: number, n: number, unloaded: number, clientWidth: number, spineSum: number, lastCw: number): number {
  const minGap = Math.max(1, Math.round(shelfH * 0.11));
  const maxGap = Math.round(shelfH * 0.32);
  if (n <= 1 || unloaded > 0) return minGap;
  const avail = clientWidth - SHELF_PAD_LEFT - (lastCw - spineFor(lastCw));
  const fitGap = (avail - spineSum) / (n - 1);
  return fitGap > minGap ? Math.round(Math.min(maxGap, minGap + (fitGap - minGap) * 0.5)) : minGap;
}

/** The mounted slice of a long shelf: books whose slots fall within scrollLeft ± VIRT_KEEP (binary searches over the prefix). */
export function virtualWindow(prefix: number[], n: number, gap: number, scrollLeft: number, clientWidth: number): { start: number; end: number } {
  const left = scrollLeft - VIRT_KEEP;
  const right = scrollLeft + clientWidth + VIRT_KEEP;
  const pos = (i: number) => SHELF_PAD_LEFT + prefix[i] + i * gap;
  let lo = 0;
  let hi = n;
  while (lo < hi) { const mid = (lo + hi) >> 1; if (pos(mid + 1) <= left) lo = mid + 1; else hi = mid; }
  const start = lo;
  lo = start; hi = n;
  while (lo < hi) { const mid = (lo + hi) >> 1; if (pos(mid) <= right) lo = mid + 1; else hi = mid; }
  return { start: Math.max(0, start - VIRT_SLACK), end: Math.min(n, lo + VIRT_SLACK) };
}
