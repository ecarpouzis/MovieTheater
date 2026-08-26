import { exploreMoreHref } from "./booksExploreLinks";

describe("Books/booksExploreLinks — the host's rail hrefs onto Books URLs", () => {
  it("maps each rail the host composes, and refuses what it cannot honour", () => {
    expect(exploreMoreHref("/browse/groups?groupBy=series&kind=comic")).toBe("/books?view=shelf&group=series");
    expect(exploreMoreHref("/suggestions?count=100")).toBe("/books/shelf?tab=suggested");
    expect(exploreMoreHref("/odata/catalog?kind=book&$filter=rating ge 80&$orderby=rating desc")).toBe("/books/novels?r=80&sort=rating");
    expect(exploreMoreHref("/odata/catalog?kind=comic&$filter=rating ge 80&$orderby=rating desc")).toBe("/books?r=80&sort=rating");
    expect(exploreMoreHref("/odata/catalog?kind=comic&$orderby=indexedAt desc")).toBe("/books?sort=relevance");
    expect(exploreMoreHref("/odata/catalog?kind=book&$orderby=indexedAt desc")).toBe("/books/novels?sort=newest");
    expect(exploreMoreHref("/kids/series/12/items")).toBe("/books/kids?series=12");
    expect(exploreMoreHref("/odata/catalog?kind=comic&$orderby=pageCount desc")).toBeNull();
    expect(exploreMoreHref("/somewhere/else")).toBeNull();
    expect(exploreMoreHref("")).toBeNull();
  });
});
