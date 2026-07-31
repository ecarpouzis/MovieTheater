import { useEffect, useState } from "react";
import { MovieAPI } from "../../MovieAPI";

// The lobby now has TWO consumers of /API/Arcade/Filters — the navbar rail's dropdowns and the console
// carousel above the grid — and they want the identical response for the identical scope. Without this
// they'd each fire their own request on every filter change, doubling the facet queries (which count
// distinct cards across the whole catalog, so they are not cheap) for no new information.
//
// The cache is keyed by the facet scope and shares the IN-FLIGHT promise, not just the settled value,
// so two components mounting in the same tick make one request between them.
const cache = new Map();

/** The params that actually change what's available. Sort/paging don't, so they're excluded — the
 *  server strips them anyway, and including them here would miss the cache on every pager click. */
const FACET_PARAMS = ["system", "hideRegions", "maxPlayers", "variant", "genre", "search", "ra"];

function scopeOf(filters) {
  const scope = {};
  for (const k of FACET_PARAMS) scope[k] = filters?.[k] || "";
  return scope;
}

export function arcadeFilterKey(filters) {
  const scope = scopeOf(filters);
  return FACET_PARAMS.map((k) => scope[k]).join("|");
}

/**
 * Faceted counts for the current lobby scope: { total, systems[], regions[], genres[], ra, ... }.
 * Returns null until the first response lands. Never throws — a facet hiccup must not take the lobby
 * with it, so a failed fetch simply leaves the counts absent and the UI renders without them.
 */
export default function useArcadeFilters(filters) {
  const key = arcadeFilterKey(filters);
  const [facets, setFacets] = useState(() => cache.get(key)?.value ?? null);

  useEffect(() => {
    let alive = true;
    let entry = cache.get(key);
    if (!entry) {
      // The entry must exist before the promise chain can write into it, so it is created empty and
      // filled on settle. A FAILED fetch drops the entry entirely rather than caching the failure —
      // otherwise one flaky response would leave the lobby permanently facet-less.
      entry = { value: undefined, promise: null };
      entry.promise = MovieAPI.getArcadeFilters(scopeOf(filters))
        .then((r) => (r.ok ? r.json() : null))
        .then((value) => { if (value == null) cache.delete(key); else entry.value = value; return value; })
        .catch(() => { cache.delete(key); return null; });
      cache.set(key, entry);
    }

    // A settled entry paints immediately; a pending one resolves into this component too. Either way
    // the entry keeps its promise, so a component that mounts later reuses it instead of refetching.
    if (entry.value !== undefined) setFacets(entry.value);
    entry.promise.then((value) => { if (alive && value != null) setFacets(value); });

    return () => { alive = false; };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [key]);

  return facets;
}
