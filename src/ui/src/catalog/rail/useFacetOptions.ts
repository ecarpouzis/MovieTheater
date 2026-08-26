/**
 * The loaded facet lists for a spec, shared by every rail surface that shows them (the sider rail,
 * the phone sheet, the smart search, the chips' label lookup) — one request per spec identity, held
 * for half an hour. Rides React Query (site-wide provider); the catalog SOURCES stay query-free.
 */
import { useQuery } from "@tanstack/react-query";
import type { FacetOptionRow, FacetSpec } from "./facetSpec";

export type FacetLists = Record<string, FacetOptionRow[]>;

export const facetOptionsKey = (identity: string) => ["catalog", "facets", identity] as const;

export default function useFacetOptions(spec: FacetSpec, enabled = true) {
  return useQuery<FacetLists>({
    queryKey: facetOptionsKey(spec.identity),
    queryFn: ({ signal }) => spec.loadFacets(signal),
    enabled,
    staleTime: 30 * 60 * 1000,
  });
}
