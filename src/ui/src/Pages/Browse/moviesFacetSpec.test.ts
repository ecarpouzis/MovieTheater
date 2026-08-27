import { describe, expect, it } from "vitest";
import { parseFacetState } from "../../catalog/rail/facetUrl";
import {
  MOVIES_PARSE_SPEC, isPlainMoviesSearch, legacyToFacetSearch, moviesFacetSpec, moviesFilterParams, myListsOf,
  seededMoviesSearch, typesFromSearch, typesOf,
} from "./moviesFacetSpec";

const parse = (search: string) => parseFacetState(search, MOVIES_PARSE_SPEC);

function memoryStorage(): Pick<Storage, "getItem" | "setItem"> {
  const m = new Map<string, string>();
  return { getItem: (k) => m.get(k) ?? null, setItem: (k, v) => { m.set(k, v); } };
}

describe("moviesFilterParams — the URL state in /API/Browse's vocabulary", () => {
  it("maps includes, excludes, tags, MPA, years and the viewer's lists", () => {
    const state = parse("?q=heat&f=type:Movies&f=genre:Crime&x=genre:Horror&f=franchise:mann-verse&f=person:Al%20Pacino&x=person:Bob&f=mood:tense&x=subgenre:slasher&f=mpa:3&f=mpa:5&y=1990-1999&my=seen,want");
    const p = moviesFilterParams(state);
    expect(p.get("q")).toBe("heat");
    expect(p.getAll("genre")).toEqual(["Crime"]);
    expect(p.getAll("exGenre")).toEqual(["Horror"]);
    expect(p.getAll("franchise")).toEqual(["mann-verse"]);
    expect(p.getAll("person")).toEqual(["Al Pacino"]);
    expect(p.getAll("exPerson")).toEqual(["Bob"]);
    expect(p.getAll("tag")).toEqual(["mood:tense"]);
    expect(p.getAll("exTag")).toEqual(["subgenre:slasher"]);
    expect(p.get("mpa")).toBe("3,5");
    expect(p.get("yearMin")).toBe("1990");
    expect(p.get("yearMax")).toBe("1999");
    expect(p.get("my")).toBe("seen,want");
    // The Type scope is the search hook's own param, never a filter param.
    expect(p.has("type")).toBe(false);
    expect(p.has("types")).toBe(false);
    expect(typesOf(state)).toEqual(["Movies"]);
    expect(myListsOf(state)).toEqual(["seen", "want"]);
  });

  it("is empty for the plain landing and drops junk MPA values", () => {
    expect(moviesFilterParams(parse("?f=type:Movies")).toString()).toBe("");
    expect(moviesFilterParams(parse("?f=mpa:9&f=mpa:x")).has("mpa")).toBe(false);
  });
});

describe("the Type scope", () => {
  it("reads f=type: entries API-spelled, de-duplicated, unknowns dropped", () => {
    expect(typesFromSearch("?f=type:movies&f=type:Series&f=type:MOVIES&f=type:Books&f=genre:Crime")).toEqual(["Movies", "Series"]);
    expect(typesFromSearch("")).toEqual([]);
  });

  it("keys the spec identity on the scope so the counts describe it", () => {
    expect(moviesFacetSpec("eric:3", ["Movies", "Series"]).identity).toBe("movies:eric:3:Movies,Series");
    expect(moviesFacetSpec("eric:3").identity).toBe("movies:eric:3:");
  });
});

describe("isPlainMoviesSearch — the landing test", () => {
  it("is plain with only the Type scope, not with any other narrowing", () => {
    expect(isPlainMoviesSearch(parse(""))).toBe(true);
    expect(isPlainMoviesSearch(parse("?f=type:Movies&f=type:Series"))).toBe(true);
    expect(isPlainMoviesSearch(parse("?f=genre:Crime"))).toBe(false);
    expect(isPlainMoviesSearch(parse("?x=genre:Crime"))).toBe(false);
    expect(isPlainMoviesSearch(parse("?q=heat"))).toBe(false);
    expect(isPlainMoviesSearch(parse("?y=1990-"))).toBe(false);
    expect(isPlainMoviesSearch(parse("?my=seen"))).toBe(false);
  });
});

describe("legacyToFacetSearch — pre-S2 links keep working", () => {
  it("leaves a facet URL alone", () => {
    expect(legacyToFacetSearch("?f=genre:Crime&sort=alpha")).toBeNull();
    expect(legacyToFacetSearch("")).toBeNull();
  });

  it("rewrites the rail's old vocabulary into facets and keeps the rest", () => {
    const out = legacyToFacetSearch("?mode=genre&value=Crime,Drama&types=Movies,Series&sort=imdb&view=wall&title=movie:12");
    const p = new URLSearchParams(out!);
    expect(p.getAll("f")).toEqual(["type:Movies", "type:Series", "genre:Crime", "genre:Drama"]);
    expect(p.get("sort")).toBe("imdb");
    expect(p.get("view")).toBe("wall");
    expect(p.get("title")).toBe("movie:12");
    expect(p.has("mode")).toBe(false);
    expect(p.has("value")).toBe(false);
    expect(p.has("types")).toBe(false);
  });

  it("maps every old mode", () => {
    expect(new URLSearchParams(legacyToFacetSearch("?mode=title&value=Heat")!).get("q")).toBe("Heat");
    expect(new URLSearchParams(legacyToFacetSearch("?mode=actor&value=Al%20Pacino")!).getAll("f")).toEqual(["person:Al Pacino"]);
    expect(new URLSearchParams(legacyToFacetSearch("?mode=franchise&value=mann-verse")!).getAll("f")).toEqual(["franchise:mann-verse"]);
    expect(new URLSearchParams(legacyToFacetSearch("?mode=rating&value=5,6")!).getAll("f")).toEqual(["mpa:5"]);
    expect(new URLSearchParams(legacyToFacetSearch("?mode=rating&value=6")!).getAll("f")).toEqual(["mpa:5"]);
    expect(new URLSearchParams(legacyToFacetSearch("?mode=letter&value=H")!).get("sort")).toBe("alpha");
    expect(new URLSearchParams(legacyToFacetSearch("?mode=seen")!).get("my")).toBe("seen");
    expect(new URLSearchParams(legacyToFacetSearch("?mode=want&types=")!).get("my")).toBe("want");
  });

  it("an explicit empty types (the old 'all types') yields no type chip", () => {
    expect(legacyToFacetSearch("?types=")).toBe("");
  });
});

describe("seededMoviesSearch — the persisted Type scope, once per tab session", () => {
  it("seeds a clean landing and not again in the same session", () => {
    const storage = memoryStorage();
    expect(seededMoviesSearch("", ["Movies"], storage)).toBe("?f=type%3AMovies");
    expect(seededMoviesSearch("", ["Movies"], storage)).toBeNull();
  });

  it("keeps the catalog params and never touches a URL with a filter", () => {
    expect(seededMoviesSearch("?view=wall&sort=imdb", ["Movies", "Series"], memoryStorage())).toBe("?view=wall&sort=imdb&f=type%3AMovies&f=type%3ASeries");
    expect(seededMoviesSearch("?f=genre:Crime", ["Movies"], memoryStorage())).toBeNull();
    expect(seededMoviesSearch("?my=seen", ["Movies"], memoryStorage())).toBeNull();
  });

  it("an empty persisted scope seeds nothing (all types)", () => {
    expect(seededMoviesSearch("", [], memoryStorage())).toBeNull();
  });
});
