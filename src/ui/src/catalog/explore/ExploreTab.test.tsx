import { cleanup, fireEvent, render, screen, within } from "@testing-library/react";
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

    // The top-series rail: Shuffle + See all; the wall: neither Shuffle (unseeded) nor See all (unmapped).
    // (The marquee carries a Shuffle of its own.)
    const rail = screen.getByText("Highest-rated series").closest("section")!;
    expect(within(rail).getAllByText("Shuffle")).toHaveLength(1);
    expect(within(screen.getByText("Fresh arrivals").closest("section")!).queryByText("Shuffle")).toBeNull();
    fireEvent.click(within(rail).getByText("Shuffle"));
    expect(onSeed).toHaveBeenCalledWith(expect.any(Number));
    fireEvent.click(screen.getByText("See all"));
    expect(screen.getByTestId("loc")).toHaveTextContent("/books?view=shelf&group=series");

    // A series card goes to onOpenGroup as a one-card group; an issue card to onOpen.
    fireEvent.click(screen.getByRole("button", { name: "Hellboy" }));
    expect(onOpenGroup).toHaveBeenCalledWith(expect.objectContaining({ key: "9", totalItems: 5 }), "series");
    fireEvent.click(screen.getByRole("button", { name: "Hellboy #10" }));
    expect(onOpen).toHaveBeenCalledWith(expect.objectContaining({ id: 10 }));

    // The marquee's Up next queue switches the spotlight.
    fireEvent.click(screen.getByRole("tab", { name: "Hellboy #8" }));
    expect(screen.getByRole("heading", { level: 1 })).toHaveTextContent("Hellboy #8");
  });

  it("draws each module shape: ranked, focus, columns and doors (a doors tab reports the axis)", () => {
    const onAxis = vi.fn();
    const shapes: ExploreResponse = {
      spotlight: [],
      rails: [
        { key: "top", title: "Top ten", kind: "ranked", items: [card(1), card(2)] },
        { key: "who", title: "A closer look", kind: "focus", items: [card(3)], focus: { name: "Mike Mignola", count: 40, href: "/books?f=creator:Mignola" } },
        { key: "quick", title: "Quick picks", kind: "columns", items: [card(4)], columns: [{ key: "a", title: "Short reads", items: [card(4)] }] },
        { key: "ways", title: "Ways in", kind: "doors", items: [], activeAxis: "genre", axes: [
          { key: "genre", label: "Genre", doors: [{ key: "horror", label: "Horror", count: 12, href: "/books?f=genre:Horror", covers: [{ src: "x" }] }] },
          { key: "decade", label: "Decade" },
        ] },
      ],
    };
    render(
      <MemoryRouter initialEntries={["/books/explore"]}>
        <Probe />
        <ExploreTab data={shapes} onOpen={() => {}} onAxis={onAxis} eagerRails={9} />
      </MemoryRouter>,
    );
    expect(screen.getByRole("button", { name: "1. Hellboy #1" })).toBeInTheDocument();
    expect(screen.getByRole("heading", { name: "Mike Mignola" })).toBeInTheDocument();
    expect(screen.getByText("Short reads")).toBeInTheDocument();
    fireEvent.click(screen.getByRole("tab", { name: "Decade" }));
    expect(onAxis).toHaveBeenCalledWith("ways", "decade");
    fireEvent.click(screen.getByRole("button", { name: "Horror, 12 titles" }));
    expect(screen.getByTestId("loc")).toHaveTextContent("/books?f=genre:Horror");
    fireEvent.click(screen.getByText("See all 40"));
    expect(screen.getByTestId("loc")).toHaveTextContent("/books?f=creator:Mignola");
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
