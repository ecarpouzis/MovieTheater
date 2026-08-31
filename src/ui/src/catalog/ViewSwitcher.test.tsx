import { render, screen } from "@testing-library/react";
import type { CatalogViewState } from "./state/useCatalogView";
import type { CatalogSource, GroupSpec } from "./types";
import { ViewPills } from "./ViewSwitcher";

/**
 * The Items pill's two rules, both learned from labels that lied:
 *
 * 1. It is FLAT-ONLY. The grouped views never read `state.items` (only `flatStream` does), so a pill
 *    drawn beside them changed nothing on screen — a control that silently stored a preference for
 *    the next flat view. Books shipped that state by default (`defaultView: "extended"`).
 * 2. Its collapsed label names the axis the flat stream will ACTUALLY collapse on, because
 *    `state.group` survives a view change: "By Publisher" in Extended, then Grid, used to offer
 *    Books' constant "Series" for a mode that collapses publishers.
 *
 * The pill renders its CURRENT value, so these assert on `items: "groups"` — what the reader sees
 * once the collapsed mode is on.
 */
const source = (groups: GroupSpec[], extra: Partial<CatalogSource> = {}): CatalogSource => ({
  queryKey: "q",
  supports: ["grid", "wall", "list", "extended"],
  groups,
  sorts: [{ value: "alpha", label: "A–Z", alpha: true }],
  itemsModes: ["items", "groups"],
  defaultGroup: groups[0]?.value,
  fetchFlatBand: async () => ({ items: [], total: 0 }),
  fetchGroupBand: async () => ({ groups: [], totalGroups: 0 }),
  onOpen: vi.fn(),
  ...extra,
});

const AXES: GroupSpec[] = [
  { value: "collection", label: "Collection", one: "collection" },
  { value: "publisher", label: "Publisher", one: "publisher" },
  { value: "author", label: "Writer", one: "writer" },
  // A bucket / pair axis carries no noun on purpose.
  { value: "kind", label: "Base or expansion" },
];

const state = (over: Partial<CatalogViewState> = {}): CatalogViewState =>
  ({ view: "grid", group: "collection", items: "groups", sort: "alpha", ...over });

const ALL = ["grid", "wall", "list", "extended"] as const;

function pills(src: CatalogSource, st: CatalogViewState) {
  return render(<ViewPills state={st} source={src} available={[...ALL]} onView={vi.fn()} onGroup={vi.fn()} onItems={vi.fn()} onSort={vi.fn()} />);
}

describe("catalog/ViewSwitcher — the Items pill", () => {
  it("is drawn on a flat view", () => {
    pills(source(AXES), state({ view: "grid" }));
    expect(screen.getByText("Items")).toBeInTheDocument();
  });

  it("is NOT drawn on a grouped view, which ignores the mode entirely", () => {
    pills(source(AXES), state({ view: "extended", group: "publisher" }));
    expect(screen.queryByText("Items")).toBeNull();
    // The Group pill is the grouped view's control, and it stays.
    expect(screen.getByText("Group")).toBeInTheDocument();
  });

  it("names the axis it will collapse on, not a constant", () => {
    pills(source(AXES), state({ view: "grid", group: "publisher" }));
    expect(screen.getByText("One per publisher")).toBeInTheDocument();
  });

  it("falls back to the section's default axis when the URL carries no group", () => {
    pills(source(AXES), state({ view: "wall", group: "none" }));
    expect(screen.getByText("One per collection")).toBeInTheDocument();
  });

  it("keeps the generic noun for an axis whose heads are a bucket or a pair", () => {
    pills(source(AXES), state({ view: "grid", group: "kind" }));
    expect(screen.getByText("One per group")).toBeInTheDocument();
  });

  it("lets a source with ONE fixed meaning override the axis (Music's flat mode is always artists)", () => {
    pills(source(AXES, { itemsLabels: { items: "Albums", groups: "Artists" } }), state({ view: "grid", group: "publisher" }));
    expect(screen.getByText("Artists")).toBeInTheDocument();
    expect(screen.queryByText("One per publisher")).toBeNull();
  });
});
