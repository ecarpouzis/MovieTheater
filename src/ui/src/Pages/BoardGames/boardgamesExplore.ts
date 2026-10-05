/**
 * The Boardgames Explore composition (R9 S7) — built entirely out of the catalog the section
 * ALREADY ships to the browser (`useBoardgamesCatalog`: the cached `/odata/Boardgames` rows plus
 * `/API/Boardgames/Facets`). Zero new endpoints, zero extra fetches: the tab costs one render.
 *
 * | Rail | Source |
 * |---|---|
 * | spotlight + `top` | the cached rows, by BGG rating |
 * | `recent` | the cached rows, by descending id |
 * | `designers` | the cached facet rows — GROUP cards, routed by `f=designer:<name>` |
 * | `random` | the cached rows, deterministically shuffled by `?seed=` |
 * | `ways-in` | the cached rows cut by players / play time / mechanic / category — DOORS |
 * | `top` | now the RANKED ten; the marquee rotates a seeded handful of the next thirty |
 * | `designer` | one designer with three or more games here, by the seed — a FOCUS plate |
 * | `quick` | three columns: quick to teach (light), the big table (6+ players), brain burners (heavy) |
 *
 * The same honesty note the Music tab carries: **a boardgame has no "added" stamp.** The row has
 * `yearPublished` and nothing about when the BGG sync inserted it, so "Newest on the shelf" orders
 * by descending id — the identity column IS the insert order — and is labelled for that.
 */
import { exploreColumn, exploreColumns, exploreDoors, exploreFocus, exploreRail, exploreResponse, distinctCovers, seededItem } from "../../catalog/explore/composeExplore";
import type { HeroDetail } from "../../catalog/explore/HeroSpotlight";
import { facetHref } from "../../catalog/rail/facetUrl";
import type { CardItem, ExploreDoor, ExploreResponse } from "../../catalog/types";
import { toBoardgameCard, type BoardgameFacets, type BoardgameRow } from "../../catalog/sources/boardgamesSource";

export interface BoardgamesExploreInput {
  games?: BoardgameRow[];
  facetsById?: Map<number, BoardgameFacets>;
  /** Which "Ways in" tab is showing (computed from the cached rows, so every tab is free). */
  doorAxis?: string;
  seed?: number;
}

export const BOARDGAME_SPOTLIGHT_SIZE = 5;
const RECENT_TAKE = 24;
const RANDOM_TAKE = 24;
const DESIGNERS_TAKE = 18;
/** A designer with one game is a credit, not a shelf. */
const DESIGNER_MIN = 2;

export const BOARDGAMES_MORE = {
  top: "/boardgames?sort=rating_desc",
  designers: "/boardgames?group=designer",
  random: "/boardgames",
};

/** `/boardgames?f=designer:Reiner%20Knizia` — the section's rail takes the designer NAME. */
export function boardgameFacetHref(token: string, value: string): string {
  return facetHref("/boardgames", [[token, value]]);
}

/** A base game — expansions have their own place on the base game's card, never a rail of their own. */
export function isBaseGame(g: BoardgameRow): boolean {
  return g.baseGameId == null;
}

/** Deterministic shuffle: the same seed is the same page, so Back walks the rolls. */
export function seededShuffle<T>(rows: readonly T[], seed: number): T[] {
  const list = rows.slice();
  let s = (seed || 1) >>> 0;
  for (let i = list.length - 1; i > 0; i -= 1) {
    s = (s * 1664525 + 1013904223) >>> 0;
    const j = s % (i + 1);
    [list[i], list[j]] = [list[j], list[i]];
  }
  return list;
}

export interface DesignerShelf { name: string; count: number; face?: BoardgameRow }

/** Designers with more than one game on the shelf, biggest first; the face is their best-rated game. */
export function designerShelves(
  games: readonly BoardgameRow[],
  facetsById: Map<number, BoardgameFacets> | undefined,
  take = DESIGNERS_TAKE,
): DesignerShelf[] {
  if (!facetsById || facetsById.size === 0) return [];
  const byName = new Map<string, { count: number; face?: BoardgameRow }>();
  for (const g of games) {
    for (const name of facetsById.get(Number(g.id))?.designers ?? []) {
      const key = (name ?? "").trim();
      if (!key || key.toLowerCase() === "(uncredited)") continue;
      const hit = byName.get(key) ?? { count: 0, face: undefined };
      hit.count += 1;
      if (!hit.face || Number(g.averageRating ?? 0) > Number(hit.face.averageRating ?? 0)) hit.face = g;
      byName.set(key, hit);
    }
  }
  return [...byName.entries()]
    .filter(([, v]) => v.count >= DESIGNER_MIN)
    .sort((a, b) => b[1].count - a[1].count || a[0].localeCompare(b[0]))
    .slice(0, take)
    .map(([name, v]) => ({ name, count: v.count, face: v.face }));
}

const rated = (g: BoardgameRow) => Number(g.averageRating ?? 0);

// ── Doors, focus, columns ──────────────────────────────────────────────────────────────────────

export const BOARDGAME_DOOR_AXES = [
  { key: "players", label: "Players" },
  { key: "time", label: "Play time" },
  { key: "mechanic", label: "Mechanic" },
  { key: "category", label: "Category" },
] as const;
const DOORS_TAKE = 18;
const DOOR_MIN = 3;
/** The play-time bands a door offers, in minutes — each becomes the rail's `t=` range. */
export const TIME_BANDS = [
  { key: "-30", label: "Under 30 minutes", min: 0, max: 30 },
  { key: "30-60", label: "Up to an hour", min: 31, max: 60 },
  { key: "60-120", label: "One to two hours", min: 61, max: 120 },
  { key: "120-", label: "An evening", min: 121, max: Infinity },
];

function coversOf(rows: readonly BoardgameRow[]): ExploreDoor["covers"] {
  return rows.slice().sort((a, b) => rated(b) - rated(a)).slice(0, 24).map((g) => toBoardgameCard(g)).map((c) => ({ src: c.imageThumbUrl ?? c.imageUrl, hue: c.hue }));
}

/** The cached rows cut along one axis, as doors. */
export function boardgameDoors(axis: string, games: readonly BoardgameRow[], facetsById: Map<number, BoardgameFacets> | undefined): ExploreDoor[] {
  const doors = rawBoardgameDoors(axis, games, facetsById);
  const covers = distinctCovers(doors, (d) => d.covers);
  return doors.map((d, i) => ({ ...d, covers: covers[i] }));
}

function rawBoardgameDoors(axis: string, games: readonly BoardgameRow[], facetsById: Map<number, BoardgameFacets> | undefined): ExploreDoor[] {
  if (axis === "players") {
    const doors: ExploreDoor[] = [];
    for (const n of [1, 2, 3, 4, 5, 6, 8]) {
      const fits = games.filter((g) => (g.minPlayers ?? 1) <= n && (g.maxPlayers ?? 0) >= n);
      if (fits.length >= DOOR_MIN) {
        doors.push({ key: String(n), label: n === 1 ? "Solo" : n === 8 ? "Eight or more" : `${n} players`, count: fits.length, href: boardgameFacetHref("players", String(n)), covers: coversOf(fits) });
      }
    }
    return doors;
  }
  if (axis === "time") {
    return TIME_BANDS.map((b) => {
      const fits = games.filter((g) => g.playingTime != null && g.playingTime >= b.min && g.playingTime <= b.max);
      return { key: b.key, label: b.label, count: fits.length, href: `/boardgames?t=${b.key}`, covers: coversOf(fits) };
    }).filter((d) => (d.count ?? 0) >= DOOR_MIN);
  }
  if (!facetsById || facetsById.size === 0) return [];
  const pick = axis === "mechanic" ? (f: BoardgameFacets) => f.mechanics : (f: BoardgameFacets) => f.categories;
  const byName = new Map<string, BoardgameRow[]>();
  for (const g of games) for (const raw of pick(facetsById.get(Number(g.id)) ?? { id: 0 }) ?? []) {
    const name = (raw ?? "").trim();
    if (name) (byName.get(name) ?? byName.set(name, []).get(name)!).push(g);
  }
  return [...byName.entries()].filter(([, rows]) => rows.length >= DOOR_MIN)
    .sort((a, b) => b[1].length - a[1].length || a[0].localeCompare(b[0])).slice(0, DOORS_TAKE)
    .map(([name, rows]) => ({ key: name, label: name, count: rows.length, href: boardgameFacetHref(axis, name), covers: coversOf(rows) }));
}

/** The designer the focus plate looks at: one with three or more games here, by the seed. */
export function pickDesigner(games: readonly BoardgameRow[], facetsById: Map<number, BoardgameFacets> | undefined, seed: number): { name: string; games: BoardgameRow[] } | null {
  if (!facetsById) return null;
  const byName = new Map<string, BoardgameRow[]>();
  for (const g of games) for (const raw of facetsById.get(Number(g.id))?.designers ?? []) {
    const name = (raw ?? "").trim();
    if (name && name.toLowerCase() !== "(uncredited)") (byName.get(name) ?? byName.set(name, []).get(name)!).push(g);
  }
  const list = [...byName.entries()].filter(([, rows]) => rows.length >= 3).sort((a, b) => a[0].localeCompare(b[0]));
  const hit = seededItem(list, seed, 5);
  return hit ? { name: hit[0], games: hit[1].slice().sort((a, b) => rated(b) - rated(a)) } : null;
}

/** BGG descriptions arrive with HTML entities for their line breaks — the first paragraph, as text. */
export function plainDescription(raw: string | null | undefined): string | null {
  if (!raw) return null;
  const text = raw.replace(/&#10;/g, "\n").replace(/&mdash;/g, "\u2014").replace(/&ndash;/g, "\u2013").replace(/&quot;/g, '"')
    .replace(/&rsquo;|&#39;/g, "\u2019").replace(/&amp;/g, "&").replace(/<[^>]+>/g, "");
  return text.split(/\n\s*\n|\n/).map((x) => x.trim()).find((x) => x.length > 40) ?? (text.trim() || null);
}

/** A game's own row as the marquee's lines: the blurb, then players, time and weight. */
export function boardgameHeroDetail(item: CardItem): HeroDetail | null {
  const g = (item.raw ?? null) as BoardgameRow | null;
  if (!g || typeof g !== "object") return null;
  const players = g.minPlayers && g.maxPlayers ? (g.minPlayers === g.maxPlayers ? `${g.minPlayers} players` : `${g.minPlayers}\u2013${g.maxPlayers} players`) : null;
  const meta = [g.yearPublished ? String(g.yearPublished) : null, players, g.playingTime ? `${g.playingTime} min` : null,
    g.averageWeight ? `Weight ${Number(g.averageWeight).toFixed(1)} of 5` : null].filter((x): x is string => !!x);
  return { synopsis: plainDescription(g.description), meta, subtitle: null };
}

const weight = (g: BoardgameRow) => Number(g.averageWeight ?? 0);

export function composeBoardgamesExplore(input: BoardgamesExploreInput): ExploreResponse {
  const bases = (input.games ?? []).filter(isBaseGame);
  const seed = input.seed ?? 1;
  const byRating = bases.slice().sort((a, b) => rated(b) - rated(a));
  // The ten are the ranked module; the marquee rotates a seeded handful of the NEXT best.
  const pool = byRating.length >= 10 + BOARDGAME_SPOTLIGHT_SIZE ? byRating.slice(10, 40) : byRating;
  const spotlight = seededShuffle(pool, seed).slice(0, BOARDGAME_SPOTLIGHT_SIZE).map((g) => toBoardgameCard(g));
  const recent = bases.slice().sort((a, b) => Number(b.id) - Number(a.id)).slice(0, RECENT_TAKE);
  const shuffled = seededShuffle(bases, seed).slice(0, RANDOM_TAKE);
  const designer = pickDesigner(bases, input.facetsById, seed);
  const axis = input.doorAxis ?? "players";
  const doorAxes = BOARDGAME_DOOR_AXES.map((a) => ({ key: a.key, label: a.label, doors: boardgameDoors(a.key, bases, input.facetsById) }));
  const column = (rows: BoardgameRow[], salt: number) => seededShuffle(rows.sort((a, b) => rated(b) - rated(a)).slice(0, 30), seed + salt).slice(0, 6).map((g) => toBoardgameCard(g));

  return exploreResponse(spotlight, [
    bases.length ? exploreDoors("ways-in", "Ways in", doorAxes, axis) : null,
    exploreRail("top", "The ten best-rated", "ranked", byRating.slice(0, 10).map((g) => toBoardgameCard(g)), BOARDGAMES_MORE.top),
    exploreRail("recent", "Newest on the shelf", "wall", recent.map((g) => toBoardgameCard(g))),
    designer
      ? exploreFocus("designer", "A closer look", { name: designer.name, count: designer.games.length, blurb: "Designed by the same hand. Best-rated first.", href: boardgameFacetHref("designer", designer.name) }, designer.games.map((g) => toBoardgameCard(g)))
      : null,
    exploreColumns("quick", "Quick picks", [
      exploreColumn("light", "Quick to teach", column(bases.filter((g) => weight(g) > 0 && weight(g) <= 1.8), 3), "/boardgames?w=-1.8&sort=rating_desc"),
      exploreColumn("big", "The big table", column(bases.filter((g) => (g.maxPlayers ?? 0) >= 6), 5), "/boardgames?f=players%3A6&sort=rating_desc"),
      exploreColumn("heavy", "Brain burners", column(bases.filter((g) => weight(g) >= 3.5), 7), "/boardgames?w=3.5-&sort=rating_desc"),
    ]),
    exploreRail("random", "Pull one off the shelf", "grid", shuffled.map((g) => toBoardgameCard(g)), BOARDGAMES_MORE.random),
  ], input.seed);
}

/** Everything but the shuffles reports a standing fact. */
export const BOARDGAMES_UNSEEDED_RAILS: ReadonlySet<string> = new Set(["top", "recent", "designers", "ways-in"]);
