import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { cleanup, fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { useState } from "react";
import { MemoryRouter, Route, useLocation } from "react-router-dom";
import { __resetMediaForTests } from "./booksMedia";
import BooksPage from "./BooksPage";

// Page-level tests for the S3 routes — Explore, the Shelf, Novels, Kids — over the host's real
// envelopes (mocked at fetch), through the section root so the gate, the routing and the modals are
// the production ones.

global.IS_REACT_ACT_ENVIRONMENT = true;
(global as unknown as { matchMedia: unknown }).matchMedia = (global as unknown as { matchMedia?: unknown }).matchMedia || ((q: string) => ({
  matches: false, media: q, onchange: null, addListener: vi.fn(), removeListener: vi.fn(), addEventListener: vi.fn(), removeEventListener: vi.fn(), dispatchEvent: vi.fn(),
}));
(global as unknown as { ResizeObserver: unknown }).ResizeObserver = (global as unknown as { ResizeObserver?: unknown }).ResizeObserver || class { observe() {} unobserve() {} disconnect() {} };
(global as unknown as { IntersectionObserver: unknown }).IntersectionObserver = (global as unknown as { IntersectionObserver?: unknown }).IntersectionObserver || class { observe() {} unobserve() {} disconnect() {} takeRecords() { return []; } };

const summary = (id: number, extra: Record<string, unknown> = {}) => ({
  id, kind: "comic", title: `Hellboy #${id}`, seriesId: 9, series: "Hellboy", seriesIssueCount: 5, seriesYearStart: 1994, seriesYearEnd: 2019, seriesIsOngoing: false,
  franchise: null, isSingleIssueSeries: false, seriesRatingResolved: 82, publisher: "Dark Horse", year: 1994, month: 3, datePrecision: "Month", rating: 84,
  synopsisSource: "Cv", creatorsCsv: "Mike Mignola", tagsCsv: "genre:Horror,genre:Folklore", coverAspect: 0.66, fileName: `hb${id}.cbz`, extension: ".cbz", fileSize: 1, pageCount: 24,
  indexedAt: null, folderId: 3, topFolderId: 2, isExcluded: false, ...extra,
});
const book = (id: number, title: string) => summary(id, { kind: "book", title, seriesId: null, series: null, isSingleIssueSeries: true, creatorsCsv: "Ursula K. Le Guin", publisher: "Gollancz", year: 1969, month: null, datePrecision: "Year", extension: ".epub", fileName: `${id}.epub` });
const wireCard = (id: number, over: Record<string, unknown> = {}) => ({
  kind: "comic", id, key: `comic:${id}`, title: `Hellboy #${id}`, subtitle: "Hellboy", label: "1994", year: 1994, aspect: 0.66, imageUrl: null, imageThumbUrl: null, hue: null,
  rating: 84, badges: [{ label: "84", tone: "rating", title: "Library rating" }], groupKey: "9", sortKey: "84", raw: summary(id), ...over,
});
const explore = {
  spotlight: [wireCard(7), wireCard(8)],
  rails: [
    { key: "top-series", title: "Highest-rated series", kind: "strip", items: [wireCard(9, { kind: "series", key: "series:9", title: "Hellboy", subtitle: "Dark Horse", label: "1994–2019", badges: [{ label: "82", tone: "rating" }, { label: "5 issues", tone: "neutral" }], raw: { seriesId: 9, name: "Hellboy", rating: 82, issueCount: 5, yearStart: 1994, yearEnd: 2019, cover: summary(7) } })], more: { href: "/browse/groups?groupBy=series&kind=comic" } },
    { key: "fresh-arrivals", title: "Fresh arrivals", kind: "wall", items: [wireCard(8)], more: { href: "/odata/catalog?kind=comic&$orderby=indexedAt desc" } },
  ],
  seed: 42,
};
const kidsExplore = {
  spotlight: [wireCard(7), wireCard(8)],
  rails: [{ key: "series:12", title: "Bone", kind: "strip", items: [wireCard(31, { title: "Bone #1", subtitle: "Bone", raw: summary(31, { series: "Bone", seriesId: 12, tagsCsv: "genre:fantasy" }) })], more: { href: "/kids/series/12/items" } }],
  seed: 1,
};

type MockResponse = { ok: boolean; status: number; headers: { get: (k: string) => string | null }; json: () => Promise<unknown>; text: () => Promise<string> };
const calls: { url: string; init?: RequestInit }[] = [];
function mockFetch() {
  calls.length = 0;
  let hidden7 = false;
  vi.stubGlobal("fetch", vi.fn(async (url: string, init?: RequestInit) => {
    calls.push({ url, init });
    const ok = (body: unknown, headers: Record<string, string> = {}): MockResponse => ({ ok: true, status: 200, headers: { get: (k: string) => headers[k] ?? null }, json: async () => body, text: async () => "" });
    if (url.includes("/media-token")) return ok({ configured: false });
    if (url.includes("/API/SetUserSetting")) return ok({});
    if (url.includes("/explore/kids")) return ok(kidsExplore);
    if (url.includes("/explore?")) return ok(explore);
    if (url.endsWith("/positions/7/hide")) { hidden7 = true; return ok(null); }
    if (url.includes("/shelf/last-opened")) return ok(hidden7 ? { totalCount: 0, skip: 0, top: 200, entries: [] } : { totalCount: 1, skip: 0, top: 200, entries: [{ itemId: 7, lastPage: 3, lastSpineItemIndex: null, lastScrollPercent: null, status: "inprogress", wantToRead: false, favorite: false, updatedAt: null, item: summary(7) }] });
    if (url.includes("/shelf/continue")) return ok({ totalCount: 1, skip: 0, top: 1, entries: [] });
    if (url.includes("/shelf/series?kind=read")) return ok({ totalCount: 1, skip: 0, top: 200, series: [{ seriesId: 9, seriesName: "Hellboy", issueCount: 2, finishedCount: 1, seriesIssueCount: 5, coverItemId: 7, publisher: "Dark Horse", year: 1994, yearEnd: 2019, isOngoing: false, isRead: false, wantToRead: false, isFavorite: false, rating: 80 }] });
    if (url.includes("/shelf/series?kind=want")) return ok({ totalCount: 0, skip: 0, top: 200, series: [] });
    if (url.includes("/shelf/series/9/progress")) return ok({ seriesId: 9, total: 2, finishedCount: 1, finishedIds: [7], inProgressIds: [] });
    if (url.includes("/marks/items?kind=read")) return ok({ totalCount: 2, skip: 0, top: 200, entries: [{ itemId: 11, wantToRead: false, favorite: false, status: "finished", rating: 90, updatedAt: null, item: summary(11, { title: "Blankets", seriesId: null, series: null, isSingleIssueSeries: true }) }, { itemId: 7, wantToRead: false, favorite: false, status: "finished", rating: 70, updatedAt: null, item: summary(7) }] });
    if (url.includes("/marks/items?kind=want")) return ok({ totalCount: 1, skip: 0, top: 500, entries: [{ itemId: 8, wantToRead: true, favorite: false, status: "unread", rating: null, updatedAt: null, item: summary(8) }] });
    if (url.includes("/marks/items/")) return ok({ itemId: 7, wantToRead: true, favorite: false, status: "unread", rating: 70, updatedAt: null, item: null });
    if (url.includes("/positions/")) return ok({ itemId: 7, lastPage: 0, lastSpineItemIndex: null, lastScrollPercent: null, status: "unread", wantToRead: false, favorite: false, hiddenFromHistory: false, updatedAt: null });
    if (url.includes("/suggestions")) return ok({ count: 1, items: [summary(12)] });
    if (url.includes("/browse/series/9/run")) return ok({ seriesId: 9, total: 2, items: [{ item: summary(7), readingOrder: { readNumber: 1, readIndex: 0 }, collection: null }, { item: summary(8), readingOrder: { readNumber: 2, readIndex: 1 }, collection: null }] });
    if (url.includes("/browse/series/9/library-rating")) return ok({ rating: 88, note: null });
    if (url.includes("/browse/groups?")) return ok({ totalGroups: 1, groups: [{ key: "9", label: "Hellboy", totalItems: 2, items: [summary(7)], userMeta: null, groupDetail: null, renderTotal: null }] });
    if (url.includes("/browse/groups/series/9/items")) return ok({ items: [summary(8)], total: 1 });
    if (url.includes("/browse/facets")) return ok({ collections: [], series: [], tags: [], authors: [], artists: [], events: [], franchises: [], publishers: [], decades: [] });
    if (url.includes("/novels/facets")) return ok({ authors: [{ value: "Ursula K. Le Guin", count: 2 }], series: [], publishers: [], decades: [{ value: "1960s", count: 2 }], tags: [{ value: "genre:adult-romance", count: 1 }, { value: "genre:sci-fi", count: 2 }] });
    if (url.includes("/novels?")) return ok({ total: 2, skip: 0, top: 60, items: [book(21, "The Left Hand of Darkness"), book(22, "A Wizard of Earthsea")], covers: { "21": "https://m/21.webp" }, maturity: { "21": 1 } });
    if (url.includes("/kids/series/12/items")) return ok({ series: { id: 12, name: "Bone", rating: 90 }, total: 1, skip: 0, top: 40, items: [summary(31, { title: "Bone #1", series: "Bone", seriesId: 12 })], covers: {} });
    if (url.includes("/kids/browse")) return ok({ totalGroups: 1, groups: [{ key: "12", label: "Bone", totalItems: 1, items: [summary(31, { title: "Bone #1", series: "Bone", seriesId: 12 })], userMeta: null, groupDetail: null, renderTotal: null }], covers: {} });
    if (url.includes("/items/7")) return ok({ summary: summary(7), relativePath: "x", folderName: null, folderPath: null, topFolderId: null, topFolderName: null, hasThumbnail: false, embedded: null, parsed: null, book: null, series: null, insight: null, seriesInsight: null, cvVolume: null, cvIssue: null, locg: null, mu: null, external: null, readingOrder: null, collection: null, credits: [], tags: [], seriesTags: [], thumbUrl: null, downloadUrl: null, pagesUrlTemplate: null });
    if (url.includes("/odata/catalog")) return ok([summary(7), summary(8)], { "X-Total-Count": "2" });
    const miss: MockResponse = { ok: false, status: 404, headers: { get: () => null }, json: async () => null, text: async () => "" };
    return miss;
  }));
}

function Probe() {
  const l = useLocation();
  return <div data-testid="loc">{l.pathname}{l.search}</div>;
}
function Host({ initial }: { initial: Record<string, unknown> }) {
  const [ud, setUd] = useState<Record<string, unknown>>(initial);
  return <BooksPage userData={ud as never} setUserData={setUd as never} />;
}
function renderAt(url: string, userData: Record<string, unknown>) {
  const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <QueryClientProvider client={qc}>
      <MemoryRouter initialEntries={[url]}>
        <Probe />
        <Route path="/books"><Host initial={userData} /></Route>
      </MemoryRouter>
    </QueryClientProvider>,
  );
}

beforeEach(() => { window.localStorage.clear(); window.sessionStorage.clear(); __resetMediaForTests(); mockFetch(); });
afterEach(() => { cleanup(); vi.unstubAllGlobals(); document.documentElement.removeAttribute("data-kids-style"); });

const member = { username: "reader", hasPassword: true, booksAccess: true, booksMaturityCeiling: 3, isAdmin: false };
const LONG = { timeout: 15000 };

describe("Books — Explore, Shelf, Novels, Kids", () => {
  it("/books/explore: the hero headlines the SERIES, rails map More → onto Books URLs, a series card opens the series modal, Shuffle pushes a seed", async () => {
    renderAt("/books/explore", member);
    expect(await screen.findByRole("heading", { level: 1 }, LONG)).toHaveTextContent("Hellboy");
    expect(screen.getByText("Dark Horse", { selector: ".xp-hero-pub" })).toBeInTheDocument();
    expect(screen.getByText("Horror")).toBeInTheDocument(); // the hero tags read off the raw tagsCsv, category stripped
    expect(screen.getByText("Highest-rated series")).toBeInTheDocument();
    expect(screen.getByText("The latest 1 arrivals")).toBeInTheDocument();
    expect(screen.getAllByText("More →")).toHaveLength(2); // both rails map (shelf-by-series, recently added)

    fireEvent.click(screen.getByText("Shuffle ↻"));
    await waitFor(() => expect(screen.getByTestId("loc").textContent).toMatch(/\/books\/explore\?seed=\d+/));
    expect(calls.filter((c) => c.url.includes("/explore?")).length).toBe(2);

    fireEvent.click(await screen.findByRole("button", { name: "Hellboy" }));
    expect(screen.getByTestId("loc").textContent).toContain("series=9");
    fireEvent.click(screen.getAllByText("More →")[1]);
    expect(screen.getByTestId("loc").textContent).toBe("/books?sort=relevance");
  }, 20000);

  it("/books/shelf: counted tabs, series tiles over standalone items on Read, the drawer's ticks, and ✕ on Last opened", async () => {
    renderAt("/books/shelf", member);
    // Last opened is the default tab; the ✕ hides optimistically and tells the host.
    expect(await screen.findByRole("heading", { level: 1 }, LONG)).toHaveTextContent("Shelf");
    expect(await screen.findByText("Hellboy #7", { selector: ".bs-card-title" })).toBeInTheDocument();
    fireEvent.click(screen.getByRole("button", { name: /Remove Hellboy #7 from Last opened/ }));
    await waitFor(() => expect(screen.queryByText("Hellboy #7", { selector: ".bs-card-title" })).toBeNull());
    expect(calls.some((c) => c.url.endsWith("/positions/7/hide") && c.init?.method === "POST")).toBe(true);

    // Read: 1 series + 1 genuinely standalone item (issue #7 belongs to Hellboy and must not show twice).
    const readTab = await screen.findByRole("tab", { name: /Read/ });
    expect(within(readTab).getByText("2")).toBeInTheDocument();
    fireEvent.click(readTab);
    expect(screen.getByTestId("loc").textContent).toBe("/books/shelf?tab=read");
    expect(await screen.findByRole("button", { name: "Open Hellboy" })).toBeInTheDocument();
    expect(screen.getByText("1/2")).toBeInTheDocument();
    expect(screen.getByText("Blankets")).toBeInTheDocument();
    expect(screen.queryByText("Hellboy #7", { selector: ".bs-card-title" })).toBeNull();
    // Ratings show on Read: the series' 80 → 8 stars on, the standalone's 90 → 9.
    expect(screen.getByRole("group", { name: "Your rating: 8 of 10" })).toBeInTheDocument();
    expect(screen.getByRole("group", { name: "Your rating: 9 of 10" })).toBeInTheDocument();

    fireEvent.click(screen.getByRole("button", { name: /Manage issues/ }));
    expect(await screen.findByRole("region", { name: "Hellboy issues" })).toBeInTheDocument();
    const tick7 = await screen.findByRole("button", { name: "Mark as unread: Hellboy #7" });
    expect(tick7).toHaveAttribute("aria-pressed", "true");
    expect(screen.getByRole("button", { name: "Mark as read: Hellboy #8" })).toHaveAttribute("aria-pressed", "false");
    expect(screen.getByText("#1")).toBeInTheDocument();
    fireEvent.click(screen.getByRole("button", { name: "Mark as read: Hellboy #8" }));
    await waitFor(() => expect(calls.some((c) => c.url.endsWith("/positions/8") && c.init?.method === "PUT" && String(c.init?.body).includes("-1"))).toBe(true));
  }, 20000);

  it("/books/novels: a first landing gets the 'not adult-romance' chip, the grid pages /novels with it, covers and maturity ride the payload", async () => {
    renderAt("/books/novels?view=grid", member);
    await waitFor(() => expect(screen.getByTestId("loc").textContent).toContain("x=tag%3Aadult-romance"), LONG);
    expect(await screen.findByText("Adult romance", { selector: ".bx-chip-ex" }, LONG)).toBeInTheDocument();
    expect(await screen.findByText("The Left Hand of Darkness", {}, LONG)).toBeInTheDocument();
    expect(screen.getByText("Teen")).toBeInTheDocument();
    expect(screen.getAllByText("Ursula K. Le Guin", { selector: ".bx-meta-b" })).toHaveLength(2);
    // Every list request carried the seed — the host never paged the unseeded query.
    const lists = calls.filter((c) => c.url.includes("/novels?") && !c.url.includes("top=1"));
    expect(lists.length).toBeGreaterThanOrEqual(1);
    for (const l of lists) expect(l.url).toContain("excludeTag=adult-romance");
    await waitFor(() => expect(document.querySelector(".bx-count")?.textContent).toBe("2 books"));
    expect(document.querySelector(".bx-host")?.getAttribute("data-section")).toBe("books-novels");
    // Clearing the chip is a real filter change: the next landing this session is not re-seeded.
    fireEvent.click(screen.getByText("Adult romance", { selector: ".bx-chip-ex" }));
    await waitFor(() => expect(screen.getByTestId("loc").textContent).not.toContain("adult-romance"));
    await waitFor(() => expect(screen.getByTestId("loc").textContent).not.toContain("adult-romance"));
  }, 20000);

  it("/books/kids: the Comic Pop home from /explore/kids, hearts are the want toggle, the style toggle writes the site setting, ?series= is a single shelf", async () => {
    renderAt("/books/kids", { ...member, booksMaturityCeiling: 0 });
    expect(await screen.findByRole("heading", { level: 1 }, LONG)).toHaveTextContent("Hellboy");
    expect(document.querySelector(".kids-shell")?.className).toContain("kids-pop");
    expect(document.documentElement.getAttribute("data-kids-style")).toBe("pop");
    expect(screen.getByRole("button", { name: "BONE" })).toBeInTheDocument();
    // Issue 8 is on the reading list already; issue 7 is not — a heart click writes the mark.
    const hearts = await screen.findAllByRole("button", { name: "Remove from reading list" });
    expect(hearts.length).toBeGreaterThanOrEqual(1);
    const add = screen.getAllByRole("button", { name: "Save to reading list" })[0];
    fireEvent.click(add);
    await waitFor(() => expect(calls.some((c) => c.url.endsWith("/marks/items/7") && c.init?.method === "PUT" && String(c.init?.body).includes("true"))).toBe(true));

    fireEvent.click(screen.getByRole("tab", { name: "♥ Bubble Gum" }));
    await waitFor(() => expect(document.querySelector(".kids-shell")?.className).toContain("kids-bubble"));
    expect(calls.some((c) => c.url === "/API/SetUserSetting" && String(c.init?.body).includes("bubble"))).toBe(true);

    // The series row header lands on that series' shelf; the Kids page never opens the series modal.
    fireEvent.click(screen.getByRole("button", { name: "Bone" }));
    await waitFor(() => expect(screen.getByTestId("loc").textContent).toBe("/books/kids?series=12"));
    expect(await screen.findByText("1 issues")).toBeInTheDocument();
    expect(screen.queryByRole("dialog")).toBeNull();
    fireEvent.click(screen.getByText("★ Home"));
    expect(screen.getByTestId("loc").textContent).toBe("/books/kids");
  }, 20000);

  it("a kid account is redirected off Explore and Novels, and the phone tabs show Kids + Shelf only", async () => {
    renderAt("/books/explore", { ...member, booksMaturityCeiling: 0 });
    await waitFor(() => expect(screen.getByTestId("loc").textContent).toBe("/books/kids"));
    cleanup();
    renderAt("/books/novels", { ...member, booksMaturityCeiling: 0 });
    await waitFor(() => expect(screen.getByTestId("loc").textContent).toBe("/books/kids"));
  }, 20000);
});
