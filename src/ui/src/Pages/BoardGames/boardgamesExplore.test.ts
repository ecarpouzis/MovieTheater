import {
  BOARDGAMES_MORE,
  BOARDGAMES_UNSEEDED_RAILS,
  boardgameDoors,
  boardgameFacetHref,
  boardgameHeroDetail,
  composeBoardgamesExplore,
  pickDesigner,
  plainDescription,
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
    // The fixture rows carry no player counts, play times or mechanics, so every doors axis comes up
    // empty and the module drops rather than drawing empty tabs.
    expect(out.rails.map((r) => r.key)).toEqual(["top", "recent", "random"]);
    expect(out.spotlight).toHaveLength(5);
    expect(out.rails.find((r) => r.key === "top")!.kind).toBe("ranked");
    // An empty shelf: the doors compute to nothing and drop; nothing else has anything to say.
    expect(composeBoardgamesExplore({ games: [] }).rails).toHaveLength(0);
  });

  it("a designer with three or more games gets the focus plate, linked to the designer facet", () => {
    const fx = facets([[1, ["Reiner Knizia"]], [2, ["Reiner Knizia"]], [3, ["Reiner Knizia"]], [4, ["Uwe Rosenberg"]]]);
    const out = composeBoardgamesExplore({ games, facetsById: fx, seed: 1 });
    const focus = out.rails.find((r) => r.key === "designer")!;
    expect(focus.kind).toBe("focus");
    expect(focus.focus).toMatchObject({ name: "Reiner Knizia", count: 3, href: "/boardgames?f=designer%3AReiner+Knizia" });
    expect(pickDesigner(games, facets([[1, ["Pair"]], [2, ["Pair"]]]), 1)).toBeNull();
    expect(BOARDGAMES_MORE.designers).toBe("/boardgames?group=designer");
  });

  it("the doors: player counts as a ladder, play-time bands as the t= range, covers never repeated", () => {
    const table = [
      game(1, { minPlayers: 1, maxPlayers: 4, playingTime: 20 }), game(2, { minPlayers: 2, maxPlayers: 4, playingTime: 45 }),
      game(3, { minPlayers: 2, maxPlayers: 5, playingTime: 90 }), game(4, { minPlayers: 1, maxPlayers: 2, playingTime: 25 }),
      game(5, { minPlayers: 3, maxPlayers: 6, playingTime: 200 }), game(6, { minPlayers: 2, maxPlayers: 4, playingTime: 30 }),
    ];
    const players = boardgameDoors("players", table, undefined);
    expect(players.map((d) => d.key)).toEqual(["2", "3", "4"]);
    expect(players[0].href).toBe("/boardgames?f=players%3A2");
    const firstCovers = new Set(players[0].covers.map((c) => c.src));
    expect(players[1].covers.some((c) => !firstCovers.has(c.src))).toBe(true);
    const time = boardgameDoors("time", table, undefined);
    expect(time[0]).toMatchObject({ key: "-30", href: "/boardgames?t=-30", count: 3 });
  });

  it("the marquee reads the BGG blurb as plain text, with players, time and weight", () => {
    expect(plainDescription("Build a farm.&#10;&#10;Players take turns &mdash; and more text that runs past forty characters here.")).toBe("Players take turns \u2014 and more text that runs past forty characters here.");
    const d = boardgameHeroDetail({ kind: "boardgame", id: 1, key: "boardgame:1", title: "X", aspect: 1, imageUrl: "", raw: game(1, { minPlayers: 2, maxPlayers: 4, playingTime: 60, averageWeight: 2.46 }) })!;
    expect(d.meta).toEqual(["2011", "2\u20134 players", "60 min", "Weight 2.5 of 5"]);
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
