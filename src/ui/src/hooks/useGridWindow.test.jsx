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

/** True top of a row in the virtual document — what the hook has to converge on, never told to it. */
function trueTop(row) {
  return row < SHORT_ROWS
    ? row * SHORT_H
    : SHORT_ROWS * SHORT_H + (row - SHORT_ROWS) * TALL_H;
}

let scrollY;
let realRect;
let realComputed;

function Grid({ count, onApi }) {
  const api = useGridWindow(count, { resetKey: "fixed" });
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
  realRect = Element.prototype.getBoundingClientRect;
  realComputed = window.getComputedStyle;

  // One row per card, at its true height, positioned by the virtual scroll.
  Element.prototype.getBoundingClientRect = function rect() {
    const row = Number(this.dataset?.row);
    if (Number.isFinite(row)) {
      const top = trueTop(row) - scrollY;
      const height = row < SHORT_ROWS ? SHORT_H : TALL_H;
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
      return Number.isFinite(row) ? (row < SHORT_ROWS ? SHORT_H : TALL_H) : 0;
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
  render(<Grid count={count} onApi={(a) => { api = a; }} />);
  await frames(6);
  return () => api;
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
});
