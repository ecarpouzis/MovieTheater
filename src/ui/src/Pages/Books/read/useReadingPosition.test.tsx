import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { act, renderHook, waitFor } from "@testing-library/react";
import type { ReactNode } from "react";
import useReadingPosition, { EPUB_SAVE_MS, PAGE_SAVE_MS } from "./useReadingPosition";

const calls: { url: string; method?: string; body?: string }[] = [];
let stored = { itemId: 7, lastPage: 5, lastSpineItemIndex: null as number | null, lastScrollPercent: null as number | null, status: "inprogress", wantToRead: false, favorite: false, hiddenFromHistory: false, updatedAt: null };

function mockFetch() {
  calls.length = 0;
  vi.stubGlobal("fetch", vi.fn(async (url: string, init?: RequestInit) => {
    calls.push({ url, method: init?.method, body: init?.body as string | undefined });
    if (init?.method === "PUT") {
      const b = JSON.parse(String(init.body));
      if (b.lastPage === -1) stored = { ...stored, lastPage: 23, status: "finished" };
      else if (b.lastPage != null) stored = { ...stored, lastPage: b.lastPage, status: b.lastPage === 0 ? "unread" : "inprogress" };
      else stored = { ...stored, lastSpineItemIndex: b.lastSpineItemIndex, lastScrollPercent: b.lastScrollPercent, status: "inprogress" };
    }
    if (init?.method === "DELETE") { stored = { ...stored, lastPage: 0, status: "unread" }; return { ok: true, status: 204, headers: { get: () => null }, json: async () => null }; }
    return { ok: true, status: 200, headers: { get: () => null }, json: async () => stored };
  }));
}

function wrapper() {
  const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return ({ children }: { children: ReactNode }) => <QueryClientProvider client={qc}>{children}</QueryClientProvider>;
}

beforeEach(() => { stored = { ...stored, lastPage: 5, status: "inprogress", lastSpineItemIndex: null, lastScrollPercent: null }; mockFetch(); });
afterEach(() => { vi.useRealTimers(); vi.unstubAllGlobals(); });

describe("Books/read/useReadingPosition — the ONE progress API from the reader's side", () => {
  it("resumes ONCE from the saved page, debounces a turn by 220 ms, dedupes the saved page, and never downgrades after Mark read", async () => {
    const { result, unmount } = renderHook(() => useReadingPosition(7, 24), { wrapper: wrapper() });
    await waitFor(() => expect(result.current.resume).not.toBeNull());
    expect(result.current.resume).toMatchObject({ page: 5, status: "inprogress" });

    vi.useFakeTimers();
    act(() => { result.current.savePage(6); result.current.savePage(7); });
    expect(calls.filter((c) => c.method === "PUT")).toHaveLength(0);
    await act(async () => { await vi.advanceTimersByTimeAsync(PAGE_SAVE_MS + 5); });
    const puts = () => calls.filter((c) => c.method === "PUT");
    expect(puts()).toHaveLength(1);
    expect(puts()[0].url).toBe("/API/Books/positions/7");
    expect(JSON.parse(puts()[0].body!)).toEqual({ lastPage: 7 });

    // The same page again is not written.
    act(() => { result.current.savePage(7); });
    await act(async () => { await vi.advanceTimersByTimeAsync(PAGE_SAVE_MS + 5); });
    expect(puts()).toHaveLength(1);

    // Mark read is lastPage -1; a later page turn does not downgrade the book.
    await act(async () => { await result.current.markFinished(); });
    expect(JSON.parse(puts()[1].body!)).toEqual({ lastPage: -1 });
    act(() => { result.current.savePage(9); });
    await act(async () => { await vi.advanceTimersByTimeAsync(PAGE_SAVE_MS + 5); });
    expect(puts()).toHaveLength(2);
    expect(result.current.isFinished).toBe(true);

    // The resume point did not move when the position refetched after the mark.
    expect(result.current.resume).toMatchObject({ page: 5 });
    unmount();
  });

  it("an EPUB spot is debounced by 600 ms and deduped to the millifraction; undo resets", async () => {
    const { result } = renderHook(() => useReadingPosition(7, null), { wrapper: wrapper() });
    await waitFor(() => expect(result.current.resume).not.toBeNull());
    vi.useFakeTimers();
    act(() => { result.current.saveEpub(2, 0.25); });
    await act(async () => { await vi.advanceTimersByTimeAsync(EPUB_SAVE_MS + 5); });
    const puts = () => calls.filter((c) => c.method === "PUT");
    expect(puts()).toHaveLength(1);
    expect(JSON.parse(puts()[0].body!)).toEqual({ lastSpineItemIndex: 2, lastScrollPercent: 0.25 });
    act(() => { result.current.saveEpub(2, 0.2504); });
    await act(async () => { await vi.advanceTimersByTimeAsync(EPUB_SAVE_MS + 5); });
    expect(puts()).toHaveLength(1);
    await act(async () => { await result.current.reset(); });
    expect(calls.some((c) => c.method === "DELETE" && c.url === "/API/Books/positions/7")).toBe(true);
  });
});
