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
    expect(pageStrip(3)).toEqual([1, 2, 3]);
  });

  // The condensed strip (1 … 145 146 147 148 149 … 289) is GONE. Eric, 2026-08-28: "the buttons
  // themselves become too wide, and can't be scrolled like the letters can be. Both are defects." A
  // seven-button run cannot overflow a desktop row, so there was nothing to scroll; the full run is
  // what gives the strip somewhere to go, exactly like the 27 letters on a phone.
  it("renders the FULL run of a long catalog — no ellipses, the way letters mode renders every letter", () => {
    const strip = pageStrip(440);
    expect(strip).toHaveLength(440);
    expect(strip[0]).toBe(1);
    expect(strip[439]).toBe(440);
    expect(strip.every((p) => typeof p === "number")).toBe(true);
  });

  it("collapses to a single page", () => {
    expect(pageStrip(1)).toEqual([1]);
    expect(pageStrip(0)).toEqual([1]);
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

// ── One strip, one layout; a page button is a letter button (2026-08-28) ────────────────────────
// Eric: "What I had meant about numbers not stretching is that the buttons themselves become too
// wide, and can't be scrolled like the letters can be. Both are defects." Both halves are pinned
// here: the WIDTH rule is the letters' (shared, and capped so nothing fills a wide row), and the RUN
// is the full 1…N so the strip has somewhere to scroll — which is what the letters have always had.
describe("a page button is a letter button", () => {
  const LETTERS = [
    { letter: "A", count: 100, offset: 0 },
    { letter: "B", count: 100, offset: 100 },
  ];
  const pager = (mode, props = {}) => render(
    <CatalogPager
      mode={mode}
      letters={mode === "letters" ? LETTERS : null}
      total={200}
      pageSize={60}
      currentIndex={0}
      onJump={() => {}}
      {...props}
    />
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

  it("renders the whole run of pages — one button per page, no ellipsis", () => {
    // 6,060 titles at 60 a band = 101 pages. Every one of them gets a button, the way every letter
    // gets one; a condensed run is what left seven fat slabs with nothing to scroll.
    const { container } = pager("pages", { total: 6060, pageSize: 60 });
    const btns = [...container.querySelectorAll(".catalog-pager__btn")];
    expect(btns).toHaveLength(101);
    expect(btns[0].textContent).toBe("1");
    expect(btns[100].textContent).toBe("101");
    expect(container.querySelector(".catalog-pager__gap")).toBeNull();
    expect(container.textContent).not.toContain("…");
  });

  it("marks the page the grid is on, and only that one", () => {
    // Card 2,430 of the 6,060 is page 41 — the readout follows the grid the way the letter does.
    const { container } = pager("pages", { total: 6060, pageSize: 60, currentIndex: 2430 });
    const active = [...container.querySelectorAll(".catalog-pager__btn--active")];
    expect(active).toHaveLength(1);
    expect(active[0].textContent).toBe("41");
    expect(active[0].getAttribute("aria-current")).toBe("true");
    // The aria semantics are the letters': a labelled nav of buttons, one carrying aria-current.
    expect(container.querySelector("nav").getAttribute("aria-label")).toBe("Jump to page");
    expect([...container.querySelectorAll('[aria-current="true"]')]).toHaveLength(1);
  });

  it("seeks the grid to that page's offset when a page is tapped", () => {
    const jumps = [];
    const { container } = pager("pages", { total: 6060, pageSize: 60, onJump: (o) => jumps.push(o) });
    container.querySelectorAll(".catalog-pager__btn")[40].click();
    expect(jumps).toEqual([2400]);
  });

  // happy-dom's getComputedStyle is stubbed to inline styles only (setupTests.js), so the rules that
  // decide the width and the scroll are checked where they live.
  it("shares ONE width rule, bounded, and ONE scrolling strip", () => {
    // The path is a VARIABLE on purpose: Vite statically rewrites a literal
    // `new URL("./x", import.meta.url)` into an asset URL (http://localhost:3000/…), and readFileSync
    // then refuses it. Same trick as `sectionCardParity.test.jsx`.
    const rel = "./CatalogPager.css";
    const css = readFileSync(new URL(rel, import.meta.url), "utf8");
    const btnRule = css.match(/\n\.catalog-pager__btn\s*\{[^}]*\}/)[0];
    // Grows to share the row, never shrinks, and NEVER past a letter's pill — the cap is the half
    // that was missing when seven page numbers split a 1228px row into ~290px slabs.
    expect(btnRule).toMatch(/flex:\s*1 0 auto/);
    expect(btnRule).toMatch(/min-width:\s*28px/);
    expect(btnRule).toMatch(/max-width:\s*46px/);
    // The phone override moves BOTH ends together — a min without a max is how this broke.
    const phoneRule = css.match(/@media \(max-width: 640px\)[\s\S]*?\.catalog-pager__btn\s*\{[^}]*\}/)[0];
    expect(phoneRule).toMatch(/min-width:\s*22px/);
    expect(phoneRule).toMatch(/max-width:\s*34px/);
    // One strip, and it scrolls sideways in BOTH modes: the rule is on the unscoped nav.
    expect(css).toMatch(/\n\.catalog-pager\s*\{[^}]*overflow-x:\s*auto/);
    expect(css).toMatch(/\n\.catalog-pager\s*\{[^}]*flex-wrap:\s*nowrap/);
    // …and no mode-scoped layout RULE survives (the words still appear, in the comments that record
    // why the modifier went away — so this must match a rule, not a mention).
    expect(css).not.toMatch(/\.catalog-pager--letters[^{\n]*\{/);
  });
});
