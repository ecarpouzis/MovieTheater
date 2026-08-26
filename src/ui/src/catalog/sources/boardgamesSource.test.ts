import { createBoardgamesSource, facetsMap, playersBucket, playersLabel, playTimeLabel, toBoardgameCard } from "./boardgamesSource";

const game = (id: number, extra: Record<string, unknown> = {}) => ({ id, name: `Game ${id}`, yearPublished: 2000 + id, minPlayers: 2, maxPlayers: 4, minPlayTime: 30, maxPlayTime: 60, minAge: 10, averageRating: 7.84, averageWeight: 2.5, imageVersion: 2, description: "<p>A fine&nbsp;game of <b>cubes</b>.</p>", ...extra });

describe("catalog/boardgamesSource — cards and groupers over the page's list", () => {
  it("maps a game onto a card with the box art URLs, the players/time labels and badges", () => {
    const c = toBoardgameCard(game(3), [game(9, { maxPlayers: 6 })]);
    expect(c.key).toBe("boardgame:3");
    expect(c.imageUrl).toBe("/BoardgameImage/3?v=2");
    expect(c.imageThumbUrl).toBe("/BoardgameImageThumb/3?v=2");
    expect(c.label).toBe("2003");
    expect(c.rating).toBe(78);
    expect(c.subtitle).toBe("👥 2–4→6 · ⏱ 30–60");
    expect(c.badges?.map((b) => b.label)).toEqual(["★ 7.8", "👥 2–4→6", "⏱ 30–60", "⚖ 2.5"]);
    expect(playersLabel({ id: 1, minPlayers: 3, maxPlayers: 3 })).toBe("3");
    expect(playersLabel({ id: 1 })).toBeNull();
    expect(playTimeLabel({ id: 1, playingTime: 1200 })).toBe("∞");
    expect(playersBucket({ id: 1, maxPlayers: 8 })).toEqual({ key: "7", label: "7+ players" });
    expect(playersBucket({ id: 1 })).toBeNull();
  });

  it("groups by facets, decade and players; names the page's sort; the directory walks publishers", async () => {
    const facets = facetsMap([
      { id: 1, publishers: ["Alpha Games", "Beta Press"], designers: ["Someone"], categories: ["Strategy"] },
      { id: 2, publishers: ["Beta Press"], mechanics: ["Drafting"] },
      { id: 3, publishers: [] },
    ]);
    const onOpen = vi.fn();
    const source = createBoardgamesSource({ games: [game(1), game(2, { maxPlayers: 2 }), game(3, { yearPublished: 1995 })], expansionMap: {}, facetsById: facets, listKey: "k", currentSort: "rating_desc", onOpen });
    expect(source.currentSort).toBe("rating_desc");
    expect(source.groups.map((g) => g.value)).toEqual(["publisher", "family", "decade", "players", "designer", "category", "mechanic"]);
    const pubs = await source.fetchGroupBand!(0, 10, 10, "publisher", "rating_desc");
    expect(pubs.groups.map((g) => [g.label, g.totalItems])).toEqual([["Alpha Games", 1], ["Beta Press", 2]]);
    expect(pubs.groups[0].detail).toEqual({ kicker: "Publisher", synopsis: "A fine game of cubes.", byline: "From Game 1" });
    const decades = await source.fetchGroupBand!(0, 10, 10, "decade", "rating_desc");
    expect(decades.groups.map((g) => g.label)).toEqual(["2000s", "1990s"]);
    const players = await source.fetchGroupBand!(0, 10, 10, "players", "rating_desc");
    expect(players.groups.map((g) => [g.label, g.totalItems])).toEqual([["2 players", 1], ["3–4 players", 2]]);
    const roots = await source.directory!.roots();
    expect(roots.map((r) => r.label)).toEqual(["Alpha Games", "Beta Press"]);
    source.onOpen(pubs.groups[0].items[0]);
    expect(onOpen).toHaveBeenCalledWith(1);
    expect(createBoardgamesSource({ games: [], expansionMap: {}, facetsById: facets, listKey: "k", currentSort: "bogus", onOpen }).currentSort).toBe("name");
  });
});
