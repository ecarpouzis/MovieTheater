import { act, renderHook } from "@testing-library/react";
import type { ReactNode } from "react";
import { MemoryRouter, useLocation } from "react-router-dom";
import type { CatalogSource } from "../types";
import useCatalogView, { resolveViewState, storageKeyFor } from "./useCatalogView";

const source: CatalogSource = {
  queryKey: "q",
  supports: ["grid", "wall", "list", "shelf"],
  groups: [{ value: "genre", label: "Genre" }, { value: "decade", label: "Decade" }],
  sorts: [{ value: "alpha", label: "A–Z", alpha: true }, { value: "imdb", label: "IMDb" }],
  itemsModes: ["items", "groups"],
  defaultView: "grid",
  defaultSort: "alpha",
  fetchFlatBand: async () => ({ items: [], total: 0 }),
  onOpen: () => {},
};
const available = ["grid", "wall", "list"] as const;

describe("catalog/useCatalogView — the switcher's state lives in the URL, its default on the device", () => {
  beforeEach(() => { window.localStorage.clear(); });

  it("resolves the URL first, the stored default second, the source's default last", () => {
    expect(resolveViewState("", {}, source, available)).toEqual({ view: "grid", group: "none", items: "items", sort: "alpha" });
    expect(resolveViewState("", { view: "wall", sort: "imdb" }, source, available).view).toBe("wall");
    expect(resolveViewState("?view=list&sort=imdb&items=groups&group=decade", { view: "wall" }, source, available))
      .toEqual({ view: "list", group: "decade", items: "groups", sort: "imdb" });
  });

  it("falls back on anything the source or the package does not offer", () => {
    // shelves are supported by the source but not yet implemented → default view
    expect(resolveViewState("?view=shelf", {}, source, available).view).toBe("grid");
    expect(resolveViewState("?view=bogus&group=bogus&sort=bogus&items=bogus", { view: "shelf" }, source, available))
      .toEqual({ view: "grid", group: "none", items: "items", sort: "alpha" });
  });

  it("a section that owns its sort pins the state to it, whatever the URL or the device remembers", () => {
    const owned: CatalogSource = { ...source, currentSort: "imdb" };
    expect(resolveViewState("?sort=alpha", { sort: "alpha" }, owned, available).sort).toBe("imdb");
    expect(resolveViewState("", {}, owned, available)).toEqual({ view: "grid", group: "none", items: "items", sort: "imdb" });
  });

  it("a change pushes the URL and remembers the section's default", () => {
    const wrapper = ({ children }: { children: ReactNode }) => <MemoryRouter initialEntries={["/movies?mode=actor&value=x"]}>{children}</MemoryRouter>;
    const { result } = renderHook(() => ({ cv: useCatalogView("movies", source, available), loc: useLocation() }), { wrapper });
    expect(result.current.cv.state.view).toBe("grid");
    act(() => { result.current.cv.setView("wall"); });
    const params = new URLSearchParams(result.current.loc.search);
    expect(params.get("view")).toBe("wall");
    expect(params.get("mode")).toBe("actor"); // unrelated params survive
    expect(result.current.cv.state.view).toBe("wall");
    expect(JSON.parse(window.localStorage.getItem(storageKeyFor("movies")) ?? "{}").view).toBe("wall");
    act(() => { result.current.cv.setSort("imdb"); });
    expect(result.current.cv.state).toEqual({ view: "wall", group: "none", items: "items", sort: "imdb" });
  });
});
