import { BooksApiError, fetchCatalog, fetchGroups, fetchNext, putItemMark, qs } from "./booksApi";

function mockFetch(handler: (url: string, init?: RequestInit) => { status: number; body?: unknown; headers?: Record<string, string> }) {
  const calls: { url: string; init?: RequestInit }[] = [];
  vi.stubGlobal("fetch", vi.fn(async (url: string, init?: RequestInit) => {
    calls.push({ url, init });
    const r = handler(url, init);
    return {
      ok: r.status >= 200 && r.status < 300,
      status: r.status,
      headers: { get: (k: string) => r.headers?.[k] ?? null },
      json: async () => r.body,
      text: async () => String(r.body ?? ""),
    };
  }));
  return calls;
}
afterEach(() => vi.unstubAllGlobals());

describe("Books/booksApi — the host's envelopes through the seam", () => {
  it("builds query strings with repeatable exact params and drops blanks", () => {
    expect(qs({ q: "a b", skip: 0, top: 48, kind: undefined, author: ["X", "Y"], readOnly: false, $count: "true" })).toBe("?q=a+b&skip=0&top=48&author=X&author=Y&%24count=true");
  });

  it("the catalog reads its total from the X-Total-Count header on the counted page only", async () => {
    const calls = mockFetch((url) => (url.includes("%24count=true")
      ? { status: 200, body: [{ id: 1 }], headers: { "X-Total-Count": "321" } }
      : { status: 200, body: [{ id: 2 }] }));
    const first = await fetchCatalog({ q: "x", filter: "year ge 1980", orderby: "series asc", skip: 0, top: 48, count: true, exact: { author: ["Someone"] } });
    expect(first.total).toBe(321);
    expect(calls[0].url).toBe("/API/Books/odata/catalog?q=x&%24filter=year+ge+1980&%24orderby=series+asc&%24skip=0&%24top=48&%24count=true&author=Someone");
    const second = await fetchCatalog({ skip: 48, top: 48 });
    expect(second.total).toBe(-1);
    expect(second.items.map((i) => i.id)).toEqual([2]);
  });

  it("groups carry the mark switches and the exact params; 204 reads as null; errors carry the status", async () => {
    const calls = mockFetch((url) => (url.includes("/next") ? { status: 204 } : url.includes("/marks/") ? { status: 403 } : { status: 200, body: { totalGroups: 1, groups: [] } }));
    await fetchGroups({ groupBy: "series", readOnly: true, exact: { tag: ["Noir"] }, orderby: null });
    expect(calls[0].url).toBe("/API/Books/browse/groups?groupBy=series&readOnly=true&tag=Noir");
    expect(await fetchNext(7)).toBeNull();
    await expect(putItemMark(7, { rating: null })).rejects.toMatchObject({ status: 403 });
    expect(calls[2].init?.method).toBe("PUT");
    expect(calls[2].init?.body).toBe(JSON.stringify({ rating: null }));
    await expect(putItemMark(7, {})).rejects.toBeInstanceOf(BooksApiError);
  });
});
