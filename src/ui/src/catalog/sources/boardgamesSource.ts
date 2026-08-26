/**
 * Boardgames → `CatalogSource`. The page already holds the whole catalog client-side (the cached
 * `/odata/Boardgames` rows) and derives the filtered + sorted list it shows; this adapter maps that
 * list onto cards and rides the in-memory client source for bands, groups, letters and the
 * directory. Publisher / family / designer / category / mechanic come from `/API/Boardgames/Facets`
 * (parsed server-side out of `LinksJson`); decade and player count come from the rows themselves.
 */
import type { CardGroup, CardItem, CatalogSource, ListColumn } from "../types";
import { cardKey } from "../types";
import { createClientSource, type ClientGrouper, type ClientSort, type GroupKey } from "./clientSource";
import { hueOf } from "./hue";

/** The normalized row `BoardGames.js` builds from an OData game. */
export interface BoardgameRow {
  id: number;
  name?: string | null;
  yearPublished?: number | null;
  minPlayers?: number | null;
  maxPlayers?: number | null;
  playingTime?: number | null;
  minPlayTime?: number | null;
  maxPlayTime?: number | null;
  minAge?: number | null;
  averageRating?: number | null;
  averageWeight?: number | null;
  imageVersion?: number | string | null;
  thingType?: string | null;
  baseGameId?: number | null;
}

/** One row of `/API/Boardgames/Facets` `items`. */
export interface BoardgameFacets {
  id: number;
  publishers?: string[];
  families?: string[];
  designers?: string[];
  categories?: string[];
  mechanics?: string[];
}

/** The page's `?sort=` vocabulary; absent = the server's name order (`name` here). The page sorts, the adapter only names it. */
export const BOARDGAME_SORTS: ClientSort[] = [
  { value: "name", label: "A–Z", alpha: true },
  { value: "rating_desc", label: "Top rated" },
  { value: "rating_asc", label: "Lowest rated" },
  { value: "complexity_desc", label: "Heaviest" },
  { value: "complexity_asc", label: "Lightest" },
  { value: "play_time_asc", label: "Shortest" },
  { value: "play_time_desc", label: "Longest" },
];

/** Box art is close to square far more often than not. */
export const BOARDGAME_ASPECT = 1;

const fmtTime = (t: number | null | undefined) => (t == null ? null : t > 999 ? "∞" : String(t));

export function playersLabel(g: BoardgameRow, expansions: BoardgameRow[] = []): string | null {
  const min = g.minPlayers ?? null;
  const max = g.maxPlayers ?? null;
  const base = min != null && max != null ? (min === max ? `${min}` : `${min}–${max}`) : min != null ? `${min}+` : max != null ? `${max}` : null;
  const ext = expansions.reduce((acc, e) => (e.maxPlayers != null && e.maxPlayers > acc ? e.maxPlayers : acc), max ?? 0);
  if (base == null) return ext > 0 ? `${ext}` : null;
  return ext > (max ?? 0) ? `${base}→${ext}` : base;
}

export function playTimeLabel(g: BoardgameRow): string | null {
  const lo = fmtTime(g.minPlayTime);
  const hi = fmtTime(g.maxPlayTime ?? g.playingTime);
  if (lo != null && hi != null) return lo === hi ? lo : `${lo}–${hi}`;
  return hi ?? lo;
}

export function toBoardgameCard(g: BoardgameRow, expansions: BoardgameRow[] = []): CardItem {
  const id = Number(g.id);
  const title = g.name ?? `#${id}`;
  const v = g.imageVersion != null && g.imageVersion !== "" ? `?v=${g.imageVersion}` : "";
  const rating = g.averageRating != null ? Math.round(Number(g.averageRating) * 10) : undefined;
  const players = playersLabel(g, expansions);
  const time = playTimeLabel(g);
  const badges: CardItem["badges"] = [];
  if (g.averageRating != null) badges.push({ label: `★ ${Number(g.averageRating).toFixed(1)}`, tone: "rating", title: "BGG rating" });
  if (players) badges.push({ label: `👥 ${players}`, tone: "neutral", title: "Players" });
  if (time) badges.push({ label: `⏱ ${time}`, tone: "neutral", title: "Minutes" });
  if (g.averageWeight != null) badges.push({ label: `⚖ ${Number(g.averageWeight).toFixed(1)}`, tone: "neutral", title: "Complexity" });
  return {
    kind: "boardgame",
    id,
    key: cardKey("boardgame", id),
    title,
    subtitle: [players && `👥 ${players}`, time && `⏱ ${time}`].filter(Boolean).join(" · ") || undefined,
    label: g.yearPublished ? String(g.yearPublished) : undefined,
    year: g.yearPublished ?? undefined,
    aspect: BOARDGAME_ASPECT,
    imageUrl: `/BoardgameImage/${id}${v}`,
    imageThumbUrl: `/BoardgameImageThumb/${id}${v}`,
    hue: hueOf(title),
    rating,
    sortKey: title,
    badges,
    raw: g,
  };
}

const rawOf = (i: CardItem) => (i.raw ?? {}) as BoardgameRow;

export const BOARDGAME_LIST_COLUMNS: ListColumn[] = [
  { key: "title", label: "Name", width: "2fr", value: (i) => i.title },
  { key: "year", label: "Year", width: "64px", mono: true, value: (i) => i.year },
  { key: "players", label: "Players", width: "90px", mono: true, value: (i) => playersLabel(rawOf(i)) },
  { key: "time", label: "Minutes", width: "90px", mono: true, value: (i) => playTimeLabel(rawOf(i)) },
  { key: "age", label: "Age", width: "56px", mono: true, align: "right", value: (i) => (rawOf(i).minAge != null ? `${rawOf(i).minAge}+` : null) },
  { key: "rating", label: "Rating", width: "64px", mono: true, align: "right", value: (i) => (rawOf(i).averageRating != null ? Number(rawOf(i).averageRating).toFixed(1) : null) },
  { key: "weight", label: "Weight", width: "64px", mono: true, align: "right", value: (i) => (rawOf(i).averageWeight != null ? Number(rawOf(i).averageWeight).toFixed(2) : null) },
];

/** Max-players buckets; the labels sort themselves. */
export function playersBucket(g: BoardgameRow): GroupKey | null {
  const max = g.maxPlayers ?? g.minPlayers ?? null;
  if (max == null) return null;
  if (max <= 1) return { key: "1", label: "1 player" };
  if (max === 2) return { key: "2", label: "2 players" };
  if (max <= 4) return { key: "3", label: "3–4 players" };
  if (max <= 6) return { key: "5", label: "5–6 players" };
  return { key: "7", label: "7+ players" };
}

function facetGrouper(value: string, label: string, pick: (f: BoardgameFacets) => string[] | undefined, facetsById: Map<number, BoardgameFacets>): ClientGrouper {
  return {
    value,
    label,
    keysOf: (i) => (pick(facetsById.get(i.id) ?? { id: i.id }) ?? []).filter(Boolean).map((k) => ({ key: k, label: k })),
  };
}

export function boardgameGroupers(facetsById: Map<number, BoardgameFacets>): ClientGrouper[] {
  return [
    facetGrouper("publisher", "Publisher", (f) => f.publishers, facetsById),
    facetGrouper("family", "Family", (f) => f.families, facetsById),
    { value: "decade", label: "Decade", order: "keyDesc", keysOf: (i) => (i.year ? { key: String(Math.floor(i.year / 10) * 10), label: `${Math.floor(i.year / 10) * 10}s` } : null) },
    { value: "players", label: "Players", keysOf: (i) => playersBucket(rawOf(i)) },
    facetGrouper("designer", "Designer", (f) => f.designers, facetsById),
    facetGrouper("category", "Category", (f) => f.categories, facetsById),
    facetGrouper("mechanic", "Mechanic", (f) => f.mechanics, facetsById),
  ];
}

export function facetsMap(items: BoardgameFacets[] | null | undefined): Map<number, BoardgameFacets> {
  const m = new Map<number, BoardgameFacets>();
  for (const f of items ?? []) if (f && Number.isInteger(f.id)) m.set(f.id, f);
  return m;
}

export interface BoardgamesSourceOptions {
  /** The list the page shows — already filtered and sorted. */
  games: BoardgameRow[];
  expansionMap: Record<number, BoardgameRow[]>;
  facetsById: Map<number, BoardgameFacets>;
  /** Names what makes the list a DIFFERENT list (the page's listKey). */
  listKey: string;
  /** The page's `?sort=`; absent = name order. */
  currentSort: string | null | undefined;
  onOpen: (id: number) => void;
  onOpenGroup?: (group: CardGroup, groupBy: string) => void;
}

export function createBoardgamesSource(o: BoardgamesSourceOptions): CatalogSource {
  const items = o.games.map((g) => toBoardgameCard(g, o.expansionMap[g.id] ?? []));
  return createClientSource({
    queryKey: `boardgames:${o.listKey}`,
    title: "Board Games",
    itemNoun: "game",
    groupNoun: "groups",
    itemsLabels: { items: "Games", groups: "One per group" },
    items,
    groups: boardgameGroupers(o.facetsById),
    sorts: BOARDGAME_SORTS,
    currentSort: o.currentSort && BOARDGAME_SORTS.some((s) => s.value === o.currentSort) ? o.currentSort : "name",
    defaultGroup: "publisher",
    directoryGroup: "publisher",
    listColumns: BOARDGAME_LIST_COLUMNS,
    defaultAspect: BOARDGAME_ASPECT,
    onOpen: (item) => o.onOpen(item.id),
    onOpenGroup: o.onOpenGroup,
  });
}
