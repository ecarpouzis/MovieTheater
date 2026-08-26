import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { MemoryRouter, Route, useLocation } from "react-router-dom";
import { __resetMediaForTests } from "../booksMedia";
import BooksPage from "../BooksPage";

// The reader route through the section root: the background-location routing (the page it was
// opened from stays mounted underneath and Close is one Back), the canvas reader's shell and menu,
// and the EPUB reader's shell — over the host's real envelopes mocked at fetch. jsdom has no canvas
// and loads no images, so the surfaces are asserted by their chrome, not their pixels.

global.IS_REACT_ACT_ENVIRONMENT = true;
(global as unknown as { matchMedia: unknown }).matchMedia = (global as unknown as { matchMedia?: unknown }).matchMedia || ((q: string) => ({
  matches: false, media: q, onchange: null, addListener: vi.fn(), removeListener: vi.fn(), addEventListener: vi.fn(), removeEventListener: vi.fn(), dispatchEvent: vi.fn(),
}));
(global as unknown as { ResizeObserver: unknown }).ResizeObserver = (global as unknown as { ResizeObserver?: unknown }).ResizeObserver || class { observe() {} unobserve() {} disconnect() {} };
(global as unknown as { IntersectionObserver: unknown }).IntersectionObserver = (global as unknown as { IntersectionObserver?: unknown }).IntersectionObserver || class { observe() {} unobserve() {} disconnect() {} takeRecords() { return []; } };
// jsdom's getContext logs "not implemented" — the reader tolerates a null context; keep the log quiet.
HTMLCanvasElement.prototype.getContext = (() => null) as unknown as HTMLCanvasElement["getContext"];

const summary = (id: number, extra: Record<string, unknown> = {}) => ({
  id, kind: "comic", title: `Hellboy #${id}`, seriesId: 9, series: "Hellboy", seriesIssueCount: 5, seriesYearStart: 1994, seriesYearEnd: 2019, seriesIsOngoing: false,
  franchise: null, isSingleIssueSeries: false, seriesRatingResolved: 82, publisher: "Dark Horse", year: 1994, month: 3, datePrecision: "Month", rating: 84,
  synopsisSource: "Cv", creatorsCsv: "Mike Mignola", tagsCsv: "genre:Horror", coverAspect: 0.66, fileName: `hb${id}.cbz`, extension: ".cbz", fileSize: 1, pageCount: 24,
  indexedAt: null, folderId: 3, topFolderId: 2, isExcluded: false, ...extra,
});
const detail = (id: number, extra: Record<string, unknown> = {}) => ({
  summary: summary(id, extra), relativePath: "Hellboy/hb.cbz", folderName: "Hellboy", folderPath: "Comics/Hellboy", topFolderId: 2, topFolderName: "Comics", hasThumbnail: true,
  embedded: null, parsed: null, book: null, series: null, insight: null, seriesInsight: null, cvVolume: null, cvIssue: null, locg: null, mu: null, external: null,
  readingOrder: null, collection: null, credits: [], tags: [], seriesTags: [], thumbUrl: null, downloadUrl: null, pagesUrlTemplate: `https://m/t/pages/${id}/{page}`,
});

type MockResponse = { ok: boolean; status: number; headers: { get: (k: string) => string | null }; json: () => Promise<unknown>; text: () => Promise<string> };
const calls: { url: string; init?: RequestInit }[] = [];
function mockFetch() {
  calls.length = 0;
  vi.stubGlobal("fetch", vi.fn(async (url: string, init?: RequestInit) => {
    calls.push({ url, init });
    const ok = (body: unknown, text = ""): MockResponse => ({ ok: true, status: 200, headers: { get: () => null }, json: async () => body, text: async () => text });
    if (url.includes("/media-token")) return ok({ configured: false });
    if (url.includes("/positions/7")) return ok({ itemId: 7, lastPage: 0, lastSpineItemIndex: null, lastScrollPercent: null, status: "unread", wantToRead: false, favorite: false, hiddenFromHistory: false, updatedAt: null });
    if (url.includes("/positions/8")) return ok({ itemId: 8, lastPage: 0, lastSpineItemIndex: 1, lastScrollPercent: 0, status: "inprogress", wantToRead: false, favorite: false, hiddenFromHistory: false, updatedAt: null });
    if (url.includes("/marks/items/")) return ok({ itemId: 7, wantToRead: true, favorite: false, status: "unread", rating: null, updatedAt: null, item: null });
    if (url.includes("/items/7/next") || url.includes("/items/7/prev")) return { ok: true, status: 204, headers: { get: () => null }, json: async () => null, text: async () => "" };
    if (url.includes("/items/7")) return ok(detail(7));
    if (url.includes("/items/8")) return ok(detail(8, { kind: "book", title: "A Wizard of Earthsea", extension: ".epub", fileName: "wizard.epub", pageCount: null }));
    if (url.includes("/epub/8/spine")) return ok({ id: 8, count: 2, fixedLayout: false, direction: "ltr", items: [{ index: 0, href: "ch1.xhtml", title: null }, { index: 1, href: "ch2.xhtml", title: null }] });
    if (url.includes("/epub/8/toc")) return ok({ id: 8, count: 2, entries: [{ label: "Warriors in the Mist", spineIndex: 0, anchor: null, depth: 0 }, { label: "The Shadow", spineIndex: 1, anchor: null, depth: 0 }] });
    if (url.includes("/epub/8/chapters/")) return ok(null, "<html><body><p>Only in silence the word.</p></body></html>");
    if (url.includes("/odata/catalog")) return ok([summary(7)]);
    if (url.includes("/browse/facets")) return ok({ collections: [], series: [], tags: [], authors: [], artists: [], events: [], franchises: [], publishers: [], decades: [] });
    if (url.includes("/library/comic/folders")) return ok([]);
    return { ok: false, status: 404, headers: { get: () => null }, json: async () => null, text: async () => "" };
  }));
}

function Probe() {
  const l = useLocation();
  return <div data-testid="loc">{l.pathname}{l.search}</div>;
}
function renderAt(url: string) {
  const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <QueryClientProvider client={qc}>
      <MemoryRouter initialEntries={[url]}>
        <Probe />
        <Route path="/books"><BooksPage userData={{ username: "reader", hasPassword: true, booksAccess: true, booksMaturityCeiling: 3, isAdmin: false } as never} /></Route>
      </MemoryRouter>
    </QueryClientProvider>,
  );
}

beforeEach(() => { window.localStorage.clear(); __resetMediaForTests(); mockFetch(); });
afterEach(() => { cleanup(); vi.unstubAllGlobals(); });
const LONG = { timeout: 15000 };

describe("Books/read — the reader route", () => {
  it("opens over the page it came from, keeps that page mounted underneath, and Close is one Back to the modal", async () => {
    renderAt("/books?item=7");
    const read = await screen.findByRole("button", { name: /Read now/ }, LONG);
    fireEvent.click(read);
    await waitFor(() => expect(screen.getByTestId("loc").textContent).toBe("/books/read/7"));
    expect(await screen.findByTestId("reader-canvas-root", {}, LONG)).toBeInTheDocument();
    expect(await screen.findByTestId("reader-badge")).toHaveTextContent("Page 1 of 24");
    const under = document.querySelector(".books-under") as HTMLElement;
    expect(under.style.visibility).toBe("hidden");
    expect(under.querySelector(".bx-host")).toBeInTheDocument(); // the browse is still mounted beneath
    expect(screen.queryByRole("dialog")).toBeNull(); // the item modal is not open over the reader

    // Keyboard: "m" raises the Command Deck with the book's title and the library pills; Escape closes it, then the reader.
    fireEvent.keyDown(window, { key: "m" });
    expect(await screen.findByRole("dialog", { name: "Reader controls" })).toBeInTheDocument();
    expect(screen.getByRole("heading", { level: 1 })).toHaveTextContent("Hellboy #7");
    expect(await screen.findByText("On your list")).toBeInTheDocument();
    expect(screen.getByText("Mark read")).toBeInTheDocument();
    fireEvent.keyDown(window, { key: "Escape" });
    await waitFor(() => expect(screen.queryByRole("dialog", { name: "Reader controls" })).toBeNull());
    fireEvent.keyDown(window, { key: "Escape" });
    await waitFor(() => expect(screen.getByTestId("loc").textContent).toBe("/books?item=7"));
    expect(screen.queryByTestId("reader-canvas-root")).toBeNull();
    expect((document.querySelector(".books-under") as HTMLElement).style.visibility).toBe("");
  }, 30000);

  it("a page turn writes the position after the debounce; a cold landing with no origin closes to the browse", async () => {
    renderAt("/books/read/7");
    expect(await screen.findByTestId("reader-canvas-root", {}, LONG)).toBeInTheDocument();
    await screen.findByTestId("reader-badge");
    fireEvent.keyDown(window, { key: "ArrowRight" });
    expect(screen.getByTestId("reader-badge")).toHaveTextContent("Page 2 of 24");
    await waitFor(() => expect(calls.some((c) => c.url.endsWith("/positions/7") && c.init?.method === "PUT" && String(c.init.body).includes("\"lastPage\":1"))).toBe(true), { timeout: 3000 });
    fireEvent.keyDown(window, { key: "Escape" });
    await waitFor(() => expect(screen.getByTestId("loc").textContent).toBe("/books"));
  }, 30000);

  it("an .epub opens the EPUB reader, resumes at the saved section, and lists the contents", async () => {
    renderAt("/books/read/8");
    expect(await screen.findByTestId("reader-epub-root", {}, LONG)).toBeInTheDocument();
    expect(await screen.findByTestId("epub-frame")).toBeInTheDocument();
    await waitFor(() => expect(screen.getByTestId("epub-pill")).toHaveTextContent("Section 2/2"));
    expect(calls.some((c) => c.url.endsWith("/epub/8/chapters/1"))).toBe(true);
    fireEvent.keyDown(window, { key: "t" });
    expect(await screen.findByTestId("epub-toc")).toBeInTheDocument();
    expect(screen.getAllByTestId("epub-toc-entry")).toHaveLength(2);
    fireEvent.click(screen.getByText("Warriors in the Mist"));
    await waitFor(() => expect(screen.getByTestId("epub-pill")).toHaveTextContent("Section 1/2"));
  }, 30000);
});
