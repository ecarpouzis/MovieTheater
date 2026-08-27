import { fireEvent, render, screen } from "@testing-library/react";
import ActiveChips from "./ActiveChips";
import type { FacetSpec, FacetState } from "./facetSpec";
import { EMPTY_FACET_STATE } from "./facetSpec";

const spec: FacetSpec = {
  identity: "t",
  facets: [
    { key: "tags", token: "tag", label: "Tags", one: "Tag", valueType: "string" },
    { key: "series", token: "series", label: "Series", one: "Series", valueType: "number" },
  ],
  flags: [{ key: "want", token: "want", label: "Want to read" }],
  rating: { presets: [{ value: 80, label: "4★+" }] },
  ranges: [{ key: "age", token: "a", label: "Age", one: "Age", stops: [3, 8, 12, 18], openTop: true }],
  loadFacets: async () => ({}),
};
const facets = { series: [{ value: 9, label: "Hellboy", count: 5 }] };

function actions() {
  return { remove: vi.fn(), setText: vi.fn(), setYears: vi.fn(), setRating: vi.fn(), setRange: vi.fn(), setFlag: vi.fn(), clearAll: vi.fn() };
}

describe("ActiveChips", () => {
  it("renders nothing without filters", () => {
    const { container } = render(<ActiveChips spec={spec} state={EMPTY_FACET_STATE} actions={actions()} />);
    expect(container.firstChild).toBeNull();
  });

  it("one chip per value — includes, a red 'not' for excludes, number facets by their label — plus text, years, rating, flags", () => {
    const state: FacetState = { q: "hell", include: { tags: ["Noir"], series: [9] }, exclude: { tags: ["Manga"] }, yearMin: 1990, yearMax: null, ratingMin: 80, ranges: { age: { min: 12, max: null } }, flags: { want: true } };
    const a = actions();
    render(<ActiveChips spec={spec} state={state} actions={a} facets={facets} onSave={vi.fn()} />);
    const chips = screen.getAllByRole("button").filter((b) => b.className.startsWith("bx-chip") && !b.className.includes("clear") && !b.className.includes("save"));
    expect(chips.map((c) => c.textContent)).toEqual(["searchhell×", "TagNoir×", "not tagManga×", "SeriesHellboy×", "years1990–…×", "rating4★+×", "age12+×", "myWant to read×"]);
    expect(chips[2].className).toContain("bx-chip-ex");

    fireEvent.click(chips[1]);
    expect(a.remove).toHaveBeenCalledWith("tags", "Noir");
    fireEvent.click(chips[2]);
    expect(a.remove).toHaveBeenCalledWith("tags", "Manga");
    fireEvent.click(chips[0]);
    expect(a.setText).toHaveBeenCalledWith("");
    fireEvent.click(chips[4]);
    expect(a.setYears).toHaveBeenCalledWith(null, null);
    fireEvent.click(chips[5]);
    expect(a.setRating).toHaveBeenCalledWith(0);
    fireEvent.click(chips[6]);
    expect(a.setRange).toHaveBeenCalledWith("age", null, null);
    fireEvent.click(chips[7]);
    expect(a.setFlag).toHaveBeenCalledWith("want", false);
    fireEvent.click(screen.getByText("Clear all"));
    expect(a.clearAll).toHaveBeenCalled();
    expect(screen.getByText("＋ Save view")).toBeInTheDocument();
  });
});
