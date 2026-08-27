import { ageBucket, createBoardgamesSource, facetsMap, kindBucket, playTimeLabel, playersBuckets, playersLabel, ratingTier, timeBucket, toBoardgameCard, weightBucket } from "./boardgamesSource";

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
    // R9 S8: the audited axis set — players fixed, and play time / min age / weight / rating tier /
    // base-or-expansion added.
    expect(source.groups.map((g) => g.value)).toEqual(["publisher", "family", "decade", "players", "time", "age", "weight", "rating", "kind", "designer", "category", "mechanic"]);
    const pubs = await source.fetchGroupBand!(0, 10, 10, "publisher", "rating_desc");
    expect(pubs.groups.map((g) => [g.label, g.totalItems])).toEqual([["Alpha Games", 1], ["Beta Press", 2]]);
    expect(pubs.groups[0].detail).toEqual({ kicker: "Publisher", synopsis: "A fine game of cubes.", byline: "From Game 1" });
    const decades = await source.fetchGroupBand!(0, 10, 10, "decade", "rating_desc");
    expect(decades.groups.map((g) => g.label)).toEqual(["2000s", "1990s"]);
    const roots = await source.directory!.roots();
    expect(roots.map((r) => r.label)).toEqual(["Alpha Games", "Beta Press"]);
    source.onOpen(pubs.groups[0].items[0]);
    expect(onOpen).toHaveBeenCalledWith(1);
    expect(createBoardgamesSource({ games: [], expansionMap: {}, facetsById: facets, listKey: "k", currentSort: "bogus", onOpen }).currentSort).toBe("name");
  });
});

describe("catalog/boardgamesSource — the R9 S8 bucketing rules", () => {
  it("players is RANGE-AWARE: a game stands on every count it plays at, its expansions extending it", () => {
    // The old axis filed a 2–4 game under "3–4 players" alone — invisible to someone with two players.
    expect(playersBuckets(game(1))).toEqual([
      { key: "2", label: "Plays 2" },
      { key: "3", label: "Plays 3" },
      { key: "4", label: "Plays 4" },
    ]);
    // An expansion that takes a 2–4 game to 6 makes it a 5- and a 6-player game.
    expect(playersBuckets(game(1), [game(9, { minPlayers: 2, maxPlayers: 6 })]).map((b) => b.key)).toEqual(["2", "3", "4", "5", "6"]);
    // 8 is the cap and means "8 or more".
    expect(playersBuckets(game(1, { minPlayers: 7, maxPlayers: 12 }))).toEqual([
      { key: "7", label: "Plays 7" },
      { key: "8", label: "Plays 8+" },
    ]);
    expect(playersBuckets({ id: 1 })).toEqual([]);
  });

  it("a players band lists a game under EVERY count, in numeric order, with no letter rail", async () => {
    const source = createBoardgamesSource({
      games: [game(1), game(2, { minPlayers: 2, maxPlayers: 2 })],
      expansionMap: { 1: [game(9, { minPlayers: 2, maxPlayers: 6 })] },
      facetsById: facetsMap([]),
      listKey: "k",
      currentSort: "name",
      onOpen: vi.fn(),
    });
    const players = await source.fetchGroupBand!(0, 10, 10, "players", "name");
    expect(players.groups.map((g) => [g.label, g.totalItems])).toEqual([
      ["Plays 2", 2], ["Plays 3", 1], ["Plays 4", 1], ["Plays 5", 1], ["Plays 6", 1],
    ]);
    // A numeric ladder has no A–Z rail; the strip falls back to page numbers.
    expect(await source.groupLetters!("players", "name")).toEqual([]);
    expect((await source.groupLetters!("publisher", "name")).length).toBeGreaterThanOrEqual(0);
  });

  it("play time files on the rail's TIME_STOPS ladder, by the midpoint of the sane span", () => {
    expect(timeBucket(game(1))).toEqual({ key: "45", label: "45m–1h" }); // 30–60 → 45
    expect(timeBucket({ id: 1, playingTime: 10 })).toEqual({ key: "0", label: "Under 15m" });
    expect(timeBucket({ id: 1, minPlayTime: 240, maxPlayTime: 300 })).toEqual({ key: "240", label: "4h+" });
    // BGG's garbage tops (a 30,000,000-minute entry exists) fall back to the low end.
    expect(timeBucket({ id: 1, minPlayTime: 60, maxPlayTime: 30_000_000 })).toEqual({ key: "60", label: "1h–1.5h" });
    expect(timeBucket({ id: 1 })).toBeNull();
  });

  it("min age files on the AGE_STOPS ladder — the highest stop the game clears", () => {
    expect(ageBucket(game(1))).toEqual({ key: "10", label: "10+" });
    expect(ageBucket({ id: 1, minAge: 9 })).toEqual({ key: "8", label: "8+" });
    expect(ageBucket({ id: 1, minAge: 2 })).toEqual({ key: "3", label: "3+" });
    expect(ageBucket({ id: 1, minAge: 21 })).toEqual({ key: "18", label: "18+" });
    expect(ageBucket({ id: 1 })).toBeNull();
  });

  it("weight is 0.5 steps of AverageWeight, capped at the 4.5–5.0 step", () => {
    expect(weightBucket(game(1))).toEqual({ key: "2.5", label: "2.5–3.0" });
    expect(weightBucket({ id: 1, averageWeight: 2.49 })).toEqual({ key: "2.0", label: "2.0–2.5" });
    expect(weightBucket({ id: 1, averageWeight: 5 })).toEqual({ key: "4.5", label: "4.5–5.0" });
    expect(weightBucket({ id: 1, averageWeight: 0 })).toBeNull();
  });

  it("the rating tiers are 8.0+ / 7.5–8.0 / 7.0–7.5 / 6.5–7.0 / 6.0–6.5 / Under 6.0", () => {
    expect(ratingTier({ id: 1, averageRating: 8.4 })).toEqual({ key: "8.0", label: "8.0+" });
    expect(ratingTier(game(1))).toEqual({ key: "7.5", label: "7.5–8.0" }); // 7.84
    expect(ratingTier({ id: 1, averageRating: 7 })).toEqual({ key: "7.0", label: "7.0–7.5" });
    expect(ratingTier({ id: 1, averageRating: 6.5 })).toEqual({ key: "6.5", label: "6.5–7.0" });
    expect(ratingTier({ id: 1, averageRating: 6.2 })).toEqual({ key: "6.0", label: "6.0–6.5" });
    expect(ratingTier({ id: 1, averageRating: 5.1 })).toEqual({ key: "0.0", label: "Under 6.0" });
    expect(ratingTier({ id: 1 })).toBeNull();
  });

  it("base-or-expansion reads ThingType AND the site's own baseGameId grouping", () => {
    expect(kindBucket({ id: 1, thingType: "boardgame" })).toEqual({ key: "base", label: "Base games" });
    expect(kindBucket({ id: 1, thingType: "boardgameexpansion", baseGameId: 5 })).toEqual({ key: "expansion", label: "Expansions" });
    expect(kindBucket({ id: 1, thingType: "boardgameaccessory" })).toEqual({ key: "accessory", label: "Accessories" });
    // The 24 standalone rows parked under a base game are neither — they get their own shelf.
    expect(kindBucket({ id: 1, thingType: "boardgame", baseGameId: 5 })).toEqual({ key: "grouped", label: "Grouped under a base game" });
  });

  it("the ladder bands come out in ladder order, best-rated tier first", async () => {
    const source = createBoardgamesSource({
      games: [game(1, { averageRating: 8.2, averageWeight: 4.9, minAge: 14, minPlayTime: 120, maxPlayTime: 120 }), game(2, { averageRating: 6.1, averageWeight: 1.2, minAge: 6, minPlayTime: 20, maxPlayTime: 20 })],
      expansionMap: {},
      facetsById: facetsMap([]),
      listKey: "k",
      currentSort: "name",
      onOpen: vi.fn(),
    });
    expect((await source.fetchGroupBand!(0, 10, 10, "age", "name")).groups.map((g) => g.label)).toEqual(["6+", "14+"]);
    expect((await source.fetchGroupBand!(0, 10, 10, "time", "name")).groups.map((g) => g.label)).toEqual(["20m–30m", "2h–3h"]);
    expect((await source.fetchGroupBand!(0, 10, 10, "weight", "name")).groups.map((g) => g.label)).toEqual(["1.0–1.5", "4.5–5.0"]);
    expect((await source.fetchGroupBand!(0, 10, 10, "rating", "name")).groups.map((g) => g.label)).toEqual(["8.0+", "6.0–6.5"]);
  });
});
