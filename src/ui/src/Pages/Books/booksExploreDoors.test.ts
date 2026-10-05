import { bookDoorHref, bookDoors, topFacetValues } from "./booksExploreDoors";
import type { BrowseFacetsResult, ItemSummary } from "./booksApi";

const item = (id: number) => ({ id, kind: "comic", title: `Issue ${id}`, coverAspect: 0.66 } as unknown as ItemSummary);

describe("Pages/Books/booksExploreDoors — the comics 'Ways in'", () => {
  it("takes the biggest publishers (by NAME) and franchises from the facet lists, tiny ones dropped", () => {
    const facets = {
      publishers: [{ id: 1, name: "Marvel", full: "Marvel Comics", count: 900 }, { id: 2, name: "Tiny", full: null, count: 2 }, { id: 3, name: "DC Comics", full: null, count: 1200 }],
      franchises: [{ value: "2000 AD", count: 4532 }, { value: "Alien & Predator", count: 172 }],
    } as unknown as BrowseFacetsResult;
    expect(topFacetValues(facets, "publisher").map((p) => p.value)).toEqual(["DC Comics", "Marvel"]);
    expect(topFacetValues(facets, "franchise", 1)).toEqual([{ value: "2000 AD", count: 4532 }]);
    expect(topFacetValues(null, "publisher")).toEqual([]);
  });

  it("each door opens the browse with its facet chip, a decade as the year range", () => {
    expect(bookDoorHref("publisher", "Dark Horse Comics")).toBe("/books?f=publisher%3ADark+Horse+Comics");
    expect(bookDoorHref("franchise", "Alien & Predator")).toBe("/books?f=franchise%3AAlien+%26+Predator");
    expect(bookDoorHref("decade", "1980")).toBe("/books?y=1980-1989");
  });

  it("doors carry their count and covers that are not repeated across the axis", () => {
    const doors = bookDoors("decade", [
      { key: "1980", label: "1980s", totalItems: 40, items: [item(1), item(2), item(3), item(4)] },
      { key: "1990", label: "1990s", totalItems: 90, items: [item(1), item(2), item(3), item(5), item(6), item(7)] },
      { key: "1900", label: "1900s", totalItems: 2, items: [item(9)] },
    ]);
    expect(doors.map((d) => d.label)).toEqual(["1980s", "1990s"]);
    const first = new Set(doors[0].covers.map((c) => c.src));
    expect(doors[1].covers.every((c) => !first.has(c.src))).toBe(true);
  });
});
