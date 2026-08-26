/**
 * Movies/TV → `CatalogSource`. Wraps the search object `useMovieSearch` already builds (its URL is
 * the whole filter state: endpoint = mode, query = value/types/sort/seed) so the catalog views page
 * EXACTLY the rows the existing grid pages — same endpoint, same order, same age gate — and the
 * grouped views ride `/API/BrowseGroups` under the same scope.
 *
 * Facts the adapter encodes:
 * - the flat envelopes are `{ movies, totalCount, page, pageSize }` with the COUNT ON PAGE 1 ONLY
 *   (`-1` after) — the source carries the first value forward;
 * - the sort is the section's (NavBar persists it, the URL already carries it) — `currentSort`;
 * - the id space is shared between movies and series, and misc has its own — every card is keyed
 *   `${kind}:${id}` and opened by kind;
 * - the Seen/Want id-list searches and the one-shot random landing are NOT catalog scopes (they have
 *   no paged URL) — `scopeOf` returns null and the page keeps its existing renderer.
 */
import { MovieAPI } from "../../MovieAPI";
import type { CardGroup, CardItem, CardKind, CardPage, CatalogSource, DirectoryNode, GroupPage, GroupSpec, LetterBucket, ListColumn, SortSpec, ViewMode } from "../types";
import { cardKey } from "../types";
import { hueOf } from "./hue";

export { hueOf };

/** The slice of `useMovieSearch`'s search object the adapter reads. */
export interface MovieSearch {
  url?: string | null;
  lettersUrl?: string;
  sort?: string;
  infinite?: boolean;
  pending?: boolean;
  movieIds?: number[];
}

/** `MovieCardDto` as the JSON serializer emits it (camelCase; `id` is already lower-case server-side). */
export interface MovieCardRow {
  id: number;
  kind?: string;
  title?: string | null;
  simpleTitle?: string | null;
  releaseDate?: string | null;
  rating?: string | null;
  ratingEstimated?: boolean;
  runtime?: string | null;
  imdbRating?: number | string | null;
  rtTomatometer?: number | null;
  rtPopcornmeter?: number | null;
  posterVersion?: number;
  uploadedDate?: string | null;
}

interface GroupRow {
  key: string;
  label: string;
  totalItems: number;
  renderTotal?: number;
  items?: MovieCardRow[];
}

/** The `NormalizeSort` vocabulary, labelled. `alpha` is the only order with letters to jump to. */
export const MOVIE_SORTS: SortSpec[] = [
  { value: "random", label: "Shuffle" },
  { value: "alpha", label: "A–Z", alpha: true },
  { value: "added", label: "Recently added" },
  { value: "imdb", label: "IMDb" },
  { value: "rt", label: "Tomatometer" },
  { value: "popcorn", label: "Popcornmeter" },
];

/** `/API/BrowseGroups?groupBy=` values. */
export const MOVIE_GROUPS: GroupSpec[] = [
  { value: "genre", label: "Genre" },
  { value: "decade", label: "Decade" },
  { value: "franchise", label: "Franchise" },
  { value: "letter", label: "Letter" },
];

/** Which browse mode a group header opens (`?mode=&value=`); decades have no browse of their own. */
const GROUP_BROWSE_MODE: Record<string, string> = { genre: "genre", franchise: "franchise", letter: "letter" };

export const POSTER_ASPECT = 0.667;
/** Matches the server default in `GetMoviesByType` and the grid's own page size. */
export const MOVIES_PAGE_SIZE = 60;
const ALL_VIEWS: ViewMode[] = ["grid", "wall", "list", "extended", "shelf", "newspaper", "directory"];
const FLAT_ONLY_VIEWS: ViewMode[] = ["grid", "wall", "list"];
/** `/API/BrowseGroups` caps `groupsTop` at 50 for the narrow modes; the directory walks the heads in pages of it. */
const DIRECTORY_HEADS_PAGE = 50;
const DIRECTORY_MAX_PAGES = 40;

/** The filter state a search URL stands for, in `/API/BrowseGroups`' own vocabulary. */
export interface BrowseScope {
  /** Comma list of title types, "" for all. */
  types: string;
  mode: string | null;
  value: string | null;
  sort: string;
  seed: string | null;
  /** False for scopes `/API/BrowseGroups` cannot express (the MPA-rating browse): flat views only. */
  groupable: boolean;
}

const SCOPE_BY_ENDPOINT: Record<string, { mode: string | null; valueParam: string | null; typesParam: string }> = {
  "/API/GetMoviesByType": { mode: null, valueParam: null, typesParam: "type" },
  "/API/BrowseTitle": { mode: "title", valueParam: "q", typesParam: "types" },
  "/API/BrowsePerson": { mode: "actor", valueParam: "q", typesParam: "types" },
  "/API/BrowseGenre": { mode: "genre", valueParam: "genres", typesParam: "types" },
  "/API/BrowseFranchise": { mode: "franchise", valueParam: "franchise", typesParam: "types" },
  "/API/BrowseLetter": { mode: "letter", valueParam: "letter", typesParam: "types" },
};
const FLAT_ONLY_ENDPOINTS = new Set(["/API/GetMoviesByRating"]);

/** Null when the search is not a paged URL browse (pending, id-list, the one-shot random landing). */
export function scopeOf(search: MovieSearch | null | undefined): BrowseScope | null {
  if (!search || search.pending || !search.url || !search.infinite || search.movieIds) return null;
  let u: URL;
  try {
    u = new URL(search.url, "http://localhost");
  } catch {
    return null;
  }
  const p = u.searchParams;
  const sort = p.get("sort") ?? search.sort ?? "random";
  const seed = p.get("seed");
  const spec = SCOPE_BY_ENDPOINT[u.pathname];
  if (spec) {
    return { types: p.get(spec.typesParam) ?? "", mode: spec.mode, value: spec.valueParam ? p.get(spec.valueParam) : null, sort, seed, groupable: true };
  }
  if (FLAT_ONLY_ENDPOINTS.has(u.pathname)) return { types: p.get("types") ?? "", mode: null, value: null, sort, seed, groupable: false };
  return null;
}

function yearOf(iso: string | null | undefined): number | undefined {
  if (!iso) return undefined;
  const y = Number(String(iso).slice(0, 4));
  return Number.isFinite(y) && y > 0 ? y : undefined;
}

function kindOf(row: MovieCardRow): CardKind {
  return row.kind === "series" || row.kind === "misc" ? row.kind : "movie";
}

export function toCard(row: MovieCardRow): CardItem {
  const kind = kindOf(row);
  const id = Number(row.id);
  const title = row.title ?? row.simpleTitle ?? `#${id}`;
  const year = yearOf(row.releaseDate);
  const imdb = row.imdbRating == null || row.imdbRating === "" ? null : Number(row.imdbRating);
  const badges: CardItem["badges"] = [];
  if (imdb != null && Number.isFinite(imdb) && imdb > 0) badges.push({ label: `IMDb ${imdb.toFixed(1)}`, tone: "rating" });
  if (row.rtTomatometer != null) badges.push({ label: `RT ${row.rtTomatometer}%`, tone: "rating", title: "Tomatometer" });
  if (row.rating) badges.push({ label: row.ratingEstimated ? `${row.rating} ~` : row.rating, tone: "neutral", title: row.ratingEstimated ? "Estimated rating" : "Rated" });
  const posterVersion = row.posterVersion ?? 0;
  return {
    kind,
    id,
    key: cardKey(kind, id),
    title,
    subtitle: kind === "series" ? "Series" : kind === "misc" ? "Video" : undefined,
    label: year ? String(year) : undefined,
    year,
    aspect: POSTER_ASPECT,
    imageUrl: MovieAPI.getMoviePoster(id, posterVersion, kind),
    imageThumbUrl: MovieAPI.getPosterThumbnail(id, posterVersion, kind),
    hue: hueOf(title),
    rating: imdb != null && Number.isFinite(imdb) && imdb > 0 ? Math.round(imdb * 10) : row.rtTomatometer ?? undefined,
    sortKey: row.simpleTitle ?? undefined,
    badges,
    raw: row,
  };
}

function rawOf(item: CardItem): MovieCardRow {
  return (item.raw ?? {}) as MovieCardRow;
}

export const MOVIE_LIST_COLUMNS: ListColumn[] = [
  { key: "title", label: "Title", width: "2fr", value: (i) => i.title },
  { key: "year", label: "Year", width: "64px", mono: true, value: (i) => i.year },
  { key: "kind", label: "Type", width: "80px", value: (i) => (i.kind === "series" ? "Series" : i.kind === "misc" ? "Video" : "Movie") },
  { key: "rated", label: "Rated", width: "84px", value: (i) => rawOf(i).rating },
  { key: "runtime", label: "Runtime", width: "90px", mono: true, value: (i) => rawOf(i).runtime },
  { key: "imdb", label: "IMDb", width: "64px", mono: true, align: "right", value: (i) => rawOf(i).imdbRating },
  { key: "rt", label: "RT", width: "56px", mono: true, align: "right", value: (i) => (rawOf(i).rtTomatometer != null ? `${rawOf(i).rtTomatometer}%` : null) },
];

/** Append page/pageSize to a browse URL, keeping its query string (the `withPage` of the grid). */
export function withPage(url: string, page: number, pageSize: number): string {
  const u = new URL(url, "http://localhost");
  u.searchParams.set("page", String(page));
  u.searchParams.set("pageSize", String(pageSize));
  return u.pathname + u.search;
}

async function getJson<T>(url: string, signal?: AbortSignal): Promise<T> {
  const r = await fetch(url, { signal });
  if (!r.ok) throw new Error(`${url} → ${r.status}`);
  return (await r.json()) as T;
}

function toGroup(g: GroupRow): CardGroup {
  const items = (g.items ?? []).map((row) => ({ ...toCard(row), groupKey: g.key }));
  return { key: g.key, label: g.label, totalItems: g.totalItems, renderTotal: g.renderTotal ?? g.totalItems, items };
}

export interface MoviesSourceOptions {
  search: MovieSearch;
  /** Open the detail modal (`?title=<kind>:<id>`). */
  onOpen: (id: number, kind: CardKind) => void;
  /** Jump to a browse (`?mode=&value=`) — a group header's click. */
  onBrowse: (mode: string, value: string) => void;
}

/** Null when the search has no catalog scope (see `scopeOf`); the page then keeps its existing renderer. */
export function createMoviesSource({ search, onOpen, onBrowse }: MoviesSourceOptions): CatalogSource | null {
  const scope = scopeOf(search);
  if (!scope || !search.url) return null;
  const base = search.url;
  let knownTotal = -1;

  const scoped = (extra: Record<string, string | number | null | undefined>, withSort: boolean): string => {
    const p = new URLSearchParams();
    if (scope.types) p.set("types", scope.types);
    if (scope.mode) {
      p.set("mode", scope.mode);
      p.set("value", scope.value ?? "");
    }
    if (withSort) {
      p.set("sort", scope.sort);
      if (scope.seed) p.set("seed", scope.seed);
    }
    for (const [k, v] of Object.entries(extra)) if (v != null) p.set(k, String(v));
    return p.toString();
  };

  const fetchGroupMore = async (groupKey: string, skip: number, top: number, groupBy: string, _sort: string, signal?: AbortSignal): Promise<CardPage> => {
    const data = await getJson<{ groups?: GroupRow[] }>(`/API/BrowseGroups?${scoped({ groupBy, singleGroupKey: groupKey, perGroupSkip: skip, perGroupTop: top }, true)}`, signal);
    const g = data.groups?.[0];
    return g ? { items: toGroup(g).items, total: g.totalItems } : { items: [], total: 0 };
  };

  const groupable = scope.groupable;
  const lettersUrl = search.lettersUrl;

  const directory = groupable
    ? {
        roots: async (signal?: AbortSignal): Promise<DirectoryNode[]> => {
          const nodes: DirectoryNode[] = [];
          let total = Infinity;
          for (let page = 0; page < DIRECTORY_MAX_PAGES && nodes.length < total; page += 1) {
            const data = await getJson<{ totalGroups: number; groups: GroupRow[] }>(
              `/API/BrowseGroups?${scoped({ groupBy: "franchise", groupsSkip: page * DIRECTORY_HEADS_PAGE, groupsTop: DIRECTORY_HEADS_PAGE, perGroupTop: 1 }, true)}`,
              signal,
            );
            total = data.totalGroups;
            if (!data.groups?.length) break;
            for (const g of data.groups) {
              const rep = g.items?.[0] ? toCard(g.items[0]) : null;
              nodes.push({ id: g.key, label: g.label, count: g.totalItems, imageUrl: rep?.imageThumbUrl ?? rep?.imageUrl, hue: rep?.hue ?? hueOf(g.label) });
            }
          }
          return nodes;
        },
        children: async () => [],
        items: (id: string, skip: number, top: number, signal?: AbortSignal) => fetchGroupMore(id, skip, top, "franchise", scope.sort, signal),
      }
    : undefined;

  return {
    queryKey: `movies:${base}`,
    title: "Movies",
    groupNoun: "groups",
    itemNoun: "title",
    supports: groupable ? ALL_VIEWS : FLAT_ONLY_VIEWS,
    groups: groupable ? MOVIE_GROUPS : [],
    sorts: MOVIE_SORTS,
    currentSort: scope.sort,
    itemsModes: groupable ? ["items", "groups"] : undefined,
    itemsLabels: { items: "Titles", groups: "One per group" },
    listColumns: MOVIE_LIST_COLUMNS,
    directory,
    defaultGroup: "genre",
    pageSize: MOVIES_PAGE_SIZE,
    defaultAspect: POSTER_ASPECT,
    fetchFlatBand: async (skip, top, _sort, signal) => {
      const page = Math.floor(skip / top) + 1;
      const data = await getJson<{ movies?: MovieCardRow[]; totalCount?: number }>(withPage(base, page, top), signal);
      const rows = Array.isArray(data.movies) ? data.movies : [];
      if (typeof data.totalCount === "number" && data.totalCount >= 0) knownTotal = data.totalCount;
      return { items: rows.map(toCard), total: knownTotal };
    },
    fetchGroupBand: groupable
      ? async (groupsSkip, groupsTop, perGroupTop, groupBy, _sort, signal): Promise<GroupPage> => {
          const data = await getJson<{ totalGroups: number; groups: GroupRow[] }>(`/API/BrowseGroups?${scoped({ groupBy, groupsSkip, groupsTop, perGroupTop }, true)}`, signal);
          return { groups: (data.groups ?? []).map(toGroup), totalGroups: data.totalGroups ?? 0 };
        }
      : undefined,
    fetchGroupMore: groupable ? fetchGroupMore : undefined,
    letters: lettersUrl
      ? async (_sort, signal): Promise<LetterBucket[]> => (await getJson<{ letters?: LetterBucket[] }>(lettersUrl, signal)).letters ?? []
      : undefined,
    groupLetters: groupable
      ? async (groupBy, _sort, signal) => (await getJson<{ letters?: { letter: string; firstIndex: number }[] }>(`/API/BrowseGroupLetters?${scoped({ groupBy }, false)}`, signal)).letters ?? []
      : undefined,
    onOpen: (item) => onOpen(item.id, item.kind),
    onOpenGroup: groupable
      ? (group, groupBy) => {
          const mode = GROUP_BROWSE_MODE[groupBy];
          if (mode) onBrowse(mode, group.key);
        }
      : undefined,
  };
}
