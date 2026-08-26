import { groupOf, isGroupCard, mapExplore, toExploreCard } from "./mapExplore";

const wire = (over: Record<string, unknown> = {}) => ({
  kind: "comic", id: 7, key: "comic:7", title: "Hellboy #7", subtitle: "Hellboy", label: "1994", year: 1994, aspect: 0.7,
  imageUrl: null, imageThumbUrl: null, hue: null, rating: 84, badges: [{ label: "84", tone: "rating", title: "Library rating" }], groupKey: "9", sortKey: "84", raw: { id: 7 }, ...over,
});

describe("catalog/explore/mapExplore — the wire envelope onto cards", () => {
  it("gives a card without art a hue tile derived from its title, and keeps art that came", () => {
    const bare = toExploreCard(wire());
    expect(bare.imageUrl.startsWith("data:image/svg+xml")).toBe(true);
    expect(bare.hue).toBe(toExploreCard(wire({ id: 8 })).hue); // same title → same tint
    expect(bare.badges?.[0]).toEqual({ label: "84", tone: "rating", title: "Library rating" });
    const drawn = toExploreCard(wire({ imageUrl: "https://m/7.webp", imageThumbUrl: "https://m/7.webp", hue: 120 }));
    expect(drawn.imageUrl).toBe("https://m/7.webp");
    expect(drawn.hue).toBe(120);
  });

  it("maps the envelope: rail kinds it does not know become strips, an unknown badge tone is neutral, the seed is echoed", () => {
    const r = mapExplore({
      spotlight: [wire()],
      rails: [
        { key: "a", title: "A", kind: "wall", items: [wire({ id: 8, badges: [{ label: "x", tone: "weird" }] })], more: { href: "/suggestions" } },
        { key: "b", title: "B", kind: "carousel", items: [], more: null },
      ],
      seed: 42,
    });
    expect(r.seed).toBe(42);
    expect(r.spotlight[0].key).toBe("comic:7");
    expect(r.rails[0].kind).toBe("wall");
    expect(r.rails[0].more).toEqual({ href: "/suggestions" });
    expect(r.rails[0].items[0].badges?.[0].tone).toBe("neutral");
    expect(r.rails[1].kind).toBe("strip");
    expect(r.rails[1].more).toBeUndefined();
  });

  it("a series card is a group card whose CardGroup sizes on the raw issue count", () => {
    const series = toExploreCard(wire({ kind: "series", id: 9, key: "series:9", title: "Hellboy", groupKey: "9", raw: { seriesId: 9, issueCount: 5 } }));
    expect(isGroupCard(series)).toBe(true);
    expect(isGroupCard(toExploreCard(wire()))).toBe(false);
    const g = groupOf(series);
    expect(g).toMatchObject({ key: "9", label: "Hellboy", totalItems: 5, renderTotal: 1 });
    expect(g.items[0]).toBe(series);
  });
});
