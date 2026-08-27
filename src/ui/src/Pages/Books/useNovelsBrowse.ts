/**
 * What the Novels rail surfaces share: the result count for a facet state (one `top=1` request per
 * state, held five minutes — the sider rail and the page read the same query) and the URL facts the
 * page needs. The default content exclusion is applied here as a one-time landing rewrite.
 */
import type { FacetSpec, FacetState } from "../../catalog/rail/facetSpec";
import { facetStateKey, writeFacetState } from "../../catalog/rail/facetUrl";
import { useCountQuery } from "../../catalog/rail/useResultCount";
import { buildNovelsQuery } from "../../catalog/sources/novelsSource";
import { fetchNovels } from "./booksApi";
import { NOVELS_DEFAULT_EXCLUDE_TAG } from "./novelsFacetSpec";

export const novelsCountKey = (state: FacetState) => ["books", "novels-count", facetStateKey(state)] as const;

export function useNovelsTotal(state: FacetState, enabled = true) {
  return useCountQuery(novelsCountKey(state), async ({ signal }) => (await fetchNovels({ ...buildNovelsQuery(state), top: 1 }, signal)).total, enabled);
}

const SEEDED_KEY = "books.novels.seeded.v1";
const FACET_PARAMS = ["q", "f", "x", "r", "my", "y"];

/**
 * The search string a fresh landing on /books/novels should have: the default "not adult-romance"
 * chip, once per tab session, only when the URL carries no filter of its own (a saved search, a link
 * from Explore and a cleared rail all carry theirs). Null = leave the URL alone.
 */
export function seededNovelsSearch(search: string, spec: FacetSpec, state: FacetState, storage: Pick<Storage, "getItem" | "setItem"> | null): string | null {
  const p = new URLSearchParams(search);
  if (FACET_PARAMS.some((k) => p.has(k))) return null;
  try {
    if (storage?.getItem(SEEDED_KEY)) return null;
    storage?.setItem(SEEDED_KEY, "1");
  } catch {
    /* private mode: seed this once anyway */
  }
  const next: FacetState = { ...state, exclude: { ...state.exclude, tags: [NOVELS_DEFAULT_EXCLUDE_TAG] } };
  writeFacetState(p, next, spec);
  const s = p.toString();
  return s ? `?${s}` : "";
}

export function sessionStorageOrNull(): Storage | null {
  try {
    return typeof window !== "undefined" ? window.sessionStorage : null;
  } catch {
    return null;
  }
}
