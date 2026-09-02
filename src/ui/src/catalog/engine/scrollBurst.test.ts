import { SCROLL_BURST_CLASS, SCROLL_SETTLE_MS, scrollBurstGate } from "./scroller";

describe("catalog/engine/scroller — the scroll-burst hover gate every scrolled surface shares", () => {
  beforeEach(() => vi.useFakeTimers());
  afterEach(() => vi.useRealTimers());

  it("wears the class from the first scroll event until SETTLE_MS after the last, one toggle per burst", () => {
    const el = document.createElement("div");
    const add = vi.spyOn(el.classList, "add");
    const gate = scrollBurstGate(() => el);
    gate.onScroll();
    gate.onScroll();
    gate.onScroll();
    expect(el.classList.contains(SCROLL_BURST_CLASS)).toBe(true);
    expect(add).toHaveBeenCalledTimes(1);
    vi.advanceTimersByTime(SCROLL_SETTLE_MS - 1);
    expect(el.classList.contains(SCROLL_BURST_CLASS)).toBe(true);
    vi.advanceTimersByTime(2);
    expect(el.classList.contains(SCROLL_BURST_CLASS)).toBe(false);
    gate.onScroll();
    expect(add).toHaveBeenCalledTimes(2);
    gate.dispose();
    expect(el.classList.contains(SCROLL_BURST_CLASS)).toBe(false);
  });
});
