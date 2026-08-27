/**
 * `useCachedResource`'s contract for a resource TWO TREES read (R9 S2c): the same
 * stale-while-revalidate — the last payload renders instantly from localStorage, a fresh fetch
 * replaces it in the background and rewrites the cache, and a failed refresh over a warm cache keeps
 * the stale copy — but held in React Query, so the sider rail and the page share one fetch and one
 * in-memory copy instead of one each.
 *
 * Which one to use:
 *   `useCachedResource`        one consumer (a page, a modal). Per-mount state, no provider.
 *   `useSharedCachedResource`  two or more consumers in DIFFERENT trees (the sider rail + the page),
 *                              or anything that must patch the copy the others see (`setData`).
 *
 * Same rule as `useCachedResource`: USER-INDEPENDENT resources only. A per-user payload in
 * localStorage would show one user's data to the next login on a shared device.
 *
 * - `storageKey` is VERSIONED by convention; a seed that fails `parse` is treated as a cold cache.
 * - `initialDataUpdatedAt: 0` makes the seed stale at once, so the mount refetches in the background
 *   while the seed renders — that IS the stale-while-revalidate.
 * - `setData` patches the in-memory copy after an edit; the cache is only rewritten by the next
 *   successful fetch (the boardgames convention).
 */
import { useQuery, useQueryClient, type QueryKey } from "@tanstack/react-query";
import { useCallback } from "react";
import { readStored, writeStored } from "../utils/storage";

export const SHARED_CACHE_STALE_MS = 5 * 60 * 1000;

export interface SharedCachedResource<T> {
  data: T | undefined;
  /** Cold cache with the fetch in flight. */
  loading: boolean;
  /** The fetch failed AND there is nothing cached to show. */
  error: boolean;
  refresh: () => void;
  /** Patch the in-memory copy every consumer reads. */
  setData: (update: (prev: T | undefined) => T) => void;
  /** Changes whenever the payload does — a spec identity or a list key can ride it. */
  version: string;
}

export interface SharedCachedResourceOptions<T> {
  queryKey: QueryKey;
  /** localStorage key for the seed + the write-back; null = memory only. */
  storageKey: string | null;
  fetcher: (signal?: AbortSignal) => Promise<T>;
  /** Validate a parsed seed (and a parsed shape change); undefined = treat as a cold cache. */
  parse?: (raw: unknown) => T | undefined;
  enabled?: boolean;
  staleTime?: number;
}

export function readCachedJson<T>(key: string | null, parse?: (raw: unknown) => T | undefined): T | undefined {
  if (!key) return undefined;
  const raw = readStored(key);
  if (raw == null) return undefined;
  try {
    const parsed: unknown = JSON.parse(raw as string);
    return parse ? parse(parsed) : (parsed as T);
  } catch {
    return undefined;
  }
}

export function writeCachedJson(key: string | null, value: unknown): void {
  if (!key) return;
  try { writeStored(key, JSON.stringify(value)); } catch { /* payload too big — render-only */ }
}

export default function useSharedCachedResource<T>(opts: SharedCachedResourceOptions<T>): SharedCachedResource<T> {
  const { queryKey, storageKey, fetcher, parse, enabled = true, staleTime = SHARED_CACHE_STALE_MS } = opts;
  const client = useQueryClient();
  const query = useQuery<T>({
    queryKey,
    queryFn: async ({ signal }) => {
      const fresh = await fetcher(signal);
      writeCachedJson(storageKey, fresh);
      return fresh;
    },
    initialData: () => readCachedJson<T>(storageKey, parse),
    initialDataUpdatedAt: 0,
    staleTime,
    enabled,
  });
  const setData = useCallback((update: (prev: T | undefined) => T) => {
    client.setQueryData<T>(queryKey, (prev) => update(prev));
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [client, JSON.stringify(queryKey)]);
  const refresh = useCallback(() => {
    void client.refetchQueries({ queryKey });
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [client, JSON.stringify(queryKey)]);
  return {
    data: query.data,
    loading: enabled && query.data == null && query.isPending,
    error: query.data == null && query.isError,
    refresh,
    setData,
    version: String(query.dataUpdatedAt),
  };
}
