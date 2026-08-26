import { closeEntity, openEntity, readEntityParams, resolveTarget } from "./openEntity";

function nav(search = "") {
  const history = { push: vi.fn(), replace: vi.fn() };
  const location = { pathname: "/books", search, state: { keep: 1 } };
  return { history, location };
}

describe("Books/openEntity — the modals' URL contract", () => {
  it("opens exactly one entity, pushes, and swaps item ↔ series", () => {
    const { history, location } = nav("?view=wall&item=4");
    openEntity(history, location, { kind: "series", id: 9 });
    expect(history.push).toHaveBeenCalledWith({ pathname: "/books", search: "?view=wall&series=9", state: { keep: 1 } });
    openEntity(history, { ...location, search: "?view=wall&series=9" }, { kind: "series", id: 9 });
    expect(history.push).toHaveBeenCalledTimes(1); // already open: no new entry
  });

  it("a single-issue series is its item (the one collapse point)", () => {
    expect(resolveTarget({ kind: "series", id: 9, single: { isSingleIssueSeries: true, itemId: 77 } })).toEqual({ param: "item", id: 77 });
    expect(resolveTarget({ kind: "series", id: 9, single: { isSingleIssueSeries: false, itemId: 77 } })).toEqual({ param: "series", id: 9 });
    expect(resolveTarget({ kind: "item", id: 3 })).toEqual({ param: "item", id: 3 });
  });

  it("closes by replacing, and reads only plain positive integers", () => {
    const { history, location } = nav("?item=4&view=grid");
    closeEntity(history, location);
    expect(history.replace).toHaveBeenCalledWith({ pathname: "/books", search: "?view=grid", state: { keep: 1 } });
    closeEntity(history, { ...location, search: "?view=grid" });
    expect(history.replace).toHaveBeenCalledTimes(1);
    expect(readEntityParams("?item=12&series=abc")).toEqual({ item: 12, series: null });
    expect(readEntityParams("?item=-1")).toEqual({ item: null, series: null });
  });
});
