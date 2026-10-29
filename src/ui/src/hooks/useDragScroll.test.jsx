import React from "react";
import { render } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import useDragScroll, { DRAG_SCROLL_KEY, readDragScrollEnabled } from "./useDragScroll";

// A scroll root whose geometry happy-dom can't compute: overflow inline (setupTests' getComputedStyle
// reads inline styles only) and a faked scrollHeight/clientHeight so getScrollParent picks it.
function Harness({ enabled, onCard }) {
  const ref = (el) => { Harness.surface = el; };
  return (
    <div data-testid="scroller" style={{ overflowY: "auto" }} ref={(el) => { Harness.scroller = el; }}>
      <Surface enabled={enabled} onCard={onCard} surfaceRef={ref} />
    </div>
  );
}

function Surface({ enabled, onCard, surfaceRef }) {
  const [el, setEl] = React.useState(null);
  useDragScroll(el, enabled);
  return (
    <div ref={(n) => { setEl(n); surfaceRef(n); }}>
      <button type="button" data-testid="card" onClick={onCard}>card</button>
      <input data-testid="field" />
    </div>
  );
}

function pointer(type, target, x, y, extra = {}) {
  const e = new MouseEvent(type, { bubbles: true, cancelable: true, clientX: x, clientY: y, button: 0 });
  Object.defineProperty(e, "pointerType", { value: extra.pointerType ?? "mouse" });
  target.dispatchEvent(e);
}

function mountScroller(enabled, onCard = () => {}) {
  const utils = render(<Harness enabled={enabled} onCard={onCard} />);
  const scroller = Harness.scroller;
  Object.defineProperty(scroller, "scrollHeight", { configurable: true, value: 5000 });
  Object.defineProperty(scroller, "clientHeight", { configurable: true, value: 500 });
  scroller.scrollTop = 1000;
  return { ...utils, scroller };
}

describe("useDragScroll", () => {
  beforeEach(() => {
    vi.spyOn(window, "requestAnimationFrame").mockImplementation(() => 0);
  });
  afterEach(() => {
    vi.restoreAllMocks();
    window.localStorage.removeItem(DRAG_SCROLL_KEY);
  });

  it("a vertical mouse drag scrolls the scroll root and swallows the click that ends it", () => {
    const onCard = vi.fn();
    const { scroller, getByTestId } = mountScroller(true, onCard);
    const card = getByTestId("card");
    pointer("pointerdown", card, 100, 300);
    pointer("pointermove", window, 100, 280);
    pointer("pointermove", window, 100, 200);
    expect(scroller.scrollTop).toBe(1100); // dragged up 100 px → content moves up
    pointer("pointerup", window, 100, 200);
    card.click();
    expect(onCard).not.toHaveBeenCalled();
  });

  it("a press inside the dead zone is still a click", () => {
    const onCard = vi.fn();
    const { scroller, getByTestId } = mountScroller(true, onCard);
    const card = getByTestId("card");
    pointer("pointerdown", card, 100, 300);
    pointer("pointermove", window, 103, 304);
    pointer("pointerup", window, 103, 304);
    card.click();
    expect(onCard).toHaveBeenCalledTimes(1);
    expect(scroller.scrollTop).toBe(1000);
  });

  it("leaves real touch, form fields and a disabled hook alone", () => {
    const { scroller, getByTestId } = mountScroller(true);
    pointer("pointerdown", getByTestId("card"), 100, 300, { pointerType: "touch" });
    pointer("pointermove", window, 100, 100);
    pointer("pointerup", window, 100, 100);
    pointer("pointerdown", getByTestId("field"), 100, 300);
    pointer("pointermove", window, 100, 100);
    pointer("pointerup", window, 100, 100);
    expect(scroller.scrollTop).toBe(1000);
  });

  it("does nothing when disabled, and marks <html> only while enabled", () => {
    const { scroller, getByTestId, rerender } = mountScroller(false);
    expect(document.documentElement.classList.contains("drag-scroll-on")).toBe(false);
    pointer("pointerdown", getByTestId("card"), 100, 300);
    pointer("pointermove", window, 100, 100);
    pointer("pointerup", window, 100, 100);
    expect(scroller.scrollTop).toBe(1000);
    rerender(<Harness enabled onCard={() => {}} />);
    expect(document.documentElement.classList.contains("drag-scroll-on")).toBe(true);
    rerender(<Harness enabled={false} onCard={() => {}} />);
    expect(document.documentElement.classList.contains("drag-scroll-on")).toBe(false);
  });

  it("the stored choice beats the touchscreen auto-detect", () => {
    window.localStorage.setItem(DRAG_SCROLL_KEY, "off");
    expect(readDragScrollEnabled()).toBe(false);
    window.localStorage.setItem(DRAG_SCROLL_KEY, "on");
    expect(readDragScrollEnabled()).toBe(true);
  });
});
