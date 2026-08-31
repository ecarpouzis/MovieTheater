import type { ItemSummary } from "../../Pages/Books/booksApi";
import { __resetMediaForTests } from "../../Pages/Books/booksMedia";
import type { FacetSpec, FacetState } from "../rail/facetSpec";
import { EMPTY_FACET_STATE } from "../rail/facetSpec";
import { buildNovelsQuery, createNovelsSource, firstAuthor, novelsGroupsFor, toNovelCard } from "./novelsSource";

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

  // The grouped half (series / author / publisher / decade). It is gated on the HOST saying it applies
  // the novels filter, because the failure of a stale host here is silent: `book.author=` is ignored,
  // not rejected, so the shelves would page the whole library under a rail full of active chips.
  it("stays flat until the host advertises that it applies the novels filter", () => {
    const src = createNovelsSource({ facetState: EMPTY_FACET_STATE, spec, onOpen: () => {}, groupAxes: ["series", "author"] });
    expect(src.groups).toEqual([]);
    expect(src.itemsModes).toBeUndefined();
    expect(src.fetchGroupBand).toBeUndefined();
    expect(src.supports).toEqual(["grid", "wall", "list"]);
    expect(novelsGroupsFor(["series"], false)).toEqual([]);
    // Advertised-but-unknown axes are ignored; an empty advertisement from a host that HAS the filter
    // means "it told me nothing", so the four stand.
    expect(novelsGroupsFor(["series", "collection"], true).map((g) => g.value)).toEqual(["series"]);
    expect(novelsGroupsFor([], true).map((g) => g.value)).toEqual(["series", "author", "publisher", "decade"]);
  });

  it("offers the advertised axes and sends the rail's own filter under `book.` on the grouped calls", async () => {
    const calls = mockFetch((url) => url.includes("/items?")
      ? { items: [book(22)], total: 5 }
      : { totalGroups: 1, groups: [{ key: "7", label: "Earthsea", totalItems: 5, renderTotal: null, items: [book(21)], userMeta: null, groupDetail: null }] });
    const src = createNovelsSource({
      facetState: {
        ...EMPTY_FACET_STATE,
        include: { authors: ["Le Guin"], decades: ["1960s"] },
        exclude: { tags: ["adult-romance"] },
        flags: { unknown: true },
      },
      spec, onOpen: () => {}, bookFilters: true, groupAxes: ["series", "author", "publisher", "decade"],
    });
    expect(src.groups.map((g) => [g.value, g.one])).toEqual([["series", "series"], ["author", "author"], ["publisher", "publisher"], ["decade", "decade"]]);
    expect(src.itemsModes).toEqual(["items", "groups"]);
    expect(src.defaultGroup).toBe("series");
    expect(src.supports).toContain("shelf");
    // "Books" / "One per series" — the collapsed side is the axis's noun, never a constant.
    expect(src.itemsLabels).toEqual({ items: "Books" });

    const page = await src.fetchGroupBand!(0, 20, 1, "series", "author");
    expect(page.totalGroups).toBe(1);
    expect(page.groups[0]).toMatchObject({ key: "7", label: "Earthsea", totalItems: 5, renderTotal: 5 });
    expect(page.groups[0].items[0].groupKey).toBe("7");
    const url = calls[0];
    expect(url).toContain("/browse/groups?");
    expect(url).toContain("kind=book");
    expect(url).toContain("book.author=Le+Guin");
    expect(url).toContain("book.decade=1960s");
    expect(url).toContain("book.excludeTag=adult-romance");
    expect(url).toContain("book.unknown=true");
    // The author default sends no orderby, exactly as the flat list does.
    expect(url).not.toContain("orderby");

    const more = await src.fetchGroupMore!("7", 0, 24, "series", "title");
    expect(more).toMatchObject({ total: 5 });
    expect(more.items[0].groupKey).toBe("7");
    expect(calls[1]).toContain("/browse/groups/series/7/items?");
    expect(calls[1]).toContain("book.author=Le+Guin");
    expect(calls[1]).toContain("orderby=title");
  });

  it("a series header opens the series modal; the other axes scope in place, the decade re-spelled as its chip", () => {
    const onOpenSeries = vi.fn();
    const onScope = vi.fn();
    const src = createNovelsSource({ facetState: EMPTY_FACET_STATE, spec, onOpen: () => {}, bookFilters: true, onOpenSeries, onScope });
    const head = (key: string, label: string) => ({ key, label, totalItems: 1, renderTotal: 1, items: [] });
    src.onOpenGroup!(head("7", "Earthsea"), "series");
    expect(onOpenSeries).toHaveBeenCalledWith(7, "Earthsea");
    src.onOpenGroup!(head("Le Guin", "Le Guin"), "author");
    expect(onScope).toHaveBeenCalledWith({ facet: { key: "authors", value: "Le Guin" }, group: "series" });
    // The head's key is the bare decade; the FACET is spelled "1960s" (what /novels/facets hands back).
    src.onOpenGroup!(head("1960", "1960s"), "decade");
    expect(onScope).toHaveBeenLastCalledWith({ facet: { key: "decades", value: "1960s" }, group: "series" });
  });
});
