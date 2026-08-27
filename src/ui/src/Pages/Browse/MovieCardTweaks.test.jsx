import { render, waitFor } from "@testing-library/react";
import { describe, it, expect } from "vitest";
import GridView from "../../catalog/views/GridView";
import { createMoviesListSource } from "../../catalog/sources/moviesSource";
import { MovieCard, MOVIE_GRID_CELL } from "./MovieCard";

/**
 * R9 S3's promise for Movies: the card is EXACTLY the card it always was, and every Tweaks-panel
 * lever now moves it. The four levers reach a card three ways, all pinned here —
 *  - cover size: the Grid's `--cell` on the wrap (CardList.css sizes the poster box off it);
 *  - hover: the host's one hover class, worn beside `bx-card`;
 *  - rounded + dim: `bx-cover` on the poster box, which is what `.bx-rounded .bx-cover` and
 *    `.bx-results[data-hover="dim"] … .bx-cover` select;
 *  - metadata: "minimal" drops the badge row, the cast row and the plot.
 */
const rows = [
  { id: 1, kind: "movie", title: "Alien", releaseDate: "1979-05-25", rating: "R", runtime: "117 min", imdbRating: 8.5, topCast: "Sigourney Weaver", plotFull: "A crew answers a distress call.", posterVersion: 1 },
  { id: 2, kind: "movie", title: "Blade Runner", releaseDate: "1982-06-25", rating: "R", runtime: "117 min", imdbRating: 8.1, topCast: "Harrison Ford", plotFull: "A blade runner hunts replicants.", posterVersion: 1 },
];

const CTX = {
  showOptions: false, activeName: "", seenSet: new Set(), wantSet: new Set(),
  onMovieClick: () => {}, onActorSearch: () => {}, onToggleSeen: () => {}, onToggleWant: () => {},
};

function makeSource() {
  const base = createMoviesListSource({ rows, listKey: "t", sort: "alpha", onOpen: () => {} });
  return {
    ...base,
    gridClass: "bx-grid--movies",
    gridCell: MOVIE_GRID_CELL,
    renderCard: (item, view) => (
      <MovieCard
        item={item.raw}
        eager={view.eager}
        metadata={view.metadata}
        hoverClass={view.hoverClass}
        activeName={CTX.activeName}
        showOptions={CTX.showOptions}
        isWatched={false}
        isWanted={false}
        onMovieClick={CTX.onMovieClick}
        onActorSearch={CTX.onActorSearch}
        onToggleSeen={CTX.onToggleSeen}
        onToggleWant={CTX.onToggleWant}
      />
    ),
  };
}

const props = (over = {}) => ({
  source: makeSource(),
  state: { view: "grid", group: "", items: "items", sort: "alpha" },
  coverScale: 1,
  metadata: "label",
  hover: "lift",
  hoverClass: "bx-hover-lift",
  ...over,
});

async function mount(over) {
  const r = render(<GridView {...props(over)} />);
  await waitFor(() => expect(r.container.querySelector(".movie-card")).toBeTruthy());
  return r;
}

describe("the movie card, on the catalog Grid", () => {
  it("still renders the movie card — poster, title, badges, cast, plot", async () => {
    const { container } = await mount();
    expect(container.querySelectorAll(".movie-card")).toHaveLength(2);
    expect(container.textContent).toContain("Alien");
    expect(container.querySelector(".card-meta-row")).toBeTruthy();
    expect(container.querySelector(".card-actor-row")).toBeTruthy();
    expect(container.querySelector(".card-plot")).toBeTruthy();
  });

  it("cover size — the Grid's --cell is the section's base cell times the tweak", async () => {
    const one = await mount();
    expect(one.container.querySelector(".bx-grid--movies").style.getPropertyValue("--cell")).toBe(`${MOVIE_GRID_CELL}px`);
    one.unmount();
    const big = await mount({ coverScale: 1.4 });
    expect(big.container.querySelector(".bx-grid--movies").style.getPropertyValue("--cell")).toBe(`${Math.round(MOVIE_GRID_CELL * 1.4)}px`);
  });

  it("hover — the host's one hover class rides every card", async () => {
    const lift = await mount();
    expect(lift.container.querySelectorAll(".bx-card.bx-hover-lift")).toHaveLength(2);
    lift.unmount();
    const zoom = await mount({ hover: "zoom", hoverClass: "bx-hover-zoom" });
    expect(zoom.container.querySelectorAll(".bx-card.bx-hover-zoom")).toHaveLength(2);
    expect(zoom.container.querySelector(".bx-hover-lift")).toBeNull();
  });

  it("rounded + dim — the poster box is a bx-cover, which is what both rules select", async () => {
    const { container } = await mount();
    expect(container.querySelectorAll(".card-poster-container.bx-cover")).toHaveLength(2);
  });

  it("metadata: minimal — the badge row, the cast row and the plot go", async () => {
    const { container } = await mount({ metadata: "minimal" });
    expect(container.querySelectorAll(".movie-card")).toHaveLength(2);
    expect(container.textContent).toContain("Alien");
    expect(container.querySelector(".card-meta-row")).toBeNull();
    expect(container.querySelector(".card-actor-row")).toBeNull();
    expect(container.querySelector(".card-plot")).toBeNull();
  });
});
