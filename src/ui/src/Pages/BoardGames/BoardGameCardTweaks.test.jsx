import { render, waitFor } from "@testing-library/react";
import { describe, it, expect } from "vitest";
import GridView from "../../catalog/views/GridView";
import { BOARDGAME_GRID_CELL, createBoardgamesSource } from "../../catalog/sources/boardgamesSource";
import BoardGameCard, { NO_EXPANSIONS } from "./BoardGameCard";

/**
 * R9 S3 for Boardgames: the BGG card is the card it always was — box art, player/time/rating/weight
 * chips, the description, the expansion flag — and every Tweaks-panel lever now moves it, because
 * the Grid laying it out is the package's.
 */
const games = [
  { id: 1, name: "Brass: Birmingham", yearPublished: 2018, minPlayers: 2, maxPlayers: 4, minPlayTime: 60, maxPlayTime: 120, averageRating: 8.6, averageWeight: 3.9, description: "<p>Build canals and rails.</p>", imageVersion: 3 },
  { id: 2, name: "Carcassonne", yearPublished: 2000, minPlayers: 2, maxPlayers: 5, minPlayTime: 30, maxPlayTime: 45, averageRating: 7.4, averageWeight: 1.9, description: "<p>Place tiles, claim land.</p>" },
];

function makeSource() {
  return createBoardgamesSource({
    games,
    expansionMap: {},
    facetsById: new Map(),
    listKey: "t",
    currentSort: "name",
    onOpen: () => {},
    renderCard: (item, view) => (
      <BoardGameCard
        game={item.raw}
        expansions={NO_EXPANSIONS}
        tooltipTrigger="hover"
        metadata={view.metadata}
        hoverClass={view.hoverClass}
        eager={view.eager}
        onGameClick={() => {}}
      />
    ),
  });
}

const props = (over = {}) => ({
  source: makeSource(),
  state: { view: "grid", group: "", items: "items", sort: "name" },
  coverScale: 1,
  metadata: "label",
  hover: "lift",
  hoverClass: "bx-hover-lift",
  ...over,
});

async function mount(over) {
  const r = render(<GridView {...props(over)} />);
  await waitFor(() => expect(r.container.querySelector(".boardgame-card")).toBeTruthy());
  return r;
}

describe("the boardgame card, on the catalog Grid", () => {
  it("still renders the BGG card — box art, chips, description", async () => {
    const { container } = await mount();
    expect(container.querySelectorAll(".boardgame-card")).toHaveLength(2);
    expect(container.textContent).toContain("Brass: Birmingham");
    expect(container.querySelector(".card-meta-row")).toBeTruthy();
    expect(container.textContent).toContain("Build canals and rails.");
    expect(container.querySelector(".bx-grid--boardgames")).toBeTruthy();
  });

  it("cover size — the Grid's --cell is the section's base cell times the tweak", async () => {
    const one = await mount();
    expect(one.container.querySelector(".bx-grid--boardgames").style.getPropertyValue("--cell")).toBe(`${BOARDGAME_GRID_CELL}px`);
    one.unmount();
    const big = await mount({ coverScale: 1.6 });
    expect(big.container.querySelector(".bx-grid--boardgames").style.getPropertyValue("--cell")).toBe(`${Math.round(BOARDGAME_GRID_CELL * 1.6)}px`);
  });

  it("hover — the host's one hover class rides every card", async () => {
    const tilt = await mount({ hover: "tilt", hoverClass: "bx-hover-tilt" });
    expect(tilt.container.querySelectorAll(".bx-card.bx-hover-tilt")).toHaveLength(2);
  });

  it("rounded + dim — the box art is a bx-cover, which is what both rules select", async () => {
    const { container } = await mount();
    expect(container.querySelectorAll(".boardgame-card-poster-container.bx-cover")).toHaveLength(2);
  });

  it("metadata: minimal — the chips and the description go", async () => {
    const { container } = await mount({ metadata: "minimal" });
    expect(container.querySelectorAll(".boardgame-card")).toHaveLength(2);
    expect(container.textContent).toContain("Brass: Birmingham");
    expect(container.querySelector(".card-meta-row")).toBeNull();
    expect(container.textContent).not.toContain("Build canals and rails.");
  });
});
