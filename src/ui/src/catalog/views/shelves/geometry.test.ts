import { SHELF_PAD_LEFT, VIRT_KEEP, relaxedGap, shelfBookDims, spineFor, spinePrefix, virtualWindow } from "./geometry";

describe("catalog/shelves geometry — one source of truth for spines, covers and windows", () => {
  it("a spine is a real slice of the cover, clamped to a sane range", () => {
    expect(spineFor(103)).toBe(35);
    expect(spineFor(10)).toBe(16);
    expect(spineFor(400)).toBe(52);
  });

  it("a wide cover keeps its aspect and shrinks to the width cap instead of poking past its neighbours", () => {
    expect(shelfBookDims(172, 0.66)).toEqual({ w: 114, h: 172 });
    expect(shelfBookDims(172, 1.0)).toEqual({ w: 172, h: 172 });
    const wide = shelfBookDims(172, 1.6);
    expect(wide.w).toBe(172);
    expect(wide.h).toBe(Math.round(172 / 1.6));
    expect(shelfBookDims(172, undefined)).toEqual(shelfBookDims(172, 0.66));
  });

  it("prefix sums are monotonic and end at the packed width", () => {
    const p = spinePrefix([0.66, 0.66, 1.2], 172);
    expect(p[0]).toBe(0);
    expect(p[1]).toBe(spineFor(114));
    expect(p[3]).toBe(p[2] + spineFor(172));
    for (let i = 1; i < p.length; i += 1) expect(p[i]).toBeGreaterThan(p[i - 1]);
  });

  it("the gap only relaxes on a fully-loaded shelf with slack, and never past the cap", () => {
    const minGap = Math.max(1, Math.round(172 * 0.11));
    expect(relaxedGap(172, 5, 3, 2000, 200, 114)).toBe(minGap); // unloaded tail: pack from the left
    expect(relaxedGap(172, 1, 0, 2000, 40, 114)).toBe(minGap);
    const relaxed = relaxedGap(172, 5, 0, 2000, 200, 114);
    expect(relaxed).toBeGreaterThan(minGap);
    expect(relaxed).toBeLessThanOrEqual(Math.round(172 * 0.32));
    expect(relaxedGap(172, 5, 0, 300, 200, 114)).toBe(minGap); // no slack
  });

  it("the virtual window covers the visible strip plus VIRT_KEEP either side, with slack", () => {
    const aspects = new Array(1000).fill(0.66);
    const p = spinePrefix(aspects, 172);
    const gap = 19;
    const win = virtualWindow(p, 1000, gap, 20000, 1200);
    const pos = (i: number) => SHELF_PAD_LEFT + p[i] + i * gap;
    expect(pos(win.start)).toBeLessThanOrEqual(20000 - VIRT_KEEP);
    expect(pos(win.end)).toBeGreaterThanOrEqual(20000 + 1200 + VIRT_KEEP);
    expect(win.end - win.start).toBeLessThan(1000);
    expect(virtualWindow(p, 1000, gap, 0, 1200).start).toBe(0);
  });
});
