import { render, cleanup, waitFor, fireEvent, act, screen } from "@testing-library/react";
import { vi, describe, it, expect, afterEach, beforeEach } from "vitest";
import { Router } from "react-router-dom";
import { createMemoryHistory } from "history";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";

// Page-level tests for the lobby's filter round trip. The unit tests beside these cover the pieces
// (ConsoleCarousel, arcadeSystemFilter, useArcadeFilters); what these pin down is the whole loop —
// URL → request → grid — because that is where clearing the last console went wrong in the field.

global.IS_REACT_ACT_ENVIRONMENT = true;
global.matchMedia = global.matchMedia || ((q) => ({
  matches: false, media: q, onchange: null,
  addListener: vi.fn(), removeListener: vi.fn(),
  addEventListener: vi.fn(), removeEventListener: vi.fn(), dispatchEvent: vi.fn(),
}));
global.ResizeObserver = global.ResizeObserver || class { observe() {} unobserve() {} disconnect() {} };

const gamesCalls = [];
let gamesResponder = null; // set per test to control WHEN a page resolves

const card = (i, system) => ({ key: `${system}|${i}`, title: `Game ${i}`, system, versions: [] });
const CATALOG = {
  "": Array.from({ length: 40 }, (_, i) => card(i, "snes")),
  nes: [card(99, "nes")],
};

const ok = (body) => Promise.resolve({ ok: true, status: 200, json: () => Promise.resolve(body) });

vi.mock("../../MovieAPI", () => ({
  MovieAPI: {
    getArcadeGames: (params) => {
      gamesCalls.push(params);
      if (gamesResponder) return gamesResponder(params);
      const list = CATALOG[params.system || ""] || [];
      return ok({ games: list, totalCount: list.length, skip: params.skip || 0 });
    },
    getArcadeGameLetters: () => ok({ letters: [] }),
    getArcadeFilters: () => ok({
      total: 41, multiplayer: 0,
      systems: [{ value: "nes", count: 1 }, { value: "snes", count: 40 }],
      regions: [], variants: [], genres: [], ra: { achievements: 0, highScores: 0, speedruns: 0 },
    }),
    getArcadeRooms: () => ok([]),
    // The host-health banner polls this; it already resolves to null on any failure, and null means
    // "the host has told us nothing", which renders nothing. That is the state a lobby test wants.
    getArcadeHostStatus: () => Promise.resolve(null),
    getArcadeRenderers: () => Promise.resolve({}),
    getArcadeRecentlyPlayed: () => Promise.resolve([]),
  },
}));

const ArcadePage = (await import("./ArcadePage")).default;

const renderLobby = (search = "") => {
  const history = createMemoryHistory({ initialEntries: [`/arcade${search}`] });
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  const view = render(<QueryClientProvider client={client}><Router history={history}><ArcadePage userData={{ username: "Eric" }} /></Router></QueryClientProvider>);
  return { history, ...view };
};

const cards = (c) => c.querySelectorAll(".arcade-card").length;

beforeEach(() => { gamesCalls.length = 0; gamesResponder = null; });
afterEach(cleanup);

describe("the lobby's system filter, end to end", () => {
  // The reported bug: lighting a console up and switching it off again left an empty grid. Switching
  // the LAST console off has to drop the system facet entirely and ask for the whole catalog again.
  // (R9 S2c: the carousel writes the rail's `f=system:` form; the old `?system=` still reads.)
  it("puts the whole catalog back when the last console is switched off", async () => {
    const { container, history } = renderLobby();
    await waitFor(() => expect(cards(container)).toBe(40));

    // By NAME, not position: the shelf is ordered by console release date, so an index here would
    // silently start testing a different console the day that order changes.
    const nesTile = () => screen.getByRole("button", { name: /^NES/ });
    await waitFor(() => expect(container.querySelectorAll(".arcade-console").length).toBe(2));
    await act(async () => { fireEvent.click(nesTile()); });
    await waitFor(() => expect(history.location.search).toBe("?f=system%3Anes"));
    await waitFor(() => expect(cards(container)).toBe(1));

    await act(async () => { fireEvent.click(nesTile()); });
    await waitFor(() => expect(history.location.search).toBe(""));
    await waitFor(() => expect(cards(container)).toBe(40));

    // …and the request that refilled it carried no system at all, not an empty one.
    expect(gamesCalls.at(-1).system).toBe("");
  });

  // Clearing the last console is the WIDEST query the lobby can ask for, so it is also the one most
  // likely to time out. A page that never arrived must not be reported as a filter result — that is
  // how "nothing is filtered, yet it says everything was filtered away" reached us from a phone.
  it("says a failed page request failed, instead of blaming the filters — and can retry", async () => {
    gamesResponder = () => Promise.resolve({ ok: false, status: 504, json: () => Promise.resolve(null) });
    const { container } = renderLobby("?system=nes");

    // (R9 S3: the failure surface is the package's now — one LoadFailure for every section's stream.)
    await waitFor(() => expect(screen.getByText(/Couldn't load this list/)).toBeTruthy());
    expect(screen.queryByText(/No games match/)).toBeNull();
    expect(cards(container)).toBe(0);

    // Retry re-asks for the same page, and a good answer clears the error.
    gamesResponder = null;
    await act(async () => { fireEvent.click(screen.getByText("Try again")); });
    await waitFor(() => expect(cards(container)).toBe(1));
    expect(screen.queryByText(/Couldn't load this list/)).toBeNull();
  });

  // "all" is the Mods & Hacks DEFAULT and the rail used to write it into the URL, so an untouched
  // lobby could describe itself as filtered. What matters is that it reaches the EMPTY state at all
  // rather than the failure one, and that the request it made carried no variant. Since S4 the empty
  // state SAYS which of the two it is (CatalogSource.emptyLabel + filtered), so this pins the
  // unfiltered wording — "no games match" was a lie when nothing was matching against anything.
  it("treats ?variant=all as no filter at all", async () => {
    gamesResponder = () => ok({ games: [], totalCount: 0, skip: 0 });
    renderLobby("?variant=all");
    await waitFor(() => expect(screen.getByText(/No games here yet/)).toBeTruthy());
    expect(screen.queryByText(/No games match/)).toBeNull();
    expect(screen.queryByText(/Couldn't load this list/)).toBeNull();
    expect(gamesCalls.at(-1).variant).toBeFalsy();
  });

  it("says the filters are what emptied the lobby when something narrows it", async () => {
    gamesResponder = () => ok({ games: [], totalCount: 0, skip: 0 });
    renderLobby("?f=system:nes");
    await waitFor(() => expect(screen.getByText(/No games match those filters/)).toBeTruthy());
  });
});
