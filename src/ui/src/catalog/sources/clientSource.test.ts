import type { CardItem } from "../types";
import { createClientSource } from "./clientSource";

const item = (id: number, title: string, year: number, tags: string[]): CardItem => ({
  kind: "boardgame", id, key: `boardgame:${id}`, title, sortKey: title, year, aspect: 1, imageUrl: `/i/${id}`, imageThumbUrl: `/t/${id}`, hue: id * 10, raw: { tags },
});
const items = [
  item(1, "Azul", 2017, ["Abstract"]),
  item(2, "Brass", 2007, ["Economic", "Heavy"]),
  item(3, "Carcassonne", 2000, ["Tile"]),
  item(4, "Catan", 1995, ["Economic"]),
  item(5, "Dune", 2019, ["Heavy"]),
  item(6, "7 Wonders", 2010, ["Card"]),
];

function make(currentSort?: string) {
  return createClientSource({
    queryKey: "bg:test",
    title: "Board Games",
    itemNoun: "game",
    items,
    currentSort,
    groups: [
      { value: "decade", label: "Decade", order: "keyDesc", keysOf: (i) => (i.year ? { key: String(Math.floor(i.year / 10) * 10), label: `${Math.floor(i.year / 10) * 10}s` } : null) },
      { value: "tag", label: "Tag", keysOf: (i) => ((i.raw as { tags: string[] }).tags.map((t) => ({ key: t, label: t }))) },
    ],
    sorts: [
      { value: "name", label: "A–Z", alpha: true },
      { value: "year", label: "Newest", compare: (a, b) => (b.year ?? 0) - (a.year ?? 0) },
    ],
    directoryGroup: "tag",
    onOpen: vi.fn(),
  });
}

describe("catalog/clientSource — bands, groups, letters and a directory over an in-memory list", () => {
  it("pages the incoming order for a sort without a comparator, applies one when given, and buckets letters", async () => {
    const s = make("name");
    expect(s.currentSort).toBe("name");
    const band = await s.fetchFlatBand(2, 3, "name");
    expect(band.total).toBe(6);
    expect(band.items.map((i) => i.title)).toEqual(["Carcassonne", "Catan", "Dune"]);
    const newest = await s.fetchFlatBand(0, 2, "year");
    expect(newest.items.map((i) => i.title)).toEqual(["Dune", "Azul"]);
    expect(await s.letters!("name")).toEqual([
      { letter: "A", count: 1, offset: 0 }, { letter: "B", count: 1, offset: 1 }, { letter: "C", count: 2, offset: 2 }, { letter: "D", count: 1, offset: 4 }, { letter: "#", count: 1, offset: 5 },
    ]);
  });

  it("groups by a single-key and a multi-key grouper, orders heads as asked, windows per group and pulls more", async () => {
    const s = make("name");
    const decades = await s.fetchGroupBand!(0, 20, 1, "decade", "name");
    expect(decades.totalGroups).toBe(3);
    expect(decades.groups.map((g) => [g.key, g.label, g.totalItems, g.items.length])).toEqual([["2010", "2010s", 3, 1], ["2000", "2000s", 2, 1], ["1990", "1990s", 1, 1]]);
    expect(decades.groups[0].items[0].groupKey).toBe("2010");
    const tags = await s.fetchGroupBand!(0, 2, 5, "tag", "name");
    expect(tags.totalGroups).toBe(5);
    expect(tags.groups.map((g) => g.label)).toEqual(["Abstract", "Card"]);
    const more = await s.fetchGroupMore!("Economic", 1, 5, "tag", "name");
    expect(more).toMatchObject({ total: 2 });
    expect(more.items.map((i) => i.title)).toEqual(["Catan"]);
    expect(await s.groupLetters!("tag", "name")).toEqual([{ letter: "A", firstIndex: 0 }, { letter: "C", firstIndex: 1 }, { letter: "E", firstIndex: 2 }, { letter: "H", firstIndex: 3 }, { letter: "T", firstIndex: 4 }]);
  });

  it("the directory's roots are the chosen grouping's heads with a representative image, and its items page a group", async () => {
    const s = make("name");
    const roots = await s.directory!.roots();
    expect(roots.map((r) => [r.id, r.count, r.imageUrl])).toEqual([["Abstract", 1, "/t/1"], ["Card", 1, "/t/6"], ["Economic", 2, "/t/2"], ["Heavy", 2, "/t/2"], ["Tile", 1, "/t/3"]]);
    expect(await s.directory!.children("Heavy")).toEqual([]);
    const page = await s.directory!.items("Heavy", 0, 10);
    expect(page.items.map((i) => i.title)).toEqual(["Brass", "Dune"]);
  });

  it("offers every view, both item modes and the group pills only when it has groupers", () => {
    expect(make().supports).toHaveLength(7);
    const flat = createClientSource({ queryKey: "q", items, sorts: [{ value: "name", label: "A–Z" }], onOpen: vi.fn() });
    expect(flat.supports).toEqual(["grid", "wall", "list"]);
    expect(flat.itemsModes).toBeUndefined();
    expect(flat.fetchGroupBand).toBeUndefined();
    expect(flat.directory).toBeUndefined();
  });
});
