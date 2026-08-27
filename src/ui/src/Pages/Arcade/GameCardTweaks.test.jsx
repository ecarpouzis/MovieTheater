import { render, waitFor } from "@testing-library/react";
import { describe, it, expect, vi } from "vitest";
import GridView from "../../catalog/views/GridView";
import { ARCADE_GRID_CELL, createArcadeSource } from "../../catalog/sources/arcadeSource";
import GameCard, { COVER_H } from "./GameCard";

const { api } = vi.hoisted(() => ({ api: {} }));
vi.mock("../../MovieAPI", () => ({ MovieAPI: api }));

/**
 * R9 S3 for the Arcade: the lobby card is the lobby card — box art at its true aspect, chips,
 * summary, the year · studio foot — and every Tweaks-panel lever now moves it, because the grid
 * laying it out is the package's.
 */
const GAMES = [
  { id: 1, key: "g1", title: "GoldenEye 007", system: "n64", maxPlayers: 4, genres: "Action", year: 1997, developer: "Rare", summary: "Bond, on the N64.", artId: 1, versions: [{ id: 11, region: "USA" }], versionCount: 1 },
  { id: 2, key: "g2", title: "Mario Kart 64", system: "n64", maxPlayers: 4, genres: "Racing", year: 1996, developer: "Nintendo", summary: "Blue shells.", artId: 2, versions: [{ id: 22, region: "USA" }], versionCount: 1 },
];

api.getArcadeGames = vi.fn(() => Promise.resolve({
  ok: true, status: 200, json: () => Promise.resolve({ games: GAMES, totalCount: GAMES.length, skip: 0 }),
}));
api.getArcadeGameLetters = vi.fn(() => Promise.resolve({ ok: true, json: () => Promise.resolve({ letters: [] }) }));

const renderCard = (item, view) => (
  <GameCard game={item.raw} cellH={view.cellH} metadata={view.metadata} hoverClass={view.hoverClass} eager={view.eager} onOpen={() => {}} />
);

const makeSource = () => createArcadeSource({ filters: {}, filterKey: "t", onOpen: () => {}, renderCard });

const props = (over = {}) => ({
  source: makeSource(),
  state: { view: "grid", group: "system", items: "items", sort: "alpha" },
  coverScale: 1, metadata: "label", hover: "lift", hoverClass: "bx-hover-lift",
  ...over,
});

async function mount(over) {
  const r = render(<GridView {...props(over)} />);
  await waitFor(() => expect(r.container.querySelector(".arcade-card")).toBeTruthy());
  return r;
}

describe("the arcade lobby card, on the catalog Grid", () => {
  it("still renders the lobby card — art, chips, summary, foot", async () => {
    const { container } = await mount();
    expect(container.querySelectorAll(".arcade-card")).toHaveLength(2);
    expect(container.textContent).toContain("GoldenEye 007");
    expect(container.querySelector(".arcade-tags")).toBeTruthy();
    expect(container.querySelector(".arcade-card__summary")).toBeTruthy();
    expect(container.querySelector(".arcade-card__foot")).toBeTruthy();
    expect(container.querySelector(".bx-grid.arcade-grid")).toBeTruthy();
  });

  it("cover size — the Grid's --cell drives the art box, which is what sets the card's height", async () => {
    const one = await mount();
    expect(one.container.querySelector(".arcade-grid").style.getPropertyValue("--cell")).toBe(`${ARCADE_GRID_CELL}px`);
    expect(one.container.querySelector(".arcade-card__art").style.height).toBe(`${COVER_H}px`);
    one.unmount();
    const big = await mount({ coverScale: 1.5 });
    const cell = Math.round(ARCADE_GRID_CELL * 1.5);
    expect(big.container.querySelector(".arcade-grid").style.getPropertyValue("--cell")).toBe(`${cell}px`);
    expect(big.container.querySelector(".arcade-card__art").style.height).toBe(`${cell}px`);
  });

  it("hover — the host's one hover class rides every card", async () => {
    const { container } = await mount({ hover: "zoom", hoverClass: "bx-hover-zoom" });
    expect(container.querySelectorAll(".arcade-card.bx-hover-zoom")).toHaveLength(2);
  });

  it("rounded + dim — the COVER is the bx-cover, so it keeps its true aspect and still takes both rules", async () => {
    const { container } = await mount();
    expect(container.querySelectorAll(".arcade-cover.bx-cover")).toHaveLength(2);
  });

  it("metadata: minimal — the chips, the summary and the foot go", async () => {
    const { container } = await mount({ metadata: "minimal" });
    expect(container.querySelectorAll(".arcade-card")).toHaveLength(2);
    expect(container.textContent).toContain("GoldenEye 007");
    expect(container.querySelector(".arcade-tags")).toBeNull();
    expect(container.querySelector(".arcade-card__summary")).toBeNull();
    expect(container.querySelector(".arcade-card__foot")).toBeNull();
  });
});
