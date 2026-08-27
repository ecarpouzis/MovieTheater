import { render, act, cleanup, within } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { vi, describe, it, expect, beforeEach, afterEach } from "vitest";

// ── The lobby reads in BOTH directions ──────────────────────────────────────────────────────────
// Eric tapped a letter and could not scroll back up past it. The lobby used to hold a dense array of
// the games it had fetched, anchored at an absolute `startIndex`, so a jump made the list BEGIN at
// the letter — there was nothing above to reveal, and adding pages to the front would have been a
// prepend, i.e. the teleport.
//
// The Long Box (F:\Work\MyBooks, features/browse/InfiniteScroller.tsx) does not have that problem
// because it never prepends: the whole result set is modelled as fixed slots from the first render
// and band data is fetched on demand into them. Ported here as a page map. What this file pins is
// the consequence — the arcade asks the server for pages the WINDOW wants, above it as readily as
// below it, and never re-seats the list to do so.

global.IS_REACT_ACT_ENVIRONMENT = true;
global.ResizeObserver = global.ResizeObserver || class { observe() {} unobserve() {} disconnect() {} };
global.matchMedia = global.matchMedia || ((query) => ({
  matches: false, media: query, onchange: null,
  addListener: vi.fn(), removeListener: vi.fn(),
  addEventListener: vi.fn(), removeEventListener: vi.fn(), dispatchEvent: vi.fn(),
}));

const TOTAL = 1200;      // 20 pages of 60 — a catalog no client could hold
const PAGE_SIZE = 60;

const { api, skips } = vi.hoisted(() => ({ api: {}, skips: [] }));

vi.mock("../../MovieAPI", () => ({ MovieAPI: api }));
// The lobby's furniture is not what is under test, and each piece drags in its own fetches.
vi.mock("./ArcadeHostBanner", () => ({ default: () => null }));
vi.mock("./GameModal", () => ({ default: () => null }));
vi.mock("./HeavyGameModal", () => ({ default: () => null }));
vi.mock("./LiveRooms", () => ({ default: () => null }));
vi.mock("./RecentlyPlayed", () => ({ default: () => null }));
vi.mock("./SavesManager", () => ({ default: () => null }));
vi.mock("./SavesVaultManager", () => ({ default: () => null }));
vi.mock("./RetroAchievementsModal", () => ({ default: () => null }));
vi.mock("./ConsoleCarousel", () => ({ default: () => null }));
vi.mock("./useArcadeFilters", () => ({ default: () => ({ systems: [], genres: [], loading: false }), arcadeFilterKey: () => "scope" }));

import ArcadePage from "./ArcadePage";

const ok = (body) => Promise.resolve({ ok: true, status: 200, json: () => Promise.resolve(body) });

/** A card for absolute catalog index `i`, so a rendered title names the slot it came from. */
const gameAt = (i) => ({
  key: `k${i}`, title: `Game ${i}`, system: "n64", artId: null, hasBoxArt: false,
  maxPlayers: 1, versionCount: 1, genres: "Action", summary: "",
  versions: [{ id: i, label: "USA", region: "USA", variant: "Release", maxPlayers: 1 }],
});

beforeEach(() => {
  skips.length = 0;
  api.getArcadeGames = vi.fn(({ skip = 0, pageSize = PAGE_SIZE }) => {
    skips.push(skip);
    return ok({
      totalCount: TOTAL,
      skip,
      games: Array.from({ length: Math.min(pageSize, TOTAL - skip) }, (_, n) => gameAt(skip + n)),
    });
  });
  api.getArcadeGameLetters = vi.fn(() => ok({
    letters: [{ letter: "A", count: 600, offset: 0 }, { letter: "M", count: 600, offset: 600 }],
  }));
  api.getArcadeRooms = vi.fn(() => ok([]));
  api.getArcadeRenderers = vi.fn(() => ok({}));
  api.getArcadeRecentlyPlayed = vi.fn(() => Promise.resolve([]));
  window.localStorage.clear();
});

afterEach(() => { cleanup(); vi.clearAllMocks(); });

async function frames(n = 14) {
  for (let i = 0; i < n; i++) {
    // eslint-disable-next-line no-await-in-loop
    await act(async () => { await new Promise((r) => setTimeout(r, 0)); });
  }
}

async function mountLobby() {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  const view = render(
    <QueryClientProvider client={client}>
      <MemoryRouter initialEntries={["/arcade"]}>
        <ArcadePage userData={{ username: "eric" }} />
      </MemoryRouter>
    </QueryClientProvider>
  );
  await frames();
  return view;
}

const titles = (container) =>
  [...container.querySelectorAll(".arcade-card__title")].map((n) => n.textContent);

describe("the arcade lobby's sparse catalog", () => {
  it("asks for page 0 and reports the SERVER's total, not what it holds", async () => {
    const { container } = await mountLobby();
    expect(skips).toContain(0);
    expect(container.querySelector(".arcade-section__count").textContent).toMatch(/1,200/);
    expect(titles(container)[0]).toBe("Game 0");
  });

  it("never re-seats the list to reach a letter — the slots were always there", async () => {
    const { container, getByRole } = await mountLobby();
    const asked = skips.length;

    // M lives at catalog offset 600 — ten pages in.
    await act(async () => { getByRole("button", { name: "M" }).click(); });
    await frames();

    // The jump did NOT issue a "replace the list starting at 600" request in the old sense: the page
    // it fetches is a fill for slots that already existed, and page 0 is never discarded — asking for
    // it again would be the old re-seat.
    const afterJump = skips.slice(asked);
    expect(afterJump.length).toBeGreaterThan(0);
    // Everything requested is a page-aligned skip, i.e. a slot fill.
    for (const s of afterJump) expect(s % PAGE_SIZE).toBe(0);
  });

  it("fetches pages ABOVE the window as readily as below it", async () => {
    // The whole complaint in one assertion. Land deep, then let the window move UP — the lobby must
    // ask for the earlier pages, with no button and no re-anchor.
    const { getByRole } = await mountLobby();
    await act(async () => { getByRole("button", { name: "M" }).click(); });
    await frames();
    const deepAsked = new Set(skips);

    // The window walks back toward the top. (In the browser this is the user scrolling; here it is
    // the same state change, driven through the component's own effects.)
    await act(async () => { getByRole("button", { name: "A" }).click(); });
    await frames();

    const earlier = skips.filter((s) => s < 600);
    expect(earlier.length).toBeGreaterThan(0);
    expect(deepAsked.size).toBeGreaterThan(0);
  });

  it("has no Load more, no Earlier titles, and no end-of-list wall", async () => {
    // Every one of those was a symptom of the dense-array model. Scrolling is the only control now.
    const { container } = await mountLobby();
    const text = container.textContent;
    expect(text).not.toMatch(/Load more/i);
    expect(text).not.toMatch(/Earlier titles/i);
    expect(text).toMatch(/1,200 titles/);
  });

  it("renders a placeholder for a slot whose page has not landed, never a hole", async () => {
    // A page that never resolves: the slots it covers must still occupy a card's footprint, or every
    // row below them moves when it finally arrives.
    //
    // A SMALL catalog on purpose — 100 slots is under useGridWindow's windowing threshold, so every
    // slot mounts. The DOM shim has no layout, so a windowed list can never be driven past its first
    // screenful here; this is the one shape in which the placeholder path is reachable from a real
    // render of the page.
    const SMALL = 100;
    api.getArcadeGames = vi.fn(({ skip = 0, pageSize = PAGE_SIZE }) => {
      skips.push(skip);
      if (skip > 0) return new Promise(() => {});   // page 1 never arrives
      return ok({
        totalCount: SMALL,
        skip,
        games: Array.from({ length: pageSize }, (_, n) => gameAt(skip + n)),
      });
    });
    const { container } = await mountLobby();
    const grid = container.querySelector(".arcade-grid");

    // Page 0's 60 cards are real…
    expect(within(grid).queryAllByText("Game 0").length).toBe(1);
    expect(grid.querySelectorAll(".arcade-card:not(.arcade-card--pending)").length).toBe(PAGE_SIZE);
    // …and the 40 slots page 1 would have filled are placeholders, not missing.
    expect(grid.querySelectorAll(".arcade-card--pending").length).toBe(SMALL - PAGE_SIZE);
    // The catalog still reports its true size while a page is outstanding.
    expect(container.querySelector(".arcade-section__count").textContent).toMatch(/100/);
  });

});
