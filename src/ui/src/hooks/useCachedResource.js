import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { readStored, writeStored } from "../utils/storage";

/**
 * Stale-while-revalidate for a whole-catalog fetch — the boardgames pattern
 * (localStorage "boardgames_v1"), generalized: the last successful payload renders INSTANTLY from
 * cache (no spinner on a revisit), a fresh fetch replaces it in the background and rewrites the
 * cache, and a FAILED refresh with a warm cache keeps showing the stale copy rather than an error.
 * `loading` is true only on a cold cache with the fetch in flight; `error` only when the fetch
 * failed AND there is nothing cached to show.
 *
 * Only for USER-INDEPENDENT resources (the boardgame catalog, the music library lists, the channel
 * lineup, the arcade renderer map). A per-user resource cached in localStorage would show one
 * user's data to the next login on a shared device — recently-played and playlists stay uncached
 * on purpose.
 *
 * - key: localStorage key, VERSIONED by convention (bump the suffix when the payload shape
 *   changes; the parse failure path treats old shapes as a cold cache).
 * - fetcher(signal): resolves the payload, or null for a failed request. Aborts are swallowed.
 * - setData is exposed so a page can patch the in-memory copy after an edit (the cache is only
 *   rewritten by the next successful fetch — the boardgames convention).
 */
export default function useCachedResource(key, fetcher, { enabled = true } = {}) {
  const cached = useMemo(() => {
    const raw = readStored(key);
    if (raw == null) return null;
    try { return JSON.parse(raw); } catch { return null; }
  }, [key]);

  const [data, setData] = useState(cached);
  const [loading, setLoading] = useState(enabled && cached == null);
  const [error, setError] = useState(false);
  const [nonce, setNonce] = useState(0);
  const fetcherRef = useRef(fetcher);
  fetcherRef.current = fetcher;
  const dataRef = useRef(data);
  dataRef.current = data;

  // A key change is a different resource: re-seed from ITS cache.
  useEffect(() => {
    setData(cached);
    setError(false);
    setLoading(enabled && cached == null);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [key]);

  useEffect(() => {
    if (!enabled) return undefined;
    const controller = new AbortController();
    Promise.resolve(fetcherRef.current(controller.signal))
      .then((fresh) => {
        if (controller.signal.aborted) return;
        if (fresh == null) {
          // Failed: a warm cache keeps showing; only a cold one surfaces the error.
          if (dataRef.current == null) setError(true);
          setLoading(false);
          return;
        }
        setData(fresh);
        setError(false);
        setLoading(false);
        try { writeStored(key, JSON.stringify(fresh)); } catch { /* payload too big — render-only */ }
      })
      .catch((err) => {
        if (controller.signal.aborted || err?.name === "AbortError") return;
        if (dataRef.current == null) setError(true);
        setLoading(false);
      });
    return () => controller.abort();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [key, enabled, nonce]);

  const refresh = useCallback(() => {
    setError(false);
    if (dataRef.current == null) setLoading(true);
    setNonce((n) => n + 1);
  }, []);

  return { data, setData, loading, error, refresh };
}
