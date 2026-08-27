import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { cleanup, render, waitFor } from "@testing-library/react";
import { useState } from "react";
import { MemoryRouter, Route } from "react-router-dom";
import { __resetMediaForTests } from "./booksMedia";
import BooksPage from "./BooksPage";

/**
 * R9 S4 — the Books item and series modals ARE the site's full-page sheet, at every size.
 *
 * They used to be the standalone's `.cm` card: `min(1240px, 94vw)` wide, its own 16px radius, its
 * own drop shadow, its own pop animation and its own ✕, floating over a transparent antd container
 * — "a critical failure … adapting the smaller modals Longbox had" (Eric, canvas 2026-08-27). What
 * is pinned here is what "never card mode" MEANS in markup, at a phone AND a desktop viewport,
 * since the difference between the two used to be a media query on a card:
 *
 *   • the wrap carries `sheet-modal` (the shell) — books-modal.css then repeats the shell's sheet
 *     block unconditionally, the hero-dialog rule MovieModal.css and GameModal.css follow;
 *   • `sheet-modal--themed`, because the sheet paints itself from the section tokens;
 *   • the ✕ is the SHELL's one chip (`.ant-modal-close`), not a hand-rolled `.cm-close`;
 *   • the wrap sits at 1500 — above the phone top bar (1300) and the rail sheet (1350).
 */

global.IS_REACT_ACT_ENVIRONMENT = true;
(global as unknown as { ResizeObserver: unknown }).ResizeObserver = (global as unknown as { ResizeObserver?: unknown }).ResizeObserver || class { observe() {} unobserve() {} disconnect() {} };
(global as unknown as { IntersectionObserver: unknown }).IntersectionObserver = (global as unknown as { IntersectionObserver?: unknown }).IntersectionObserver || class { observe() {} unobserve() {} disconnect() {} takeRecords() { return []; } };

/** Viewport-aware matchMedia, so a phone run really does answer `(max-width: …)` differently. */
function setViewport(width: number) {
  (window as unknown as { innerWidth: number }).innerWidth = width;
  (global as unknown as { matchMedia: unknown }).matchMedia = (q: string) => {
    const m = /max-width:\s*(\d+)px/.exec(q);
    const min = /min-width:\s*(\d+)px/.exec(q);
    const matches = m ? width <= Number(m[1]) : min ? width >= Number(min[1]) : false;
    return { matches, media: q, onchange: null, addListener: vi.fn(), removeListener: vi.fn(), addEventListener: vi.fn(), removeEventListener: vi.fn(), dispatchEvent: vi.fn() };
  };
}

const summary = (id: number, extra: Record<string, unknown> = {}) => ({
  id, kind: "comic", title: `Hellboy #${id}`, seriesId: 9, series: "Hellboy", seriesIssueCount: 5, seriesYearStart: 1994, seriesYearEnd: 2019, seriesIsOngoing: false,
  franchise: null, isSingleIssueSeries: false, seriesRatingResolved: 82, publisher: "Dark Horse", year: 1994, month: 3, datePrecision: "Month", rating: 84,
  synopsisSource: "Cv", creatorsCsv: "Mike Mignola", tagsCsv: "genre:Horror", coverAspect: 0.66, fileName: `hb${id}.cbz`, extension: ".cbz", fileSize: 1, pageCount: 24,
  indexedAt: null, folderId: 3, topFolderId: 2, isExcluded: false, ...extra,
});

type MockResponse = { ok: boolean; status: number; headers: { get: (k: string) => string | null }; json: () => Promise<unknown>; text: () => Promise<string> };
function mockFetch() {
  vi.stubGlobal("fetch", vi.fn(async (url: string) => {
    const ok = (body: unknown, headers: Record<string, string> = {}): MockResponse => ({ ok: true, status: 200, headers: { get: (k: string) => headers[k] ?? null }, json: async () => body, text: async () => "" });
    if (url.includes("/media-token")) return ok({ configured: false });
    if (url.includes("/marks/items/")) return ok({ itemId: 7, wantToRead: false, favorite: false, status: "unread", rating: null, updatedAt: null, item: null });
    if (url.includes("/browse/series/9/run")) return ok({ seriesId: 9, total: 1, items: [{ item: summary(7), readingOrder: { readNumber: 1, readIndex: 0 }, collection: null }] });
    if (url.includes("/browse/series/9/library-rating")) return ok({ rating: 88, note: null });
    if (url.includes("/shelf/series/9/progress")) return ok({ seriesId: 9, total: 1, finishedCount: 0, finishedIds: [], inProgressIds: [] });
    if (url.includes("/browse/groups?")) return ok({ totalGroups: 1, groups: [{ key: "9", label: "Hellboy", totalItems: 1, items: [summary(7)], userMeta: null, groupDetail: null, renderTotal: null }] });
    if (url.includes("/browse/facets")) return ok({ collections: [], series: [], tags: [], authors: [], artists: [], events: [], franchises: [], publishers: [], decades: [] });
    if (url.includes("/items/7")) return ok({ summary: summary(7), relativePath: "x", folderName: null, folderPath: null, topFolderId: null, topFolderName: null, hasThumbnail: false, embedded: null, parsed: null, book: null, series: null, insight: null, seriesInsight: null, cvVolume: null, cvIssue: null, locg: null, mu: null, external: null, readingOrder: null, collection: null, credits: [], tags: [], seriesTags: [], thumbUrl: null, downloadUrl: null, pagesUrlTemplate: null });
    if (url.includes("/odata/catalog")) return ok([summary(7)], { "X-Total-Count": "1" });
    return { ok: false, status: 404, headers: { get: () => null }, json: async () => null, text: async () => "" } as MockResponse;
  }));
}

const member = { username: "reader", hasPassword: true, booksAccess: true, booksMaturityCeiling: 3, isAdmin: false };

function Host({ initial }: { initial: Record<string, unknown> }) {
  const [ud, setUd] = useState<Record<string, unknown>>(initial);
  return <BooksPage userData={ud as never} setUserData={setUd as never} />;
}
function renderAt(url: string) {
  const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <QueryClientProvider client={qc}>
      <MemoryRouter initialEntries={[url]}>
        <Route path="/books"><Host initial={member} /></Route>
      </MemoryRouter>
    </QueryClientProvider>,
  );
}

beforeEach(() => { window.localStorage.clear(); window.sessionStorage.clear(); __resetMediaForTests(); mockFetch(); });
afterEach(() => { cleanup(); vi.unstubAllGlobals(); });

/** Everything "the sheet, never a card" means in markup. */
async function expectSheet(contentSelector: string) {
  await waitFor(() => expect(document.querySelector(contentSelector)).not.toBeNull(), { timeout: 15000 });
  const wrap = document.querySelector<HTMLElement>(".ant-modal-wrap");
  expect(wrap).not.toBeNull();
  expect(wrap!.classList.contains("sheet-modal")).toBe(true);
  expect(wrap!.classList.contains("sheet-modal--themed")).toBe(true);
  expect(wrap!.classList.contains("books-modal")).toBe(true);
  expect(wrap!.style.zIndex).toBe("1500");
  // The section skin still rides styles.wrapper (it must not go back to wrapProps.style).
  expect(wrap!.style.getPropertyValue("--books-bg")).not.toBe("");
  // One ✕, the shell's. The standalone's own close button is gone.
  expect(document.querySelector(".cm-close")).toBeNull();
  expect(document.querySelectorAll(".ant-modal-close").length).toBe(1);
}

describe("Books modals — the site's full-page sheet at every size", () => {
  for (const [label, width] of [["desktop", 1440], ["phone", 390]] as const) {
    it(`?item= opens the item on the sheet (${label})`, async () => {
      setViewport(width);
      renderAt("/books?item=7");
      await expectSheet(".cm.cm--book");
      // Scoped to the sheet on purpose: the browse grid behind it renders the same title.
      await waitFor(() => expect(document.querySelector(".cm .cm-title")?.textContent).toBe("Hellboy #7"), { timeout: 15000 });
    }, 25000);

    it(`?series= opens the series on the sheet (${label})`, async () => {
      setViewport(width);
      renderAt("/books?series=9");
      await expectSheet(".cm.cm--series");
    }, 25000);
  }
});
