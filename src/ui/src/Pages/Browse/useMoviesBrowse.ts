/**
 * What the Movies rail surfaces share: the result count for a facet state (one `pageSize=1` request
 * per state, held five minutes — the sider rail and the page read the same query) and the spec for
 * the viewer, built from the URL's Type scope (the counts describe the scope).
 */
import { useMemo } from "react";
import { useLocation } from "react-router-dom";
import type { FacetState } from "../../catalog/rail/facetSpec";
import { facetStateKey } from "../../catalog/rail/facetUrl";
import useResultCount from "../../catalog/rail/useResultCount";
import { moviesFacetSpec, moviesFilterParams, typesFromSearch, typesOf } from "./moviesFacetSpec";

/** The open detail modal (`?title=<kind>:<id>`) — dropped by a filter push and by a saved search. */
export const MOVIES_ENTITY_PARAMS = ["title"] as const;

/** The viewer facts the server keys its facet counts on. */
export function moviesViewerIdentity(userData: { username?: string | null; ageRestriction?: number | null } | null | undefined): string {
  return `${userData?.username ?? "anon"}:${userData?.ageRestriction ?? ""}`;
}

export function useMoviesFacetSpec(identity: string) {
  const location = useLocation();
  const types = typesFromSearch(location.search).join(",");
  return useMemo(() => moviesFacetSpec(identity, types ? types.split(",") : []), [identity, types]);
}

export const moviesCountKey = (state: FacetState) => ["movies", "count", facetStateKey(state)] as const;

/** `/API/Browse` page 1 carries the count; one row is enough to read it. */
export function browseTotalUrl(state: FacetState): string {
  const p = moviesFilterParams(state);
  const types = typesOf(state);
  if (types.length) p.set("types", types.join(","));
  p.set("page", "1");
  p.set("pageSize", "1");
  return `/API/Browse?${p.toString()}`;
}

export function useMoviesResultTotal(state: FacetState, enabled = true) {
  return useResultCount(moviesCountKey(state), ({ signal }) => fetch(browseTotalUrl(state), { signal }), enabled);
}
