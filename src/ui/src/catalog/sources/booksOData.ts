/**
 * Books facet state → what the host takes. One `$filter` string serves `/odata/catalog` and the three
 * `/browse/*` group endpoints (they share `CatalogEdm`), so it is written once here; the facets the
 * flat projection cannot express go as the host's exact params (`author= artist= tag= event=` and
 * their excludes — S0), and the personal flags as `readOnly/wantToReadOnly`.
 *
 * Vocabulary = the camelCase `ItemSummary` JSON: `topFolderId`, `seriesId`, `franchise`, `publisher`
 * (a NAME — the projection has no publisher id, and the group keys are names too), `year`, `rating`.
 */
import type { ExactParams } from "../../Pages/Books/booksApi";
import type { FacetSpec, FacetState, FacetValue } from "../rail/facetSpec";

export function escapeOData(s: string): string {
  return s.replace(/'/g, "''");
}

export interface BooksQueryParts {
  /** The OData `$filter`, or null when nothing applies. */
  filter: string | null;
  exact: ExactParams;
  readOnly: boolean;
  wantToReadOnly: boolean;
}

const str = (v: FacetValue) => `'${escapeOData(String(v))}'`;
const num = (v: FacetValue) => String(Math.trunc(Number(v)));

type Clause = { include: (v: FacetValue) => string; exclude: (v: FacetValue) => string; numeric?: boolean };

/** Facet key → the pair of clauses. Excludes are null-safe: a row with no value is NOT excluded by "not X". */
const CLAUSES: Record<string, Clause> = {
  collections: { numeric: true, include: (v) => `topFolderId eq ${num(v)}`, exclude: (v) => `(topFolderId eq null or topFolderId ne ${num(v)})` },
  series: { numeric: true, include: (v) => `seriesId eq ${num(v)}`, exclude: (v) => `(seriesId eq null or seriesId ne ${num(v)})` },
  franchises: { include: (v) => `franchise eq ${str(v)}`, exclude: (v) => `(franchise eq null or franchise ne ${str(v)})` },
  publishers: { include: (v) => `publisher eq ${str(v)}`, exclude: (v) => `(publisher eq null or publisher ne ${str(v)})` },
};

/** Facet key → the exact-param names (S0). */
const EXACT: Record<string, { include: keyof ExactParams; exclude: keyof ExactParams }> = {
  authors: { include: "author", exclude: "exAuthor" },
  artists: { include: "artist", exclude: "exArtist" },
  tags: { include: "tag", exclude: "exTag" },
  events: { include: "event", exclude: "exEvent" },
};

export function buildBooksQuery(state: FacetState, spec: FacetSpec): BooksQueryParts {
  const ands: string[] = [];
  const exact: ExactParams = {};

  for (const def of spec.facets) {
    const inc = state.include[def.key] ?? [];
    const exc = state.exclude[def.key] ?? [];
    const clause = CLAUSES[def.key];
    const exactKey = EXACT[def.key];
    if (clause) {
      const valid = (v: FacetValue) => !clause.numeric || Number.isFinite(Number(v));
      const incs = inc.filter(valid).map(clause.include);
      if (incs.length === 1) ands.push(incs[0]);
      else if (incs.length > 1) ands.push(`(${incs.join(" or ")})`);
      for (const v of exc.filter(valid)) ands.push(clause.exclude(v));
    } else if (exactKey) {
      if (inc.length) exact[exactKey.include] = inc.map(String);
      if (exc.length) exact[exactKey.exclude] = exc.map(String);
    }
  }

  if (state.yearMin != null) ands.push(`year ge ${Math.trunc(state.yearMin)}`);
  if (state.yearMax != null) ands.push(`year le ${Math.trunc(state.yearMax)}`);
  if (state.ratingMin > 0) ands.push(`rating ge ${Math.trunc(state.ratingMin)}`);

  return {
    filter: ands.length ? ands.join(" and ") : null,
    exact,
    readOnly: !!state.flags.read,
    wantToReadOnly: !!state.flags.want,
  };
}

export interface BooksSortSpec { value: string; label: string; alpha?: boolean; flat: string | null; grouped: string | null; seriesOnly?: boolean }

/**
 * The sorts, with their two spellings: the flat `$orderby` (the catalog appends the id tiebreaker) and
 * the grouped endpoints' `orderby=` names (`BrowseController.ApplySort`). `relevance` = newest indexed.
 */
export const BOOKS_SORTS: BooksSortSpec[] = [
  { value: "series", label: "Series", alpha: true, flat: "series asc,year asc", grouped: null },
  { value: "relevance", label: "Recently added", flat: "indexedAt desc", grouped: "newest" },
  { value: "newest", label: "Newest", flat: "year desc,indexedAt desc", grouped: "newest" },
  { value: "oldest", label: "Oldest", flat: "year asc,indexedAt asc", grouped: "oldest" },
  { value: "rating", label: "Top rated", flat: "rating desc", grouped: "rating" },
  { value: "title", label: "Title", alpha: true, flat: "title asc", grouped: "title" },
  { value: "publisher", label: "Publisher", alpha: true, flat: "publisher asc,year asc", grouped: "publisher" },
  { value: "pages", label: "Longest", flat: "pageCount desc", grouped: "pages" },
  { value: "reading", label: "Reading order", flat: null, grouped: "reading", seriesOnly: true },
];

export function booksSort(value: string | null | undefined): BooksSortSpec {
  return BOOKS_SORTS.find((s) => s.value === value) ?? BOOKS_SORTS[0];
}

export function flatOrderby(sort: string | null | undefined): string | null {
  return booksSort(sort).flat ?? BOOKS_SORTS[0].flat;
}

export function groupedOrderby(sort: string | null | undefined, groupBy: string): string | null {
  const s = booksSort(sort);
  if (s.seriesOnly && groupBy !== "series") return null;
  return s.grouped;
}
