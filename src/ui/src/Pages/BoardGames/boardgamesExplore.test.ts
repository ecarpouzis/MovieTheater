import {
  BOARDGAMES_MORE,
  BOARDGAMES_UNSEEDED_RAILS,
  boardgameFacetHref,
  composeBoardgamesExplore,
  designerShelves,
  isBaseGame,
  seededShuffle,
} from "./boardgamesExplore";
import type { BoardgameFacets, BoardgameRow } from "../../catalog/sources/boardgamesSource";

const game = (id: number, over: Partial<BoardgameRow> = {}): BoardgameRow =>
  ({ id, name: `Game ${id}`, yearPublished: 2011, averageRating: 7 + (id % 3), ...over });

const facets = (rows: [number, string[]][]): Map<number, BoardgameFacets> =>
  new Map(rows.map(([id, designers]) => [id, { id, designers }]));

describe("Pages/BoardGames/boardgamesExplore — the Boardgames Explore composition (R9 S7)", () => {
  const games = Array.from({ length: 30 }, (_, i) => game(i + 1));

  it("names its rails and drops the ones with nothing in them", () => {
    const out = composeBoardgamesExplore({
      games,
      facetsById: facets([[1, ["Reiner Knizia"]], [2, ["Reiner Knizia"]], [3, ["Uwe Rosenberg"]]]),
      seed: 9,
    });
    expect(out.rails.map((r) => r.key)).toEqual(["top", "recent", "designers", "random"]);
    expect(out.spotlight).toHaveLength(5);
    expect(composeBoardgamesExplore({ games: [] }).rails).toHaveLength(0);
  });

  it("routes a designer GROUP card to the browse with the designer facet", () => {
    const out = composeBoardgamesExplore({
      games,
      facetsById: facets([[1, ["Reiner Knizia"]], [2, ["Reiner Knizia"]]]),
      seed: 1,
    });
    const card = out.rails.find((r) => r.key === "designers")!.items[0];
    expect(card.kind).toBe("person");
    expect(card.groupKey).toBe("Reiner Knizia");
    expect(card.count).toBe(2);
    expect(boardgameFacetHref("designer", card.groupKey!)).toBe("/boardgames?f=designer%3AReiner+Knizia");
    expect(BOARDGAMES_MORE.designers).toBe("/boardgames?group=designer");
  });

  it("a designer with one game is a credit, not a shelf", () => {
    const shelves = designerShelves(games, facets([[1, ["Solo"]], [2, ["Pair"]], [3, ["Pair"]]]));
    expect(shelves.map((s) => s.name)).toEqual(["Pair"]);
    // The face is the designer's best-rated game.
    expect(shelves[0].face!.id).toBe(games.find((g) => g.id === 2)!.averageRating! >= games.find((g) => g.id === 3)!.averageRating! ? 2 : 3);
    expect(designerShelves(games, undefined)).toEqual([]);
  });

  it("expansions never headline: only base games reach a rail", () => {
    const withExpansion = [...games, game(99, { baseGameId: 1, averageRating: 10 })];
    expect(isBaseGame(game(99, { baseGameId: 1 }))).toBe(false);
    const out = composeBoardgamesExplore({ games: withExpansion, seed: 2 });
    const everyId = [...out.spotlight, ...out.rails.flatMap((r) => r.items)].map((c) => c.id);
    expect(everyId).not.toContain(99);
  });

  it("'Newest on the shelf' is descending id — a boardgame has no added stamp", () => {
    const out = composeBoardgamesExplore({ games, seed: 1 });
    const ids = out.rails.find((r) => r.key === "recent")!.items.map((i) => i.id);
    expect(ids.slice(0, 2)).toEqual([30, 29]);
    expect(BOARDGAMES_UNSEEDED_RAILS.has("recent")).toBe(true);
    expect(BOARDGAMES_UNSEEDED_RAILS.has("random")).toBe(false);
  });

  it("the shuffle is seeded — the same seed is the same page", () => {
    const a = seededShuffle(games, 4).map((g) => g.id);
    expect(a).toEqual(seededShuffle(games, 4).map((g) => g.id));
    expect(a).not.toEqual(seededShuffle(games, 5).map((g) => g.id));
  });
});
