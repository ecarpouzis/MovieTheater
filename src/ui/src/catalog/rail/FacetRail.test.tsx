import { fireEvent, render, screen } from "@testing-library/react";
import FacetRail from "./FacetRail";
import type { FacetSpec } from "./facetSpec";
import { EMPTY_FACET_STATE } from "./facetSpec";
import type { FacetActions } from "./useFacetState";

const spec: FacetSpec = {
  identity: "t",
  noun: "comics",
  facets: [
    { key: "collections", token: "collection", label: "Collections", one: "Collection", valueType: "number", render: "tile", defaultOpen: true },
    { key: "tags", token: "tag", label: "Tags", one: "Tag", valueType: "string" },
    { key: "shelves", token: "shelf", label: "Shelves", one: "Shelf", valueType: "string", appliesTo: "groups" },
  ],
  years: { decadesKey: "decades" },
  rating: { presets: [{ value: 80, label: "4★+" }] },
  ranges: [{ key: "age", token: "a", label: "Age", one: "Age", stops: [3, 8, 12, 18], openTop: true, after: "collections", defaultOpen: true }],
  flags: [{ key: "read", token: "read", label: "Read", appliesTo: "groups" }],
  loadFacets: async () => ({}),
};
const facets = { collections: [{ value: 2, label: "Marvel", count: 10 }], tags: [{ value: "Noir", label: "Noir", count: 3 }], decades: [{ value: "1990", label: "1990", count: 4 }] };

const actions = (): FacetActions => ({
  setText: vi.fn(), add: vi.fn(), remove: vi.fn(), setMode: vi.fn(), setYears: vi.fn(), setRating: vi.fn(), setRange: vi.fn(), setFlag: vi.fn(), clearAll: vi.fn(), apply: vi.fn(), replaceSearch: vi.fn(),
});

describe("FacetRail", () => {
  it("the rail lists every facet section, the count, and hides the groups-only parts on a flat view", () => {
    render(<FacetRail spec={spec} state={EMPTY_FACET_STATE} actions={actions()} facets={facets} total={1234} grouped={false} activeCount={0} />);
    expect(screen.getByText("1,234 comics")).toBeInTheDocument();
    expect(screen.getByText("Collections")).toBeInTheDocument();
    expect(screen.getByText("Tags")).toBeInTheDocument();
    expect(screen.queryByText("Shelves")).toBeNull();
    expect(screen.queryByText("My lists")).toBeNull();
    expect(screen.getByText("Years")).toBeInTheDocument();
    expect(screen.getByText("Rating")).toBeInTheDocument();
    // Collections is open by default: its tile is drawn.
    expect(screen.getByText("Marvel")).toBeInTheDocument();
  });

  it("a grouped view shows the groups-only facet and the flags; saved views are one disclosure line at the foot, and open to apply", () => {
    const a = actions();
    const saved = { list: [{ id: "1", name: "Noir", search: "?f=tag:Noir" }], onApply: vi.fn(), onRemove: vi.fn(), onSave: vi.fn() };
    render(<FacetRail spec={spec} state={{ ...EMPTY_FACET_STATE, include: { tags: ["Noir"] } }} actions={a} facets={facets} grouped activeCount={1} saved={saved} />);
    expect(screen.getByText("Shelves")).toBeInTheDocument();
    expect(screen.getByText("My lists")).toBeInTheDocument();
    // Collapsed by default: the line names the count, the pills are not drawn until it opens.
    expect(screen.queryByText("★ Noir")).toBeNull();
    const line = screen.getByRole("button", { name: /Saved views/ });
    expect(line).toHaveAttribute("aria-expanded", "false");
    fireEvent.click(line);
    fireEvent.click(screen.getByText("★ Noir"));
    expect(saved.onApply).toHaveBeenCalledWith("?f=tag:Noir");
    // "+ Save view" is the chip row's (RailChips), not the rail's — one door.
    expect(screen.queryByText("＋ Save view")).toBeNull();
  });

  it("draws no saved-views line at all when there is nothing saved, and no flags section when the spec says the rows are the door", () => {
    const saved = { list: [], onApply: vi.fn(), onRemove: vi.fn(), onSave: vi.fn() };
    render(<FacetRail spec={{ ...spec, flagsRail: false }} state={EMPTY_FACET_STATE} actions={actions()} facets={facets} grouped activeCount={0} saved={saved} />);
    expect(screen.queryByRole("button", { name: /Saved views/ })).toBeNull();
    expect(screen.queryByText("My lists")).toBeNull();
  });

  // The rail has ONE shape now. The full-page `sheet` variant (a dialog with a backdrop, raised by
  // the bar's phone Filters pill) was deleted on 2026-08-28: it drew the same options the nav drawer
  // already holds. This is the test that it cannot come back by accident.
  it("is a plain column — never a dialog, never a backdrop, and it never locks the page", () => {
    render(<FacetRail spec={spec} state={EMPTY_FACET_STATE} actions={actions()} facets={facets} grouped={false} activeCount={0} />);
    expect(screen.queryByRole("dialog")).toBeNull();
    expect(document.querySelector(".bx-rail-backdrop")).toBeNull();
    expect(document.querySelector(".bx-railbar-sheet")).toBeNull();
    expect(document.body.style.overflow).toBe("");
    // …and no close/collapse control: the drawer's own hamburger closes it.
    expect(screen.queryByRole("button", { name: /close filters|collapse filters/i })).toBeNull();
  });

  it("draws the SmartSearch by default and drops it when the caller says the bar has one", () => {
    const { rerender } = render(<FacetRail spec={spec} state={EMPTY_FACET_STATE} actions={actions()} facets={facets} grouped={false} activeCount={0} />);
    expect(screen.getByRole("combobox")).toBeInTheDocument();
    rerender(<FacetRail search={false} spec={spec} state={EMPTY_FACET_STATE} actions={actions()} facets={facets} grouped={false} activeCount={0} />);
    expect(screen.queryByRole("combobox")).toBeNull();
  });
});

describe("FacetRail — fixed-scale ranges", () => {
  it("draws the range under the facet it follows, reads the state's thumbs, and a thumb move commits stop values (ends open)", () => {
    const a = actions();
    render(<FacetRail spec={spec} state={{ ...EMPTY_FACET_STATE, ranges: { age: { min: 12, max: null } } }} actions={a} facets={facets} grouped={false} activeCount={1} />);
    const titles = Array.from(document.querySelectorAll(".bx-rail-facets .bx-rsec-title")).map((el) => el.textContent?.trim() ?? "");
    expect(titles.findIndex((t) => t.startsWith("Age"))).toBeGreaterThan(titles.findIndex((t) => t.startsWith("Collections")));
    expect(titles.findIndex((t) => t.startsWith("Age"))).toBeLessThan(titles.findIndex((t) => t.startsWith("Tags")));
    const from = screen.getByRole("slider", { name: "From age" }) as HTMLInputElement;
    const to = screen.getByRole("slider", { name: "To age" }) as HTMLInputElement;
    expect(from.value).toBe("2");
    expect(to.value).toBe("3");
    expect(screen.getAllByText("18+").length).toBeGreaterThan(0);
    fireEvent.change(to, { target: { value: "2" } });
    expect(a.setRange).toHaveBeenLastCalledWith("age", 12, 12);
    fireEvent.change(from, { target: { value: "0" } });
    expect(a.setRange).toHaveBeenLastCalledWith("age", null, null);
  });
});
