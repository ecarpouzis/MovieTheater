import { cleanup, fireEvent, render, screen } from "@testing-library/react";
import { MemoryRouter, useLocation } from "react-router-dom";
import type { ExploreResponse } from "../types";
import ExploreTab from "./ExploreTab";

global.IS_REACT_ACT_ENVIRONMENT = true;

function Probe() {
  const l = useLocation();
  return <div data-testid="loc">{l.pathname}{l.search}</div>;
}

const card = (id: number, over: Record<string, unknown> = {}) => ({
  kind: "comic" as const, id, key: `comic:${id}`, title: `Hellboy #${id}`, subtitle: "Hellboy", label: "1994", aspect: 0.66,
  imageUrl: "https://m/x.webp", hue: 20, rating: 84, badges: [{ label: "84", tone: "rating" as const }], raw: {}, ...over,
});

const data: ExploreResponse = {
  spotlight: [card(7), card(8)],
  rails: [
    { key: "top-series", title: "Highest-rated series", kind: "strip", items: [card(9, { kind: "series", key: "series:9", title: "Hellboy", groupKey: "9", raw: { issueCount: 5 } })], more: { href: "/browse/groups?groupBy=series" } },
    { key: "fresh-arrivals", title: "Fresh arrivals", kind: "wall", items: [card(10)], more: { href: "/odata/catalog?x=1" } },
    { key: "empty", title: "Nothing", kind: "grid", items: [] },
  ],
  seed: 5,
};

afterEach(cleanup);

describe("catalog/explore/ExploreTab", () => {
  it("draws the hero and the non-empty rails; More → walks the mapped href; Shuffle asks for a seed except on unseeded rails", () => {
    const onSeed = vi.fn();
    const onOpen = vi.fn();
    const onOpenGroup = vi.fn();
    render(
      <MemoryRouter initialEntries={["/books/explore"]}>
        <Probe />
        <ExploreTab
          data={data}
          onSeed={onSeed}
          onOpen={onOpen}
          onOpenGroup={onOpenGroup}
          moreHref={(href) => (href.startsWith("/browse/groups") ? "/books?view=shelf&group=series" : null)}
          unseededRails={new Set(["fresh-arrivals"])}
          heroIntervalMs={0}
        />
      </MemoryRouter>,
    );
    expect(screen.getByRole("heading", { level: 1 })).toHaveTextContent("Hellboy #7");
    expect(screen.getByText("Highest-rated series")).toBeInTheDocument();
    expect(screen.getByText("Fresh arrivals")).toBeInTheDocument();
    expect(screen.queryByText("Nothing")).toBeNull();

    // The top-series rail: Shuffle + More; the wall: neither Shuffle (unseeded) nor More (unmapped).
    expect(screen.getAllByText("Shuffle ↻")).toHaveLength(1);
    fireEvent.click(screen.getByText("Shuffle ↻"));
    expect(onSeed).toHaveBeenCalledWith(expect.any(Number));
    fireEvent.click(screen.getByText("More →"));
    expect(screen.getByTestId("loc")).toHaveTextContent("/books?view=shelf&group=series");

    // A series card goes to onOpenGroup as a one-card group; an issue card to onOpen.
    fireEvent.click(screen.getByRole("button", { name: "Hellboy" }));
    expect(onOpenGroup).toHaveBeenCalledWith(expect.objectContaining({ key: "9", totalItems: 5 }), "series");
    fireEvent.click(screen.getByRole("button", { name: "Hellboy #10" }));
    expect(onOpen).toHaveBeenCalledWith(expect.objectContaining({ id: 10 }));

    // The hero thumb strip switches the spotlight.
    fireEvent.click(screen.getByRole("tab", { name: "Hellboy #8" }));
    expect(screen.getByRole("heading", { level: 1 })).toHaveTextContent("Hellboy #8");
  });

  it("shows the loading, error and empty states", () => {
    const { rerender } = render(<MemoryRouter><ExploreTab onOpen={() => {}} /></MemoryRouter>);
    expect(screen.getByText("Loading…")).toBeInTheDocument();
    rerender(<MemoryRouter><ExploreTab onOpen={() => {}} error={new Error("x")} /></MemoryRouter>);
    expect(screen.getByRole("alert")).toHaveTextContent("could not load");
    rerender(<MemoryRouter><ExploreTab onOpen={() => {}} data={{ spotlight: [], rails: [] }} emptyMessage="Empty shelf" /></MemoryRouter>);
    expect(screen.getByText("Empty shelf")).toBeInTheDocument();
  });
});
