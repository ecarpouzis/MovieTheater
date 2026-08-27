import { readFileSync } from "node:fs";
import { cleanup, render } from "@testing-library/react";
import { afterEach, describe, expect, it } from "vitest";
import CatalogPager, { activeLetter, bucketsFor, letterStrip, pageOf, pageStrip } from "./CatalogPager";

describe("letterStrip", () => {
  it("renders the full #, A–Z strip, filling in the buckets the catalog has no games for", () => {
    const strip = letterStrip([
      { letter: "#", count: 120, offset: 0 },
      { letter: "A", count: 900, offset: 120 },
      { letter: "C", count: 40, offset: 1020 },
    ]);
    expect(strip).toHaveLength(27);
    expect(strip[0]).toEqual({ letter: "#", count: 120, offset: 0 });
    expect(strip[1]).toEqual({ letter: "A", count: 900, offset: 120 });
    // B has no games — it still gets a (disabled) button, so the strip doesn't reflow per filter.
    expect(strip[2]).toEqual({ letter: "B", count: 0, offset: 0 });
    expect(strip[3]).toEqual({ letter: "C", count: 40, offset: 1020 });
  });

  it("survives no letters at all (the endpoint 404s, or a non-alphabetical sort)", () => {
    expect(letterStrip(null)).toHaveLength(27);
    expect(letterStrip(null).every((l) => l.count === 0)).toBe(true);
  });
});

describe("activeLetter", () => {
  const letters = letterStrip([
    { letter: "#", count: 100, offset: 0 },
    { letter: "A", count: 100, offset: 100 },
    { letter: "C", count: 100, offset: 200 },
  ]);

  it("is the last bucket that starts at or before the card on screen", () => {
    expect(activeLetter(letters, 0)).toBe("#");
    expect(activeLetter(letters, 99)).toBe("#");
    expect(activeLetter(letters, 100)).toBe("A");
    expect(activeLetter(letters, 199)).toBe("A");
    expect(activeLetter(letters, 250)).toBe("C");
  });

  it("never lands on an empty bucket — B's offset is a placeholder 0, not a real position", () => {
    expect(activeLetter(letters, 150)).toBe("A");
  });
});

describe("pageStrip", () => {
  it("shows every page when they all fit", () => {
    expect(pageStrip(1, 3).map((i) => i.page)).toEqual([1, 2, 3]);
  });

  it("condenses a long catalog around the current page", () => {
    const strip = pageStrip(147, 289);
    expect(strip.map((i) => (i.type === "gap" ? "…" : i.page))).toEqual([1, "…", 145, 146, 147, 148, 149, "…", 289]);
  });

  it("spells out a single skipped page rather than eliding it", () => {
    // 1 … 3 4 5 would hide exactly one page behind an ellipsis — just show page 2.
    expect(pageStrip(4, 9).map((i) => (i.type === "gap" ? "…" : i.page))).toEqual([1, 2, 3, 4, 5, 6, "…", 9]);
  });

  it("keeps the first and last page reachable from anywhere", () => {
    const ends = pageStrip(150, 289).filter((i) => i.type === "page").map((i) => i.page);
    expect(ends[0]).toBe(1);
    expect(ends[ends.length - 1]).toBe(289);
  });

  it("collapses to a single page", () => {
    expect(pageStrip(1, 1)).toEqual([{ type: "page", page: 1 }]);
    expect(pageStrip(1, 0)).toEqual([{ type: "page", page: 1 }]);
  });
});

describe("pageOf", () => {
  it("maps an absolute card index onto its 1-based page", () => {
    expect(pageOf(0, 60)).toBe(1);
    expect(pageOf(59, 60)).toBe(1);
    expect(pageOf(60, 60)).toBe(2);
    expect(pageOf(8760, 60)).toBe(147);
  });
});

describe("bucketsFor", () => {
  const key = (x) => x.sortName;

  it("gives each letter its first offset and its full count", () => {
    const items = [{ sortName: "Abba" }, { sortName: "Air" }, { sortName: "Beatles, The" }];
    expect(bucketsFor(items, key)).toEqual([
      { letter: "A", count: 2, offset: 0 },
      { letter: "B", count: 1, offset: 2 },
    ]);
  });

  it("files anything that doesn't start A–Z under #", () => {
    const items = [{ sortName: "10cc" }, { sortName: "!!!" }, { sortName: "Air" }];
    expect(bucketsFor(items, key)).toEqual([
      { letter: "#", count: 2, offset: 0 },
      { letter: "A", count: 1, offset: 2 },
    ]);
  });

  it("folds accents onto the base letter — Ángel is an A, not a #", () => {
    expect(bucketsFor([{ sortName: "Ángel" }], key)).toEqual([{ letter: "A", count: 1, offset: 0 }]);
  });

  it("keeps ONE bucket per letter even when the sort interleaves them", () => {
    // The server's collation may file "Ángel" between "Anderson" and "Bach"; a naive run-length
    // pass would open a second A bucket and the strip would keep only one of them.
    const items = [{ sortName: "Anderson" }, { sortName: "Ángel" }, { sortName: "Bach" }];
    expect(bucketsFor(items, key)).toEqual([
      { letter: "A", count: 2, offset: 0 },
      { letter: "B", count: 1, offset: 2 },
    ]);
  });

  it("survives an empty list and a missing key", () => {
    expect(bucketsFor([], key)).toEqual([]);
    expect(bucketsFor([{}], key)).toEqual([{ letter: "#", count: 1, offset: 0 }]);
  });
});

// ── One strip, one layout (2026-08-28) ──────────────────────────────────────────────────────────
// Eric, on his phone: "why do the numeric buttons on the movie page all scrunch up, and not take up
// the space they can like the letters do?" They did, because the growth rule was scoped to a
// `catalog-pager--letters` modifier on the nav. The modifier is gone and the rule moved onto the
// button, so the two modes cannot drift apart again — which is what these pin.
describe("the pager's layout does not depend on its mode", () => {
  const LETTERS = [
    { letter: "A", count: 100, offset: 0 },
    { letter: "B", count: 100, offset: 100 },
  ];
  const pager = (mode) => render(
    <CatalogPager mode={mode} letters={mode === "letters" ? LETTERS : null} total={200} pageSize={60} currentIndex={0} onJump={() => {}} />
  );
  /** The class list minus the active/current modifier — that one is a READOUT, not a layout. */
  const layout = (el) => el.className.split(" ").filter((c) => !c.endsWith("--active")).sort().join(" ");

  afterEach(cleanup);

  it("draws the same nav class and the same button class under letters and under pages", () => {
    const a = pager("letters");
    const lettersNav = layout(a.container.querySelector("nav"));
    const letterBtns = [...a.container.querySelectorAll(".catalog-pager__btn")].map(layout);
    a.unmount();

    const b = pager("pages");
    const pagesNav = layout(b.container.querySelector("nav"));
    const pageBtns = [...b.container.querySelectorAll(".catalog-pager__btn")].map(layout);

    expect(pagesNav).toBe(lettersNav);
    expect(new Set(pageBtns)).toEqual(new Set(letterBtns));
    // Both modes actually drew buttons — an empty strip would pass every equality above.
    expect(letterBtns.length).toBeGreaterThan(1);
    expect(pageBtns.length).toBeGreaterThan(1);
  });

  // happy-dom's getComputedStyle is stubbed to inline styles only (setupTests.js), so the rule that
  // makes them share the row is checked where it lives.
  it("the growth rule sits on the button, unscoped by mode", () => {
    // The path is a VARIABLE on purpose: Vite statically rewrites a literal
    // `new URL("./x", import.meta.url)` into an asset URL (http://localhost:3000/…), and readFileSync
    // then refuses it. Same trick as `sectionCardParity.test.jsx`.
    const rel = "./CatalogPager.css";
    const css = readFileSync(new URL(rel, import.meta.url), "utf8");
    expect(css).toMatch(/\.catalog-pager__btn\s*\{[^}]*flex:\s*1 0 auto/);
    // …and no mode-scoped layout RULE survives (the words still appear, in the comment that records
    // why the modifier went away — so this must match a rule, not a mention).
    expect(css).not.toMatch(/\.catalog-pager--letters[^{\n]*\{/);
  });
});
