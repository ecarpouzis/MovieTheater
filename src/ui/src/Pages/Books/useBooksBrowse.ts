/**
 * What the Books browse's rail surfaces share: the result count for a facet state (one `$count`
 * request per state, held five minutes — the sider rail and the page read the same query), and the
 * two URL facts the rail needs (is the view grouped? is it the Directory?) read the way the catalog
 * resolves them, stored default included.
 */
import type { FacetSpec, FacetState } from "../../catalog/rail/facetSpec";
import { facetStateKey } from "../../catalog/rail/facetUrl";
import { useCountQuery } from "../../catalog/rail/useResultCount";
import { buildBooksQuery } from "../../catalog/sources/booksOData";
import { isDirectoryBrowse as catalogDirectory, isGroupedBrowse as catalogGrouped } from "../../catalog/state/useCatalogView";
import { fetchCatalog } from "./booksApi";

/** The Books forms of the catalog's URL facts (`catalog/state/useCatalogView`). */
export function isGroupedBrowse(search: string, section = "books"): boolean {
  return catalogGrouped(search, section);
}

export function isDirectoryBrowse(search: string, section = "books"): boolean {
  return catalogDirectory(search, section);
}

export const booksCountKey = (state: FacetState) => ["books", "count", facetStateKey(state)] as const;

export function useBooksResultTotal(state: FacetState, spec: FacetSpec, enabled = true) {
  return useCountQuery(booksCountKey(state), async ({ signal }) => {
    const parts = buildBooksQuery(state, spec);
    const r = await fetchCatalog({ kind: "comic", q: state.q.trim() || undefined, filter: parts.filter, exact: parts.exact, top: 1, count: true }, signal);
    return r.total;
  }, enabled);
}
