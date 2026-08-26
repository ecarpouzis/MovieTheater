import type { FacetSpec } from "./facetSpec";
import { activeFacetCount, EMPTY_FACET_STATE, facetEquals, hasFacetValue } from "./facetSpec";
import { facetStateKey, hasNoFacetParams, parseFacetState, writeFacetState } from "./facetUrl";

const spec: FacetSpec = {
  identity: "t",
  text: true,
  years: { decadesKey: "decades" },
  rating: { presets: [] },
  flags: [{ key: "read", token: "read", label: "Read" }, { key: "want", token: "want", label: "Want" }],
  facets: [
    { key: "series", token: "series", label: "Series", one: "Series", valueType: "number" },
    { key: "tags", token: "tag", label: "Tags", one: "Tag", valueType: "string" },
    { key: "publishers", token: "publisher", label: "Publishers", one: "Publisher", valueType: "string" },
  ],
  loadFacets: async () => ({}),
};

describe("catalog/rail — the facet state's URL form", () => {
  it("round-trips every param, drops unknown tokens and bad numbers, keeps foreign params", () => {
    const search = "?view=wall&f=series:12&f=tag:Noir&f=tag:Crime&x=publisher:Dark%20Horse&f=bogus:1&f=series:abc&y=1980-1989&r=80&my=read,want&q=hellboy&item=5";
    const state = parseFacetState(search, spec);
    expect(state).toEqual({
      q: "hellboy",
      include: { series: [12], tags: ["Noir", "Crime"] },
      exclude: { publishers: ["Dark Horse"] },
      yearMin: 1980, yearMax: 1989, ratingMin: 80,
      flags: { read: true, want: true },
    });
    const params = new URLSearchParams(search);
    writeFacetState(params, state, spec);
    expect(params.get("view")).toBe("wall");
    expect(params.get("item")).toBe("5");
    expect(params.getAll("f")).toEqual(["series:12", "tag:Noir", "tag:Crime"]);
    expect(params.getAll("x")).toEqual(["publisher:Dark Horse"]);
    expect(params.get("y")).toBe("1980-1989");
    expect(params.get("my")).toBe("read,want");
    expect(parseFacetState(`?${params.toString()}`, spec)).toEqual(state);
  });

  it("open-ended years, an empty state and the landing check", () => {
    expect(parseFacetState("?y=1990-", spec)).toMatchObject({ yearMin: 1990, yearMax: null });
    expect(parseFacetState("?y=-1965", spec)).toMatchObject({ yearMin: null, yearMax: 1965 });
    expect(parseFacetState("?y=nope", spec)).toMatchObject({ yearMin: null, yearMax: null });
    const params = new URLSearchParams("?f=tag:X&q=a&view=grid");
    writeFacetState(params, EMPTY_FACET_STATE, spec);
    expect(params.toString()).toBe("view=grid");
    expect(hasNoFacetParams("?view=grid&item=3")).toBe(true);
    expect(hasNoFacetParams("?dir=8")).toBe(false);
  });

  it("the canonical key ignores order and case, and the active count reads the state", () => {
    const a = parseFacetState("?f=tag:Noir&f=tag:Crime&f=series:12&q=Hellboy", spec);
    const b = parseFacetState("?f=series:12&f=tag:Crime&f=tag:Noir&q=hellboy", spec);
    expect(facetStateKey(a)).toBe(facetStateKey(b));
    expect(facetStateKey(a)).not.toBe(facetStateKey(parseFacetState("?f=tag:Noir", spec)));
    expect(activeFacetCount(a, spec)).toBe(4);
    expect(facetEquals("Dark Horse", "dark horse")).toBe(true);
    expect(hasFacetValue([12, 13], "12")).toBe(true);
  });
});
