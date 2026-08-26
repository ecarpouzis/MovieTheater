import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { cleanup, fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { message } from "antd";
import { MemoryRouter, Route, useLocation } from "react-router-dom";
import { __resetMediaForTests } from "../booksMedia";
import BooksPage from "../BooksPage";

// The admin route through the section root: the gate (admins only), the tab in the URL, and three
// tabs end to end over the R6 admin envelopes mocked at fetch — Overview (counts, stale registry,
// Rebuild → 202), Library (roots, Preview → counts, Apply → the card follows the job) and Config
// (the allow-list, a write). SSE is stubbed to a no-op EventSource; the poll carries the status.

global.IS_REACT_ACT_ENVIRONMENT = true;
(global as unknown as { matchMedia: unknown }).matchMedia = (global as unknown as { matchMedia?: unknown }).matchMedia || ((q: string) => ({
  matches: false, media: q, onchange: null, addListener: vi.fn(), removeListener: vi.fn(), addEventListener: vi.fn(), removeEventListener: vi.fn(), dispatchEvent: vi.fn(),
}));
(global as unknown as { ResizeObserver: unknown }).ResizeObserver = (global as unknown as { ResizeObserver?: unknown }).ResizeObserver || class { observe() {} unobserve() {} disconnect() {} };
(global as unknown as { IntersectionObserver: unknown }).IntersectionObserver = (global as unknown as { IntersectionObserver?: unknown }).IntersectionObserver || class { observe() {} unobserve() {} disconnect() {} takeRecords() { return []; } };
class NoopEventSource { onopen = null; onerror = null; addEventListener() {} close() {} }
vi.stubGlobal("EventSource", NoopEventSource);

const job = (kind: string, over: Record<string, unknown> = {}) => ({ kind, state: "idle", processed: 0, remaining: 0, nextCursor: null, failed: 0, startedAt: null, finishedAt: null, error: null, lastLine: null, batches: 0, ...over });

const calls: { url: string; init?: RequestInit }[] = [];
let scanJob = job("scan");
function mockFetch() {
  calls.length = 0;
  scanJob = job("scan");
  vi.stubGlobal("fetch", vi.fn(async (url: string, init?: RequestInit) => {
    calls.push({ url, init });
    const ok = (body: unknown, status = 200) => ({ ok: true, status, headers: { get: () => null }, json: async () => body, text: async () => "" });
    const bad = (status: number, error: string) => ({ ok: false, status, headers: { get: () => null }, json: async () => ({ error }), text: async () => "" });
    if (url.includes("/media-token")) return ok({ configured: false });
    if (url.endsWith("/admin/info")) return ok({
      catalog: { roots: 2, folders: 400, items: 141000, comics: 119000, books: 22000, excluded: 12, broken: 3, series: 19000, publishers: 900 },
      derived: { readingOrder: 1, collectionNodes: 1, collectedEditionSpans: 1, libraryRatings: 1, itemTags: 1, seriesTags: 1 },
      links: { seriesKeyLinks: 5, itemProviderLinks: 5, pending: 4, multiple: 2 }, dedupGroups: 10, openDedupGroups: 7,
      lastScan: { id: 1, rootId: null, kind: "full", startedAt: "2026-08-26T01:00:00Z", finishedAt: "2026-08-26T01:20:00Z", itemsSeen: 141000, added: 5, changed: 2, removed: 1, error: null },
      host: { cacheDir: true, mediaPlane: true, settingsOverlay: "D:\\books\\books.settings.json", comicVineConfigured: false },
      jobs: [job("thumbnails", { state: "done", processed: 500, batches: 5, finishedAt: "2026-08-26T02:00:00Z" })],
    });
    if (url.endsWith("/admin/derived")) return ok([
      { name: "Series", rebuildJob: "books-resolve --series", lastRebuiltAt: "2026-08-25T00:00:00Z", rowCount: 19000, storedFingerprint: "a", currentFingerprint: "b", stale: true },
      { name: "ReadingOrderEntry", rebuildJob: "books-reading-order", lastRebuiltAt: "2026-08-25T00:00:00Z", rowCount: 119000, storedFingerprint: "a", currentFingerprint: "a", stale: false },
    ]);
    if (url.includes("/admin/recompute/series")) return ok({ job: job("recompute:series", { state: "running", processed: 100, remaining: 900 }), statusUrl: "/admin/jobs/status?kind=recompute:series" }, 202);
    if (url.includes("/admin/jobs/status?kind=scan")) return ok(scanJob);
    if (url.includes("/admin/jobs/status?kind=")) return bad(404, "not run");
    if (url.endsWith("/admin/roots")) return ok([{ id: 1, path: "L:\\4 - Comics", kind: "Comic", isCalibre: false, enabled: true, reachable: true }, { id: 2, path: "L:\\5 - Books", kind: "Book", isCalibre: true, enabled: true, reachable: false }]);
    if (url.includes("/admin/scan/start") && !url.includes("apply=true")) return ok({ dryRun: true, preview: { wouldAdd: 12, wouldChange: 3, wouldRemove: 1, folders: 400, files: 141000 } });
    if (url.includes("/admin/scan/start") && url.includes("apply=true")) { scanJob = job("scan", { state: "running", processed: 1000, remaining: 140000, lastLine: "{ processed: 1000 }  [folders]" }); return ok({ job: scanJob, statusUrl: "/admin/jobs/status?kind=scan" }, 202); }
    if (url.endsWith("/admin/scan/status")) return ok({ job: scanJob, phase: { phase: scanJob.state === "running" ? "folders" : "done", processed: 0, remaining: 0, nextCursor: null, added: 0, changed: 0, removed: 0, failed: 0 } });
    if (url.endsWith("/admin/thumbnails/status")) return ok({ job: null, cursor: null, processed: 0, generated: 0, skipped: 0, failed: 0, remaining: 40 });
    if (url.includes("/admin/broken")) return ok({ totalCount: 1, skip: 0, top: 50, items: [{ id: 77, path: "L:\\x\\bad.cbz", fileName: "bad.cbz", isBroken: true, brokenReason: "missing", thumbnailError: null, brokenCheckedAt: null, thumbnailCheckedAt: null }] });
    if (url.endsWith("/admin/config") && init?.method === "PUT") return ok({ ComicVineApiKey: "(set)", ThumbnailQuality: 85, PageJpegQuality: null, ArchiveCacheGb: null });
    if (url.endsWith("/admin/config")) return ok({ path: "D:\\books\\books.settings.json", writable: true, keys: [
      { name: "ComicVineApiKey", kind: "Secret", min: null, max: null, description: "The ComicVine API key." },
      { name: "ThumbnailQuality", kind: "Int", min: 40, max: 100, description: "WebP quality." },
    ], values: { ComicVineApiKey: null, ThumbnailQuality: 80 } });
    return bad(404, "no such route");
  }));
}

function Probe() { const l = useLocation(); return <div data-testid="loc">{l.pathname}{l.search}</div>; }
function renderAt(url: string, isAdmin = true) {
  const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <QueryClientProvider client={qc}>
      <MemoryRouter initialEntries={[url]}>
        <Probe />
        <Route path="/books"><BooksPage userData={{ username: "eric", hasPassword: true, booksAccess: true, booksMaturityCeiling: 3, isAdmin } as never} /></Route>
      </MemoryRouter>
    </QueryClientProvider>,
  );
}

beforeEach(() => { window.localStorage.clear(); __resetMediaForTests(); mockFetch(); });
// antd's toasts and motions keep their own timers: React's "not wrapped in act" warnings from their
// late updates are noise here (nothing is asserted on them), and a log that lands as the worker
// tears down is reported as an error — so those are dropped, the toasts destroyed, and the log
// queue given a tick to flush.
// The filter stays for the file's whole life (each file runs in its own environment): a timer that
// fires after afterAll would otherwise log into the worker's teardown.
const realError = console.error;
console.error = (...args: unknown[]) => { if (typeof args[0] === "string" && args[0].includes("not wrapped in act")) return; realError(...args); };
afterEach(() => { message.destroy(); cleanup(); });
const LONG = { timeout: 15000 };

describe("Books/admin — the operator's tabs", () => {
  it("a non-admin is sent back to the browse", async () => {
    renderAt("/books/admin", false);
    await waitFor(() => expect(screen.getByTestId("loc").textContent).toBe("/books"));
  });

  it("Overview: the counts, the stale registry row with Rebuild → 202, and the jobs table", async () => {
    renderAt("/books/admin");
    expect(await screen.findByRole("heading", { level: 1, name: "Admin" }, LONG)).toBeInTheDocument();
    expect(await screen.findByText("141,000", {}, LONG)).toBeInTheDocument();
    const stale = await screen.findByText("stale");
    const row = stale.closest("tr")!;
    expect(within(row).getByText("Series")).toBeInTheDocument();
    fireEvent.click(within(row).getByRole("button", { name: "Rebuild" }));
    await waitFor(() => expect(calls.some((c) => c.url.endsWith("/admin/recompute/series") && c.init?.method === "POST")).toBe(true));
    expect(await screen.findByText(/Rebuild series: started/)).toBeInTheDocument();
    expect(screen.getByText("thumbnails", { selector: "code" })).toBeInTheDocument(); // the jobs table
  }, 30000);

  it("Library: ?tab=library lists the roots (unreachable flagged), Preview shows the counts, Apply starts the scan and the card follows it", async () => {
    renderAt("/books/admin?tab=library");
    expect(await screen.findByText("unreachable", {}, LONG)).toBeInTheDocument();
    fireEvent.click(screen.getByRole("button", { name: "Preview" }));
    expect(await screen.findByText(/would add 12, change 3, remove 1/)).toBeInTheDocument();
    expect(calls.filter((c) => c.url.includes("/admin/scan/start")).every((c) => !c.url.includes("apply=true"))).toBe(true);
    fireEvent.click(screen.getByText("Apply scan").closest("button")!);
    await waitFor(() => expect(calls.some((c) => c.url.includes("/admin/scan/start?") && c.url.includes("apply=true"))).toBe(true));
    const card = document.querySelector('[data-job="scan"]') as HTMLElement;
    await waitFor(() => expect(within(card).getByText("running")).toBeInTheDocument());
    expect(within(card).getByText(/1,000 processed/)).toBeInTheDocument();
    expect(within(card).getByRole("button", { name: "Stop" })).toBeInTheDocument();
    expect(within(card).getByText("Apply scan").closest("button")).toBeDisabled();
    expect(await screen.findByText("bad.cbz")).toBeInTheDocument(); // the broken list
  }, 30000);

  it("Config: the allow-list renders with the secret masked, and Save writes only the changed key", async () => {
    renderAt("/books/admin?tab=config");
    expect(await screen.findByText("ThumbnailQuality", { selector: "code" }, LONG)).toBeInTheDocument();
    expect(screen.getByPlaceholderText("not set")).toBeInTheDocument();
    const num = screen.getByRole("spinbutton");
    fireEvent.change(num, { target: { value: "85" } });
    fireEvent.click(screen.getByRole("button", { name: "Save" }));
    await waitFor(() => expect(calls.some((c) => c.url.endsWith("/admin/config") && c.init?.method === "PUT")).toBe(true));
    const put = calls.find((c) => c.url.endsWith("/admin/config") && c.init?.method === "PUT")!;
    expect(JSON.parse(String(put.init?.body))).toEqual({ ThumbnailQuality: 85 });
    expect(await screen.findByText("Settings written.")).toBeInTheDocument();
  }, 30000);
});
