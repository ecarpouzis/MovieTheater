import { driveBatches, NoProgressError, pagedStep } from "./driveBatches";

describe("Books/admin/driveBatches — the client-driven loop", () => {
  it("walks pages until the host says there is no next cursor, reporting progress", async () => {
    const pages: Record<number, number[]> = { 0: [1, 2, 3], 3: [4, 5, 6], 6: [7] };
    const seen: number[] = [];
    const out = await driveBatches<number>(async (cursor) => {
      const items = pages[cursor] ?? [];
      return { items, nextCursor: items.length === 3 ? cursor + 3 : null };
    }, { onProgress: (p) => seen.push(p.loaded) });
    expect(out).toEqual([1, 2, 3, 4, 5, 6, 7]);
    expect(seen).toEqual([3, 6, 7]);
  });

  it("breaks on no progress — the same cursor twice, or an empty page with a next cursor", async () => {
    await expect(driveBatches(async (cursor) => ({ items: [1], nextCursor: cursor }))).rejects.toBeInstanceOf(NoProgressError);
    await expect(driveBatches(async (cursor) => ({ items: [], nextCursor: cursor + 10 }))).rejects.toBeInstanceOf(NoProgressError);
  });

  it("stops when aborted and honours the step ceiling", async () => {
    const ac = new AbortController();
    let calls = 0;
    const out = await driveBatches<number>(async (cursor) => { calls += 1; if (calls === 2) ac.abort(); return { items: [cursor], nextCursor: cursor + 1 }; }, { signal: ac.signal });
    expect(out).toEqual([0, 1]);
    const capped = await driveBatches<number>(async (cursor) => ({ items: [cursor], nextCursor: cursor + 1 }), { maxSteps: 3 });
    expect(capped).toEqual([0, 1, 2]);
  });

  it("pagedStep reads a {totalCount, items} envelope", async () => {
    const step = pagedStep<number>(async (skip, top) => ({ totalCount: 5, items: Array.from({ length: Math.min(top, 5 - skip) }, (_, i) => skip + i) }), 2);
    expect(await driveBatches(step)).toEqual([0, 1, 2, 3, 4]);
  });
});
