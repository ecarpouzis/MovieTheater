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
    render(<FacetRail variant="rail" spec={spec} state={EMPTY_FACET_STATE} actions={actions()} facets={facets} total={1234} grouped={false} activeCount={0} />);
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

  it("a grouped view shows the groups-only facet and the flags; a saved search applies and can be saved when filters are active", () => {
    const a = actions();
    const saved = { list: [{ id: "1", name: "Noir", search: "?f=tag:Noir" }], onApply: vi.fn(), onRemove: vi.fn(), onSave: vi.fn() };
    render(<FacetRail variant="rail" spec={spec} state={{ ...EMPTY_FACET_STATE, include: { tags: ["Noir"] } }} actions={a} facets={facets} grouped activeCount={1} saved={saved} />);
    expect(screen.getByText("Shelves")).toBeInTheDocument();
    expect(screen.getByText("My lists")).toBeInTheDocument();
    fireEvent.click(screen.getByText("★ Noir"));
    expect(saved.onApply).toHaveBeenCalledWith("?f=tag:Noir");
    fireEvent.click(screen.getByText("＋ Save view"));
    fireEvent.change(screen.getByRole("textbox", { name: "Search name" }), { target: { value: "Mine" } });
    fireEvent.keyDown(screen.getByRole("textbox", { name: "Search name" }), { key: "Enter" });
    expect(saved.onSave).toHaveBeenCalledWith("Mine");
  });

  it("the sheet renders nothing while closed, is a dialog when open, locks the page and closes on Escape / backdrop", () => {
    const onClose = vi.fn();
    const { rerender } = render(<FacetRail variant="sheet" open={false} onClose={onClose} spec={spec} state={EMPTY_FACET_STATE} actions={actions()} grouped={false} activeCount={0} />);
    expect(screen.queryByRole("dialog")).toBeNull();
    rerender(<FacetRail variant="sheet" open onClose={onClose} spec={spec} state={EMPTY_FACET_STATE} actions={actions()} grouped={false} activeCount={0} />);
    expect(screen.getByRole("dialog", { name: "Filters" })).toBeInTheDocument();
    expect(document.body.style.overflow).toBe("hidden");
    fireEvent.keyDown(document, { key: "Escape" });
    expect(onClose).toHaveBeenCalledTimes(1);
    fireEvent.click(document.querySelector(".bx-rail-backdrop")!);
    expect(onClose).toHaveBeenCalledTimes(2);
    rerender(<FacetRail variant="sheet" open={false} onClose={onClose} spec={spec} state={EMPTY_FACET_STATE} actions={actions()} grouped={false} activeCount={0} />);
    expect(document.body.style.overflow).toBe("");
  });
});

describe("FacetRail — fixed-scale ranges", () => {
  it("draws the range under the facet it follows, reads the state's thumbs, and a thumb move commits stop values (ends open)", () => {
    const a = actions();
    render(<FacetRail variant="rail" spec={spec} state={{ ...EMPTY_FACET_STATE, ranges: { age: { min: 12, max: null } } }} actions={a} facets={facets} grouped={false} activeCount={1} />);
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
