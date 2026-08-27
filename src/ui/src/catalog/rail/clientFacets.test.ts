import { describe, expect, it } from "vitest";
import { applyFacetState, countClientFacets, decadeOf, matchesFacetState } from "./clientFacets";
import { emptyFacetState } from "./facetUrl";

interface Game { id: number; name: string; year: number | null; genres: string[]; players: number[]; owned?: boolean }

const games: Game[] = [
  { id: 1, name: "Catan", year: 1995, genres: ["Strategy", "Trading"], players: [3, 4], owned: true },
  { id: 2, name: "Carcassonne", year: 2000, genres: ["Strategy", "Tiles"], players: [2, 3, 4, 5] },
  { id: 3, name: "Codenames", year: 2015, genres: ["Party"], players: [2, 4, 6, 8], owned: true },
  { id: 4, name: "Untitled", year: null, genres: [], players: [] },
];

const extractors = {
  genre: (g: Game) => g.genres,
  players: (g: Game) => g.players,
  decades: (g: Game) => decadeOf(g.year),
};
const opts = { text: (g: Game) => g.name, year: (g: Game) => g.year, flags: { owned: (g: Game) => !!g.owned } };
const state = (patch: Partial<ReturnType<typeof emptyFacetState>>) => ({ ...emptyFacetState(), ...patch });
const ids = (list: Game[]) => list.map((g) => g.id);

describe("applyFacetState — the client twin of the server filter", () => {
  it("requires ALL included values of a facet by default, ANY when the facet opts in", () => {
    expect(ids(applyFacetState(games, state({ include: { genre: ["Strategy", "Trading"] } }), extractors, opts))).toEqual([1]);
    expect(ids(applyFacetState(games, state({ include: { genre: ["Strategy", "Trading"] } }), extractors, { ...opts, anyOf: ["genre"] }))).toEqual([1, 2]);
  });

  it("ANDs facets together and NOTs the excludes", () => {
    expect(ids(applyFacetState(games, state({ include: { genre: ["Strategy"], players: [2] } }), extractors, opts))).toEqual([2]);
    expect(ids(applyFacetState(games, state({ include: { genre: ["Strategy"] }, exclude: { genre: ["Tiles"] } }), extractors, opts))).toEqual([1]);
  });

  it("matches values case-insensitively (strings) and exactly (numbers)", () => {
    expect(ids(applyFacetState(games, state({ include: { genre: ["party"] } }), extractors, opts))).toEqual([3]);
    expect(ids(applyFacetState(games, state({ include: { players: [8] } }), extractors, opts))).toEqual([3]);
  });

  it("searches the text and brackets the year (an undated row leaves once a range is set)", () => {
    expect(ids(applyFacetState(games, state({ q: "ca" }), extractors, opts))).toEqual([1, 2]);
    expect(ids(applyFacetState(games, state({ yearMin: 2000, yearMax: null }), extractors, opts))).toEqual([2, 3]);
    expect(ids(applyFacetState(games, state({ yearMin: null, yearMax: 1999 }), extractors, opts))).toEqual([1]);
  });

  it("runs the section's own flag tests and ignores flags it has no test for", () => {
    expect(ids(applyFacetState(games, state({ flags: { owned: true } }), extractors, opts))).toEqual([1, 3]);
    expect(ids(applyFacetState(games, state({ flags: { mystery: true } }), extractors, opts))).toEqual([1, 2, 3, 4]);
  });

  it("an unknown facet key in the state excludes everything (no row carries it)", () => {
    expect(matchesFacetState(games[0], state({ include: { colour: ["red"] } }), extractors, opts)).toBe(false);
  });
});

describe("countClientFacets — the rail's option rows from the same rows", () => {
  it("counts each value once per row, most-common first then alphabetical, with labels", () => {
    const f = countClientFacets(games, extractors, { labelOf: { decades: (v) => `${v}s` } });
    expect(f.genre).toEqual([
      { value: "Strategy", label: "Strategy", count: 2 },
      { value: "Party", label: "Party", count: 1 },
      { value: "Tiles", label: "Tiles", count: 1 },
      { value: "Trading", label: "Trading", count: 1 },
    ]);
    expect(f.players.slice(0, 2)).toEqual([{ value: 4, label: "4", count: 3 }, { value: 2, label: "2", count: 2 }]);
    expect(f.decades).toEqual([
      { value: 1990, label: "1990s", count: 1 },
      { value: 2000, label: "2000s", count: 1 },
      { value: 2010, label: "2010s", count: 1 },
    ]);
  });

  it("folds case variants of one string value into one row", () => {
    const f = countClientFacets([{ t: "Noir" }, { t: "noir" }, { t: "NOIR" }], { t: (r: { t: string }) => r.t });
    expect(f.t).toEqual([{ value: "Noir", label: "Noir", count: 3 }]);
  });
});

describe("decadeOf", () => {
  it("floors to the decade and rejects junk", () => {
    expect(decadeOf(1995)).toBe(1990);
    expect(decadeOf(2000)).toBe(2000);
    expect(decadeOf(null)).toBeNull();
    expect(decadeOf(0)).toBeNull();
  });
});

describe("applyFacetState — fixed-scale ranges", () => {
  interface Row { id: number; age: number | null; time: [number, number] | null }
  const rows: Row[] = [
    { id: 1, age: 8, time: [30, 60] },
    { id: 2, age: 12, time: [90, 120] },
    { id: 3, age: 14, time: [15, 20] },
    { id: 4, age: null, time: null },
  ];
  const ropts = { ranges: { age: (r: Row) => r.age, time: (r: Row) => r.time } };
  const rid = (list: Row[]) => list.map((r) => r.id);

  it("brackets a number on both sides; a thumb at an open side is no bound", () => {
    expect(rid(applyFacetState(rows, state({ ranges: { age: { min: 12, max: null } } }), {}, ropts))).toEqual([2, 3]);
    expect(rid(applyFacetState(rows, state({ ranges: { age: { min: null, max: 8 } } }), {}, ropts))).toEqual([1]);
    expect(rid(applyFacetState(rows, state({ ranges: { age: { min: 10, max: 12 } } }), {}, ropts))).toEqual([2]);
  });

  it("a span passes when it overlaps the range; an item without the value is out once a range is set; an unset range is ignored", () => {
    expect(rid(applyFacetState(rows, state({ ranges: { time: { min: 45, max: 100 } } }), {}, ropts))).toEqual([1, 2]);
    expect(rid(applyFacetState(rows, state({ ranges: { time: { min: null, max: 20 } } }), {}, ropts))).toEqual([3]);
    expect(rid(applyFacetState(rows, state({ ranges: { age: { min: null, max: null } } }), {}, ropts))).toEqual([1, 2, 3, 4]);
    expect(rid(applyFacetState(rows, state({ ranges: { other: { min: 1, max: 2 } } }), {}, ropts))).toEqual([]);
  });
});
