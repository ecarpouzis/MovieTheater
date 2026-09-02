import { act, fireEvent, render, screen } from "@testing-library/react";
import type { FacetDef, FacetOptionRow } from "./facetSpec";
import FacetOptions from "./FacetOptions";

const rows = (n: number, prefix = "Tag"): FacetOptionRow[] => Array.from({ length: n }, (_, i) => ({ value: `${prefix} ${i + 1}`, label: `${prefix} ${i + 1}`, count: n - i }));

describe("FacetOptions", () => {
  const def: FacetDef = { key: "tags", token: "tag", label: "Tags", one: "Tag", valueType: "string" };

  it("include and exclude buttons report their mode; active rows sort first and are shown even when absent from the list", () => {
    const onToggle = vi.fn();
    render(<FacetOptions def={def} options={rows(3)} selected={["tag 3"]} excluded={["Missing"]} onToggle={onToggle} />);
    const labels = Array.from(document.querySelectorAll(".bx-opt-label")).map((n) => n.textContent);
    expect(labels).toEqual(["Missing", "Tag 3", "Tag 1", "Tag 2"]);
    expect(screen.getByRole("button", { name: "Include Tag 3" })).toHaveAttribute("aria-pressed", "true");
    expect(screen.getByRole("button", { name: "Exclude Missing" })).toHaveAttribute("aria-pressed", "true");
    fireEvent.click(screen.getByRole("button", { name: "Exclude Tag 1" }));
    expect(onToggle).toHaveBeenCalledWith("tags", "Tag 1", "exc");
  });

  it("a long list gets a filter box that narrows it locally", () => {
    render(<FacetOptions def={def} options={rows(12)} selected={[]} excluded={[]} onToggle={vi.fn()} />);
    const box = screen.getByRole("textbox", { name: "Filter tags" });
    fireEvent.change(box, { target: { value: "tag 1" } });
    const labels = Array.from(document.querySelectorAll(".bx-opt-label")).map((n) => n.textContent);
    expect(labels).toEqual(["Tag 1", "Tag 10", "Tag 11", "Tag 12"]);
  });

  it("a dynamic facet searches the server after 300 ms, ignoring stale answers", async () => {
    vi.useFakeTimers();
    const loadOptions = vi.fn(async (_key: string, q: string) => ({ items: [{ value: `${q}!`, label: `${q}!`, count: 1 }], total: 1 }));
    render(<FacetOptions def={{ ...def, dynamic: true }} options={rows(2)} selected={[]} excluded={[]} onToggle={vi.fn()} loadOptions={loadOptions} />);
    const box = screen.getByRole("textbox", { name: "Filter tags" });
    fireEvent.change(box, { target: { value: "no" } });
    fireEvent.change(box, { target: { value: "noir" } });
    await act(async () => { vi.advanceTimersByTime(299); });
    expect(loadOptions).not.toHaveBeenCalled();
    await act(async () => { vi.advanceTimersByTime(2); });
    expect(loadOptions).toHaveBeenCalledTimes(1);
    // …carrying an abort signal: a superseded typeahead is taken back off the wire, not just ignored.
    expect(loadOptions).toHaveBeenCalledWith("tags", "noir", 0, 50, expect.any(AbortSignal));
    await act(async () => { await Promise.resolve(); });
    expect(screen.getByText("noir!")).toBeInTheDocument();
    vi.useRealTimers();
  });

  // The row IS the include control (2026-09-02). It used to carry a checkbox-shaped square with no
  // handler plus a "+" and a "-", which left the label 46px on the 200px sider - "Comedy" drew as
  // "Come...". The square and the "+" are gone; what stays is one click on the row.
  it("the row itself includes, and it draws no dead checkbox square", () => {
    const onToggle = vi.fn();
    render(<FacetOptions def={def} options={rows(3)} selected={[]} excluded={[]} onToggle={onToggle} />);
    expect(document.querySelector(".bx-opt-box")).toBeNull();
    fireEvent.click(screen.getByRole("button", { name: "Include Tag 2" }));
    expect(onToggle).toHaveBeenCalledWith("tags", "Tag 2", "inc");
    // The label lives inside that button, so clicking the text filters.
    expect(screen.getByText("Tag 2").closest("button")).toHaveAttribute("aria-label", "Include Tag 2");
  });

  it("a facet the API can only subtract from makes the ROW the exclude control", () => {
    const onToggle = vi.fn();
    render(<FacetOptions def={{ ...def, includable: false }} options={rows(2)} selected={[]} excluded={[]} onToggle={onToggle} />);
    expect(screen.queryByRole("button", { name: "Include Tag 1" })).toBeNull();
    fireEvent.click(screen.getByRole("button", { name: "Exclude Tag 1" }));
    expect(onToggle).toHaveBeenCalledWith("tags", "Tag 1", "exc");
  });

  it("a non-excludable facet offers no second control at all", () => {
    render(<FacetOptions def={{ ...def, excludable: false }} options={rows(2)} selected={[]} excluded={[]} onToggle={vi.fn()} />);
    expect(screen.getByRole("button", { name: "Include Tag 1" })).toBeInTheDocument();
    expect(document.querySelectorAll(".bx-opt-exc")).toHaveLength(0);
  });

  it("a pill is a real button, not a div wearing a role", () => {
    const onToggle = vi.fn();
    render(<FacetOptions def={{ ...def, render: "pill" }} options={rows(2)} selected={["Tag 1"]} excluded={[]} onToggle={onToggle} />);
    const pill = screen.getByRole("button", { name: "Include Tag 1" });
    expect(pill.tagName).toBe("BUTTON");
    expect(pill).toHaveAttribute("aria-pressed", "true");
    expect(pill).toHaveClass("bx-opt-pill");
  });

  it("a tile facet draws covers, a non-filterable facet has no +/-", () => {
    render(<FacetOptions def={{ ...def, render: "tile", filterable: false }} options={[{ value: 1, label: "Marvel", count: 3, imageUrl: "x.jpg" }]} selected={[]} excluded={[]} onToggle={vi.fn()} />);
    expect(document.querySelector("img.bx-opt-cover")).toHaveAttribute("src", "x.jpg");
    expect(screen.queryByRole("button", { name: "Include Marvel" })).toBeNull();
  });
});
