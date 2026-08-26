import { parseFacetState } from "../../catalog/rail/facetUrl";
import { novelsFacetSpec, novelTagLabel } from "./novelsFacetSpec";
import { seededNovelsSearch } from "./useNovelsBrowse";

function storage() {
  const m = new Map<string, string>();
  return { getItem: (k: string) => m.get(k) ?? null, setItem: (k: string, v: string) => { m.set(k, v); } };
}

describe("Books/useNovelsBrowse — the default content exclusion", () => {
  const spec = novelsFacetSpec("reader");

  it("seeds 'not adult-romance' once per session on a landing without filters, keeping the catalog params", () => {
    const s = storage();
    const first = seededNovelsSearch("?view=wall", spec, parseFacetState("?view=wall", spec), s);
    expect(first).not.toBeNull();
    const p = new URLSearchParams(first!);
    expect(p.getAll("x")).toEqual(["tag:adult-romance"]);
    expect(p.get("view")).toBe("wall");
    // The next empty landing in the same session is left alone (the reader cleared it on purpose).
    expect(seededNovelsSearch("", spec, parseFacetState("", spec), s)).toBeNull();
  });

  it("leaves a URL that carries any filter of its own alone", () => {
    for (const search of ["?f=author:Le+Guin", "?x=tag:horror", "?q=dune", "?r=80", "?my=unknown"]) {
      expect(seededNovelsSearch(search, spec, parseFacetState(search, spec), storage())).toBeNull();
    }
  });

  it("labels a composite tag by its value half", () => {
    expect(novelTagLabel("genre:adult-romance")).toBe("Adult romance");
    expect(novelTagLabel("sci-fi")).toBe("Sci fi");
    expect(novelTagLabel("1990s")).toBe("1990s");
    expect(spec.facets.find((f) => f.key === "authors")?.excludable).toBe(false);
    expect(spec.facets.find((f) => f.key === "tags")?.excludable).toBeUndefined();
  });
});
