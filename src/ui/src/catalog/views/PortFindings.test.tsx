import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import { ShelfBook } from "./shelves/Shelf";
import WallView from "./WallView";
import { VIEW_TWEAK_ROWS } from "../CatalogHost";
import type { CardItem, CatalogSource } from "../types";

/**
 * The 2026-09-01 catalog-views port findings, pinned: a shelf spine opens from the keyboard like the
 * package Card does; the Wall measures its capacity BEFORE band 0 is fetched (one fetch, at the real
 * size, with the section's aspect); the tweak rows a view cannot honour are removed from its panel.
 */
const item = (id: number): CardItem => ({
  kind: "book", id, key: `book:${id}`, title: `Title ${id}`, aspect: 0.66, imageUrl: `/i/${id}`, hue: 120, raw: { id },
});

describe("catalog/Shelves — the spine's keyboard path", () => {
  it("is a focusable button that opens on Enter and Space", () => {
    const onOpen = vi.fn();
    render(<ShelfBook item={item(7)} shelfH={200} onOpen={onOpen} />);
    const spine = screen.getByRole("button", { name: "Title 7" });
    expect(spine.tabIndex).toBe(0);
    fireEvent.keyDown(spine, { key: "Enter" });
    fireEvent.keyDown(spine, { key: " " });
    fireEvent.keyDown(spine, { key: "a" });
    expect(onOpen).toHaveBeenCalledTimes(2);
  });
});

describe("catalog/Wall — capacity is measured before the first fetch", () => {
  it("fetches band 0 exactly once, at the measured capacity, never at the 120 fallback", async () => {
    const tops: number[] = [];
    const source: CatalogSource = {
      queryKey: "q", itemNoun: "title", supports: ["wall"], groups: [],
      sorts: [{ value: "alpha", label: "A–Z", alpha: true }],
      defaultAspect: 1,
      fetchFlatBand: async (skip, top) => { tops.push(top); return { items: Array.from({ length: top }, (_, i) => item(skip + i)), total: 500 }; },
      onOpen: vi.fn(),
    };
    const { container } = render(
      <WallView
        source={source}
        state={{ view: "wall", group: "", items: "items", sort: "alpha" }}
        coverScale={1} metadata="label" hover="lift" hoverClass="bx-hover-lift"
      />,
    );
    await waitFor(() => expect(container.querySelectorAll(".bx-card").length).toBeGreaterThan(0));
    // jsdom measures a zero-width probe (one column, a few rows). What matters is that exactly ONE
    // request went out, it carried the measured number, and the 120 fallback never hit the wire.
    expect(tops).toHaveLength(1);
    expect(tops[0]).not.toBe(120);
    expect(tops[0]).toBeGreaterThan(0);
  });
});

describe("catalog/CatalogHost — tweak rows a view cannot honour are removed", () => {
  it("names the rows per view: Grid/Extended/Directory show all, the card-less views keep only cover size", () => {
    expect(VIEW_TWEAK_ROWS.grid).toBeUndefined();
    expect(VIEW_TWEAK_ROWS.extended).toBeUndefined();
    expect(VIEW_TWEAK_ROWS.directory).toBeUndefined();
    expect(VIEW_TWEAK_ROWS.wall).toEqual({ rounded: false, metadata: false });
    for (const view of ["list", "shelf", "newspaper"] as const)
      expect(VIEW_TWEAK_ROWS[view]).toEqual({ hover: false, rounded: false, metadata: false });
  });
});
