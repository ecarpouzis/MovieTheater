import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import type { ReactNode } from "react";
import { MemoryRouter, Route } from "react-router-dom";
import { __resetMediaForTests } from "./booksMedia";
import BooksPage from "./BooksPage";

// Page-level tests for /books: the gate's answer RENDERED, the kid pinning, and the two modals
// cold-loading from their URL params over the host's real envelopes (mocked at fetch).

global.IS_REACT_ACT_ENVIRONMENT = true;
(global as unknown as { matchMedia: unknown }).matchMedia = (global as unknown as { matchMedia?: unknown }).matchMedia || ((q: string) => ({
  matches: false, media: q, onchange: null, addListener: vi.fn(), removeListener: vi.fn(), addEventListener: vi.fn(), removeEventListener: vi.fn(), dispatchEvent: vi.fn(),
}));
(global as unknown as { ResizeObserver: unknown }).ResizeObserver = (global as unknown as { ResizeObserver?: unknown }).ResizeObserver || class { observe() {} unobserve() {} disconnect() {} };
(global as unknown as { IntersectionObserver: unknown }).IntersectionObserver = (global as unknown as { IntersectionObserver?: unknown }).IntersectionObserver || class { observe() {} unobserve() {} disconnect() {} takeRecords() { return []; } };

const summary = (id: number, extra: Record<string, unknown> = {}) => ({
  id, kind: "comic", title: `Hellboy #${id}`, seriesId: 9, series: "Hellboy", seriesIssueCount: 5, seriesYearStart: 1994, seriesYearEnd: 2019, seriesIsOngoing: false,
  franchise: null, isSingleIssueSeries: false, seriesRatingResolved: 82, publisher: "Dark Horse", year: 1994, month: 3, datePrecision: "Month", rating: 84,
  synopsisSource: "Cv", creatorsCsv: "Mike Mignola", tagsCsv: "Horror", coverAspect: 0.66, fileName: `hb${id}.cbz`, extension: ".cbz", fileSize: 1, pageCount: 24,
  indexedAt: null, folderId: 3, topFolderId: 2, isExcluded: false, ...extra,
});

const detail = (id: number) => ({
  summary: summary(id), relativePath: "Hellboy/hb.cbz", folderName: "Hellboy", folderPath: "Comics/Hellboy", topFolderId: 2, topFolderName: "Comics", hasThumbnail: true,
  embedded: null, parsed: { seriesKey: "hellboy", issueNo: String(id), year: 1994, volumeNo: null, publisher: "Dark Horse", format: "SingleIssue", formatRaw: "Single Issue", isCollection: false, eventName: "Seed of Destruction", issueTitle: null },
  book: null, series: null, insight: null, seriesInsight: null,
  cvVolume: { id: 1, name: "Hellboy", deck: null, description: "<p>A demon raised by humans.</p>" }, cvIssue: { id: 2, name: null, deck: null, description: "<p>Hellboy meets the frog men.</p>" },
  locg: null, mu: null, external: null, readingOrder: null, collection: null,
  credits: [{ source: "ComicInfo", ordinal: 0, role: "Writer", name: "Mike Mignola" }, { source: "Locg", ordinal: 1, role: "Artist", name: "Mike Mignola" }],
  tags: [{ source: "ComicInfo", category: "genre", value: "Horror" }], seriesTags: [], thumbUrl: null, downloadUrl: null, pagesUrlTemplate: null,
});

type MockResponse = { ok: boolean; status: number; headers: { get: (k: string) => string | null }; json: () => Promise<unknown>; text: () => Promise<string> };

const calls: string[] = [];
function mockFetch() {
  calls.length = 0;
  vi.stubGlobal("fetch", vi.fn(async (url: string, init?: RequestInit) => {
    calls.push(url);
    const ok = (body: unknown, headers: Record<string, string> = {}): MockResponse => ({ ok: true, status: 200, headers: { get: (k: string) => headers[k] ?? null }, json: async () => body, text: async () => "" });
    if (url.includes("/media-token")) return ok({ configured: false });
    if (url.includes("/positions/")) return ok({ itemId: 7, lastPage: 0, lastSpineItemIndex: null, lastScrollPercent: null, status: "unread", wantToRead: false, favorite: false, hiddenFromHistory: false, updatedAt: null });
    if (url.includes("/marks/items/")) return ok({ itemId: 7, wantToRead: true, favorite: false, status: "unread", rating: 70, updatedAt: null, item: null });
    if (url.includes("/items/7")) return ok(detail(7));
    if (url.includes("/browse/series/9/run")) return ok({ seriesId: 9, total: 2, items: [{ item: summary(7), readingOrder: null, collection: null }, { item: summary(8), readingOrder: null, collection: null }] });
    if (url.includes("/browse/series/9/library-rating")) return ok({ rating: 88, note: "A run worth owning" });
    if (url.includes("/shelf/series/9/progress")) return ok({ seriesId: 9, total: 2, finishedCount: 1, finishedIds: [7], inProgressIds: [] });
    if (url.includes("/browse/groups?")) return ok({ totalGroups: 1, groups: [{ key: "9", label: "Hellboy", totalItems: 2, items: [summary(7)], userMeta: { isRead: false, wantToRead: true, isFavorite: false, rating: null, notes: "keep" }, groupDetail: { aiSynopsis: "Big red.", aiRating: 80, aiKnownSeries: true, aiTags: ["genre:Horror"] }, renderTotal: null }] });
    if (url.includes("/browse/groups/series/9/items")) return ok({ items: [summary(8)], total: 1 });
    if (url.includes("/odata/catalog")) return ok([summary(7), summary(8)], { "X-Total-Count": "2" });
    if (url.includes("/library/comic/folders")) return ok([]);
    if (url.includes("/browse/facets")) return ok({ collections: [{ id: 2, name: "Comics", count: 2 }], series: [{ id: 9, value: "Hellboy", count: 2 }], tags: [{ value: "Horror", count: 2 }], authors: [], artists: [], events: [], franchises: [], publishers: [{ id: 1, name: "Dark Horse", full: "Dark Horse Comics", count: 2 }], decades: [{ value: "1990", count: 2 }] });
    const miss: MockResponse = { ok: false, status: 404, headers: { get: () => null }, json: async () => null, text: async () => "" };
    return miss;
  }));
}

function renderAt(url: string, userData: Record<string, unknown> | null | undefined) {
  const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  const wrapper = ({ children }: { children: ReactNode }) => (
    <QueryClientProvider client={qc}>
      <MemoryRouter initialEntries={[url]}>
        <Route path="/books"><BooksPage userData={userData as never} /></Route>
        <Route path="/" exact><div>home</div></Route>
      </MemoryRouter>
    </QueryClientProvider>
  );
  return render(<div />, { wrapper });
}

beforeEach(() => { window.localStorage.clear(); __resetMediaForTests(); mockFetch(); });
afterEach(() => { cleanup(); vi.unstubAllGlobals(); });

const member = { username: "reader", hasPassword: true, booksAccess: true, booksMaturityCeiling: 3, isAdmin: false };

describe("Books/BooksPage — the gate, the pinning, the modals", () => {
  it("renders the plates for a non-member and for a member without a password, and nothing hits the host", async () => {
    renderAt("/books", { username: "x", hasPassword: true, booksAccess: false });
    expect(await screen.findByText(/members-only room/)).toBeInTheDocument();
    cleanup();
    renderAt("/books", { username: "x", hasPassword: false, booksAccess: true });
    expect(await screen.findByText(/needs a password-protected account/)).toBeInTheDocument();
    expect(calls.filter((c) => !c.includes("/media-token"))).toEqual([]);
  });

  it("pins a kid account to Kids and lets it read its shelf", async () => {
    renderAt("/books?view=wall", { ...member, booksMaturityCeiling: 0 });
    // The Kids page is a lazy chunk: its first import under vitest can take more than the 1 s default wait.
    await waitFor(() => expect(document.querySelector(".kids-shell")).toBeInTheDocument(), { timeout: 15000 });
    cleanup();
    renderAt("/books/shelf", { ...member, booksMaturityCeiling: 0 });
    expect(await screen.findByRole("heading", { level: 1, name: "Shelf" }, { timeout: 15000 })).toBeInTheDocument();
  }, 40000);

  it("a member gets the browse with the catalog's pill row", async () => {
    const { container } = renderAt("/books", member);
    await waitFor(() => expect(container.querySelector(".bx-host")).toBeInTheDocument());
    expect(container.querySelector(".bx-host")?.getAttribute("data-section")).toBe("books");
  });

  // ONE STRIP (Eric, 2026-08-27, on a phone: "two bars… duplicate options"). Books drew its own
  // `SectionIndexTabs` — Explore · Browse 118,322 · Novels 22,084 · Kids — directly under the
  // SectionBar's tab strip, which carries the same destinations. The page must contribute NO
  // content-navigation strip of its own at any width; the bar's is the one, and the counted index
  // lives in the SIDER (BooksNavContent), where the canvas puts it.
  it("draws no second navigation strip of its own — the bar's tabs are the only one", async () => {
    // Restore it: a leaked `matches: true` makes every LATER test in the file think it is a phone.
    const realMatchMedia = (global as unknown as { matchMedia: unknown }).matchMedia;
    (global as unknown as { matchMedia: unknown }).matchMedia = (q: string) => ({
      matches: true, media: q, onchange: null, addListener: vi.fn(), removeListener: vi.fn(),
      addEventListener: vi.fn(), removeEventListener: vi.fn(), dispatchEvent: vi.fn(),
    });
    try {
      const { container } = renderAt("/books", member);
      await waitFor(() => expect(container.querySelector(".bx-host")).toBeInTheDocument());
      expect(container.querySelector(".books-tabs")).toBeNull();
      const strips = [...container.querySelectorAll("nav")]
        .filter((n) => [...n.querySelectorAll("a, button")].some((b) => /^(Explore|Browse|Shelf|Novels|Kids)/.test((b.textContent ?? "").trim())));
      expect(strips).toEqual([]);
    } finally {
      (global as unknown as { matchMedia: unknown }).matchMedia = realMatchMedia;
    }
  });

  it("a filtered browse shows the count and one chip per filter — a number facet by its label — and Clear all keeps the catalog params", async () => {
    renderAt("/books?view=wall&f=tag:Horror&f=series:9&x=tag:Manga", member);
    // R9 S1: the count left the toolbar for the rail's head line (BooksSiderRail, in the sider); the
    // page itself shows the chips.
    await waitFor(() => expect(document.querySelectorAll(".bx-chip").length).toBeGreaterThan(0));
    expect(await screen.findByText("Hellboy", { selector: ".bx-chip" })).toBeInTheDocument();
    expect(screen.getByText("Horror", { selector: ".bx-chip" })).toBeInTheDocument();
    expect(screen.getByText("Manga", { selector: ".bx-chip-ex" })).toBeInTheDocument();
    // The chip row offers no save button: "Saved views" at the foot of the rail is the one home (2026-09-05).
    expect(screen.queryByText("＋ Save view")).toBeNull();
    fireEvent.click(screen.getByText("Clear all"));
    await waitFor(() => expect(document.querySelector(".bx-chiprow")).toBeNull());
    expect(document.querySelector(".bx-host")?.getAttribute("data-view")).toBe("wall");
  });

  it("?item= cold-loads the item modal from the host: title, credits by role, the event, the synopsis leg, the marks", async () => {
    renderAt("/books?item=7", member);
    // The modal is a lazy chunk: its first import under vitest can take more than the 1 s default wait.
    expect(await screen.findByRole("dialog", { name: "Hellboy #7" }, { timeout: 20000 })).toBeInTheDocument();
    expect(await screen.findByText("Hellboy meets the frog men.")).toBeInTheDocument();
    expect(screen.getByText("ComicVine")).toBeInTheDocument();
    expect(screen.getByText("Seed of Destruction")).toBeInTheDocument();
    expect(screen.getAllByRole("button", { name: "Mike Mignola" })).toHaveLength(2); // once as writer, once as artist
    expect(await screen.findByText("On your list")).toBeInTheDocument();
    expect(screen.getByText("7/10")).toBeInTheDocument();
    expect(screen.getAllByText("Single Issue").length).toBeGreaterThanOrEqual(1); // the kind row and the stats line both carry the format
  }, 20000);

  it("?series= cold-loads the series modal: head, run, rating, progress ticks, the caller's notes", async () => {
    renderAt("/books?series=9", member);
    expect(await screen.findByRole("dialog", { name: "Hellboy" }, { timeout: 20000 })).toBeInTheDocument();
    expect(await screen.findByText("Big red.")).toBeInTheDocument();
    expect(screen.getByText("88")).toBeInTheDocument();
    expect(screen.getByText("1 / 2 read")).toBeInTheDocument();
    expect(screen.getByDisplayValue("keep")).toBeInTheDocument();
    expect(screen.getByText("Hellboy #8")).toBeInTheDocument();
    expect(screen.getByText("On your list")).toBeInTheDocument();
  }, 20000);
});
