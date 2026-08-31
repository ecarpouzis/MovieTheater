/**
 * Boardgames → `CatalogSource`. The page already holds the whole catalog client-side (the cached
 * `/odata/Boardgames` rows) and derives the filtered + sorted list it shows; this adapter maps that
 * list onto cards and rides the in-memory client source for bands, groups, letters and the
 * directory. Publisher / family / designer / category / mechanic come from `/API/Boardgames/Facets`
 * (parsed server-side out of `LinksJson`); decade and player count come from the rows themselves.
 */
import { AGE_STOPS, PLAYERS_CAP, TIME_STOPS, ageOf, formatMinutes, playerCounts, timeSpan, weightOf } from "../../Pages/BoardGames/boardgamesFacetSpec";
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
  description?: string | null;
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
/** The Grid's base box-art height before the cover-size tweak (the card's old 200 px). */
export const BOARDGAME_GRID_CELL = 200;

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

/**
 * Player-count shelves, RANGE-AWARE (R9 S8 — the audit's one FIX here). The old axis bucketed a game
 * by its MAXIMUM alone, so a 2–4 player game landed in "3–4 players" and was missing from the shelf
 * of someone looking for a two-player evening. A game now appears on EVERY count it plays at, its
 * expansions extending it — the exact rule `playerCounts` in `boardgamesFacetSpec.ts` already applied
 * to the rail's Players facet, so a shelf and its facet finally describe the same set. 8 means 8+.
 */
export function playersBuckets(g: BoardgameRow, expansions: readonly BoardgameRow[] = []): GroupKey[] {
  return playerCounts(g, expansions).map((n) => ({ key: String(n), label: n >= PLAYERS_CAP ? `Plays ${PLAYERS_CAP}+` : `Plays ${n}` }));
}

/** The typical length of a play: the midpoint of the sane [shortest, longest] span BGG gives. */
export function typicalMinutes(g: BoardgameRow): number | null {
  const span = timeSpan(g);
  return span ? Math.round((span[0] + span[1]) / 2) : null;
}

/**
 * Play-time shelves over the rail's own `TIME_STOPS` — a game files under the interval its typical
 * length falls in, so the shelves read as a ladder ("Under 15m", "15m–20m", … "4h+") and the axis
 * uses the same scale as the Play time range facet.
 */
export function timeBucket(g: BoardgameRow): GroupKey | null {
  const t = typicalMinutes(g);
  if (t == null) return null;
  if (t < TIME_STOPS[0]) return { key: "0", label: `Under ${formatMinutes(TIME_STOPS[0])}` };
  for (let i = TIME_STOPS.length - 1; i >= 0; i -= 1) {
    if (t < TIME_STOPS[i]) continue;
    const hi = TIME_STOPS[i + 1];
    return { key: String(TIME_STOPS[i]), label: hi == null ? `${formatMinutes(TIME_STOPS[i])}+` : `${formatMinutes(TIME_STOPS[i])}–${formatMinutes(hi)}` };
  }
  return null;
}

/** Minimum-age shelves on the rail's `AGE_STOPS` ladder: the highest stop the game's `MinAge` clears. */
export function ageBucket(g: BoardgameRow): GroupKey | null {
  const age = ageOf(g);
  if (age == null) return null;
  for (let i = AGE_STOPS.length - 1; i >= 0; i -= 1) if (age >= AGE_STOPS[i]) return { key: String(AGE_STOPS[i]), label: `${AGE_STOPS[i]}+` };
  return { key: String(AGE_STOPS[0]), label: `${AGE_STOPS[0]}+` };
}

/** Complexity shelves in 0.5 steps of BGG's `AverageWeight` (1–5); the top step is 4.5–5.0. */
export function weightBucket(g: BoardgameRow): GroupKey | null {
  const w = weightOf(g);
  if (w == null) return null;
  const lo = Math.min(Math.max(Math.floor(w * 2) / 2, 1), 4.5);
  return { key: lo.toFixed(1), label: `${lo.toFixed(1)}–${(lo + 0.5).toFixed(1)}` };
}

/**
 * BGG rating tiers. BGG's average is 0–10 and the interesting range is 5.5–8.5, so the tiers are
 * half a point wide from 6.0 up and one floor below: 8.0+ · 7.5–8.0 · 7.0–7.5 · 6.5–7.0 · 6.0–6.5 ·
 * Under 6.0. An unrated game (no `averageRating`) has no tier.
 */
export const RATING_TIERS: readonly number[] = [8, 7.5, 7, 6.5, 6];

export function ratingTier(g: BoardgameRow): GroupKey | null {
  const r = g.averageRating == null ? null : Number(g.averageRating);
  if (r == null || !Number.isFinite(r) || r <= 0) return null;
  for (let i = 0; i < RATING_TIERS.length; i += 1) {
    if (r < RATING_TIERS[i]) continue;
    const hi = RATING_TIERS[i - 1];
    return { key: RATING_TIERS[i].toFixed(1), label: hi == null ? `${RATING_TIERS[i].toFixed(1)}+` : `${RATING_TIERS[i].toFixed(1)}–${hi.toFixed(1)}` };
  }
  return { key: "0.0", label: `Under ${RATING_TIERS[RATING_TIERS.length - 1].toFixed(1)}` };
}

/**
 * What KIND of row this is. `ThingType` names the BGG thing; `baseGameId` is the site's own
 * grouping, and 24 standalone rows are deliberately parked under a base game — those are neither a
 * plain base game nor an expansion, so they get their own shelf rather than being miscounted.
 */
export function kindBucket(g: BoardgameRow): GroupKey | null {
  const t = (g.thingType ?? "").toLowerCase();
  if (t === "boardgameexpansion") return { key: "expansion", label: "Expansions" };
  if (t === "boardgameaccessory") return { key: "accessory", label: "Accessories" };
  if (g.baseGameId != null) return { key: "grouped", label: "Grouped under a base game" };
  return { key: "base", label: "Base games" };
}

/** BGG descriptions arrive as HTML; the Newspaper wants a paragraph of prose. */
export function plainText(html: string | null | undefined, max = 600): string | null {
  if (!html) return null;
  // Block tags become a space (paragraphs must not fuse); inline tags vanish (a bold word keeps its stop).
  const text = html.replace(/<\/?(p|br|div|li|h\d|ul|ol|blockquote)\b[^>]*>/gi, " ").replace(/<[^>]+>/g, "").replace(/&nbsp;/g, " ").replace(/&amp;/g, "&").replace(/&quot;/g, '"').replace(/&#10;|&#13;/g, " ").replace(/\s+/g, " ").trim();
  if (!text) return null;
  return text.length > max ? `${text.slice(0, max).replace(/\s+\S*$/, "")}…` : text;
}

/** The Newspaper's per-group detail: the group's best-rated game tells the story, under the grouping's name. */
function groupDetail(kicker: string) {
  return (_key: GroupKey, items: CardItem[]): CardGroup["detail"] => {
    const lead = [...items].sort((a, b) => (b.rating ?? -1) - (a.rating ?? -1))[0];
    const synopsis = lead ? plainText(rawOf(lead).description) : null;
    return { kicker, ...(synopsis ? { synopsis, byline: `From ${lead.title}` } : {}) };
  };
}

function facetGrouper(value: string, label: string, one: string, pick: (f: BoardgameFacets) => string[] | undefined, facetsById: Map<number, BoardgameFacets>): ClientGrouper {
  return {
    value,
    label,
    one,
    keysOf: (i) => (pick(facetsById.get(i.id) ?? { id: i.id }) ?? []).filter(Boolean).map((k) => ({ key: k, label: k })),
    detail: groupDetail(label),
  };
}

/**
 * The Boardgames axis set (R9 S8). The BGG link facets come off `/API/Boardgames/Facets`; everything
 * else is computed from the row itself on the ladders the rail already uses, so a shelf and its
 * facet always describe the same set. The numeric ladders (`players`, `time`, `age`, `weight`,
 * `rating`) declare `alpha: false` — their heads are in numeric order, so there is no letter rail.
 */
export function boardgameGroupers(facetsById: Map<number, BoardgameFacets>, expansionMap: Record<number, BoardgameRow[]> = {}): ClientGrouper[] {
  return [
    facetGrouper("publisher", "Publisher", "publisher", (f) => f.publishers, facetsById),
    facetGrouper("family", "Family", "family", (f) => f.families, facetsById),
    { value: "decade", label: "Decade", one: "decade", order: "keyDesc", alpha: false, keysOf: (i) => (i.year ? { key: String(Math.floor(i.year / 10) * 10), label: `${Math.floor(i.year / 10) * 10}s` } : null), detail: groupDetail("Decade") },
    // The three remaining ladders (time / age / weight) name a BAND, not a thing - "one per play
    // time" is not a sentence - so they carry no `one` and keep the generic "One per group". Same
    // for base-or-expansion, which is a pair.
    { value: "players", label: "Players", one: "player count", order: "keyAsc", alpha: false, keysOf: (i) => playersBuckets(rawOf(i), expansionMap[i.id] ?? NO_EXPANSIONS), detail: groupDetail("Players") },
    { value: "time", label: "Play time", order: "keyAsc", alpha: false, keysOf: (i) => timeBucket(rawOf(i)), detail: groupDetail("Play time") },
    { value: "age", label: "Min age", order: "keyAsc", alpha: false, keysOf: (i) => ageBucket(rawOf(i)), detail: groupDetail("Min age") },
    { value: "weight", label: "Weight", order: "keyAsc", alpha: false, keysOf: (i) => weightBucket(rawOf(i)), detail: groupDetail("Weight") },
    { value: "rating", label: "Rating tier", one: "rating tier", order: "keyDesc", alpha: false, keysOf: (i) => ratingTier(rawOf(i)), detail: groupDetail("Rating tier") },
    { value: "kind", label: "Base or expansion", keysOf: (i) => kindBucket(rawOf(i)), detail: groupDetail("Base or expansion") },
    facetGrouper("designer", "Designer", "designer", (f) => f.designers, facetsById),
    facetGrouper("category", "Category", "category", (f) => f.categories, facetsById),
    facetGrouper("mechanic", "Mechanic", "mechanic", (f) => f.mechanics, facetsById),
  ];
}

const NO_EXPANSIONS: BoardgameRow[] = [];

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
  /** The section's own Grid card (R9 S3) — the page supplies it, module-level, with the live expansion map. */
  renderCard?: CatalogSource["renderCard"];
}

export function createBoardgamesSource(o: BoardgamesSourceOptions): CatalogSource {
  const items = o.games.map((g) => toBoardgameCard(g, o.expansionMap[g.id] ?? []));
  return createClientSource({
    queryKey: `boardgames:${o.listKey}`,
    title: "Board Games",
    itemNoun: "game",
    groupNoun: "groups",
    itemsLabels: { items: "Games" },
    items,
    groups: boardgameGroupers(o.facetsById, o.expansionMap),
    sorts: BOARDGAME_SORTS,
    currentSort: o.currentSort && BOARDGAME_SORTS.some((s) => s.value === o.currentSort) ? o.currentSort : "name",
    defaultGroup: "publisher",
    directoryGroup: "publisher",
    listColumns: BOARDGAME_LIST_COLUMNS,
    defaultAspect: BOARDGAME_ASPECT,
    onOpen: (item) => o.onOpen(item.id),
    onOpenGroup: o.onOpenGroup,
    renderCard: o.renderCard,
    gridClass: "bx-grid--boardgames",
    gridCell: BOARDGAME_GRID_CELL,
  });
}
