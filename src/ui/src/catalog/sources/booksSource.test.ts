import type { ItemSummary } from "../../Pages/Books/booksApi";
import { __resetMediaForTests } from "../../Pages/Books/booksMedia";
import { setGroupMarkOverride } from "../../Pages/Books/booksQuery";
import type { FacetSpec } from "../rail/facetSpec";
import { parseFacetState } from "../rail/facetUrl";
import { createBooksSource, toBookCard, toBookGroup } from "./booksSource";

const spec: FacetSpec = {
  identity: "books",
  facets: [
    { key: "collections", token: "collection", label: "Collections", one: "Collection", valueType: "number" },
    { key: "series", token: "series", label: "Series", one: "Series", valueType: "number" },
    { key: "authors", token: "author", label: "Authors", one: "Author", valueType: "string" },
  ],
  flags: [{ key: "read", token: "read", label: "Read" }],
  loadFacets: async () => ({}),
};

const row = (id: number, extra: Partial<ItemSummary> = {}): ItemSummary => ({
  id, kind: "comic", title: `Issue ${id}`, seriesId: 1, series: "Hellboy", seriesIssueCount: 5, seriesYearStart: 1994, seriesYearEnd: 2019, seriesIsOngoing: false,
  franchise: null, isSingleIssueSeries: false, seriesRatingResolved: 80, publisher: "Dark Horse", year: 1994, month: 3, datePrecision: "Month", rating: 84,
  synopsisSource: "Cv", creatorsCsv: "Mike Mignola", tagsCsv: "Horror", coverAspect: 0.66, fileName: `h${id}.cbz`, extension: ".cbz", fileSize: 1048576 * 40,
  pageCount: 24, indexedAt: "2026-01-01", folderId: 3, topFolderId: 2, isExcluded: false, ...extra,
});

function mockFetch(handler: (url: string) => { status?: number; body?: unknown; headers?: Record<string, string> }) {
  const calls: string[] = [];
  vi.stubGlobal("fetch", vi.fn(async (url: string) => {
    calls.push(url);
    const r = handler(url);
    return { ok: (r.status ?? 200) < 300, status: r.status ?? 200, headers: { get: (k: string) => r.headers?.[k] ?? null }, json: async () => r.body };
  }));
  return calls;
}
beforeEach(() => __resetMediaForTests());
afterEach(() => vi.unstubAllGlobals());

describe("catalog/booksSource — the facet state is the scope", () => {
  it("maps a row onto a card (hue tile without a media token, label at month precision, duplicate badge)", () => {
    const c = toBookCard(row(7));
    expect(c).toMatchObject({ kind: "comic", key: "comic:7", title: "Issue 7", subtitle: "Hellboy", label: "1994.03", year: 1994, rating: 84 });
    expect(c.imageUrl.startsWith("data:image/svg+xml")).toBe(true);
    expect(c.badges?.map((b) => b.label)).toEqual(["★ 8.4"]);
    expect(toBookCard(row(8, { isExcluded: true, isSingleIssueSeries: true })).badges?.map((b) => b.label)).toEqual(["★ 8.4", "duplicate"]);
    expect(toBookCard(row(8, { isSingleIssueSeries: true })).subtitle).toBeUndefined();
  });

  it("a series group carries its run label, AI card and the caller's mark (overrides win)", () => {
    const g = toBookGroup({ key: "1", label: "Hellboy", totalItems: 5, renderTotal: null, items: [row(1)], userMeta: { isRead: false, wantToRead: true, isFavorite: false, rating: null, notes: null }, groupDetail: { aiSynopsis: "A demon.", aiRating: 80, aiKnownSeries: true, aiTags: ["genre:Horror"] } }, "series");
    expect(g.detail).toEqual({ runLabel: "1994 – 2019", kicker: "Dark Horse", synopsis: "A demon.", tags: ["Horror"] });
    expect(g.userMark?.wantToRead).toBe(true);
    expect(g.items[0].groupKey).toBe("1");
    setGroupMarkOverride("series", "1", { isRead: true, wantToRead: false, isFavorite: false, rating: 90, notes: null });
    expect(toBookGroup({ key: "1", label: "Hellboy", totalItems: 5, renderTotal: null, items: [], userMeta: null, groupDetail: null }, "series").userMark).toMatchObject({ isRead: true, rating: 90 });
  });

  it("pages the catalog with the filter, the exact params and the count on band 0; groups under the same scope", async () => {
    const calls = mockFetch((url) => {
      if (url.includes("/browse/groups?")) return { body: { totalGroups: 3, groups: [{ key: "1", label: "Hellboy", totalItems: 5, renderTotal: null, items: [row(1)], userMeta: null, groupDetail: null }] } };
      if (url.includes("/browse/group-letters")) return { body: { totalGroups: 3, letters: [{ letter: "H", firstIndex: 0 }] } };
      if (url.includes("%24count=true")) return { body: [row(1), row(2)], headers: { "X-Total-Count": "57" } };
      return { body: [row(3)] };
    });
    const state = parseFacetState("?f=collection:2&f=author:Mike%20Mignola&my=read&q=hell", spec);
    const onOpen = vi.fn(); const onOpenSeries = vi.fn(); const onScope = vi.fn();
    const s = createBooksSource({ facetState: state, spec, epoch: 4, onOpen, onOpenSeries, onScope });
    expect(s.queryKey).toContain(":4:");
    const first = await s.fetchFlatBand(0, 48, "series");
    expect(first.total).toBe(57);
    expect(calls[0]).toBe("/API/Books/odata/catalog?q=hell&kind=comic&%24filter=topFolderId+eq+2&%24orderby=series+asc%2Cyear+asc&%24skip=0&%24top=48&%24count=true&author=Mike+Mignola");
    expect((await s.fetchFlatBand(48, 48, "series")).total).toBe(57);
    const band = await s.fetchGroupBand!(0, 20, 48, "series", "newest");
    expect(calls[2]).toBe("/API/Books/browse/groups?groupBy=series&q=hell&orderby=newest&groupsTop=20&groupsSkip=0&perGroupTop=48&%24filter=topFolderId+eq+2&kind=comic&readOnly=true&author=Mike+Mignola");
    expect(band.totalGroups).toBe(3);
    expect(await s.groupLetters!("series", "series")).toEqual([{ letter: "H", firstIndex: 0 }]);

    s.onOpen(band.groups[0].items[0]);
    expect(onOpen).toHaveBeenCalledWith(expect.objectContaining({ id: 1 }));
    s.onOpenGroup!(band.groups[0], "series");
    expect(onOpenSeries).toHaveBeenCalledWith(1, "Hellboy", null);
    s.onOpenGroup!({ key: "44", label: "Comics", totalItems: 9, renderTotal: 9, items: [] }, "collection");
    expect(onScope).toHaveBeenCalledWith({ facet: { key: "collections", value: 44 }, group: "series" });
    s.onOpenGroup!({ key: "1990", label: "1990s", totalItems: 9, renderTotal: 9, items: [] }, "decade");
    expect(onScope).toHaveBeenCalledWith({ years: [1990, 1999], group: "series" });

    // R9 S8: the Writer / Artist axes are declared and wired, but OFF until the HOST can group by a
    // credit — a stale host answers `groupBy=author` with COLLECTIONS instead of an error, and a pill
    // that draws the wrong axis is worse than one that is absent.
    expect(s.groups.map((g) => g.value)).toEqual(["collection", "series", "publisher", "decade", "franchise"]);
    const withCredits = createBooksSource({ facetState: state, spec, creditAxes: true, onOpen, onOpenSeries, onScope });
    expect(withCredits.groups.map((g) => g.value)).toEqual(["collection", "series", "publisher", "decade", "franchise", "author", "artist"]);
    const head = (key: string) => ({ key, label: key, totalItems: 1, renderTotal: 1, items: [] });
    withCredits.onOpenGroup!(head("alan moore"), "author");
    expect(onScope).toHaveBeenLastCalledWith({ facet: { key: "authors", value: "alan moore" }, group: "series" });
    withCredits.onOpenGroup!(head("dave gibbons"), "artist");
    expect(onScope).toHaveBeenLastCalledWith({ facet: { key: "artists", value: "dave gibbons" }, group: "series" });
  });

  it("a one-issue series header collapses to its issue", () => {
    const onOpenSeries = vi.fn();
    const s = createBooksSource({ facetState: parseFacetState("", spec), spec, onOpen: vi.fn(), onOpenSeries, onScope: vi.fn() });
    const lone = toBookCard(row(9, { isSingleIssueSeries: true }));
    s.onOpenGroup!({ key: "5", label: "One-shot", totalItems: 1, renderTotal: 1, items: [lone] }, "series");
    expect(onOpenSeries).toHaveBeenCalledWith(5, "One-shot", { isSingleIssueSeries: true, itemId: 9 });
  });
});
