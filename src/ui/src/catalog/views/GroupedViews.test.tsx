import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import type { CardGroup, CardItem, CatalogSource } from "../types";
import ExtendedView from "./ExtendedView";
import ShelvesView from "./ShelvesView";

const item = (id: number): CardItem => ({ kind: "movie", id, key: `movie:${id}`, title: `Title ${id}`, aspect: 0.66, imageUrl: `/i/${id}`, hue: 120, raw: null });
const group = (key: string, first: number, n: number, total: number): CardGroup => ({
  key, label: key, totalItems: total, renderTotal: total, items: Array.from({ length: n }, (_, i) => item(first + i)),
});

function makeSource(totalGroups: number, perGroup: number) {
  const fetchGroupBand = vi.fn(async (groupsSkip: number, groupsTop: number, perGroupTop: number) => {
    const groups: CardGroup[] = [];
    for (let g = groupsSkip; g < Math.min(totalGroups, groupsSkip + groupsTop); g += 1) groups.push(group(`Group ${g}`, g * 1000, Math.min(perGroupTop, perGroup), perGroup));
    return { groups, totalGroups };
  });
  const fetchGroupMore = vi.fn(async (groupKey: string, skip: number, top: number) => ({ items: Array.from({ length: Math.min(top, 5) }, (_, i) => item(90000 + skip + i)), total: -1 }));
  const source: CatalogSource = {
    queryKey: "q", title: "Movies", itemNoun: "title", groupNoun: "genres",
    supports: ["extended", "shelf"], groups: [{ value: "genre", label: "Genre" }], sorts: [{ value: "alpha", label: "A–Z", alpha: true }],
    fetchFlatBand: async () => ({ items: [], total: 0 }), fetchGroupBand, fetchGroupMore,
    groupLetters: async () => [{ letter: "G", firstIndex: 0 }],
    onOpen: vi.fn(), onOpenGroup: vi.fn(),
  };
  return { source, fetchGroupBand, fetchGroupMore };
}
const props = (source: CatalogSource) => ({ source, state: { view: "extended" as const, group: "genre", items: "items" as const, sort: "alpha" }, coverScale: 1, metadata: "label" as const, hover: "lift" as const, hoverClass: "bx-hover-lift" });

describe("catalog/ExtendedView — strips per group over the grouped stream", () => {
  it("renders band 0's groups as headers + strips, opens a group, and pulls more for a group", async () => {
    const { source, fetchGroupBand, fetchGroupMore } = makeSource(3, 60);
    render(<ExtendedView {...props(source)} />);
    await waitFor(() => expect(screen.getByText("Group 0")).toBeInTheDocument());
    expect(fetchGroupBand).toHaveBeenCalledWith(0, 20, 48, "genre", "alpha", expect.anything());
    expect(screen.getAllByRole("button", { name: /Title 0$/ })).toHaveLength(1);
    expect(screen.getAllByText("60 titles")).toHaveLength(3);
    // "more" exists because the group has 60 and the band page holds 48
    const more = screen.getAllByText("more →")[0];
    fireEvent.click(more);
    await waitFor(() => expect(fetchGroupMore).toHaveBeenCalledWith("Group 0", 48, 48, "genre", "alpha"));
    await waitFor(() => expect(screen.getByLabelText("Title 90048")).toBeInTheDocument());
    fireEvent.click(screen.getByText("Group 1"));
    expect(source.onOpenGroup).toHaveBeenCalledWith(expect.objectContaining({ key: "Group 1" }), "genre");
  });
});

describe("catalog/ExtendedView — a strip is windowed sideways like a Shelves plank", () => {
  it("mounts only the cards near the scrollport, with exact-width spacers holding the rest of the run", async () => {
    // happy-dom lays nothing out: give every element a 400 px scrollport so the window has something to measure.
    const cw = Object.getOwnPropertyDescriptor(HTMLElement.prototype, "clientWidth");
    Object.defineProperty(HTMLElement.prototype, "clientWidth", { configurable: true, get: () => 400 });
    try {
      const { source } = makeSource(1, 60);
      const { container } = render(<ExtendedView {...props(source)} />);
      await waitFor(() => expect(container.querySelector(".bx-strip")).toBeInTheDocument());
      const strip = container.querySelector(".bx-strip") as HTMLElement;
      const mounted = strip.querySelectorAll(".bx-card").length;
      expect(mounted).toBeGreaterThan(0);
      expect(mounted).toBeLessThan(48);
      // A 121 px card (184 × 0.66) + 14 px gap; the tail spacer covers the unmounted 48 − mounted cards and the gaps between them.
      const spacers = strip.querySelectorAll<HTMLElement>(".bx-strip-spacer");
      expect(spacers).toHaveLength(1);
      const unmounted = 48 - mounted;
      expect(spacers[0].style.flex).toBe(`0 0 ${unmounted * 121 + (unmounted - 1) * 14}px`);
      expect(strip.dataset.mounted).toBe(`0-${mounted}`);
    } finally {
      if (cw) Object.defineProperty(HTMLElement.prototype, "clientWidth", cw);
      else delete (HTMLElement.prototype as unknown as Record<string, unknown>).clientWidth;
    }
  });
});

describe("catalog/ShelvesView — a shelf per group with data-src covers", () => {
  it("renders band 0 as shelves of books and offers the letter pager when there is more than one band", async () => {
    const { source } = makeSource(45, 10);
    const { container } = render(<ShelvesView {...props(source)} />);
    await waitFor(() => expect(container.querySelectorAll(".shelf").length).toBe(20));
    const books = container.querySelectorAll(".shelf .bk");
    expect(books.length).toBe(200);
    expect((books[0].querySelector("img") as HTMLImageElement).getAttribute("data-src")).toBe("/i/0");
    expect((books[0].querySelector("img") as HTMLImageElement).getAttribute("src")).toBeNull(); // loaded by the engine's window pass, not eagerly
    expect(container.querySelectorAll(".bx-band-placeholder").length).toBe(2);
    await waitFor(() => expect(screen.getByRole("navigation", { name: "Jump to letter" })).toBeInTheDocument());
  });
});
