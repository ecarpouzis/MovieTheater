import { fireEvent, render, screen } from "@testing-library/react";
import type { FacetSpec } from "./facetSpec";
import SmartSearch, { suggestionsFor, buildSuggestionIndex } from "./SmartSearch";

const spec: FacetSpec = {
  identity: "t",
  facets: [
    { key: "tags", token: "tag", label: "Tags", one: "Tag", valueType: "string" },
    { key: "authors", token: "author", label: "Writers", one: "Writer", valueType: "string" },
    { key: "publishers", token: "publisher", label: "Publishers", one: "Publisher", valueType: "string", render: "swatch" },
  ],
  loadFacets: async () => ({}),
};
const facets = {
  tags: [{ value: "Noir", label: "Noir", count: 40 }, { value: "Horror", label: "Horror", count: 90 }, { value: "Humor", label: "Humor", count: 5 }],
  authors: [{ value: "Frank Miller", label: "Frank Miller", count: 12 }, { value: "Mike Mignola", label: "Mike Mignola", count: 30 }],
  publishers: [{ value: "Dark Horse", label: "Dark Horse", count: 500 }],
};

describe("suggestionsFor", () => {
  const index = buildSuggestionIndex(spec, facets);
  it("puts the free-text row first, then prefix matches by count", () => {
    const s = suggestionsFor("h", spec, index);
    expect(s[0]).toEqual({ kind: "text", value: "h" });
    expect(s.slice(1).map((x) => (x.kind === "filter" ? x.display : ""))).toEqual(["Horror", "Humor", "Dark Horse"]);
  });
  it("a token prefix narrows to that facet and drops the text row", () => {
    const s = suggestionsFor("author: mi", spec, index);
    expect(s.map((x) => (x.kind === "filter" ? x.display : "text"))).toEqual(["Frank Miller", "Mike Mignola"]);
  });
  it("swatch facets carry a hue for the type badge", () => {
    const s = suggestionsFor("dark", spec, index);
    expect(s[1].kind === "filter" && typeof s[1].hue).toBe("number");
  });
});

describe("SmartSearch", () => {
  it("Enter on the highlighted row adds the filter; Enter with the text row sets the search", () => {
    const onAdd = vi.fn();
    const onText = vi.fn();
    render(<SmartSearch spec={spec} facets={facets} onAdd={onAdd} onText={onText} />);
    const input = screen.getByRole("combobox");
    fireEvent.change(input, { target: { value: "horr" } });
    expect(screen.getAllByRole("option")).toHaveLength(2);
    fireEvent.keyDown(input, { key: "ArrowDown" });
    fireEvent.keyDown(input, { key: "Enter" });
    expect(onAdd).toHaveBeenCalledWith("tags", "Horror");
    expect((input as HTMLInputElement).value).toBe("");

    fireEvent.change(input, { target: { value: "hellboy" } });
    fireEvent.keyDown(input, { key: "Enter" });
    expect(onText).toHaveBeenCalledWith("hellboy");
  });

  it("Escape closes the list; a click on a row commits it", () => {
    const onAdd = vi.fn();
    render(<SmartSearch spec={spec} facets={facets} onAdd={onAdd} onText={vi.fn()} />);
    const input = screen.getByRole("combobox");
    fireEvent.change(input, { target: { value: "noir" } });
    fireEvent.keyDown(input, { key: "Escape" });
    expect(screen.queryByRole("listbox")).toBeNull();
    fireEvent.focus(input);
    fireEvent.click(screen.getByText("Noir"));
    expect(onAdd).toHaveBeenCalledWith("tags", "Noir");
  });
});
