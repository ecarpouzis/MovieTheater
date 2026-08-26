import type { CardGroup, CardItem } from "../types";
import { NP_COLS_PER_BAND, npBuildBands, npShuffle, runFrom, type Run } from "./NewspaperView";

const item = (id: number, year?: number, rating?: number): CardItem => ({ kind: "movie", id, key: `movie:${id}`, title: `T${id}`, aspect: 0.66, imageUrl: `/i/${id}`, year, rating, raw: null });
const group = (key: string, n: number, detail?: Record<string, unknown>): CardGroup => ({
  key, label: key.toUpperCase(), totalItems: n * 3, renderTotal: n, items: Array.from({ length: n }, (_, i) => item(i + 1, 1990 + i, 50 + i)), detail,
});

describe("catalog/NewspaperView — runs and the seeded broadsheet", () => {
  it("a run is the group with its span, average rating, best-rated lead and detail", () => {
    const run = runFrom(group("mcu", 3, { synopsis: "A big one.", byline: "Words Someone", kicker: "Franchise", tags: ["action", 7] }))!;
    expect(run.name).toBe("MCU");
    expect(run.count).toBe(9);
    expect([run.minY, run.maxY]).toEqual([1990, 1992]);
    expect(run.rating).toBe(51);
    expect(run.lead.id).toBe(3);
    expect(run.synopsis).toBe("A big one.");
    expect(run.kicker).toBe("Franchise");
    expect(run.tags).toEqual(["action"]);
    expect(runFrom({ ...group("x", 1), items: [] })).toBeNull();
  });

  it("the shuffle is deterministic per seed", () => {
    const a = npShuffle([1, 2, 3, 4, 5, 6, 7, 8], 42);
    expect(npShuffle([1, 2, 3, 4, 5, 6, 7, 8], 42)).toEqual(a);
    expect(npShuffle([1, 2, 3, 4, 5, 6, 7, 8], 43)).not.toEqual(a);
    expect([...a].sort()).toEqual([1, 2, 3, 4, 5, 6, 7, 8]);
  });

  it("bands never repeat a run within a band, and a larger count is a stable prefix", () => {
    const pool: Run[] = Array.from({ length: 14 }, (_, i) => runFrom(group(`g${i}`, 2))!);
    const three = npBuildBands(pool, 3, 7);
    const five = npBuildBands(pool, 5, 7);
    expect(five.slice(0, 3)).toEqual(three);
    for (const b of five) {
      const keys = [b.feature.key, ...b.cols.map((c) => c.key)];
      expect(new Set(keys).size).toBe(keys.length);
      expect(b.cols.length).toBeLessThanOrEqual(NP_COLS_PER_BAND);
    }
    expect(five.map((b) => b.flip)).toEqual([false, true, false, true, false]);
    expect(npBuildBands([], 3, 7)).toEqual([]);
  });
});
