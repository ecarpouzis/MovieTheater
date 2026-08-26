import { act, render } from "@testing-library/react";
import InfiniteBands, { MIN_WANT_AGE } from "./InfiniteBands";

/**
 * The pump's laws, pinned: the window wants its bands + one ahead, a band fetches only after it
 * has stayed wanted for MIN_WANT_AGE, and a scroll that sweeps the window elsewhere REPLACES the
 * want-list and aborts what was in flight for the bands it left.
 *
 * happy-dom has no layout, so every rect is 0 and every band is its estimate: with innerHeight 768
 * and an 800 px estimate the window at the top covers bands 0–2 and wants 1–3 (band 0 is given).
 */
function setScrollY(y: number) {
  Object.defineProperty(window, "scrollY", { value: y, configurable: true, writable: true });
}

describe("catalog/InfiniteBands — the sparse-band pump", () => {
  // The scroll listener coalesces into requestAnimationFrame, which Vitest does not fake by default.
  beforeEach(() => {
    vi.useFakeTimers({ toFake: ["setTimeout", "clearTimeout", "setInterval", "clearInterval", "Date", "requestAnimationFrame", "cancelAnimationFrame"] });
    setScrollY(0);
    // happy-dom has no layout: every rect is at 0 whatever the scroll. A browser reports a rect
    // SHIFTED by the scroll (an element at document y=0 has top = -scrollY once you scroll), and the
    // engine's origin math depends on exactly that. Give every element the browser's answer.
    vi.spyOn(Element.prototype, "getBoundingClientRect").mockImplementation(function rect() {
      const top = -window.scrollY;
      return { top, bottom: top, left: 0, right: 0, width: 0, height: 0, x: 0, y: top, toJSON: () => ({}) } as DOMRect;
    });
  });
  afterEach(() => { vi.restoreAllMocks(); vi.useRealTimers(); });

  function mount() {
    const calls: { band: number; signal: AbortSignal }[] = [];
    // Like fetch: never resolves on its own, rejects promptly when aborted.
    const fetchBand = vi.fn((band: number, signal: AbortSignal) => {
      calls.push({ band, signal });
      return new Promise<number[]>((_, reject) => {
        signal.addEventListener("abort", () => reject(new DOMException("Aborted", "AbortError")));
      });
    });
    const band0 = Array.from({ length: 50 }, (_, i) => i);
    const ui = render(
      <InfiniteBands<number>
        total={5000}
        perBand={50}
        band0={band0}
        queryKey="q1"
        fetchBand={fetchBand}
        estBandHeight={800}
        renderBand={(units) => <div className="band">{units.length}</div>}
      />,
    );
    return { ui, calls, fetchBand };
  }

  it("wants the window's bands plus one ahead, and fires nothing before the age gate", () => {
    const { calls } = mount();
    act(() => { vi.advanceTimersByTime(MIN_WANT_AGE - 20); });
    expect(calls).toHaveLength(0);
    act(() => { vi.advanceTimersByTime(60); });
    expect(calls.map((c) => c.band)).toEqual([1, 2, 3]);
  });

  it("a sweep replaces the want-list and aborts the bands the window left", () => {
    const { calls } = mount();
    act(() => { vi.advanceTimersByTime(MIN_WANT_AGE + 20); });
    expect(calls.map((c) => c.band)).toEqual([1, 2, 3]);
    // Drag far down: ~band 50 at 40,000 px. The scroll listener is on the window (no scroll parent
    // in happy-dom), rAF-coalesced into the maintain pass.
    setScrollY(40_000);
    act(() => { window.dispatchEvent(new Event("scroll")); vi.advanceTimersByTime(20); });
    expect(calls.slice(0, 3).every((c) => c.signal.aborted)).toBe(true);
    // The landing bands are wanted but NOT fetched yet — a drag step outruns the age gate.
    expect(calls).toHaveLength(3);
    act(() => { vi.advanceTimersByTime(MIN_WANT_AGE + 20); });
    const landed = calls.slice(3).map((c) => c.band);
    expect(landed.length).toBeGreaterThan(0);
    expect(Math.min(...landed)).toBeGreaterThanOrEqual(47);
    expect(Math.max(...landed)).toBeLessThanOrEqual(54);
    expect(calls.slice(3).every((c) => !c.signal.aborted)).toBe(true);
  });

  it("a new query drops every band and aborts what was in flight", () => {
    const { ui, calls, fetchBand } = mount();
    act(() => { vi.advanceTimersByTime(MIN_WANT_AGE + 20); });
    expect(calls).toHaveLength(3);
    ui.rerender(
      <InfiniteBands<number>
        total={5000}
        perBand={50}
        band0={[1, 2, 3]}
        queryKey="q2"
        fetchBand={fetchBand}
        estBandHeight={800}
        renderBand={(units) => <div className="band">{units.length}</div>}
      />,
    );
    expect(calls.slice(0, 3).every((c) => c.signal.aborted)).toBe(true);
    act(() => { vi.advanceTimersByTime(MIN_WANT_AGE + 20); });
    expect(calls.slice(3).map((c) => c.band)).toEqual([1, 2, 3]);
    expect(ui.container.querySelector(".band")?.textContent).toBe("3");
  });
});
