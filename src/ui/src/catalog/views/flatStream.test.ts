import { wallCapacity } from "../cards/useWallCapacity";
import type { CardGroup, CatalogSource } from "../types";
import { groupByFor, representative } from "./flatStream";

describe("catalog/flatStream helpers", () => {
  it("a representative is the group's first card wearing the group's identity", () => {
    const group: CardGroup = {
      key: "s:7", label: "Batman", totalItems: 42, renderTotal: 1,
      items: [{ kind: "comic", id: 1, key: "comic:1", title: "Batman #1", aspect: 0.66, imageUrl: "/t/1", raw: null }],
    };
    const rep = representative(group)!;
    expect(rep.title).toBe("Batman");
    expect(rep.count).toBe(42);
    expect(rep.label).toBe("42 items");
    expect(rep.group).toBe(group);
    expect(rep.key).toBe("group:s:7:comic:1");
    expect(representative({ ...group, items: [] })).toBeNull();
    expect(representative({ ...group, detail: { runLabel: "1987 – Present" } })!.label).toBe("1987 – Present");
  });

  it("the representative mode groups by the pill's choice, else the source's default, else its first group", () => {
    const base: CatalogSource = {
      queryKey: "q", supports: ["grid"], groups: [{ value: "genre", label: "Genre" }, { value: "decade", label: "Decade" }],
      sorts: [{ value: "alpha", label: "A–Z" }], fetchFlatBand: async () => ({ items: [], total: 0 }), onOpen: () => {},
    };
    const state = { view: "grid" as const, group: "none", items: "groups" as const, sort: "alpha" };
    expect(groupByFor(base, state)).toBe("genre");
    expect(groupByFor({ ...base, defaultGroup: "decade" }, state)).toBe("decade");
    expect(groupByFor(base, { ...state, group: "decade" })).toBe("decade");
  });

  it("the Wall's capacity is one screenful of covers, never fewer than two rows", () => {
    expect(wallCapacity(1000, 700, 140)).toBe(Math.floor(1000 / 92) * 5);
    expect(wallCapacity(1000, 100, 140)).toBe(Math.floor(1000 / 92) * 2);
    expect(wallCapacity(50, 700, 140)).toBe(5);
  });
});
