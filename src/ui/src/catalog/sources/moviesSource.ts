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
import { createClientSource, type ClientSort } from "./clientSource";
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
  /** The parsed facet state behind a `/API/Browse` search (R9 S2) — the grid reads the active person from it. */
  facet?: { include: Record<string, (string | number)[]> } | null;
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

/**
 * `/API/BrowseGroups?groupBy=` values — the audited axis set (R9 S8). `letter` is GONE: the A–Z strip
 * is the letter axis, and a shelf per letter was the same index drawn twice.
 *
 * Every axis here has a matching facet, so a group header can scope in place (`onOpenGroup` below):
 * type/genre/franchise/mpa are their own facets, director drills into `person`, the four AI tag axes
 * into their own tokens, decade into the year range, and "my lists" into the rail's flags.
 */
export const MOVIE_GROUPS: GroupSpec[] = [
  { value: "genre", label: "Genre" },
  { value: "decade", label: "Decade" },
  { value: "franchise", label: "Franchise" },
  { value: "type", label: "Type" },
  { value: "director", label: "Director" },
  { value: "mpa", label: "MPA rating" },
  { value: "subgenre", label: "Subgenre" },
  { value: "mood", label: "Mood" },
  { value: "era", label: "Era" },
  { value: "setting", label: "Setting" },
  { value: "my", label: "My lists" },
];

const GROUP_LABELS: Record<string, string> = Object.fromEntries(MOVIE_GROUPS.map((g) => [g.value, g.label]));

/**
 * Which FACET a group header adds when it scopes in place. `decade` is a year range and `my` is a
 * rail flag, so both are handled on their own below; `director` narrows the People facet, which is
 * the one the rail actually offers (there is no director-only facet — `BrowseFilter` matches a person
 * across every credit role).
 */
const GROUP_FACET: Record<string, string> = {
  genre: "genre",
  franchise: "franchise",
  type: "type",
  mpa: "mpa",
  director: "person",
  subgenre: "subgenre",
  mood: "mood",
  era: "era",
  setting: "setting",
};

/** Which browse mode a group header opens on a page with no rail (`?mode=&value=`). */
const GROUP_BROWSE_MODE: Record<string, string> = { genre: "genre", franchise: "franchise", director: "actor", mpa: "rating" };

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
  /** The combinable filter's own params (`/API/Browse`'s BrowseFilterQuery), carried onto every scoped call. */
  fq: [string, string][];
}

const SCOPE_BY_ENDPOINT: Record<string, { mode: string | null; valueParam: string | null; typesParam: string; passthrough?: boolean }> = {
  // The facet rail's scope (R9 S2): every param but the paging/sort is the filter itself.
  "/API/Browse": { mode: null, valueParam: null, typesParam: "types", passthrough: true },
  "/API/GetMoviesByType": { mode: null, valueParam: null, typesParam: "type" },
  "/API/BrowseTitle": { mode: "title", valueParam: "q", typesParam: "types" },
  "/API/BrowsePerson": { mode: "actor", valueParam: "q", typesParam: "types" },
  "/API/BrowseGenre": { mode: "genre", valueParam: "genres", typesParam: "types" },
  "/API/BrowseFranchise": { mode: "franchise", valueParam: "franchise", typesParam: "types" },
  "/API/BrowseLetter": { mode: "letter", valueParam: "letter", typesParam: "types" },
};
const FLAT_ONLY_ENDPOINTS = new Set(["/API/GetMoviesByRating"]);
const NOT_FILTER_PARAMS = new Set(["types", "type", "sort", "seed", "page", "pageSize"]);

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
    const fq: [string, string][] = spec.passthrough ? [...p.entries()].filter(([k]) => !NOT_FILTER_PARAMS.has(k)) : [];
    return { types: p.get(spec.typesParam) ?? "", mode: spec.mode, value: spec.valueParam ? p.get(spec.valueParam) : null, sort, seed, groupable: true, fq };
  }
  if (FLAT_ONLY_ENDPOINTS.has(u.pathname)) return { types: p.get("types") ?? "", mode: null, value: null, sort, seed, groupable: false, fq: [] };
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

function toGroup(g: GroupRow, groupBy: string): CardGroup {
  const items = (g.items ?? []).map((row) => ({ ...toCard(row), groupKey: g.key }));
  // The Newspaper's eyebrow. The movie endpoints carry no per-group prose (no synopsis, no byline),
  // so the honest detail is what KIND of shelf this is — never an invented sentence.
  const kicker = GROUP_LABELS[groupBy];
  return {
    key: g.key,
    label: g.label,
    totalItems: g.totalItems,
    renderTotal: g.renderTotal ?? g.totalItems,
    items,
    detail: kicker ? { kicker } : undefined,
  };
}

/** The axes on offer for this reader: "my lists" needs a reader with lists (a control that does not apply is REMOVED). */
export function movieGroupsFor(signedIn: boolean): GroupSpec[] {
  return signedIn ? MOVIE_GROUPS : MOVIE_GROUPS.filter((g) => g.value !== "my");
}

export interface MoviesSourceOptions {
  search: MovieSearch;
  /** A signed-in reader — the only one whose Seen / Want / Rated shelves exist. */
  signedIn?: boolean;
  /** Open the detail modal (`?title=<kind>:<id>`). */
  onOpen: (id: number, kind: CardKind) => void;
  /** Jump to a browse (`?mode=&value=`) — a group header's click when the page has no facet rail. */
  onBrowse: (mode: string, value: string) => void;
  /** Scope in place (R9 S2): a group header adds its facet (or year range, or `my` flag) and regroups a level — one push. */
  onScope?: (patch: { facet?: { key: string; value: string }; years?: [number, number]; flag?: string; group?: string }) => void;
}

/** Null when the search has no catalog scope (see `scopeOf`); the page then keeps its existing renderer. */
export function createMoviesSource({ search, signedIn, onOpen, onBrowse, onScope }: MoviesSourceOptions): CatalogSource | null {
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
    for (const [k, v] of scope.fq) p.append(k, v);
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
    return g ? { items: toGroup(g, groupBy).items, total: g.totalItems } : { items: [], total: 0 };
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
    groups: groupable ? movieGroupsFor(!!signedIn) : [],
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
          return { groups: (data.groups ?? []).map((g) => toGroup(g, groupBy)), totalGroups: data.totalGroups ?? 0 };
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
          if (onScope) {
            // Every axis drills: it adds its own facet and drops the grouping a level, one push.
            // Genre regroups by decade (the only pair where "the same axis again" would be useless);
            // everything else lands on the section's default axis.
            if (groupBy === "decade") {
              const d = parseInt(group.key, 10);
              if (Number.isFinite(d)) return onScope({ years: [d, d + 9], group: "genre" });
            }
            // "My lists" is a rail FLAG (`my=seen`), not a facet value.
            if (groupBy === "my") return onScope({ flag: group.key, group: "genre" });
            const facet = GROUP_FACET[groupBy];
            if (facet) return onScope({ facet: { key: facet, value: group.key }, group: groupBy === "genre" ? "decade" : "genre" });
          }
          const mode = GROUP_BROWSE_MODE[groupBy];
          if (mode) onBrowse(mode, group.key);
        }
      : undefined,
  };
}

export interface MoviesListSourceOptions {
  /** The rows the page already holds, in the order it shows them. */
  rows: MovieCardRow[];
  /** Identity of the LIST (which search it is) — NOT its contents; `dataVersion` covers edits in place. */
  listKey: string;
  /** The order the server returned (`NormalizeSort`'s vocabulary); the section owns it, so nothing re-sorts here. */
  sort?: string | null;
  /** False when the array order is NOT the alphabetical one (the back-nav restore reorders it) — no letter strip. */
  alphabetical?: boolean;
  onOpen: (id: number, kind: CardKind) => void;
}

/**
 * The DENSE movie lists — Seen, Want to watch, the back-nav restore, and any one-shot browse — as a
 * `CatalogSource` over the rows the page is already holding (R9 S3). They kept their own renderer
 * for one reason: removal-on-untoggle edits a dense array in place, which a sparse page map cannot
 * express without re-seating every following slot. It still does; the array is the source's `items`
 * and the page bumps `dataVersion` when it edits one out, so the engine re-reads the bands and
 * leaves the scroll position alone.
 *
 * Flat views only: an id list has no server grouping behind it, and the site's rule is that a
 * control which does not apply is REMOVED, not disabled.
 */
export function createMoviesListSource(o: MoviesListSourceOptions): CatalogSource {
  const alpha = o.alphabetical !== false;
  const sorts: ClientSort[] = MOVIE_SORTS.map((s) => ({ ...s, alpha: !!s.alpha && alpha }));
  const sort = o.sort && MOVIE_SORTS.some((s) => s.value === o.sort) ? o.sort : "alpha";
  return createClientSource({
    queryKey: `movies:list:${o.listKey}`,
    title: "Movies",
    itemNoun: "title",
    items: o.rows.map(toCard),
    sorts,
    // The rows arrive in the server's order and the NavBar owns the Sort control, so the pill pins
    // to what is on screen instead of re-ordering a page the server already ordered.
    currentSort: sort,
    listColumns: MOVIE_LIST_COLUMNS,
    defaultAspect: POSTER_ASPECT,
    pageSize: MOVIES_PAGE_SIZE,
    onOpen: (item) => o.onOpen(item.id, item.kind),
  });
}
