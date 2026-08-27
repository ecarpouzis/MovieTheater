import { render, screen, act, fireEvent, cleanup } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import CatalogPager, { PIN_ARM_MS } from "./CatalogPager";

// ── "I tap a letter and the bar highlights the letter BEFORE it" (2026-08-13) ────────────────────
// The readout names whatever the grid's scroll-spy reports at the top of the list — and the spy's
// unit is a grid ROW of `cols` cards. A letter whose first card is not in column 0 therefore shares
// its top row with the tail of the previous letter, and the spy reports that row's FIRST item. Tap M,
// land on M, get told L. (The Long Box never hits this: its spy unit is a whole shelf, which cannot
// straddle a letter boundary.)
//
// So an explicit tap is held as the truth until the reader scrolls for themselves. These pin both
// halves of that: the tap always wins, and the honest readout always comes back.

const LETTERS = [
  { letter: "A", count: 100, offset: 0 },
  { letter: "L", count: 100, offset: 100 },
  { letter: "M", count: 100, offset: 203 },   // NOT a multiple of any sane column count
  { letter: "N", count: 100, offset: 300 },
];

/** What the grid reports when M's first card sits mid-row: the row's first item, which is still L's. */
const SPY_SAYS_L = 200;

function pager(props = {}) {
  return render(
    <CatalogPager
      mode="letters"
      letters={LETTERS}
      total={400}
      pageSize={60}
      currentIndex={SPY_SAYS_L}
      onJump={() => {}}
      {...props}
    />
  );
}

const activeName = () => {
  const el = document.querySelector(".catalog-pager__btn--active");
  return el ? el.textContent : null;
};

beforeEach(() => { vi.useFakeTimers({ shouldAdvanceTime: true }); window.localStorage.clear(); });
afterEach(() => { cleanup(); vi.useRealTimers(); });

describe("the letter rail's active readout", () => {
  it("names the tapped letter, even when the grid reports the one before it", () => {
    pager();
    // The spy's honest answer, before anyone taps anything.
    expect(activeName()).toBe("L");

    fireEvent.click(screen.getByRole("button", { name: "M" }));

    // THE bug. Nothing about currentIndex has changed — the grid still reports an L card at the top
    // of the row M landed in — but the reader asked for M and must be shown M.
    expect(activeName()).toBe("M");
  });

  it("keeps the tapped letter through the scroll it caused", () => {
    const view = pager();
    fireEvent.click(screen.getByRole("button", { name: "M" }));
    // The landing settles and the spy re-reports, still naming L's card.
    view.rerender(
      <CatalogPager mode="letters" letters={LETTERS} total={400} pageSize={60}
        currentIndex={SPY_SAYS_L} onJump={() => {}} />
    );
    expect(activeName()).toBe("M");
  });

  it("hands the readout back the moment the reader scrolls for themselves", () => {
    pager();
    fireEvent.click(screen.getByRole("button", { name: "M" }));
    expect(activeName()).toBe("M");

    // The pin is armed a beat after the tap — a thumb's drift and the smooth scroll's settle both
    // land inside that window and must not count as "the reader took over".
    act(() => { window.dispatchEvent(new WheelEvent("wheel", { bubbles: true })); });
    expect(activeName()).toBe("M");

    act(() => { vi.advanceTimersByTime(PIN_ARM_MS + 10); });
    act(() => { window.dispatchEvent(new WheelEvent("wheel", { bubbles: true })); });

    // Honest again: whatever the grid says is at the top is what the bar says.
    expect(activeName()).toBe("L");
  });

  it("hands back on a touch drag and on a navigation key too", () => {
    pager();
    fireEvent.click(screen.getByRole("button", { name: "M" }));
    act(() => { vi.advanceTimersByTime(PIN_ARM_MS + 10); });
    act(() => { window.dispatchEvent(new Event("touchmove", { bubbles: true })); });
    expect(activeName()).toBe("L");

    cleanup();
    pager();
    fireEvent.click(screen.getByRole("button", { name: "M" }));
    act(() => { vi.advanceTimersByTime(PIN_ARM_MS + 10); });
    act(() => { window.dispatchEvent(new KeyboardEvent("keydown", { key: "PageDown" })); });
    expect(activeName()).toBe("L");
  });

  it("does not hand back on a keypress that isn't a scroll", () => {
    pager();
    fireEvent.click(screen.getByRole("button", { name: "M" }));
    act(() => { vi.advanceTimersByTime(PIN_ARM_MS + 10); });
    act(() => { window.dispatchEvent(new KeyboardEvent("keydown", { key: "a" })); });
    expect(activeName()).toBe("M");
  });

  it("drops the pin when the catalog underneath it changes", () => {
    const view = pager();
    fireEvent.click(screen.getByRole("button", { name: "M" }));
    expect(activeName()).toBe("M");

    // A new filter / a new shelf: the pin describes a letter in a list that no longer exists.
    view.rerender(
      <CatalogPager mode="letters" letters={[...LETTERS]} total={400} pageSize={60}
        currentIndex={SPY_SAYS_L} onJump={() => {}} />
    );
    expect(activeName()).toBe("L");
  });

  it("still tells the jump where to go", () => {
    const onJump = vi.fn();
    pager({ onJump });
    fireEvent.click(screen.getByRole("button", { name: "M" }));
    expect(onJump).toHaveBeenCalledWith(203);
  });
});
