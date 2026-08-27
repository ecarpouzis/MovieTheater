import { act, render } from "@testing-library/react";
import PerfHud, { PERF_HUD_KEY, formatFacts, perfHudEnabled, readDomFacts, resetPerfHudFlag } from "./PerfHud";

/**
 * The HUD's whole contract is "nothing at all when off, the engine's facts when on". The first
 * half is the one that matters: it ships in the production bundle on every catalog page.
 */
describe("catalog/PerfHud", () => {
  const realRaf = window.requestAnimationFrame;
  const realFetch = window.fetch;

  beforeEach(() => {
    resetPerfHudFlag();
    window.localStorage.removeItem(PERF_HUD_KEY);
  });
  afterEach(() => {
    window.requestAnimationFrame = realRaf;
    window.fetch = realFetch;
    window.localStorage.removeItem(PERF_HUD_KEY);
    resetPerfHudFlag();
    vi.restoreAllMocks();
  });

  it("renders nothing, listens to nothing and starts no rAF while the flag is unset", () => {
    const raf = vi.fn(() => 1);
    window.requestAnimationFrame = raf as unknown as typeof window.requestAnimationFrame;
    const addListener = vi.spyOn(window, "addEventListener");
    const fetchBefore = window.fetch;

    const { container } = render(<PerfHud />);

    expect(perfHudEnabled()).toBe(false);
    expect(container.innerHTML).toBe("");
    expect(raf).not.toHaveBeenCalled();
    expect(addListener).not.toHaveBeenCalled();
    expect(window.fetch).toBe(fetchBefore); // fetch is not patched when the HUD is off
  });

  it("draws the engine's facts when the flag is set, and restores window.fetch on unmount", () => {
    window.localStorage.setItem(PERF_HUD_KEY, "1");
    resetPerfHudFlag();
    let cb: FrameRequestCallback | null = null;
    window.requestAnimationFrame = ((fn: FrameRequestCallback) => { cb = fn; return 1; }) as typeof window.requestAnimationFrame;
    const fetchBefore = window.fetch;

    const { container, unmount } = render(<PerfHud />);
    const hud = container.querySelector<HTMLElement>("[data-testid=catalog-perfhud]");
    expect(hud).not.toBeNull();
    expect(window.fetch).not.toBe(fetchBefore); // patched to count in-flight requests

    // one sampler pass, far enough past the sample window to publish
    expect(cb).not.toBeNull();
    const t0 = performance.now();
    act(() => { cb!(t0 + 5000); });
    expect(hud!.textContent).toMatch(/fps /);
    expect(hud!.textContent).toMatch(/bands 0\+0ph/);
    expect(hud!.textContent).toMatch(/covers 0 loading/);

    unmount();
    expect(window.fetch).toBe(fetchBefore);
  });

  it("counts what the engine writes into the DOM — bands, placeholders, cards, dormant covers", () => {
    const root = document.createElement("div");
    root.innerHTML = `
      <div class="bx-results">
        <div data-iband="0"><div class="bx-card"><img src="/a.webp"></div><div class="bx-card"><img src="/b.webp" data-fallback="1"></div></div>
        <div class="bx-band-placeholder"></div>
      </div>`;
    const facts = readDomFacts(root);
    expect(facts).toMatchObject({ bands: 1, placeholders: 1, cards: 2, imgsDead: 1 });
  });

  it("says heap n/a where the engine offers no reading (every browser but Chromium)", () => {
    const line = formatFacts({
      fps: 60, scrollFrameMs: 12, bands: 3, placeholders: 1, cards: 48, fetches: 2,
      longTasks: 0, longTaskMaxMs: 0, heapMb: null, imgsPending: 4, imgsDead: 0,
    });
    expect(line).toContain("heap n/a");
    expect(line).toContain("bands 3+1ph");
    expect(line).toContain("fetch 2");
  });
});
