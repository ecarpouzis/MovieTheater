/**
 * What the Books browse's rail surfaces share: the result count for a facet state (one `$count`
 * request per state, held five minutes — the sider rail and the page read the same query), and the
 * two URL facts the rail needs (is the view grouped? is it the Directory?) read the way the catalog
 * resolves them, stored default included.
 */
import { useQuery } from "@tanstack/react-query";
import type { FacetSpec, FacetState } from "../../catalog/rail/facetSpec";
import { facetStateKey } from "../../catalog/rail/facetUrl";
import { buildBooksQuery } from "../../catalog/sources/booksOData";
import { readCatalogDefaults } from "../../catalog/state/useCatalogView";
import { fetchCatalog } from "./booksApi";

const GROUPED_VIEWS = new Set(["extended", "shelf", "newspaper"]);

export function isGroupedBrowse(search: string, section = "books"): boolean {
  const p = new URLSearchParams(search);
  const stored = readCatalogDefaults(section);
  const view = p.get("view") ?? stored.view ?? "grid";
  const items = p.get("items") ?? stored.items ?? "items";
  return GROUPED_VIEWS.has(view) || items === "groups";
}

export function isDirectoryBrowse(search: string, section = "books"): boolean {
  const p = new URLSearchParams(search);
  return (p.get("view") ?? readCatalogDefaults(section).view ?? "grid") === "directory";
}

export const booksCountKey = (state: FacetState) => ["books", "count", facetStateKey(state)] as const;

export function useBooksResultTotal(state: FacetState, spec: FacetSpec, enabled = true) {
  return useQuery({
    queryKey: booksCountKey(state),
    queryFn: async ({ signal }) => {
      const parts = buildBooksQuery(state, spec);
      const r = await fetchCatalog({ kind: "comic", q: state.q.trim() || undefined, filter: parts.filter, exact: parts.exact, top: 1, count: true }, signal);
      return r.total;
    },
    enabled,
    staleTime: 5 * 60 * 1000,
  });
}
