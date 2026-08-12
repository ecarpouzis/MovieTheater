// Justified-grid layout for the photo timeline (docs/photos-plan.md §4).
//
// Pure functions, no DOM: the layout is arithmetic over known aspect ratios, and keeping it that way
// is what lets the grid place a row BEFORE any image has loaded (dimensions come from the ingest,
// already EXIF-oriented) and what lets it be tested without rendering anything.
//
// A justified grid, not a uniform one: family photos are a mix of portrait phone shots, landscape
// cameras and square scans, and a fixed-size cell either crops them or wastes half its area. Rows
// share a height and fill the width exactly, so nothing is cropped and no gaps appear.

export const DEFAULT_TARGET_ROW_HEIGHT = 200;
export const DEFAULT_GAP = 6;
export const HEADER_HEIGHT = 44;

/** Aspect ratio to lay a card out at. Missing dimensions (an un-probed video, an undecoded HEIC)
 *  fall back to 4:3 rather than to zero, which would make a row of infinite width. */
export function aspectOf(item) {
  const w = Number(item?.width) || 0;
  const h = Number(item?.height) || 0;
  if (w > 0 && h > 0) return w / h;
  return 4 / 3;
}

/**
 * Pack `items` into rows that each fill `containerWidth` exactly.
 *
 * The last row is deliberately NOT stretched: with three photos left over, justifying them across a
 * full-width row blows them up to several times everything else and the eye reads it as a mistake.
 * It keeps the target height and simply ends early, left-aligned — including when the row is also
 * the only one, which is what a section holding two photos should look like.
 */
export function packRows(items, { containerWidth, targetRowHeight = DEFAULT_TARGET_ROW_HEIGHT, gap = DEFAULT_GAP } = {}) {
  const rows = [];
  if (!items?.length || !(containerWidth > 0)) return rows;

  let current = [];
  let ratioSum = 0;

  const flush = (justify) => {
    if (!current.length) return;
    const gaps = gap * (current.length - 1);
    const usable = containerWidth - gaps;
    const height = justify ? usable / ratioSum : targetRowHeight;
    const laid = current.map((item) => ({
      item,
      width: Math.floor(aspectOf(item) * height),
      height: Math.round(height),
    }));
    // Hand the rounding remainder to the last tile so the row's total is EXACTLY the container
    // width; per-tile rounding otherwise leaves a ragged right edge that shifts row by row.
    if (justify && laid.length) {
      const used = laid.reduce((sum, t) => sum + t.width, 0) + gaps;
      laid[laid.length - 1].width += containerWidth - used;
    }
    rows.push({ tiles: laid, height: Math.round(height) });
    current = [];
    ratioSum = 0;
  };

  for (const item of items) {
    current.push(item);
    ratioSum += aspectOf(item);
    const gaps = gap * (current.length - 1);
    if (ratioSum * targetRowHeight + gaps >= containerWidth) flush(true);
  }
  flush(false);
  return rows;
}

/** The section a dated item belongs to. Month granularity: a day header per photo-heavy holiday is
 *  noise, and a year header hides the shape of a collection entirely. */
export function sectionKeyOf(item) {
  if (!item?.takenAt) return "undated";
  return String(item.takenAt).slice(0, 7); // YYYY-MM
}

const MONTHS = [
  "January", "February", "March", "April", "May", "June",
  "July", "August", "September", "October", "November", "December",
];

export function sectionLabelOf(key) {
  if (key === "undated") return "Date unknown";
  const [year, month] = key.split("-");
  const index = Number(month) - 1;
  return `${MONTHS[index] ?? month} ${year}`;
}

/**
 * The whole scrollable surface as a flat list of positioned BLOCKS — one per section header and one
 * per row. Flat and pre-measured on purpose: windowing then needs only a binary search over block
 * tops, with no measuring pass and no scroll-anchor correction, because nothing here is estimated.
 */
export function buildBlocks(items, options = {}) {
  const { gap = DEFAULT_GAP, groupBySection = true } = options;
  const blocks = [];
  let top = 0;

  const emitRows = (sectionItems) => {
    for (const row of packRows(sectionItems, { ...options, gap })) {
      blocks.push({ type: "row", top, height: row.height, tiles: row.tiles });
      top += row.height + gap;
    }
  };

  if (!groupBySection) {
    emitRows(items || []);
    return { blocks, totalHeight: Math.max(0, top - gap) };
  }

  let key = null;
  let bucket = [];
  for (const item of items || []) {
    const itemKey = sectionKeyOf(item);
    if (itemKey !== key) {
      emitRows(bucket);
      bucket = [];
      key = itemKey;
      blocks.push({ type: "header", top, height: HEADER_HEIGHT, key, label: sectionLabelOf(key) });
      top += HEADER_HEIGHT;
    }
    bucket.push(item);
  }
  emitRows(bucket);
  return { blocks, totalHeight: Math.max(0, top - gap) };
}

/** Index of the last block starting at or before `offset` (binary search over the block tops). */
export function blockAtOffset(blocks, offset) {
  let lo = 0;
  let hi = blocks.length - 1;
  while (lo < hi) {
    const mid = (lo + hi + 1) >> 1;
    if (blocks[mid].top <= offset) lo = mid;
    else hi = mid - 1;
  }
  return Math.max(0, lo);
}

/** The [start, end) slice of blocks that intersects the visible band plus `overscan` px. */
export function visibleRange(blocks, { top, bottom }, overscan) {
  if (!blocks.length) return [0, 0];
  const start = blockAtOffset(blocks, top - overscan);
  let end = start;
  while (end < blocks.length && blocks[end].top <= bottom + overscan) end += 1;
  return [start, Math.max(end, start + 1)];
}
