import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { act, renderHook, waitFor } from "@testing-library/react";
import type { ReactNode } from "react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import useSharedCachedResource from "./useSharedCachedResource";

// `useCachedResource`'s contract, held in React Query so two trees share one fetch: the localStorage
// seed renders at once, the fetch replaces it and rewrites the cache, a failed refresh over a warm
// cache keeps the stale copy, and `setData` patches the copy EVERY consumer reads.

const KEY = "test.shared.v1";
const wrapperFor = (client: QueryClient) => ({ children }: { children: ReactNode }) => (
  <QueryClientProvider client={client}>{children}</QueryClientProvider>
);

function newClient() {
  return new QueryClient({ defaultOptions: { queries: { retry: false } } });
}

beforeEach(() => { window.localStorage.clear(); });
afterEach(() => { vi.restoreAllMocks(); });

describe("useSharedCachedResource", () => {
  it("renders the localStorage seed first, then the fresh payload, and rewrites the cache", async () => {
    window.localStorage.setItem(KEY, JSON.stringify(["cached"]));
    const fetcher = vi.fn(async () => ["fresh"]);
    const { result } = renderHook(
      () => useSharedCachedResource<string[]>({ queryKey: ["t", 1], storageKey: KEY, fetcher }),
      { wrapper: wrapperFor(newClient()) },
    );
    expect(result.current.data).toEqual(["cached"]);
    expect(result.current.loading).toBe(false);
    await waitFor(() => expect(result.current.data).toEqual(["fresh"]));
    expect(window.localStorage.getItem(KEY)).toBe(JSON.stringify(["fresh"]));
  });

  it("a cold cache loads, and a failure with nothing cached is an error", async () => {
    const fetcher = vi.fn(async () => { throw new Error("nope"); });
    const { result } = renderHook(
      () => useSharedCachedResource<string[]>({ queryKey: ["t", 2], storageKey: KEY, fetcher }),
      { wrapper: wrapperFor(newClient()) },
    );
    expect(result.current.loading).toBe(true);
    await waitFor(() => expect(result.current.error).toBe(true));
    expect(result.current.data).toBeUndefined();
  });

  it("a failed refresh over a warm cache keeps the stale copy and is not an error", async () => {
    window.localStorage.setItem(KEY, JSON.stringify(["cached"]));
    const fetcher = vi.fn(async () => { throw new Error("nope"); });
    const { result } = renderHook(
      () => useSharedCachedResource<string[]>({ queryKey: ["t", 3], storageKey: KEY, fetcher }),
      { wrapper: wrapperFor(newClient()) },
    );
    await waitFor(() => expect(fetcher).toHaveBeenCalled());
    expect(result.current.data).toEqual(["cached"]);
    expect(result.current.error).toBe(false);
  });

  it("a seed of the wrong shape reads as a cold cache", async () => {
    window.localStorage.setItem(KEY, JSON.stringify({ old: "shape" }));
    const fetcher = vi.fn(async () => ["fresh"]);
    const { result } = renderHook(
      () => useSharedCachedResource<string[]>({
        queryKey: ["t", 4], storageKey: KEY, fetcher,
        parse: (raw) => (Array.isArray(raw) ? (raw as string[]) : undefined),
      }),
      { wrapper: wrapperFor(newClient()) },
    );
    expect(result.current.data).toBeUndefined();
    await waitFor(() => expect(result.current.data).toEqual(["fresh"]));
  });

  it("is ONE fetch and one copy for two consumers, and setData patches both", async () => {
    const client = newClient();
    const wrapper = wrapperFor(client);
    const fetcher = vi.fn(async () => ["a"]);
    const opts = { queryKey: ["t", 5], storageKey: null, fetcher };
    const one = renderHook(() => useSharedCachedResource<string[]>(opts), { wrapper });
    const two = renderHook(() => useSharedCachedResource<string[]>(opts), { wrapper });
    await waitFor(() => expect(two.result.current.data).toEqual(["a"]));
    expect(fetcher).toHaveBeenCalledTimes(1);
    act(() => { one.result.current.setData((prev) => [...(prev ?? []), "b"]); });
    await waitFor(() => expect(two.result.current.data).toEqual(["a", "b"]));
    // A patch is in-memory only — the cache is rewritten by the next successful fetch.
    expect(window.localStorage.getItem("t5")).toBeNull();
  });

  it("does not fetch while disabled", () => {
    const fetcher = vi.fn(async () => ["a"]);
    const { result } = renderHook(
      () => useSharedCachedResource<string[]>({ queryKey: ["t", 6], storageKey: null, fetcher, enabled: false }),
      { wrapper: wrapperFor(newClient()) },
    );
    expect(fetcher).not.toHaveBeenCalled();
    expect(result.current.loading).toBe(false);
  });
});
