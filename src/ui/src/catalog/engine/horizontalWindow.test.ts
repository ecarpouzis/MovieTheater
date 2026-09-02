import { horizontalWindow, prefixSums, spacerWidths } from "./horizontalWindow";

describe("catalog/engine/horizontalWindow — the one horizontal virtualiser (Shelves planks + Extended strips)", () => {
  const widths = Array.from({ length: 100 }, (_, i) => 100 + (i % 3) * 20); // 100 / 120 / 140 …
  const prefix = prefixSums(widths);

  it("prefix sums are exact and gap-free", () => {
    expect(prefix[0]).toBe(0);
    expect(prefix[1]).toBe(100);
    expect(prefix[3]).toBe(360);
    expect(prefix[100]).toBe(widths.reduce((s, w) => s + w, 0));
  });

  it("the window covers the scrollport ± keepPx, widened by slack, and clamps to the run", () => {
    const g = { prefix, n: 100, gap: 10, padLeft: 4, keepPx: 200, slack: 2 };
    // At rest: units within [0, 400 + 200) → the first ~5 units, plus slack.
    const rest = horizontalWindow(g, 0, 400);
    expect(rest.start).toBe(0);
    const pos = (i: number) => 4 + prefix[i] + i * 10;
    expect(pos(rest.end - 2)).toBeGreaterThan(600); // the slack sits past the kept region
    expect(pos(rest.end - 3)).toBeLessThanOrEqual(600);
    // Deep in the run: the unit under scrollLeft − keepPx opens the window, minus slack.
    const deep = horizontalWindow(g, 5000, 400);
    expect(pos(deep.start + 2 + 1)).toBeGreaterThan(4800);
    expect(deep.end).toBeGreaterThan(deep.start);
    // Past the end: clamps.
    const end = horizontalWindow(g, 1e6, 400);
    expect(end.end).toBe(100);
    expect(end.start).toBeLessThanOrEqual(100);
  });

  it("the spacers reserve exactly the unmounted width under a flex gap, so the run's scroll width never changes", () => {
    const n = 100;
    const gap = 14;
    const full = prefix[n] + (n - 1) * gap; // every unit, a gap between each pair
    for (const [start, end] of [[0, 100], [0, 9], [7, 30], [40, 41], [95, 100], [3, 100]] as const) {
      const { lead, tail } = spacerWidths(prefix, n, gap, start, end);
      const mounted = prefix[end] - prefix[start];
      // children: [lead?] units [tail?] — a gap before every child after the first
      const children = (start > 0 ? 1 : 0) + (end - start) + (end < n ? 1 : 0);
      expect(lead + mounted + tail + (children - 1) * gap).toBe(full);
    }
  });

  it("with gap 0 the spacers are plain prefix differences (the Shelves' case)", () => {
    const { lead, tail } = spacerWidths(prefix, 100, 0, 10, 20);
    expect(lead).toBe(prefix[10]);
    expect(tail).toBe(prefix[100] - prefix[20]);
  });
});
