import { useCallback, useEffect, useRef, useState } from "react";

/**
 * A server-paged catalog as SPARSE SLOTS (ported from The Long Box's InfiniteScroller via the
 * arcade lobby, where this logic lived first — see that page's history for the war stories).
 *
 * The old shape — a dense array of whatever had been fetched, anchored at a start index — is what
 * makes a letter/page jump one-directional: the array BEGINS at the jump target, so there is
 * nothing above it, and scrolling up reveals nothing (arcade, reported 2026-08-13). Prepending is
 * the classic teleport. Here the whole result set is modelled as `total` fixed slots from the
 * first response: `pages[n]` holds item indices [n*pageSize, (n+1)*pageSize), anything unfetched
 * renders as a placeholder slot, the scrollbar is honest immediately, and a slot the user scrolls
 * into is fetched whether it is above them or below them. No array is ever prepended to.
 *
 * The band-fetch pump ships with it, and its three rules are the load-bearing part on a large
 * catalog: the want-list is REPLACED on every window move (never appended), a fetch only fires
 * once a page has stayed wanted for MIN_WANT_AGE (so a scrollbar sweep fires ZERO fetches), and
 * in-flight fetches for pages the window has left are aborted so their slot frees immediately.
 * The Long Box measured the alternative: "a halfway drag issued ~50 deep-$skip queries … no band
 * mounted 8s after landing."
 *
 * Contract:
 *   usePagedCatalog({ resetKey, pageSize, fetchPage })
 *   - resetKey: serialized query. Changing it drops everything and refetches from page 0.
 *   - fetchPage(skip, pageSize, signal) → Promise<{ items, totalCount } | null>. Return null for
 *     a failed request (renders as loadError, never as an empty catalog). Abort is never a failure.
 *     Read your filters from a ref inside fetchPage — the hook holds fetchPage itself in a ref, so
 *     a fresh closure each render is fine.
 *   - Returns { pages, total, loading, firstLoaded, loadError, itemAt, contentKey,
 *     notifyWindow(start, end), retry }.
 *     Wire notifyWindow to the grid window's [start, end): it wants the window's pages plus one
 *     either side (symmetric — the user can be travelling upward). contentKey feeds useGridWindow
 *     so placeholder→real replacements re-measure.
 */
const MAX_INFLIGHT = 4;
const MIN_WANT_AGE = 150;

export default function usePagedCatalog({ resetKey, pageSize, fetchPage, enabled = true }) {
  const [pages, setPages] = useState({});
  const [firstLoaded, setFirstLoaded] = useState(false); // has ANY page come back for this query
  const [total, setTotal] = useState(0);
  const [loading, setLoading] = useState(false); // the first page of a query (nothing to show yet)
  const [loadError, setLoadError] = useState(false); // a page request failed — not an empty catalog

  const epochRef = useRef(0);
  const pagesRef = useRef({});
  const wantRef = useRef([]);
  const wantAgeRef = useRef(new Map());
  const loadingPagesRef = useRef(new Set());
  const abortersRef = useRef(new Map());
  const inFlightRef = useRef(0);
  const pumpTimerRef = useRef(null);
  const fetchPageRef = useRef(fetchPage);
  fetchPageRef.current = fetchPage;
  const enabledRef = useRef(enabled);
  enabledRef.current = enabled;
  pagesRef.current = pages;

  const pump = useCallback(() => {
    let soonest = Infinity;
    while (inFlightRef.current < MAX_INFLIGHT) {
      const now = Date.now();
      const page = wantRef.current.find((b) => {
        if (loadingPagesRef.current.has(b) || pagesRef.current[b]) return false;
        const age = now - (wantAgeRef.current.get(b) ?? now);
        if (age >= MIN_WANT_AGE) return true;
        soonest = Math.min(soonest, MIN_WANT_AGE - age);
        return false;
      });
      if (page === undefined) break;
      wantRef.current = wantRef.current.filter((b) => b !== page);

      const epoch = epochRef.current;
      loadingPagesRef.current.add(page);
      inFlightRef.current += 1;
      const controller = new AbortController();
      abortersRef.current.set(page, controller);
      const first = Object.keys(pagesRef.current).length === 0;
      if (first) setLoading(true);

      Promise.resolve(fetchPageRef.current(page * pageSize, pageSize, controller.signal))
        .then((data) => {
          if (epochRef.current !== epoch) return; // a newer query owns the grid now
          // A page that didn't arrive is NOT an empty catalog. Falling through to an empty list
          // renders as "nothing matches" — the grid blaming the user's filters for a broken fetch.
          if (!data) { setLoadError(true); setFirstLoaded(true); return; }
          setLoadError(false);
          setTotal((prev) => (data.totalCount >= 0 ? data.totalCount : prev));
          setFirstLoaded(true);
          setPages((prev) => (prev[page] ? prev : { ...prev, [page]: data.items }));
        })
        .catch((err) => {
          if (controller.signal.aborted || err?.name === "AbortError") return; // deliberate — never a failure
          if (epochRef.current !== epoch) return;
          setLoadError(true);
          setFirstLoaded(true);
        })
        .finally(() => {
          if (abortersRef.current.get(page) === controller) abortersRef.current.delete(page);
          loadingPagesRef.current.delete(page);
          inFlightRef.current -= 1;
          if (epochRef.current === epoch) setLoading(false);
          pump();
        });
    }
    // Wants exist but none are old enough yet — come back when the youngest matures.
    if (soonest !== Infinity) {
      if (pumpTimerRef.current) clearTimeout(pumpTimerRef.current);
      pumpTimerRef.current = setTimeout(() => { pumpTimerRef.current = null; pump(); }, soonest + 10);
    }
  }, [pageSize]);

  /** Replace the want-list with the pages this window needs, and abort anything it has left. */
  const wantPages = useCallback((loPage, hiPage) => {
    const want = [];
    const now = Date.now();
    for (let p = loPage; p <= hiPage; p += 1) {
      if (!pagesRef.current[p] && !loadingPagesRef.current.has(p)) want.push(p);
    }
    wantRef.current = want;
    wantAgeRef.current.forEach((_, p) => {
      if ((p < loPage || p > hiPage) && !loadingPagesRef.current.has(p)) wantAgeRef.current.delete(p);
    });
    for (const p of want) if (!wantAgeRef.current.has(p)) wantAgeRef.current.set(p, now);
    abortersRef.current.forEach((a, p) => { if (p < loPage || p > hiPage) a.abort(); });
    pump();
  }, [pump]);

  const totalRefFor = useRef(0);
  totalRefFor.current = total;

  /** Fetch what the window is looking at, plus one page either side. */
  const notifyWindow = useCallback((start, end) => {
    const t = totalRefFor.current;
    if (!t) return;
    const lastPage = Math.max(0, Math.ceil(t / pageSize) - 1);
    const lo = Math.max(0, Math.floor(start / pageSize) - 1);
    const hi = Math.min(lastPage, Math.floor(Math.max(start, end - 1) / pageSize) + 1);
    wantPages(lo, hi);
  }, [pageSize, wantPages]);

  /** Drop everything and start this query again from page 0. */
  const resetQuery = useCallback(() => {
    epochRef.current += 1;
    abortersRef.current.forEach((a) => a.abort());
    abortersRef.current.clear();
    loadingPagesRef.current.clear();
    wantRef.current = [];
    wantAgeRef.current.clear();
    inFlightRef.current = 0;
    if (pumpTimerRef.current) { clearTimeout(pumpTimerRef.current); pumpTimerRef.current = null; }
    setPages({});
    setFirstLoaded(false);
    setLoadError(false);
    pagesRef.current = {};
    // Disabled (a consumer whose current mode doesn't use the paged catalog): stay empty and fetch
    // nothing until enabled flips back with a live query.
    if (!enabledRef.current) { setTotal(0); return; }
    wantRef.current = [0];
    wantAgeRef.current.set(0, 0); // page 0 is wanted NOW, not after the age gate
    pump();
  }, [pump]);

  // Reset + fetch from the top whenever the query changes (or the catalog is switched on/off).
  useEffect(() => {
    resetQuery();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [resetKey, enabled]);

  useEffect(() => () => {
    // eslint-disable-next-line react-hooks/exhaustive-deps
    abortersRef.current.forEach((a) => a.abort());
    // eslint-disable-next-line react-hooks/exhaustive-deps
    if (pumpTimerRef.current) clearTimeout(pumpTimerRef.current);
  }, []);

  /** The catalog item at an absolute index, or undefined while its page is still on the wire. */
  const itemAt = useCallback(
    (i) => pages[Math.floor(i / pageSize)]?.[i % pageSize],
    [pages, pageSize],
  );

  /** Map every loaded item in place (an edit landing from a detail modal). Slots keep their
   *  positions; unloaded pages are untouched. */
  const mapItems = useCallback((fn) => {
    setPages((prev) => {
      const next = {};
      for (const k of Object.keys(prev)) next[k] = prev[k].map(fn);
      return next;
    });
  }, []);

  return {
    pages,
    total,
    mapItems,
    loading,
    firstLoaded,
    loadError,
    itemAt,
    /** Feed to useGridWindow's contentKey so placeholder→real replacements re-measure. */
    contentKey: Object.keys(pages).length,
    notifyWindow,
    retry: resetQuery,
  };
}
