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

/** `listsOwner` (the friend named by `for=`, resolved to their display name by the caller — null =
 *  the viewer's own) titles the rail's flags section and keys its chips. */
export function useMoviesFacetSpec(identity: string, listsOwner: string | null = null) {
  const location = useLocation();
  const types = typesFromSearch(location.search).join(",");
  return useMemo(() => moviesFacetSpec(`${identity}:${listsOwner ?? ""}`, types ? types.split(",") : [], listsOwner), [identity, types, listsOwner]);
}

/** `forUser` = the `for=<username>` scope (whose lists `my=` reads) — part of the key, or Alex's
 * `my=seen` count and mine would share one entry. */
export const moviesCountKey = (state: FacetState, forUser: string | null = null) =>
  ["movies", "count", facetStateKey(state), (forUser ?? "").toLowerCase()] as const;

/** `/API/Browse` page 1 carries the count; one row is enough to read it. */
export function browseTotalUrl(state: FacetState, forUser: string | null = null): string {
  const p = moviesFilterParams(state);
  const types = typesOf(state);
  if (types.length) p.set("types", types.join(","));
  if (forUser) p.set("for", forUser);
  p.set("page", "1");
  p.set("pageSize", "1");
  return `/API/Browse?${p.toString()}`;
}

/** The `for=` scope of a search string, or null (duplicated from hooks/useUserLists to keep this file pure TS). */
export function forUserOfSearch(search: string): string | null {
  const v = (new URLSearchParams(search || "").get("for") || "").trim();
  return v || null;
}

export function useMoviesResultTotal(state: FacetState, enabled = true) {
  const location = useLocation();
  const forUser = forUserOfSearch(location.search);
  return useResultCount(moviesCountKey(state, forUser), ({ signal }) => fetch(browseTotalUrl(state, forUser), { signal }), enabled);
}
