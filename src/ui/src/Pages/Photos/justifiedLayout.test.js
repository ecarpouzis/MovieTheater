import { describe, it, expect } from "vitest";
import {
  aspectOf,
  packRows,
  buildBlocks,
  sectionKeyOf,
  sectionLabelOf,
  visibleRange,
  DEFAULT_GAP,
  HEADER_HEIGHT,
} from "./justifiedLayout";

// The photo grid's layout is arithmetic, so it is tested as arithmetic — no DOM, no rendering. The
// properties that matter are the ones a rounding slip breaks invisibly: rows must fill the width
// EXACTLY (a ragged right edge is the classic justified-grid bug), aspect ratios must survive, and
// undated items must never be mixed into a dated section.

const photo = (id, width, height, takenAt = null) => ({ id, width, height, takenAt, path: `p/${id}.jpg` });

describe("aspectOf", () => {
  it("uses the stored (already EXIF-oriented) dimensions", () => {
    expect(aspectOf(photo(1, 800, 400))).toBe(2);
  });

  it("falls back to 4:3 rather than to zero for an unmeasured item", () => {
    // A video before Phase 5's ffprobe, or a HEIC with no decoder — zero here would make a row of
    // infinite width and take the whole page down with it.
    expect(aspectOf(photo(1, null, null))).toBeCloseTo(4 / 3);
    expect(aspectOf(photo(1, 0, 0))).toBeCloseTo(4 / 3);
  });
});

describe("packRows", () => {
  it("fills the container width exactly on every justified row", () => {
    const items = Array.from({ length: 20 }, (_, i) => photo(i, 300 + (i % 5) * 60, 200));
    const rows = packRows(items, { containerWidth: 1000, targetRowHeight: 200, gap: DEFAULT_GAP });

    // The last row is deliberately not stretched, so it is excluded from the width assertion.
    for (const row of rows.slice(0, -1)) {
      const width = row.tiles.reduce((sum, t) => sum + t.width, 0) + DEFAULT_GAP * (row.tiles.length - 1);
      expect(width).toBe(1000);
    }
  });

  it("keeps every item exactly once, in order", () => {
    const items = Array.from({ length: 17 }, (_, i) => photo(i, 400, 300));
    const rows = packRows(items, { containerWidth: 900, targetRowHeight: 180 });
    const ids = rows.flatMap((r) => r.tiles.map((t) => t.item.id));
    expect(ids).toEqual(items.map((i) => i.id));
  });

  it("does not stretch a short trailing row to full width", () => {
    const items = [photo(1, 400, 300), photo(2, 400, 300), photo(3, 400, 300)];
    const rows = packRows(items, { containerWidth: 4000, targetRowHeight: 200 });
    expect(rows).toHaveLength(1);
    // Three leftovers blown up across a 4000px row would be twice the height of everything above.
    expect(rows[0].height).toBe(200);
  });

  it("returns nothing before the container has been measured", () => {
    expect(packRows([photo(1, 400, 300)], { containerWidth: 0 })).toEqual([]);
  });
});

describe("sections", () => {
  it("groups by month and labels them readably", () => {
    expect(sectionKeyOf(photo(1, 100, 100, "2014-03-12T10:15:30"))).toBe("2014-03");
    expect(sectionLabelOf("2014-03")).toBe("March 2014");
  });

  it("calls a date-unknown item undated rather than inventing a date", () => {
    expect(sectionKeyOf(photo(1, 100, 100, null))).toBe("undated");
    expect(sectionLabelOf("undated")).toBe("Date unknown");
  });
});

describe("buildBlocks", () => {
  it("emits a header per section and never mixes two sections into one row", () => {
    const items = [
      photo(1, 400, 300, "2014-03-12T10:00:00"),
      photo(2, 400, 300, "2014-03-13T10:00:00"),
      photo(3, 400, 300, "2014-04-01T10:00:00"),
    ];
    const { blocks } = buildBlocks(items, { containerWidth: 5000, targetRowHeight: 200 });

    const headers = blocks.filter((b) => b.type === "header").map((b) => b.label);
    expect(headers).toEqual(["March 2014", "April 2014"]);

    for (const row of blocks.filter((b) => b.type === "row")) {
      const keys = new Set(row.tiles.map((t) => sectionKeyOf(t.item)));
      expect(keys.size).toBe(1);
    }
  });

  it("stacks blocks without overlap and reports the total height", () => {
    const items = Array.from({ length: 12 }, (_, i) => photo(i, 400, 300, "2014-03-12T10:00:00"));
    const { blocks, totalHeight } = buildBlocks(items, { containerWidth: 900, targetRowHeight: 180 });

    for (let i = 1; i < blocks.length; i += 1) {
      expect(blocks[i].top).toBeGreaterThanOrEqual(blocks[i - 1].top + blocks[i - 1].height);
    }
    const last = blocks[blocks.length - 1];
    expect(totalHeight).toBe(last.top + last.height);
    expect(blocks[0]).toMatchObject({ type: "header", top: 0, height: HEADER_HEIGHT });
  });

  it("skips section headers entirely when asked (the folder view)", () => {
    const items = [photo(1, 400, 300, "2014-03-12T10:00:00"), photo(2, 400, 300, null)];
    const { blocks } = buildBlocks(items, { containerWidth: 900, groupBySection: false });
    expect(blocks.every((b) => b.type === "row")).toBe(true);
  });
});

describe("visibleRange", () => {
  const items = Array.from({ length: 200 }, (_, i) => photo(i, 400, 300, "2014-03-12T10:00:00"));
  const { blocks } = buildBlocks(items, { containerWidth: 900, targetRowHeight: 180 });

  it("mounts only the blocks near the viewport", () => {
    const [start, end] = visibleRange(blocks, { top: 4000, bottom: 4800 }, 200);
    expect(end - start).toBeLessThan(blocks.length / 2);
    expect(blocks[start].top).toBeLessThanOrEqual(4000);
  });

  it("covers the whole visible band", () => {
    const band = { top: 3000, bottom: 3900 };
    const [start, end] = visibleRange(blocks, band, 0);
    const covered = blocks.slice(start, end);
    // Nothing intersecting the band may be left unmounted — a hole in the middle of the grid is the
    // failure mode windowing is most likely to produce and least likely to be noticed in a test.
    for (const block of blocks) {
      const intersects = block.top + block.height > band.top && block.top < band.bottom;
      if (intersects) expect(covered).toContain(block);
    }
  });

  it("is safe on an empty list", () => {
    expect(visibleRange([], { top: 0, bottom: 500 }, 100)).toEqual([0, 0]);
  });
});
