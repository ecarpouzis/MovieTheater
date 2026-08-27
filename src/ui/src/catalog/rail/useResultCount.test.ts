import { describe, expect, it } from "vitest";
import { readCount } from "./useResultCount";

// One reading of "how many results" over the two envelopes the sections' browse endpoints use.
const res = (ok: boolean, body: unknown, status = ok ? 200 : 500) =>
  ({ ok, status, json: async () => body }) as Response;

describe("readCount", () => {
  it("reads totalCount (the movie/arcade envelope) and total (the rest)", async () => {
    await expect(readCount(res(true, { totalCount: 12, movies: [] }))).resolves.toBe(12);
    await expect(readCount(res(true, { total: 7, items: [] }))).resolves.toBe(7);
    await expect(readCount(res(true, { totalCount: 0 }))).resolves.toBe(0);
  });

  it("is -1 when the endpoint did not count", async () => {
    await expect(readCount(res(true, { items: [] }))).resolves.toBe(-1);
    await expect(readCount(res(true, { totalCount: "many" }))).resolves.toBe(-1);
  });

  it("throws on a failed request, so the query reports an error instead of a wrong number", async () => {
    await expect(readCount(res(false, null, 503))).rejects.toThrow("count → 503");
  });
});
