import { act, renderHook } from "@testing-library/react";
import { readSavedSearches, savableSearch, savedSearchesKey, useSavedSearches, writeSavedSearches } from "./savedSearches";

describe("savedSearches", () => {
  beforeEach(() => window.localStorage.clear());

  it("savableSearch keeps the facets and the catalog params but drops the modal params", () => {
    expect(savableSearch("?view=wall&f=tag:Noir&item=7&series=9&sort=title")).toBe("?view=wall&f=tag%3ANoir&sort=title");
    expect(savableSearch("?item=7")).toBe("");
  });

  it("persists per section, replaces a same-named search, removes by id, and survives bad storage", () => {
    const { result } = renderHook(() => useSavedSearches("books"));
    act(() => result.current.save("Noir", "?f=tag:Noir"));
    act(() => result.current.save("Old", "?f=tag:Old"));
    act(() => result.current.save("noir", "?f=tag:Noir&view=wall"));
    expect(result.current.list.map((s) => [s.name, s.search])).toEqual([["Old", "?f=tag:Old"], ["noir", "?f=tag:Noir&view=wall"]]);
    expect(readSavedSearches("books")).toHaveLength(2);
    expect(readSavedSearches("movies")).toEqual([]);

    act(() => result.current.remove(result.current.list[0].id));
    expect(result.current.list.map((s) => s.name)).toEqual(["noir"]);

    window.localStorage.setItem(savedSearchesKey("books"), "{not json");
    expect(readSavedSearches("books")).toEqual([]);
    writeSavedSearches("books", []);
    expect(window.localStorage.getItem(savedSearchesKey("books"))).toBeNull();
  });
});
