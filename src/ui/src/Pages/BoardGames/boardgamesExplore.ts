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
 *
 * The same honesty note the Music tab carries: **a boardgame has no "added" stamp.** The row has
 * `yearPublished` and nothing about when the BGG sync inserted it, so "Newest on the shelf" orders
 * by descending id — the identity column IS the insert order — and is labelled for that.
 */
import { exploreRail, exploreResponse, groupCard } from "../../catalog/explore/composeExplore";
import { facetHref } from "../../catalog/rail/facetUrl";
import type { CardItem, ExploreResponse } from "../../catalog/types";
import { toBoardgameCard, type BoardgameFacets, type BoardgameRow } from "../../catalog/sources/boardgamesSource";

export interface BoardgamesExploreInput {
  games?: BoardgameRow[];
  facetsById?: Map<number, BoardgameFacets>;
  seed?: number;
}

export const BOARDGAME_SPOTLIGHT_SIZE = 5;
const TOP_TAKE = 24;
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

function toDesignerCard(shelf: DesignerShelf): CardItem {
  const face = shelf.face ? toBoardgameCard(shelf.face) : null;
  return groupCard({
    kind: "person",
    key: shelf.name,
    title: shelf.name,
    count: shelf.count,
    imageUrl: face?.imageUrl,
    imageThumbUrl: face?.imageThumbUrl,
    aspect: 1,
    raw: shelf,
  });
}

const rated = (g: BoardgameRow) => Number(g.averageRating ?? 0);

export function composeBoardgamesExplore(input: BoardgamesExploreInput): ExploreResponse {
  const bases = (input.games ?? []).filter(isBaseGame);
  const byRating = bases.slice().sort((a, b) => rated(b) - rated(a));
  const spotlight = byRating.slice(0, BOARDGAME_SPOTLIGHT_SIZE).map((g) => toBoardgameCard(g));
  const recent = bases.slice().sort((a, b) => Number(b.id) - Number(a.id)).slice(0, RECENT_TAKE);
  const shuffled = seededShuffle(bases, input.seed ?? 1).slice(0, RANDOM_TAKE);

  return exploreResponse(spotlight, [
    exploreRail("top", "Best on the shelf", "grid", byRating.slice(BOARDGAME_SPOTLIGHT_SIZE, TOP_TAKE).map((g) => toBoardgameCard(g)), BOARDGAMES_MORE.top),
    exploreRail("recent", "Newest on the shelf", "wall", recent.map((g) => toBoardgameCard(g))),
    exploreRail("designers", "Designers on the shelf", "strip", designerShelves(bases, input.facetsById).map(toDesignerCard), BOARDGAMES_MORE.designers),
    exploreRail("random", "Pull one off the shelf", "grid", shuffled.map((g) => toBoardgameCard(g)), BOARDGAMES_MORE.random),
  ], input.seed);
}

/** Everything but the shuffle reports a standing fact. */
export const BOARDGAMES_UNSEEDED_RAILS: ReadonlySet<string> = new Set(["top", "recent", "designers"]);
