import type { ItemSummary } from "../../Pages/Books/booksApi";
import { __resetMediaForTests } from "../../Pages/Books/booksMedia";
import type { FacetSpec, FacetState } from "../rail/facetSpec";
import { EMPTY_FACET_STATE } from "../rail/facetSpec";
import { buildNovelsQuery, createNovelsSource, firstAuthor, toNovelCard } from "./novelsSource";

const spec: FacetSpec = { identity: "novels", facets: [], loadFacets: async () => ({}) };

const book = (id: number, extra: Partial<ItemSummary> = {}): ItemSummary => ({
  id, kind: "book", title: `Book ${id}`, seriesId: null, series: "Earthsea", seriesIssueCount: null, seriesYearStart: null, seriesYearEnd: null, seriesIsOngoing: false,
  franchise: null, isSingleIssueSeries: true, seriesRatingResolved: null, publisher: "Gollancz", year: 1968, month: null, datePrecision: "Year", rating: 88,
  synopsisSource: "External", creatorsCsv: "Ursula K. Le Guin, Someone Else", tagsCsv: "genre:fantasy", coverAspect: 0.64, fileName: `b${id}.epub`, extension: ".epub", fileSize: 1,
  pageCount: null, indexedAt: null, folderId: 1, topFolderId: 1, isExcluded: false, ...extra,
});

function mockFetch(handler: (url: string) => unknown) {
  const calls: string[] = [];
  vi.stubGlobal("fetch", vi.fn(async (url: string) => {
    calls.push(url);
    return { ok: true, status: 200, headers: { get: () => null }, json: async () => handler(url) };
  }));
  return calls;
}
beforeEach(() => __resetMediaForTests());
afterEach(() => vi.unstubAllGlobals());

describe("catalog/novelsSource", () => {
  it("builds the /novels query: includes join as CSV, only tags exclude, the floor, the flag, the text", () => {
    const state: FacetState = {
      ...EMPTY_FACET_STATE,
      q: " left hand ",
      include: { authors: ["Le Guin", "Herbert"], series: ["Earthsea"], publishers: [], decades: ["1960s"], tags: ["genre:sci-fi"] },
      exclude: { tags: ["adult-romance"], authors: ["Nobody"] },
      ratingMin: 80,
      ranges: {},
      flags: { unknown: true },
    };
    expect(buildNovelsQuery(state)).toEqual({
      author: "Le Guin,Herbert", series: "Earthsea", publisher: undefined, decade: "1960s", tag: "genre:sci-fi",
      excludeTag: "adult-romance", q: "left hand", minRating: 80, unknown: true,
    });
    expect(buildNovelsQuery(EMPTY_FACET_STATE)).toEqual({ author: undefined, series: undefined, publisher: undefined, decade: undefined, tag: undefined, excludeTag: undefined, q: undefined });
  });

  it("maps a row onto a card: the payload's cover, the first author, the rating and maturity badges", () => {
    const c = toNovelCard(book(21), { "21": "https://m/21.webp" }, { "21": 2 });
    expect(c).toMatchObject({ kind: "book", key: "book:21", title: "Book 21", subtitle: "Ursula K. Le Guin", label: "1968", imageUrl: "https://m/21.webp", imageThumbUrl: "https://m/21.webp", rating: 88 });
    expect(c.badges?.map((b) => b.label)).toEqual(["★ 8.8", "Mature"]);
    const bare = toNovelCard(book(22, { creatorsCsv: null, rating: null }));
    expect(bare.subtitle).toBe("Gollancz");
    expect(bare.imageUrl.startsWith("data:image/svg+xml")).toBe(true);
    expect(bare.badges).toEqual([]);
    expect(firstAuthor("A; B")).toBe("A");
    expect(firstAuthor("")).toBeUndefined();
  });

  it("pages /novels with the sort's orderby (none for the author default) and carries the total forward", async () => {
    const calls = mockFetch(() => ({ total: 2, skip: 0, top: 60, items: [book(21), book(22)], covers: {}, maturity: {} }));
    const src = createNovelsSource({ facetState: { ...EMPTY_FACET_STATE, include: { authors: ["Le Guin"] } }, spec, onOpen: () => {} });
    expect(src.supports).toEqual(["grid", "wall", "list"]);
    expect(src.groups).toEqual([]);
    const p1 = await src.fetchFlatBand(0, 60, "author");
    expect(p1.items.map((i) => i.id)).toEqual([21, 22]);
    expect(p1.total).toBe(2);
    expect(calls[0]).toContain("/API/Books/novels?");
    expect(calls[0]).toContain("author=Le+Guin");
    expect(calls[0]).not.toContain("orderby");
    await src.fetchFlatBand(60, 60, "rating");
    expect(calls[1]).toContain("orderby=rating");
    expect(calls[1]).toContain("skip=60");
  });
});
