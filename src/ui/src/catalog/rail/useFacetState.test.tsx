import { act, render } from "@testing-library/react";
import { MemoryRouter, useLocation } from "react-router-dom";
import type { FacetSpec } from "./facetSpec";
import { EMPTY_FACET_STATE } from "./facetSpec";
import useFacetState, { facetTransitions, type UseFacetStateResult } from "./useFacetState";

// The transitions on their own, then the hook's URL writing: catalog params survive, the modal
// param does not, and a saved search replaces the whole query.

const spec: FacetSpec = {
  identity: "t",
  facets: [
    { key: "tags", token: "tag", label: "Tags", one: "Tag", valueType: "string" },
    { key: "series", token: "series", label: "Series", one: "Series", valueType: "number" },
  ],
  flags: [{ key: "read", token: "read", label: "Read" }],
  years: { decadesKey: "decades" },
  rating: { presets: [{ value: 80, label: "4★+" }] },
  loadFacets: async () => ({}),
};

describe("facetTransitions", () => {
  const base = { ...EMPTY_FACET_STATE, include: { tags: ["Noir"] }, exclude: { tags: ["Manga"] }, flags: {} };

  it("add is idempotent and pulls a value out of the excludes", () => {
    expect(facetTransitions.add(base, "tags", "noir").include.tags).toEqual(["Noir"]);
    const moved = facetTransitions.add(base, "tags", "Manga");
    expect(moved.include.tags).toEqual(["Noir", "Manga"]);
    expect(moved.exclude.tags).toEqual([]);
  });

  it("setMode toggles within a list and moves between lists", () => {
    expect(facetTransitions.setMode(base, "tags", "Noir", "inc").include.tags).toEqual([]);
    const excluded = facetTransitions.setMode(base, "tags", "Noir", "exc");
    expect(excluded.include.tags).toEqual([]);
    expect(excluded.exclude.tags).toEqual(["Manga", "Noir"]);
    expect(facetTransitions.setMode(base, "tags", "Manga", "exc").exclude.tags).toEqual([]);
  });

  it("remove drops from both lists; clearAll keeps nothing", () => {
    const r = facetTransitions.remove(base, "tags", "manga");
    expect(r.exclude.tags).toEqual([]);
    expect(r.include.tags).toEqual(["Noir"]);
    const c = facetTransitions.clearAll({ ...base, q: "x", yearMin: 1990, ratingMin: 80, flags: { read: true } });
    expect(c).toEqual({ ...EMPTY_FACET_STATE, include: {}, exclude: {}, flags: {} });
  });
});

describe("useFacetState", () => {
  let latest: UseFacetStateResult | null = null;
  let search = "";
  function Probe() {
    latest = useFacetState(spec);
    search = useLocation().search;
    return null;
  }
  const mount = (initial: string) => render(<MemoryRouter initialEntries={[initial]}><Probe /></MemoryRouter>);

  it("writes filters into the URL, keeps the catalog params, and closes an open modal", () => {
    mount("/books?view=wall&sort=title&item=7&f=tag:Noir");
    expect(latest!.state.include.tags).toEqual(["Noir"]);
    act(() => latest!.actions.add("series", 12));
    const p = new URLSearchParams(search);
    expect(p.get("view")).toBe("wall");
    expect(p.get("sort")).toBe("title");
    expect(p.has("item")).toBe(false);
    expect(p.getAll("f")).toEqual(["tag:Noir", "series:12"]);
    act(() => latest!.actions.setMode("tags", "Noir", "exc"));
    expect(new URLSearchParams(search).getAll("x")).toEqual(["tag:Noir"]);
    act(() => latest!.actions.setYears(1980, null));
    expect(new URLSearchParams(search).get("y")).toBe("1980-");
    act(() => latest!.actions.setFlag("read", true));
    expect(new URLSearchParams(search).get("my")).toBe("read");
    expect(latest!.activeCount).toBe(4);
  });

  it("apply makes one push for several changes and can set other params", () => {
    mount("/books?group=collection");
    act(() => latest!.actions.apply((d) => { d.include.series = [3]; d.yearMin = 1990; d.yearMax = 1999; }, { group: "series" }));
    const p = new URLSearchParams(search);
    expect(p.get("group")).toBe("series");
    expect(p.get("f")).toBe("series:3");
    expect(p.get("y")).toBe("1990-1999");
  });

  it("clearAll empties the facets but not the catalog params; replaceSearch swaps the whole query", () => {
    mount("/books?view=shelf&f=tag:Noir&q=hell&r=80");
    act(() => latest!.actions.clearAll());
    expect(search).toBe("?view=shelf");
    act(() => latest!.actions.replaceSearch("view=list&f=tag:Manga"));
    expect(search).toBe("?view=list&f=tag:Manga");
    expect(latest!.state.include.tags).toEqual(["Manga"]);
  });
});
