import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import type { CatalogSource, DirectoryNode } from "../types";
import DirectoryView from "./DirectoryView";

const nodes: Record<string, DirectoryNode[]> = {
  root: [{ id: "a", label: "Alpha", count: 2, hasChildren: true }, { id: "e", label: "Empty", count: 0 }, { id: "z", label: "Zulu", count: 5 }],
  a: [{ id: "a1", label: "Alpha One", count: 1 }],
};
const source: CatalogSource = {
  queryKey: "q", title: "Movies", itemNoun: "title", supports: ["directory"], groups: [],
  sorts: [{ value: "alpha", label: "A–Z", alpha: true }, { value: "count", label: "Most" }],
  fetchFlatBand: async () => ({ items: [], total: 0 }),
  onOpen: vi.fn(),
  directory: {
    roots: async () => nodes.root,
    children: async (id) => nodes[id] ?? [],
    items: async (id) => ({ items: id === "a" ? [{ kind: "movie", id: 1, key: "movie:1", title: "Inside Alpha", aspect: 0.66, imageUrl: "/i/1", raw: null }] : [], total: id === "a" ? 1 : 0 }),
  },
};
const base = { source, coverScale: 1, metadata: "label" as const, hover: "lift" as const, hoverClass: "bx-hover-lift" };

describe("catalog/DirectoryView — the section's hierarchy as a file explorer", () => {
  it("shows the roots (empty ones hidden), drills in, lists loose items, and the breadcrumb pops back", async () => {
    render(<DirectoryView {...base} state={{ view: "directory", group: "none", items: "items", sort: "alpha" }} showEmpty={false} />);
    await waitFor(() => expect(screen.getByText("Alpha")).toBeInTheDocument());
    expect(screen.queryByText("Empty")).toBeNull();
    expect(screen.getByText("Zulu")).toBeInTheDocument();
    fireEvent.click(screen.getByLabelText("Alpha"));
    await waitFor(() => expect(screen.getByText("Alpha One")).toBeInTheDocument());
    expect(screen.getByText("Inside Alpha")).toBeInTheDocument();
    expect(screen.getByText("Alpha", { selector: ".bx-crumb-current" })).toBeInTheDocument();
    fireEvent.click(screen.getByText("Movies"));
    await waitFor(() => expect(screen.getByText("Zulu")).toBeInTheDocument());
  });

  it("the show-empty tweak reveals empty nodes and a non-alphabetical sort orders by size", async () => {
    render(<DirectoryView {...base} state={{ view: "directory", group: "none", items: "items", sort: "count" }} showEmpty />);
    await waitFor(() => expect(screen.getByText("Empty")).toBeInTheDocument());
    const labels = screen.getAllByText(/^(Alpha|Empty|Zulu)$/).map((el) => el.textContent);
    expect(labels).toEqual(["Zulu", "Alpha", "Empty"]);
  });
});
