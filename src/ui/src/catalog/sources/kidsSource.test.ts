import type { ItemSummary } from "../../Pages/Books/booksApi";
import { __resetMediaForTests } from "../../Pages/Books/booksMedia";
import { createKidsSource } from "./kidsSource";

const row = (id: number, extra: Partial<ItemSummary> = {}): ItemSummary => ({
  id, kind: "comic", title: `Issue ${id}`, seriesId: 1, series: "Bone", seriesIssueCount: 55, seriesYearStart: 1991, seriesYearEnd: 2004, seriesIsOngoing: false,
  franchise: null, isSingleIssueSeries: false, seriesRatingResolved: 90, publisher: "Cartoon Books", year: 1991, month: null, datePrecision: "Year", rating: null,
  synopsisSource: "None", creatorsCsv: "Jeff Smith", tagsCsv: "genre:fantasy", coverAspect: 0.66, fileName: `b${id}.cbz`, extension: ".cbz", fileSize: 1,
  pageCount: 24, indexedAt: null, folderId: 1, topFolderId: 1, isExcluded: false, ...extra,
});

const browse = {
  totalGroups: 3,
  groups: [
    { key: "12", label: "Zita the Spacegirl", totalItems: 3, items: [row(1), row(2), row(3)], userMeta: null, groupDetail: null, renderTotal: null },
    { key: "7", label: "Bone", totalItems: 45, items: Array.from({ length: 40 }, (_, i) => row(100 + i)), userMeta: null, groupDetail: null, renderTotal: null },
    { key: "books", label: "Books", totalItems: 2, items: [row(500, { kind: "book", series: null }), row(501, { kind: "book", series: null })], userMeta: null, groupDetail: null, renderTotal: null },
  ],
  covers: { "1": "https://m/1.webp" },
};

function mockFetch() {
  const calls: string[] = [];
  vi.stubGlobal("fetch", vi.fn(async (url: string) => {
    calls.push(url);
    const body = url.includes("/kids/browse")
      ? browse
      : url.includes("/kids/series/7/items")
        ? { series: { id: 7, name: "Bone", rating: 90 }, total: 45, skip: 40, top: 5, items: [row(140), row(141), row(142), row(143), row(144)], covers: {} }
        : null;
    return { ok: body != null, status: body ? 200 : 404, headers: { get: () => null }, json: async () => body };
  }));
  return calls;
}
beforeEach(() => __resetMediaForTests());
afterEach(() => vi.unstubAllGlobals());

describe("catalog/kidsSource — one bounded load, sliced every way", () => {
  it("loads /kids/browse once for every band, letter and 'more' (host order by default, A–Z on the alpha sort)", async () => {
    const calls = mockFetch();
    const src = createKidsSource({ onOpen: () => {} });
    expect(src.supports).toEqual(["shelf", "extended"]);
    const best = await src.fetchGroupBand!(0, 20, 36, "series", "best");
    expect(best.totalGroups).toBe(3);
    expect(best.groups.map((g) => g.label)).toEqual(["Zita the Spacegirl", "Bone", "Books"]);
    expect(best.groups[0].items[0].imageUrl).toBe("https://m/1.webp");
    expect(best.groups[1].items).toHaveLength(36); // perGroupTop
    expect(best.groups[1].renderTotal).toBe(45);

    const alpha = await src.fetchGroupBand!(0, 20, 36, "series", "alpha");
    expect(alpha.groups.map((g) => g.label)).toEqual(["Bone", "Books", "Zita the Spacegirl"]);
    const letters = await src.groupLetters!("series", "alpha");
    expect(letters).toEqual([{ letter: "B", firstIndex: 0 }, { letter: "Z", firstIndex: 2 }]);

    const more = await src.fetchGroupMore!("12", 1, 10, "series", "best");
    expect(more.items.map((i) => i.id)).toEqual([2, 3]);
    expect(more.total).toBe(3);
    expect(calls.filter((c) => c.includes("/kids/browse"))).toHaveLength(1);
    expect(calls[0]).toContain("groupsTop=160");
    expect(calls[0]).toContain("perGroupTop=40");
  });

  it("pages a shelf past what it came with from /kids/series/{id}/items, and never for the trailing Books shelf", async () => {
    const calls = mockFetch();
    const src = createKidsSource({ onOpen: () => {} });
    const within = await src.fetchGroupMore!("7", 36, 4, "series", "best");
    expect(within.items.map((i) => i.id)).toEqual([136, 137, 138, 139]);
    const beyond = await src.fetchGroupMore!("7", 40, 5, "series", "best");
    expect(beyond.items.map((i) => i.id)).toEqual([140, 141, 142, 143, 144]);
    expect(beyond.items[0].groupKey).toBe("7");
    expect(calls.some((c) => c.includes("/kids/series/7/items?skip=40&top=5"))).toBe(true);
    const books = await src.fetchGroupMore!("books", 40, 5, "series", "best");
    expect(books.items).toEqual([]);
    expect(calls.some((c) => c.includes("/kids/series/books"))).toBe(false);
  });
});
