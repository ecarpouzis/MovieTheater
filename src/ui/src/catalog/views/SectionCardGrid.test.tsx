import { memo } from "react";
import { render, screen, waitFor } from "@testing-library/react";
import GridView from "./GridView";
import WallView from "./WallView";
import type { CardItem, CardRenderProps, CatalogSource } from "../types";

/**
 * R9 S3's seam: a section keeps its OWN card and gives up its engine. `CatalogSource.renderCard`
 * is honoured by the GRID only — every other view keeps the package card, so the Wall/List/
 * Extended/Shelves stay one look across the site — and the card is handed the tweak values as flat
 * props so it can wear `bx-card`/`bx-cover`, size from `--cell` and drop its meta block.
 */
const item = (id: number): CardItem => ({
  kind: "movie", id, key: `movie:${id}`, title: `Title ${id}`, aspect: 0.66, imageUrl: `/i/${id}`, hue: 120, raw: { id },
});

// Module-level, as the contract demands: a renderer defined inside a view is a new component type
// every render and React remounts the whole band.
const SectionCard = memo(function SectionCard({ item: card, cellH, hoverClass, metadata, onOpen }: { item: CardItem } & CardRenderProps) {
  return (
    <div className={`bx-card section-card ${hoverClass}`} data-testid="section-card" onClick={() => onOpen(card)}>
      <div className="bx-cover" style={{ height: cellH }} data-cell={cellH} />
      {metadata !== "minimal" && <div className="section-card-meta">{card.title}</div>}
    </div>
  );
});

function makeSource(over: Partial<CatalogSource> = {}): CatalogSource {
  return {
    queryKey: "q", itemNoun: "title", supports: ["grid", "wall"], groups: [],
    sorts: [{ value: "alpha", label: "A–Z", alpha: true }],
    pageSize: 4,
    fetchFlatBand: async (skip, top) => ({ items: Array.from({ length: top }, (_, i) => item(skip + i)), total: 8 }),
    onOpen: vi.fn(),
    renderCard: (card, view) => <SectionCard item={card} {...view} />,
    ...over,
  };
}

const props = (source: CatalogSource, coverScale = 1) => ({
  source,
  state: { view: "grid" as const, group: "", items: "items" as const, sort: "alpha" },
  coverScale, metadata: "label" as const, hover: "lift" as const, hoverClass: "bx-hover-lift",
});

describe("catalog/GridView — a section's own card in the shared bands", () => {
  it("renders the section's card, not the package card, and passes the tweak values through", async () => {
    const { container } = render(<GridView {...props(makeSource({ gridCell: 200 }))} />);
    await waitFor(() => expect(screen.getAllByTestId("section-card")).toHaveLength(4));
    // The package card is not in the tree at all.
    expect(container.querySelector(".bx-meta-title")).toBeNull();
    // Cover size comes from the Grid's cell (gridCell × coverScale).
    expect(container.querySelector<HTMLElement>(".section-card .bx-cover")?.dataset.cell).toBe("200");
    // The hover class rides on every card, from the ONE source of truth.
    expect(container.querySelectorAll(".section-card.bx-hover-lift")).toHaveLength(4);
  });

  it("scales the cover with the cover-size tweak and hides the meta block on metadata: minimal", async () => {
    const big = render(<GridView {...props(makeSource({ gridCell: 200 }), 1.5)} />);
    await waitFor(() => expect(big.container.querySelector(".section-card .bx-cover")).toBeTruthy());
    expect(big.container.querySelector<HTMLElement>(".section-card .bx-cover")?.dataset.cell).toBe("300");
    big.unmount();

    const bare = render(<GridView {...{ ...props(makeSource({ gridCell: 200 })), metadata: "minimal" as const }} />);
    await waitFor(() => expect(bare.container.querySelector(".section-card")).toBeTruthy());
    expect(bare.container.querySelector(".section-card-meta")).toBeNull();
  });

  it("puts the section's own wrap class on the Grid so its card layout replaces the package flow", async () => {
    const { container } = render(<GridView {...props(makeSource({ gridClass: "bx-grid--movies" }))} />);
    await waitFor(() => expect(container.querySelector(".bx-grid")).toBeTruthy());
    expect(container.querySelector(".bx-grid.bx-grid--movies")).toBeTruthy();
  });

  it("leaves the OTHER views on the package card — renderCard is the Grid's alone", async () => {
    const { container } = render(<WallView {...{ ...props(makeSource()), state: { view: "wall" as const, group: "", items: "items" as const, sort: "alpha" } }} />);
    await waitFor(() => expect(container.querySelector(".bx-wall .bx-card")).toBeTruthy());
    expect(screen.queryAllByTestId("section-card")).toHaveLength(0);
  });
});

describe("catalog/GridView — dataVersion re-reads a dense list in place", () => {
  it("re-reads band 0 when the source's data changed under an unchanged queryKey", async () => {
    let rows = [item(1), item(2), item(3)];
    const fetchFlatBand = vi.fn(async (skip: number, top: number) => ({ items: rows.slice(skip, skip + top), total: rows.length }));
    const source = makeSource({ fetchFlatBand, dataVersion: 0 });
    const { rerender } = render(<GridView {...props(source)} />);
    await waitFor(() => expect(screen.getAllByTestId("section-card")).toHaveLength(3));

    // The dense edit: a row removed, the query the SAME query.
    rows = [item(1), item(3)];
    rerender(<GridView {...props({ ...source, dataVersion: 1 })} />);
    await waitFor(() => expect(screen.getAllByTestId("section-card")).toHaveLength(2));
    expect(screen.queryByText("Title 2")).toBeNull();
  });
});
