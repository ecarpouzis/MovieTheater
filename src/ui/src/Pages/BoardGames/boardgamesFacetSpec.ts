/**
 * The Boardgames browse's facets (R9 S2c), over the list the browser already holds — the client
 * twin of the Movies rail: the same `q/f/x/y` URL contract plus the fixed-scale ranges, applied in
 * memory by `clientFacets`. Rail order (Eric, canvas 2026-08-27): Players · Age · Play time ·
 * Weight · Publisher · Family · Designer · Category · Mechanic · Year. Age is the two-thumb range
 * over `MinAge` with stops 3 · 4 · 5 · 6 · 7 · 8 · 10 · 12 · 14 · 16 · 18+ — fine at the young end,
 * one 18+ cap, and the LOWER thumb filters (12+ hides the kid games).
 *
 * This file also owns the section's translations: the pre-S2c `?players=&age=&time=&mode=title`
 * links rewritten once on entry (`legacyToBoardgamesSearch`), the sort switch the page applies
 * (`sortBoardgames`), and the extractors both the page and the sider rail filter with.
 */
import { applyFacetState, countClientFacets, decadeOf, type ClientFacetOptions, type FacetExtractor, type RangeExtractor } from "../../catalog/rail/clientFacets";
import type { FacetOptionRow, FacetSpec, FacetState, FacetValue, RangeFacetDef } from "../../catalog/rail/facetSpec";
import { parseFacetState, writeFacetState } from "../../catalog/rail/facetUrl";
import type { BoardgameFacets, BoardgameRow } from "../../catalog/sources/boardgamesSource";

/** The player-count facet's cap: 8 stands for "8 or more" (the old rail's "8+ players"). */
export const PLAYERS_CAP = 8;

export const AGE_STOPS = [3, 4, 5, 6, 7, 8, 10, 12, 14, 16, 18];
export const TIME_STOPS = [15, 20, 30, 45, 60, 90, 120, 180, 240];
export const WEIGHT_STOPS = [1, 1.5, 2, 2.5, 3, 3.5, 4, 4.5, 5];

/** A play time whose upper end is above this is BGG garbage (a 30,000,000-minute entry exists). */
const TIME_SANITY_MAX = 6000;

export const formatMinutes = (m: number): string => (m < 60 ? `${m}m` : Number.isInteger(m / 60) ? `${m / 60}h` : `${(m / 60).toFixed(1)}h`);
export const playersFacetLabel = (v: FacetValue): string => (Number(v) >= PLAYERS_CAP ? `${PLAYERS_CAP}+` : String(v));

export const BOARDGAME_RANGES: RangeFacetDef[] = [
  { key: "age", token: "a", label: "Age", one: "Age", stops: AGE_STOPS, openTop: true, after: "players" },
  { key: "time", token: "t", label: "Play time", one: "Time", stops: TIME_STOPS, openTop: true, format: formatMinutes, after: "players" },
  { key: "weight", token: "w", label: "Weight", one: "Weight", stops: WEIGHT_STOPS, format: (v) => v.toFixed(1), after: "players" },
];

/** The BGG link facets, in rail order: state key = token = the `/API/Boardgames/Facets` row's list, singular. */
export const LINK_FACETS = [
  { key: "publisher", label: "Publisher", one: "Publisher", pick: (f: BoardgameFacets) => f.publishers },
  { key: "family", label: "Family", one: "Family", pick: (f: BoardgameFacets) => f.families },
  { key: "designer", label: "Designer", one: "Designer", pick: (f: BoardgameFacets) => f.designers },
  { key: "category", label: "Category", one: "Category", pick: (f: BoardgameFacets) => f.categories },
  { key: "mechanic", label: "Mechanic", one: "Mechanic", pick: (f: BoardgameFacets) => f.mechanics },
] as const;
export type LinkFacetKey = (typeof LINK_FACETS)[number]["key"];

const positive = (v: number | null | undefined): number | null => (v != null && Number.isFinite(v) && v > 0 ? v : null);

/** [min, max] players a row supports, capped at 8; null when BGG has neither number. A missing side is open (1 / 8+). */
export function playerSpan(g: BoardgameRow): [number, number] | null {
  const min = positive(g.minPlayers);
  const max = positive(g.maxPlayers);
  if (min == null && max == null) return null;
  return [Math.min(min ?? 1, PLAYERS_CAP), Math.min(max ?? PLAYERS_CAP, PLAYERS_CAP)];
}

/**
 * Every player count a game plays at (1 … 8, where 8 means 8+), its expansions' spans included —
 * an expansion that takes a 4-player game to 6 makes it a 5- and 6-player game (the old rail's rule).
 */
export function playerCounts(g: BoardgameRow, expansions: readonly BoardgameRow[] = []): number[] {
  const out = new Set<number>();
  for (const row of [g, ...expansions]) {
    const span = playerSpan(row);
    if (!span) continue;
    for (let n = span[0]; n <= span[1]; n += 1) out.add(n);
  }
  return [...out].sort((a, b) => a - b);
}

export const ageOf = (g: BoardgameRow): number | null => positive(g.minAge);
export const weightOf = (g: BoardgameRow): number | null => positive(g.averageWeight);
export const yearOf = (g: BoardgameRow): number | null => positive(g.yearPublished);

/** The [shortest, longest] play in minutes; a lone number stands for both; garbage tops fall back to the low end. */
export function timeSpan(g: BoardgameRow): [number, number] | null {
  const lo = positive(g.minPlayTime) ?? positive(g.playingTime) ?? positive(g.maxPlayTime);
  let hi = positive(g.maxPlayTime) ?? positive(g.playingTime) ?? positive(g.minPlayTime);
  if (lo == null || hi == null || lo > TIME_SANITY_MAX) return null;
  if (hi > TIME_SANITY_MAX) hi = lo;
  return [Math.min(lo, hi), Math.max(lo, hi)];
}

export interface BoardgameFacetData {
  expansionMap: Record<number, BoardgameRow[]>;
  facetsById: Map<number, BoardgameFacets>;
}

/** The facet extractors over a game row (the `f=`/`x=` facets + the decades list the year range sizes). */
export function boardgameExtractors(data: BoardgameFacetData): Record<string, FacetExtractor<BoardgameRow>> {
  const out: Record<string, FacetExtractor<BoardgameRow>> = {
    players: (g) => playerCounts(g, data.expansionMap[g.id] ?? []),
    decades: (g) => decadeOf(yearOf(g)),
  };
  for (const f of LINK_FACETS) out[f.key] = (g) => f.pick(data.facetsById.get(g.id) ?? { id: g.id }) ?? [];
  return out;
}

export const BOARDGAME_RANGE_EXTRACTORS: Record<string, RangeExtractor<BoardgameRow>> = {
  age: ageOf,
  time: timeSpan,
  weight: weightOf,
};

export function boardgameFacetOptions(): ClientFacetOptions<BoardgameRow> {
  return {
    text: (g) => g.name,
    year: yearOf,
    ranges: BOARDGAME_RANGE_EXTRACTORS,
    labelOf: { players: playersFacetLabel, decades: (v) => `${v}s` },
  };
}

/** The games a facet state keeps, in the order given. */
export function applyBoardgameFacets(games: readonly BoardgameRow[], state: FacetState, data: BoardgameFacetData): BoardgameRow[] {
  return applyFacetState(games, state, boardgameExtractors(data), boardgameFacetOptions());
}

/** The option rows the rail lists, counted over `games` (the scope the rail can reach); players in numeric order. */
export function countBoardgameFacets(games: readonly BoardgameRow[], data: BoardgameFacetData): Record<string, FacetOptionRow[]> {
  const counts = countClientFacets(games, boardgameExtractors(data), { labelOf: boardgameFacetOptions().labelOf });
  counts.players = [...(counts.players ?? [])].sort((a, b) => Number(a.value) - Number(b.value));
  counts.decades = [...(counts.decades ?? [])].sort((a, b) => Number(a.value) - Number(b.value));
  return counts;
}

/**
 * The spec over one snapshot of the catalog: `identity` carries the rows' version (the counts are
 * memoized on it) and the expansion toggle (which changes the reachable scope), `games` is that scope.
 */
export function boardgamesFacetSpec(identity: string, games: readonly BoardgameRow[], data: BoardgameFacetData): FacetSpec {
  return {
    identity: `boardgames:${identity}`,
    noun: "games",
    text: true,
    years: { decadesKey: "decades", decadePills: false },
    ranges: BOARDGAME_RANGES,
    facets: [
      { key: "players", token: "players", label: "Players", one: "Players", valueType: "number", render: "pill", excludable: false, labelOf: playersFacetLabel },
      ...LINK_FACETS.map((f) => ({ key: f.key, token: f.key, label: f.label, one: f.one, valueType: "string" as const })),
    ],
    async loadFacets() {
      return countBoardgameFacets(games, data);
    },
  };
}

/** A parse-only spec (no rows): the URL codec needs the tokens, not the counts. */
export const BOARDGAMES_PARSE_SPEC: FacetSpec = boardgamesFacetSpec("parse", [], { expansionMap: {}, facetsById: new Map() });

/** The page's `?sort=` vocabulary applied in memory; `name` (or nothing) keeps the server's name order. */
export function sortBoardgames<T extends BoardgameRow>(games: readonly T[], sort: string | null | undefined): T[] {
  const playTime = (g: BoardgameRow) => g.minPlayTime ?? g.playingTime ?? g.maxPlayTime ?? 0;
  const rating = (g: BoardgameRow) => g.averageRating ?? 0;
  const weight = (g: BoardgameRow) => g.averageWeight ?? 0;
  switch (sort) {
    case "play_time_asc": return [...games].sort((a, b) => playTime(a) - playTime(b));
    case "play_time_desc": return [...games].sort((a, b) => playTime(b) - playTime(a));
    case "rating_asc": return [...games].sort((a, b) => rating(a) - rating(b));
    case "rating_desc": return [...games].sort((a, b) => rating(b) - rating(a));
    case "complexity_asc": return [...games].sort((a, b) => weight(a) - weight(b));
    case "complexity_desc": return [...games].sort((a, b) => weight(b) - weight(a));
    default: return [...games];
  }
}

const LEGACY_KEYS = ["mode", "value", "players", "age", "time"] as const;

/**
 * A pre-S2c browse link (`?players=4&age=10&time=60&mode=title&value=catan` — the old rail's
 * Selects and search) as the facet search it means; null when the URL carries none of them. The
 * old Age select was "suitable for age N" (an upper bound), Play Time "up to N minutes"; a letter
 * mode just drops (the strip scrolls there now). Every other param rides through untouched.
 */
export function legacyToBoardgamesSearch(search: string): string | null {
  const p = new URLSearchParams(search);
  if (!LEGACY_KEYS.some((k) => p.has(k))) return null;
  const mode = p.get("mode") ?? "";
  const value = (p.get("value") ?? "").trim();
  const players = Number(p.get("players"));
  const age = Number(p.get("age"));
  const time = Number(p.get("time"));
  for (const k of LEGACY_KEYS) p.delete(k);
  const state = parseFacetState(`?${p.toString()}`, BOARDGAMES_PARSE_SPEC);
  const next: FacetState = { ...state, include: { ...state.include }, exclude: { ...state.exclude }, ranges: { ...state.ranges }, flags: { ...state.flags } };
  if (mode === "title" && value) next.q = value;
  if (Number.isInteger(players) && players >= 1) next.include.players = [Math.min(players, PLAYERS_CAP)];
  if (Number.isFinite(age) && age > 0) next.ranges.age = { min: null, max: age };
  if (Number.isFinite(time) && time > 0) next.ranges.time = { min: null, max: time };
  writeFacetState(p, next, BOARDGAMES_PARSE_SPEC);
  const s = p.toString();
  return s ? `?${s}` : "";
}

/** The group axis a scoped group header drills to next (publisher → designer, …) — a header click scopes AND regroups. */
export const DRILL_NEXT_GROUP: Record<string, string> = {
  publisher: "designer",
  family: "category",
  designer: "category",
  category: "mechanic",
  mechanic: "category",
  decade: "category",
  players: "category",
  time: "category",
  age: "category",
  weight: "category",
};

/** The three group axes that are the two-thumb RANGES of the rail (`a=`/`t=`/`w=`), not facet values. */
export const RANGE_GROUP_KEYS = new Set(BOARDGAME_RANGES.map((r) => r.key));

/**
 * The [min, max] a ladder shelf stands for, so its header can become the rail's own range: an age
 * shelf is open at the top (`10+` = 10 and up), a play-time or weight shelf is the interval between
 * its stop and the next one. `time`'s "0" key is everything below the first stop.
 */
export function rangeForGroup(groupBy: string, key: string): { min: number | null; max: number | null } | null {
  const n = Number(key);
  if (!Number.isFinite(n)) return null;
  if (groupBy === "age") return { min: n, max: null };
  if (groupBy === "time") {
    if (n <= 0) return { min: null, max: TIME_STOPS[0] };
    const next = TIME_STOPS[TIME_STOPS.indexOf(n) + 1];
    return { min: n, max: next ?? null };
  }
  if (groupBy === "weight") return { min: n, max: Math.min(n + 0.5, WEIGHT_STOPS[WEIGHT_STOPS.length - 1]) };
  return null;
}
