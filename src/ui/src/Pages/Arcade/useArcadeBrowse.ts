/**
 * What the Arcade rail surfaces share (the page, the sider rail, the phone sheet): the facet state
 * read off the URL, the lobby's filter object in the API's vocabulary, the faceted counts for that
 * scope (`useArcadeFilters` — one request shared with the console carousel), the spec over them, and
 * the result count for the scope (one `pageSize=1` page, held five minutes).
 */
import { useMemo } from "react";
import { useLocation } from "react-router-dom";
import { parseFacetState } from "../../catalog/rail/facetUrl";
import useResultCount from "../../catalog/rail/useResultCount";
import { serverSort, type ArcadeFilters } from "../../catalog/sources/arcadeSource";
import { MovieAPI } from "../../MovieAPI";
import { ARCADE_PARSE_SPEC, arcadeFacetSpec, arcadeFilterParams, type ArcadeFacetsDto } from "./arcadeFacetSpec";
import useArcadeFilters, { arcadeFilterKey } from "./useArcadeFilters";

export interface ArcadeBrowse {
  /** The lobby's filters, from the URL (the rail's `q/f/x` + the catalog's `sort`). */
  filters: ArcadeFilters;
  filterKey: string;
  facets: ArcadeFacetsDto | null;
  spec: ReturnType<typeof arcadeFacetSpec>;
}

export default function useArcadeBrowse(): ArcadeBrowse {
  const location = useLocation();
  const filters = useMemo(() => {
    const p = new URLSearchParams(location.search);
    // The catalog switcher names the default order "alpha"; the server knows it as "".
    return arcadeFilterParams(parseFacetState(location.search, ARCADE_PARSE_SPEC), serverSort(p.get("sort")));
  }, [location.search]);
  const filterKey = JSON.stringify(filters);
  const facets = useArcadeFilters(filters) as ArcadeFacetsDto | null;
  const scopeKey = arcadeFilterKey(filters);
  const spec = useMemo(() => arcadeFacetSpec(`${scopeKey}:${facets ? "1" : "0"}`, facets), [scopeKey, facets]);
  return { filters, filterKey, facets, spec };
}

export const arcadeCountKey = (filterKey: string) => ["arcade", "count", filterKey] as const;

/** `/API/Arcade/Games` page 1 carries the count; one row is enough to read it (the sider rail's head line). */
export function useArcadeResultTotal(filters: ArcadeFilters, filterKey: string, enabled = true) {
  return useResultCount(arcadeCountKey(filterKey), ({ signal }) => MovieAPI.getArcadeGames({ ...filters, skip: 0, pageSize: 1 }, signal), enabled);
}
