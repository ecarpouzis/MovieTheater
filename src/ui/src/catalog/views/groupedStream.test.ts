import { groupLetterBuckets, groupRunLabel } from "./groupedStream";

describe("catalog/groupedStream helpers", () => {
  it("letter buckets over the grouped order: offset = first group index, count = to the next letter", () => {
    const buckets = groupLetterBuckets([{ letter: "A", firstIndex: 0 }, { letter: "B", firstIndex: 4 }, { letter: "M", firstIndex: 15 }], 28);
    expect(buckets).toEqual([
      { letter: "A", offset: 0, count: 4 },
      { letter: "B", offset: 4, count: 11 },
      { letter: "M", offset: 15, count: 13 },
    ]);
  });

  it("a group's run label prefers the detail's span, else the count with the section's noun", () => {
    const g = { key: "k", label: "L", totalItems: 1, renderTotal: 1, items: [] };
    expect(groupRunLabel(g, "title")).toBe("1 title");
    expect(groupRunLabel({ ...g, totalItems: 12 }, "title")).toBe("12 titles");
    expect(groupRunLabel({ ...g, detail: { runLabel: "1987 – Present" } }, "title")).toBe("1987 – Present");
  });
});
