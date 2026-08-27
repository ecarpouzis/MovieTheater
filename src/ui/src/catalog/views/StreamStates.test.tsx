import { emptyLine } from "./StreamStates";

/**
 * An empty result is TWO different reports and the views used to file only one of them: "nothing
 * matched" reads as a filter problem even on a section that has no rows at all. `emptyLabel` +
 * `filtered` let the source say which (R9 S4).
 */
describe("catalog/StreamEmpty — the section's own empty line", () => {
  it("falls back to the noun sentence when a source says nothing", () => {
    expect(emptyLine({ itemNoun: "album" })).toBe("No albums match.");
    expect(emptyLine({})).toBe("No items match.");
  });

  it("picks the source's unfiltered line when nothing narrows", () => {
    expect(emptyLine({
      itemNoun: "game",
      emptyLabel: { empty: "No games here yet.", filtered: "No games match those filters." },
      filtered: false,
    })).toBe("No games here yet.");
  });

  it("picks the filtered line when something does", () => {
    expect(emptyLine({
      itemNoun: "game",
      emptyLabel: { empty: "No games here yet.", filtered: "No games match those filters." },
      filtered: true,
    })).toBe("No games match those filters.");
  });
});
