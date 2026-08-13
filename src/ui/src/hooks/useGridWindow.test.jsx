import { render, act, cleanup } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import useGridWindow from "./useGridWindow";

// ── scrollToIndex: a jump that does not truncate the list ───────────────────────────────────────
// The A–Z strip used to "jump" by re-anchoring the rendered slice at the letter's offset, which threw
// away everything before it — tap J and there was no way back up to A–I (reported 2026-08-13). The
// list stays whole now and the jump is a SCROLL, which puts the burden on this hook: it has to work
// out where a row it has never mounted actually IS.
//
// That is the part worth testing, and it cannot be tested against a uniform grid — with every row the
// same height the first guess is already right and the interesting code never runs. So the model
// below is deliberately LUMPY (short rows at the top, tall ones after), which is what an arcade card
// grid genuinely looks like: the running average measured from the first screenful under-estimates
// everything below it, the first scroll lands short, and only mounting-and-measuring where it landed
// corrects the rest. Converging on that is the whole of scrollToIndex.

const SHORT_ROWS = 20;   // rows 0..19
const SHORT_H = 120;
const TALL_H = 240;
const COUNT = 400;
const VIEWPORT_H = 600;

/** Per-row heights, MUTABLE: a row growing after it has been measured is the whole point of the
 *  compensation test below (a placeholder slot becoming a real card). */
let heights;
function resetHeights() {
  heights = Array.from({ length: COUNT }, (_, r) => (r < SHORT_ROWS ? SHORT_H : TALL_H));
}

/** True top of a row in the virtual document — what the hook has to converge on, never told to it. */
function trueTop(row) {
  let y = 0;
  for (let r = 0; r < row; r += 1) y += heights[r] ?? TALL_H;
  return y;
}
const rowH = (row) => heights[row] ?? TALL_H;

let scrollY;
let realRect;
let realComputed;

function Grid({ count, onApi, contentKey = "" }) {
  const api = useGridWindow(count, { resetKey: "fixed", contentKey });
  onApi(api);
  const { hostRef, gridRef, start, end, padTop, padBottom } = api;
  return (
    <div ref={hostRef}>
      <div style={{ height: `${padTop}px` }} />
      {/* Inline styles, because setupTests.js makes getComputedStyle return the inline declaration —
          which is exactly how measureCols reads the column count. */}
      <div ref={gridRef} style={{ display: "grid", gridTemplateColumns: "1fr", rowGap: "0px" }}>
        {Array.from({ length: Math.max(0, end - start) }, (_, i) => (
          <div key={start + i} data-row={start + i} className="card" />
        ))}
      </div>
      <div style={{ height: `${padBottom}px` }} />
    </div>
  );
}

beforeEach(() => {
  scrollY = 0;
  resetHeights();
  realRect = Element.prototype.getBoundingClientRect;
  realComputed = window.getComputedStyle;

  // One row per card, at its true height, positioned by the virtual scroll.
  Element.prototype.getBoundingClientRect = function rect() {
    const row = Number(this.dataset?.row);
    if (Number.isFinite(row)) {
      const top = trueTop(row) - scrollY;
      const height = rowH(row);
      return { top, bottom: top + height, height, left: 0, right: 0, width: 100, x: 0, y: top };
    }
    return { top: 0, bottom: 0, height: 0, left: 0, right: 0, width: 0, x: 0, y: 0 };
  };
  // offsetTop drives the row-pitch measurement; offsetHeight the last row's fallback.
  Object.defineProperty(HTMLElement.prototype, "offsetTop", {
    configurable: true,
    get() {
      const row = Number(this.dataset?.row);
      return Number.isFinite(row) ? trueTop(row) : 0;
    },
  });
  Object.defineProperty(HTMLElement.prototype, "offsetHeight", {
    configurable: true,
    get() {
      const row = Number(this.dataset?.row);
      return Number.isFinite(row) ? rowH(row) : 0;
    },
  });

  window.innerHeight = VIEWPORT_H;
  window.scrollBy = (x, dy) => { scrollY += dy; };
  window.requestAnimationFrame = (fn) => setTimeout(() => fn(Date.now()), 0);
  window.cancelAnimationFrame = (id) => clearTimeout(id);
  window.localStorage.clear();
});

afterEach(() => {
  cleanup();
  Element.prototype.getBoundingClientRect = realRect;
  window.getComputedStyle = realComputed;
  delete HTMLElement.prototype.offsetTop;
  delete HTMLElement.prototype.offsetHeight;
  vi.restoreAllMocks();
});

/** Let the settle loop's rAF chain — and the React commits it triggers — actually run. */
async function frames(n = 20) {
  for (let i = 0; i < n; i++) {
    // eslint-disable-next-line no-await-in-loop
    await act(async () => { await new Promise((r) => setTimeout(r, 0)); });
  }
}

async function mount(count = COUNT) {
  let api;
  const view = render(<Grid count={count} onApi={(a) => { api = a; }} />);
  await frames(6);
  const get = () => api;
  /** Announce that the SAME slots now hold different content — what a paged list does when a page
   *  lands and its placeholders become real cards. */
  get.contentChanged = async (key) => {
    await act(async () => { view.rerender(<Grid count={count} onApi={(a) => { api = a; }} contentKey={key} />); });
    await frames(8);
  };
  return get;
}

describe("useGridWindow.scrollToIndex", () => {
  it("lands on a row it has never mounted, correcting its own estimate on the way", async () => {
    const api = await mount();
    expect(scrollY).toBe(0);

    await act(async () => { api().scrollToIndex(150); });
    await frames();

    // Within a couple of pixels of where row 150 actually starts. A single-pass version cannot get
    // here: the average measured off the first screenful is ~190px against a true 240px, so its one
    // guess lands thousands of pixels short.
    expect(Math.abs(scrollY - trueTop(150))).toBeLessThanOrEqual(2);
  });

  it("does not need to move at all when the row is already at the top", async () => {
    const api = await mount();
    await act(async () => { api().scrollToIndex(0); });
    await frames(4);
    expect(scrollY).toBe(0);
  });

  it("clamps rather than scrolling past the end of the list", async () => {
    const api = await mount();
    await act(async () => { api().scrollToIndex(COUNT + 500); });
    await frames();
    expect(Math.abs(scrollY - trueTop(COUNT - 1))).toBeLessThanOrEqual(2);
  });

  it("can come back UP — the direction the whole fix exists for", async () => {
    const api = await mount();
    await act(async () => { api().scrollToIndex(300); });
    await frames();
    expect(scrollY).toBeGreaterThan(trueTop(200));

    await act(async () => { api().scrollToIndex(5); });
    await frames();
    expect(Math.abs(scrollY - trueTop(5))).toBeLessThanOrEqual(2);
  });

  it("is inert on an empty list instead of throwing", async () => {
    const api = await mount(0);
    await act(async () => { api().scrollToIndex(4); });
    await frames(2);
    expect(scrollY).toBe(0);
  });
  // ── The prepend-without-teleport property ──────────────────────────────────────────────────────
  // The arcade lobby now models its whole 17k-title catalog as slots that exist from the first render
  // (The Long Box's sparse-band model), so scrolling UP into never-fetched territory is not a prepend
  // — the slots were always there, they were just placeholders. What DOES move is their height: a
  // placeholder becoming a real card changes what the rows above the viewport are worth, and every
  // estimated row above the fold shifts with it. Uncompensated, that is the teleport.
  //
  // The assertion is therefore about the READER, not about scrollTop: whatever they were looking at
  // must still be exactly where it was.
  it("holds the viewport still when rows above the fold are re-measured", async () => {
    const api = await mount();
    await act(async () => { api().scrollToIndex(150); });
    await frames();

    // The contract, stated exactly: padTop changing while startRow does NOT is the hook correcting
    // the estimated height of content ABOVE the fold. Everything below it just moved by that delta,
    // so scrollTop has to move by the same amount or the reader's page jumps under them.
    const before = { padTop: api().padTop, startRow: api().startRow, scrollY };

    // Every row from here down turns out to be TALLER than the estimate that placed it — exactly what
    // a screenful of placeholders resolving into real cards does to the running average.
    for (let r = 150; r < COUNT; r += 1) heights[r] = 360;
    await api.contentChanged("pages-loaded");

    const after = { padTop: api().padTop, startRow: api().startRow, scrollY };
    expect(after.padTop).not.toBe(before.padTop);      // the estimate really did move
    expect(after.startRow).toBe(before.startRow);      // …and this is the compensated case
    expect(after.scrollY - before.scrollY).toBe(after.padTop - before.padTop);
  });

  it("reserves the whole list up front, so there is never anything to prepend", async () => {
    // The property the arcade port rests on: the window is over `count` — the SERVER's total — not
    // over "how many have been fetched". Row 399 has a real position from the first render, and so
    // does row 0 after you have travelled to the end, which is why coming back is just scrolling.
    const api = await mount();
    await act(async () => { api().scrollToIndex(COUNT - 1); });
    await frames();
    const deep = scrollY;
    expect(deep).toBeGreaterThan(trueTop(COUNT - 2) - 5);

    await act(async () => { api().scrollToIndex(0); });
    await frames();
    expect(scrollY).toBe(0);
    expect(deep).toBeGreaterThan(0);
  });

  it("abandons a pending jump the moment the reader scrolls for themselves", async () => {
    // Long Box: wheel/touchmove/keydown cancel the landing, or it snaps the viewport back out from
    // under someone who has started reading and re-fetches everything they were looking at.
    const api = await mount();
    await act(async () => {
      api().scrollToIndex(300);
      window.dispatchEvent(new WheelEvent("wheel", { bubbles: true }));
    });
    await frames();
    // The phase-1 estimate scroll still happened (it is synchronous), but the phase-2 landing was
    // abandoned — so the position is the estimate, not the exact row-300 top.
    expect(Math.abs(scrollY - trueTop(300))).toBeGreaterThan(2);
  });
});
