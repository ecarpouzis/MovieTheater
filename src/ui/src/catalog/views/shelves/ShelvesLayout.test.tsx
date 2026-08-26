import { cleanup, render } from "@testing-library/react";
import type { CardGroup } from "../../types";
import ShelvesLayout from "./ShelvesLayout";

// a17 — the shelves' load pass re-runs itself while a band's data has not mounted (clientWidth 0,
// which is every band under jsdom). The regression: that retry timer used to be untracked, so a
// layout unmounted mid-retry (a query change) kept scheduling load passes over disconnected nodes.

global.IS_REACT_ACT_ENVIRONMENT = true;
(global as unknown as { matchMedia: unknown }).matchMedia = (global as unknown as { matchMedia?: unknown }).matchMedia || ((q: string) => ({
  matches: false, media: q, onchange: null, addListener: vi.fn(), removeListener: vi.fn(), addEventListener: vi.fn(), removeEventListener: vi.fn(), dispatchEvent: vi.fn(),
}));
(global as unknown as { ResizeObserver: unknown }).ResizeObserver = (global as unknown as { ResizeObserver?: unknown }).ResizeObserver || class { observe() {} unobserve() {} disconnect() {} };
(global as unknown as { IntersectionObserver: unknown }).IntersectionObserver = (global as unknown as { IntersectionObserver?: unknown }).IntersectionObserver || class { observe() {} unobserve() {} disconnect() {} takeRecords() { return []; } };

const card = (id: number) => ({ kind: "comic" as const, id, key: `comic:${id}`, title: `Issue ${id}`, aspect: 0.66, imageUrl: `https://m/${id}.webp`, imageThumbUrl: `https://m/${id}.webp`, hue: 20, raw: {} });
const group: CardGroup = { key: "9", label: "Hellboy", totalItems: 3, renderTotal: 3, items: [card(1), card(2), card(3)] };

afterEach(() => { cleanup(); vi.useRealTimers(); });

describe("catalog/shelves/ShelvesLayout — the a17 trailing-timer guard", () => {
  it("stops re-running the load pass once unmounted (no rAF is scheduled after cleanup)", () => {
    vi.useFakeTimers();
    const { unmount } = render(
      <ShelvesLayout slots={[{ band: 0, groups: [group] }]} extras={{}} scale={1} noun="issue" onOpen={() => {}} onOpenGroup={null}
        onLoadMore={() => {}} onNeedBand={() => {}} onBandFar={() => {}} onBandHeight={() => {}} onActiveChange={() => {}} pendingJump={null} onJumpHandled={() => {}} />,
    );
    // Let the first load pass run and arm its retry (every .shelf-books has clientWidth 0 here).
    vi.advanceTimersByTime(200);
    unmount();
    const raf = vi.spyOn(window, "requestAnimationFrame");
    vi.advanceTimersByTime(5000);
    expect(raf).not.toHaveBeenCalled();
    raf.mockRestore();
  });
});
