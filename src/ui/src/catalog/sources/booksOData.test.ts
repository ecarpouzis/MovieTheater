import type { FacetSpec } from "../rail/facetSpec";
import { parseFacetState } from "../rail/facetUrl";
import { buildBooksQuery, escapeOData, flatOrderby, groupedOrderby } from "./booksOData";

const spec: FacetSpec = {
  identity: "books",
  facets: [
    { key: "collections", token: "collection", label: "Collections", one: "Collection", valueType: "number" },
    { key: "series", token: "series", label: "Series", one: "Series", valueType: "number" },
    { key: "tags", token: "tag", label: "Tags", one: "Tag", valueType: "string" },
    { key: "authors", token: "author", label: "Authors", one: "Author", valueType: "string" },
    { key: "artists", token: "artist", label: "Artists", one: "Artist", valueType: "string" },
    { key: "events", token: "event", label: "Events", one: "Event", valueType: "string" },
    { key: "franchises", token: "franchise", label: "Franchises", one: "Franchise", valueType: "string" },
    { key: "publishers", token: "publisher", label: "Publishers", one: "Publisher", valueType: "string" },
  ],
  flags: [{ key: "read", token: "read", label: "Read" }, { key: "want", token: "want", label: "Want" }],
  loadFacets: async () => ({}),
};

describe("catalog/booksOData — one filter string for the catalog and the group endpoints", () => {
  it("projection facets become $filter clauses (OR within, AND across, null-safe excludes)", () => {
    const state = parseFacetState("?f=collection:44&f=series:12&f=series:13&x=publisher:Dark%20Horse&f=franchise:Batman&y=1980-&r=70", spec);
    const q = buildBooksQuery(state, spec);
    expect(q.filter).toBe("topFolderId eq 44 and (seriesId eq 12 or seriesId eq 13) and franchise eq 'Batman' and (publisher eq null or publisher ne 'Dark Horse') and year ge 1980 and rating ge 70");
    expect(q.exact).toEqual({});
    expect(q.readOnly).toBe(false);
  });

  it("credits, tags and events go as the host's exact params; personal flags as the group-endpoint switches", () => {
    const state = parseFacetState("?f=author:Alan%20Moore&x=artist:Someone&f=tag:genre:Horror&f=event:Year%20One&my=read", spec);
    const q = buildBooksQuery(state, spec);
    expect(q.filter).toBeNull();
    expect(q.exact).toEqual({ author: ["Alan Moore"], exArtist: ["Someone"], tag: ["genre:Horror"], event: ["Year One"] });
    expect(q.readOnly).toBe(true);
    expect(q.wantToReadOnly).toBe(false);
  });

  it("escapes quotes and ignores numbers that are not numbers", () => {
    expect(escapeOData("O'Neil")).toBe("O''Neil");
    const state = parseFacetState("?f=publisher:O'Neil%20Press", spec);
    expect(buildBooksQuery(state, spec).filter).toBe("publisher eq 'O''Neil Press'");
    expect(buildBooksQuery({ ...state, include: { ...state.include, series: [Number.NaN] } }, spec).filter).toBe("publisher eq 'O''Neil Press'");
  });

  it("the sorts spell themselves for both surfaces; reading order only inside a series group", () => {
    expect(flatOrderby("series")).toBe("series asc,year asc");
    expect(flatOrderby("bogus")).toBe("series asc,year asc");
    expect(flatOrderby("rating")).toBe("rating desc");
    expect(groupedOrderby("series", "collection")).toBeNull();
    expect(groupedOrderby("newest", "collection")).toBe("newest");
    expect(groupedOrderby("reading", "series")).toBe("reading");
    expect(groupedOrderby("reading", "publisher")).toBeNull();
  });
});
