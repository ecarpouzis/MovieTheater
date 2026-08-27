/**
 * The phone drawer IS the section's sider (2026-08-27).
 *
 * Eric's report, with a photograph: the Arcade hamburger opened on a name, a cog, one "Filters"
 * button and 1,200 px of nothing. The drawer now renders the SAME components the desktop sider
 * renders for the section — the user block, the index rows where the section has them, and its
 * `FacetRail` — so the tests below read the rail's own section titles (`.bx-rsec-title`) out of the
 * open drawer.
 *
 * 2026-08-28: the drawer is also the section's ONE filter surface. The bar's phone Filters pill and
 * the full-page rail sheet it raised are gone (Eric: "this filter button seems to present the same
 * options opening the drawer does"), and the top bar's magnifier — which used to raise that sheet —
 * opens THIS drawer and drops the caret in the rail's SmartSearch. The last describe below is what
 * used to be the sheet's test.
 *
 * Playwright covers Movies / Board games / Arcade / Channels against the live prod-proxied site.
 * Music, Photos and Books are gated (a password session, a family-album grant, BooksAccess), so
 * they are pinned HERE — this file is their half of the verification, not a duplicate of the smoke.
 */
import { cleanup, render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { MemoryRouter } from "react-router-dom";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { vi, describe, it, expect, beforeEach, afterEach } from "vitest";

global.IS_REACT_ACT_ENVIRONMENT = true;
global.matchMedia = global.matchMedia || ((query) => ({
  matches: false, media: query,
  addListener() {}, removeListener() {}, addEventListener() {}, removeEventListener() {},
}));

const photos = vi.hoisted(() => ({
  status: {
    assets: 4210, photos: 4000, videos: 210, albums: 7, people: 12, undated: 0, empty: false, dataPlane: true,
  },
}));

vi.mock("../MovieAPI", () => ({
  MovieAPI: new Proxy({}, {
    get: (_t, name) => {
      if (name === "getPhotosStatus") {
        return () => Promise.resolve({ ok: true, status: 200, json: () => Promise.resolve(photos.status) });
      }
      if (name === "getPhotoPeople") {
        return () => Promise.resolve({ ok: true, status: 200, json: () => Promise.resolve({ people: [], unnamed: [] }) });
      }
      return vi.fn(() => Promise.resolve({ ok: true, json: () => Promise.resolve([]) }));
    },
  }),
}));

// THE point of this file: the phone.
vi.mock("../hooks/useIsMobile", () => ({ default: () => true }));

import NavBar from "./NavBar";
import { resetPhotosAlbum } from "../hooks/usePhotosAlbum";

const noopSearchProps = Object.fromEntries(
  ["search", "facetSearch", "restoreMovieIdsSearch", "moviesSeenSearch", "moviesWantToWatchSearch"]
    .map((k) => [k, vi.fn()])
);

const baseUser = { username: "Eric", hasPassword: true, moviesSeen: [], moviesToWatch: [], ratings: {} };

function renderNav(route, ud = baseUser) {
  render(
    <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
      <MemoryRouter initialEntries={[route]}>
        <NavBar
          {...noopSearchProps}
          userData={ud}
          setUserData={vi.fn()}
          onUserLoggedIn={vi.fn()}
          isAuthReady
          collapsed={false}
          onCollapse={vi.fn()}
          theme="dark"
          toggleTheme={vi.fn()}
        />
      </MemoryRouter>
    </QueryClientProvider>
  );
}

/** Open the hamburger and read the drawer the way the headless smoke's `readDrawer` does. */
async function openDrawer() {
  await userEvent.click(document.querySelector(".navbar-menu-btn"));
  await waitFor(() => expect(document.querySelector(".navbar-dropdown--open")).toBeTruthy());
  return document.querySelector(".navbar-dropdown--open");
}

const railTitles = (drawer) =>
  Array.from(drawer.querySelectorAll(".bx-rsec-title")).map((n) => n.textContent);

let origFetch;
beforeEach(() => {
  vi.clearAllMocks();
  resetPhotosAlbum();
  origFetch = global.fetch;
  // Every rail's option/count fetch: an empty-but-OK envelope. The rail draws a section per SPEC
  // facet whether or not options came back, which is exactly what these tests assert on.
  global.fetch = vi.fn(async (url) => {
    const u = String(url);
    const body = u.includes("/browse/facets")
      ? { collections: [], series: [], tags: [], authors: [], artists: [], events: [], franchises: [], publishers: [], decades: [] }
      : u.includes("Facets") ? {} : [];
    return {
      ok: true, status: 200,
      headers: { get: (k) => (k === "X-Total-Count" ? "0" : null) },
      json: async () => body,
      text: async () => "",
    };
  });
});
afterEach(() => { global.fetch = origFetch; });

describe("the phone drawer is the section's sider", () => {
  it("Music: the shelf's facet rail, and NOT a second copy of the bar's tabs", async () => {
    renderNav("/music");
    const drawer = await openDrawer();
    await waitFor(() => expect(railTitles(drawer).length).toBeGreaterThan(0));

    // Shelf · Artist · Genre · Tag (+ the shared Date range / Rating / …) — the rail's own sections.
    expect(railTitles(drawer)).toEqual(expect.arrayContaining(["Shelf", "Artist", "Genre", "Tag"]));

    // Task 2 (Eric, 2026-08-27): the sider's index rows were Browse · Playlists · Now playing —
    // the exact destinations of the Music BAR tabs. No duplicate options: they are gone.
    expect(drawer.querySelector('.navbar-index-nav[aria-label="Music sections"]')).toBeNull();
    expect(screen.queryByText("Now playing")).toBeNull();
  });

  it("Photos: the album index AND the reel's facet rail under it", async () => {
    renderNav("/photos/browse", { ...baseUser, familyAlbum: true });
    const drawer = await openDrawer();
    await waitFor(() => expect(railTitles(drawer).length).toBeGreaterThan(0));

    expect(railTitles(drawer)).toEqual(expect.arrayContaining(["Album", "People", "Kind", "Camera"]));
    // The index rows the desktop sider has stay: they are the album's ways in, not bar tabs.
    expect(drawer.querySelector('.navbar-index-nav[aria-label="Album sections"]')).toBeTruthy();
  });

  it("Books: the counted index AND the catalog's facet rail", async () => {
    renderNav("/books", { ...baseUser, booksAccess: true, booksMaturityCeiling: 3 });
    const drawer = await openDrawer();
    await waitFor(() => expect(railTitles(drawer).length).toBeGreaterThan(0));

    expect(railTitles(drawer)).toEqual(expect.arrayContaining(["Collections", "Series", "Publishers"]));
    // Books' index carries counts and an Operate group the bar has no room for — it stays.
    expect(drawer.querySelector(".navbar-index-nav")).toBeTruthy();
  });

  it("Movies: the Seen · Want · Rate rows AND the browse rail", async () => {
    renderNav("/");
    const drawer = await openDrawer();
    await waitFor(() => expect(railTitles(drawer).length).toBeGreaterThan(0));

    expect(railTitles(drawer)).toEqual(expect.arrayContaining(["Type", "Genre", "Years"]));
    expect(Array.from(drawer.querySelectorAll(".stat-label")).map((n) => n.textContent))
      .toEqual(["Seen", "Want to Watch", "Rate Movies"]);
  });

  it("Arcade: the lobby's facet rail — the drawer Eric found empty", async () => {
    renderNav("/arcade");
    const drawer = await openDrawer();
    await waitFor(() => expect(railTitles(drawer).length).toBeGreaterThan(0));

    expect(railTitles(drawer)).toEqual(
      expect.arrayContaining(["Genre", "Players", "Region", "Mods & hacks", "RetroAchievements"])
    );
    // The lone door it used to hold instead.
    expect(drawer.querySelector(".navbar-drawer-filters")).toBeNull();
  });

  it("Board games: the facet rail and the BGG badge, in the sider's order", async () => {
    renderNav("/boardgames");
    const drawer = await openDrawer();
    await waitFor(() => expect(railTitles(drawer).length).toBeGreaterThan(0));

    expect(railTitles(drawer)).toEqual(expect.arrayContaining(["Players", "Publisher", "Category"]));
    const badge = drawer.querySelector('img[alt="Powered by BoardGameGeek"]');
    expect(badge).toBeTruthy();
    // …after the rail, before Log Out — the same order the desktop sider draws.
    const rail = drawer.querySelector(".bx-rail-on-sider");
    expect(rail.compareDocumentPosition(badge) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy();
  });
});

describe("the drawer survives a facet click", () => {
  // The law this replaced said "never host the filters inside NavBar's phone drawer — it closes on
  // every location.search change". That was a NavBar BEHAVIOUR, and it is fixed: the drawer closes
  // on a PATHNAME change, not on a search change.
  it("stays open when a facet writes ?f=, and closes on a destination", async () => {
    // Books, because its facet OPTIONS come off a fetch this file controls — a rail with no options
    // has nothing to click, and a check that skips its own assertion proves nothing.
    global.fetch = vi.fn(async (url) => {
      const body = String(url).includes("/browse/facets")
        ? { collections: [], series: [], tags: [{ value: "Noir", count: 3 }], authors: [], artists: [], events: [], franchises: [], publishers: [], decades: [] }
        : [];
      return {
        ok: true, status: 200,
        headers: { get: (k) => (k === "X-Total-Count" ? "42" : null) },
        json: async () => body, text: async () => "",
      };
    });
    renderNav("/books", { ...baseUser, booksAccess: true, booksMaturityCeiling: 3 });
    const drawer = await openDrawer();
    // Tags is collapsed until something in it is active — open it, in the drawer.
    await userEvent.click(await screen.findByText("Tags"));
    const include = await screen.findByRole("button", { name: "Include Noir" });
    expect(drawer.contains(include)).toBe(true);

    // A facet click is a history push that rewrites `search` and nothing else.
    await userEvent.click(include);
    await waitFor(() => expect(include).toHaveAttribute("aria-pressed", "true"));
    expect(document.querySelector(".navbar-dropdown--open")).toBeTruthy();

    // A destination, on the other hand, closes it: the switcher pushes a new PATHNAME.
    await userEvent.click(document.querySelector(".navbar-home-btn"));
    await userEvent.click(await screen.findByRole("button", { name: /Board Games/i }));
    await waitFor(() => expect(document.querySelector(".navbar-dropdown--open")).toBeNull());
  });

  afterEach(cleanup);
});

describe("the phone top bar's magnifier", () => {
  it("opens the drawer and puts the caret in the section's search", async () => {
    renderNav("/");
    expect(document.querySelector(".navbar-dropdown--open")).toBeNull();

    await userEvent.click(screen.getByRole("button", { name: "Search" }));
    await waitFor(() => expect(document.querySelector(".navbar-dropdown--open")).toBeTruthy());
    const drawer = document.querySelector(".navbar-dropdown--open");

    // The section's search is the rail's SmartSearch, at the top of the rail — the phone bar has no
    // centre search box for it to portal into, so the drawer carries it.
    const box = await within(drawer).findByRole("combobox");
    await waitFor(() => expect(document.activeElement).toBe(box));

    // …and there is nowhere else to reach the filters from: no Filters pill, no full-page sheet.
    expect(document.querySelector(".bx-filter-pill")).toBeNull();
    expect(document.querySelector(".bx-railbar-sheet")).toBeNull();
  });

  it("focuses the search on a tap while the drawer is already open", async () => {
    renderNav("/");
    await openDrawer();
    const drawer = document.querySelector(".navbar-dropdown--open");
    const box = await within(drawer).findByRole("combobox");
    box.blur();
    expect(document.activeElement).not.toBe(box);

    await userEvent.click(screen.getByRole("button", { name: "Search" }));
    await waitFor(() => expect(document.activeElement).toBe(box));
  });

  afterEach(cleanup);
});
