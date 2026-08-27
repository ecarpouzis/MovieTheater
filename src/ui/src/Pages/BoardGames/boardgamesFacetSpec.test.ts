import { describe, expect, it } from "vitest";
import { emptyFacetState, parseFacetState } from "../../catalog/rail/facetUrl";
import type { BoardgameRow } from "../../catalog/sources/boardgamesSource";
import {
  BOARDGAMES_PARSE_SPEC, applyBoardgameFacets, countBoardgameFacets, formatMinutes, legacyToBoardgamesSearch, playerCounts, sortBoardgames, timeSpan,
} from "./boardgamesFacetSpec";

const game = (id: number, patch: Partial<BoardgameRow>): BoardgameRow => ({ id, name: `Game ${id}`, ...patch });

const catan = game(1, { name: "Catan", minPlayers: 3, maxPlayers: 4, minAge: 10, minPlayTime: 60, maxPlayTime: 120, averageWeight: 2.3, yearPublished: 1995 });
const catan56 = game(11, { name: "Catan: 5-6 Player Extension", minPlayers: 5, maxPlayers: 6, baseGameId: 1, thingType: "boardgameexpansion" });
const codenames = game(2, { name: "Codenames", minPlayers: 2, maxPlayers: 8, minAge: 14, playingTime: 15, averageWeight: 1.3, yearPublished: 2015 });
const gloom = game(3, { name: "Gloomhaven", minPlayers: 1, maxPlayers: 4, minAge: 14, minPlayTime: 60, maxPlayTime: 150, averageWeight: 3.9, yearPublished: 2017 });
const werewolf = game(4, { name: "Werewolf", minPlayers: 8, maxPlayers: 24, minAge: 8, playingTime: 30, averageWeight: 1.4, yearPublished: 1986 });
const unknown = game(5, { name: "Mystery", minAge: 0, playingTime: 30000000, averageWeight: 0, yearPublished: 0 });

const data = {
  expansionMap: { 1: [catan56] },
  facetsById: new Map([
    [1, { id: 1, publishers: ["Kosmos"], designers: ["Klaus Teuber"], categories: ["Negotiation"], mechanics: ["Trading"] }],
    [2, { id: 2, publishers: ["CGE"], designers: ["Vlaada Chvátil"], categories: ["Party"], mechanics: ["Team"] }],
    [3, { id: 3, publishers: ["Cephalofair"], categories: ["Adventure"], mechanics: ["Hand management", "Cooperative"] }],
  ]),
};
const games = [catan, codenames, gloom, werewolf, unknown];
const ids = (list: BoardgameRow[]) => list.map((g) => g.id);
const state = (search: string) => parseFacetState(search, BOARDGAMES_PARSE_SPEC);

describe("boardgames facet extractors", () => {
  it("player counts span min…max, cap at 8 (= 8+), extend through the expansions, and treat a missing side as open", () => {
    expect(playerCounts(catan)).toEqual([3, 4]);
    expect(playerCounts(catan, [catan56])).toEqual([3, 4, 5, 6]);
    expect(playerCounts(werewolf)).toEqual([8]);
    expect(playerCounts(codenames)).toEqual([2, 3, 4, 5, 6, 7, 8]);
    expect(playerCounts(game(9, { maxPlayers: 2 }))).toEqual([1, 2]);
    expect(playerCounts(game(9, { minPlayers: 6 }))).toEqual([6, 7, 8]);
    expect(playerCounts(unknown)).toEqual([]);
  });

  it("play time is the [shortest, longest] span, a lone number stands for both, garbage tops fall back", () => {
    expect(timeSpan(catan)).toEqual([60, 120]);
    expect(timeSpan(codenames)).toEqual([15, 15]);
    expect(timeSpan(unknown)).toBeNull();
    expect(timeSpan(game(9, { minPlayTime: 30, maxPlayTime: 30000000 }))).toEqual([30, 30]);
    expect(formatMinutes(45)).toBe("45m");
    expect(formatMinutes(90)).toBe("1.5h");
    expect(formatMinutes(240)).toBe("4h");
  });
});

describe("applyBoardgameFacets — the same URL contract as Movies, in memory", () => {
  it("players: a count the game (or an expansion) supports; two counts must BOTH be supported", () => {
    expect(ids(applyBoardgameFacets(games, state("?f=players:5"), data))).toEqual([1, 2]);
    expect(ids(applyBoardgameFacets(games, state("?f=players:8"), data))).toEqual([2, 4]);
    expect(ids(applyBoardgameFacets(games, state("?f=players:1&f=players:4"), data))).toEqual([3]);
  });

  it("age: the lower thumb hides the kid games, the upper keeps what suits a young player; unknown ages drop once set", () => {
    expect(ids(applyBoardgameFacets(games, state("?a=12-"), data))).toEqual([2, 3]);
    expect(ids(applyBoardgameFacets(games, state("?a=-10"), data))).toEqual([1, 4]);
    expect(ids(applyBoardgameFacets(games, state("?a=8-10"), data))).toEqual([1, 4]);
  });

  it("time overlaps the span, weight brackets, the link facets include/exclude, q searches the name, y brackets the year", () => {
    expect(ids(applyBoardgameFacets(games, state("?t=-30"), data))).toEqual([2, 4]);
    expect(ids(applyBoardgameFacets(games, state("?t=90-"), data))).toEqual([1, 3]);
    expect(ids(applyBoardgameFacets(games, state("?w=2-3"), data))).toEqual([1]);
    expect(ids(applyBoardgameFacets(games, state("?f=mechanic:Cooperative"), data))).toEqual([3]);
    expect(ids(applyBoardgameFacets(games, state("?x=category:Party&f=players:2"), data))).toEqual([3]);
    expect(ids(applyBoardgameFacets(games, state("?q=cat"), data))).toEqual([1]);
    expect(ids(applyBoardgameFacets(games, state("?y=2010-"), data))).toEqual([2, 3]);
    expect(ids(applyBoardgameFacets(games, emptyFacetState(), data))).toEqual([1, 2, 3, 4, 5]);
  });

  it("counts list players in numeric order with the 8+ label, decades ascending, link facets by count", () => {
    const f = countBoardgameFacets(games, data);
    expect(f.players.map((r) => `${r.label}:${r.count}`)).toEqual(["1:1", "2:2", "3:3", "4:3", "5:2", "6:2", "7:1", "8+:2"]);
    expect(f.decades.map((r) => r.label)).toEqual(["1980s", "1990s", "2010s"]);
    expect(f.mechanic[0]).toEqual({ value: "Cooperative", label: "Cooperative", count: 1 });
    expect(f.publisher.map((r) => r.value)).toEqual(["Cephalofair", "CGE", "Kosmos"]);
  });
});

describe("legacyToBoardgamesSearch", () => {
  it("rewrites the old Selects + title search into the facet form, keeps foreign params, and is null without them", () => {
    expect(legacyToBoardgamesSearch("?view=wall&game=4")).toBeNull();
    expect(legacyToBoardgamesSearch("?players=4&age=10&time=60&mode=title&value=catan&view=wall&sort=rating_desc"))
      .toBe("?view=wall&sort=rating_desc&q=catan&f=players%3A4&a=-10&t=-60");
    expect(legacyToBoardgamesSearch("?players=9")).toBe("?f=players%3A8");
    expect(legacyToBoardgamesSearch("?mode=letter&value=C")).toBe("");
    expect(legacyToBoardgamesSearch("?players=&age=")).toBe("");
  });
});

describe("sortBoardgames", () => {
  it("applies the page's sort vocabulary and leaves name order alone", () => {
    expect(ids(sortBoardgames(games, "rating_desc"))).toEqual([1, 2, 3, 4, 5]);
    expect(ids(sortBoardgames(games, "complexity_desc"))).toEqual([3, 1, 4, 2, 5]);
    expect(ids(sortBoardgames(games, "play_time_asc"))).toEqual([2, 4, 1, 3, 5]);
    expect(ids(sortBoardgames(games, null))).toEqual([1, 2, 3, 4, 5]);
  });
});
