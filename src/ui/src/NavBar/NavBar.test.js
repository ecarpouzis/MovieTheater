import { cleanup, render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { MemoryRouter } from "react-router-dom";
import { vi, describe, it, expect, beforeEach } from "vitest";

// React 18 only enables act() support when this flag is set (vitest doesn't set it for us).
global.IS_REACT_ACT_ENVIRONMENT = true;

// antd's Sider subscribes to breakpoints via matchMedia, which happy-dom doesn't implement.
global.matchMedia =
  global.matchMedia ||
  ((query) => ({
    matches: false,
    media: query,
    addListener() {},
    removeListener() {},
    addEventListener() {},
    removeEventListener() {},
  }));

// The album's rail asks for two things by name, and what they answer decides what it draws — so
// those two are fixtures the tests set, and everything else stays generic.
const photos = vi.hoisted(() => ({
  status: { ok: true, status: 200, body: null },
  people: { people: [], unnamed: [] },
}));

// The nav panels call a spread of MovieAPI endpoints (getGenres, getMPARatings, getArcadeFilters, …)
// and grow more over time; a Proxy answers any of them with an empty OK response.
vi.mock("../MovieAPI", () => ({
  MovieAPI: new Proxy(
    {},
    {
      get: (_target, name) => {
        if (name === "getPhotosStatus") {
          return () =>
            Promise.resolve({
              ok: photos.status.ok,
              status: photos.status.status,
              json: () => Promise.resolve(photos.status.body),
            });
        }
        if (name === "getPhotoPeople") {
          return () => Promise.resolve({ ok: true, status: 200, json: () => Promise.resolve(photos.people) });
        }
        // [] not null: LoginForm maps the user list straight off the response.
        return vi.fn(() => Promise.resolve({ ok: true, json: () => Promise.resolve([]) }));
      },
    }
  ),
}));

// Desktop rail; the mobile drawer renders the same shared footer node.
vi.mock("../hooks/useIsMobile", () => ({ default: () => false }));

import NavBar from "./NavBar";
import { resetPhotosAlbum } from "../hooks/usePhotosAlbum";

const userData = { username: "Eric", hasPassword: true, moviesSeen: [], moviesToWatch: [], ratings: {} };
const familyMember = { ...userData, familyAlbum: true };

// The big route-dispatch effect calls these; they're irrelevant to the footer.
const noopSearchProps = Object.fromEntries(
  [
    "search", "resetSearch", "titleSearch", "actorSearch", "genreSearch", "franchiseSearch",
    "firstLetterSearch", "titleTypeSearch", "landingSearch", "ratingSearch", "restoreMovieIdsSearch",
    "moviesSeenSearch", "moviesWantToWatchSearch",
  ].map((k) => [k, vi.fn()])
);

function renderNav(route, ud = userData, setUserData = vi.fn()) {
  render(
    <MemoryRouter initialEntries={[route]}>
      <NavBar
        {...noopSearchProps}
        userData={ud}
        setUserData={setUserData}
        onUserLoggedIn={vi.fn()}
        isAuthReady
        collapsed={false}
        onCollapse={vi.fn()}
        theme="dark"
        toggleTheme={vi.fn()}
      />
    </MemoryRouter>
  );
}

const logoutBtn = () => screen.queryByRole("button", { name: /log out/i });

beforeEach(() => {
  vi.clearAllMocks();
  global.fetch = vi.fn(() => Promise.resolve({ ok: true }));
  photos.status = {
    ok: true,
    status: 200,
    body: { assets: 4210, photos: 4000, videos: 210, albums: 7, people: 12, undated: 0, empty: false, dataPlane: true },
  };
  photos.people = { people: [], unnamed: [] };
  resetPhotosAlbum();
});

describe("navbar footer Log Out", () => {
  // The whole point of the move: one Log Out, in the footer, on every section.
  it.each([
    ["movies", "/"],
    ["board games", "/boardgames"],
    ["arcade", "/arcade"],
  ])("renders exactly one Log Out in the footer on %s", async (_name, route) => {
    renderNav(route);
    await waitFor(() => expect(logoutBtn()).toBeTruthy());

    expect(screen.getAllByRole("button", { name: /log out/i })).toHaveLength(1);

    const footer = document.querySelector(".navbar-footer");
    expect(footer).toBeTruthy();
    expect(footer.contains(logoutBtn())).toBe(true);

    // It must sit BELOW the theme row, not above it.
    const themeRow = footer.querySelector(".navbar-theme-row");
    expect(themeRow).toBeTruthy();
    expect(themeRow.compareDocumentPosition(logoutBtn()) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy();

    // And it must no longer live in the user panel next to the username/gear.
    const userPanel = document.querySelector(".user-panel");
    if (userPanel) expect(userPanel.querySelector(".logout-button")).toBeNull();
  });

  it("hides Log Out when signed out but keeps the theme row", () => {
    renderNav("/", null); // NOT undefined — that would re-trigger the default param and log us in

    expect(logoutBtn()).toBeNull();
    expect(document.querySelector(".navbar-footer .navbar-theme-row")).toBeTruthy();
  });

  it("logs the session out when clicked", async () => {
    const setUserData = vi.fn();
    renderNav("/", userData, setUserData);
    await userEvent.click(logoutBtn());

    expect(global.fetch).toHaveBeenCalledWith("/API/Logout", { method: "POST" });
    await waitFor(() => expect(setUserData).toHaveBeenCalled());
  });
});

// The owner's report: "the Nav Bar makes no sense for Photos (it just uses the movie nav bar right
// now)". /photos was falling through to the movies branch, so an album page carried a title/actor/
// genre search over a film library. These pin the replacement down — and pin down that nothing else
// moved, because a section rail is the one component every page on the site renders.
describe("the family album rail", () => {
  const photosNav = () => document.querySelector(".navbar-photos-nav");
  const railLabels = () =>
    Array.from(document.querySelectorAll(".navbar-photos-link-label")).map((node) => node.textContent);

  it("lists the album's views instead of the movie search tools", async () => {
    renderNav("/photos", familyMember);
    await waitFor(() => expect(photosNav()).toBeTruthy());

    expect(railLabels()).toEqual(["Timeline", "Albums", "Folders", "People"]);
    // The movie rail's search panel is what used to be here.
    expect(document.querySelector("#SearchToolContainer")).toBeNull();
  });

  it("carries the album's own word-mark and feature tint", async () => {
    renderNav("/photos", familyMember);
    await waitFor(() => expect(photosNav()).toBeTruthy());

    expect(document.querySelector(".navbar-photos-wordmark")).toBeTruthy();
    expect(document.querySelector(".navbar-sider.navbar-photos-theme")).toBeTruthy();
    expect(document.documentElement.dataset.feature).toBe("photos");
  });

  it("marks the view you are actually on", async () => {
    renderNav("/photos/albums/summer-1994", familyMember);
    await waitFor(() => expect(photosNav()).toBeTruthy());

    const active = document.querySelector(".navbar-photos-link.is-active");
    expect(active.textContent).toContain("Albums");
    expect(active.getAttribute("aria-current")).toBe("page");
  });

  it("shows what is waiting for an answer, and only when something is", async () => {
    photos.status.body = { ...photos.status.body, pendingDupeGroups: 3, admin: true };
    renderNav("/photos", { ...familyMember, isAdmin: true });
    await waitFor(() => expect(photosNav()).toBeTruthy());

    await waitFor(() => expect(railLabels()).toContain("Dupes"));
    expect(document.querySelector(".navbar-photos-count.is-waiting").textContent).toBe("3");
  });

  it("draws no index for someone the gate refused", async () => {
    photos.status = { ok: false, status: 403, body: {} };
    renderNav("/photos", familyMember);
    // The page renders the refusal; the rail simply has no album to index.
    await waitFor(() => expect(logoutBtn()).toBeTruthy());
    expect(photosNav()).toBeNull();
  });

  it("offers the admin show-hidden switch to an admin and to nobody else", async () => {
    renderNav("/photos", { ...familyMember, isAdmin: true });
    await waitFor(() => expect(document.querySelector(".navbar-photos-controls")).toBeTruthy());

    cleanup();
    renderNav("/photos", familyMember);
    await waitFor(() => expect(logoutBtn()).toBeTruthy());
    expect(document.querySelector(".navbar-photos-controls")).toBeNull();
  });

  it.each([
    ["movies", "/", "movies"],
    ["board games", "/boardgames", "boardgames"],
    ["arcade", "/arcade", "arcade"],
    ["music", "/music", "music"],
  ])("leaves the %s rail exactly as it was", async (_name, route, feature) => {
    renderNav(route, familyMember);
    await waitFor(() => expect(logoutBtn()).toBeTruthy());

    expect(photosNav()).toBeNull();
    expect(document.querySelector(".navbar-photos-wordmark")).toBeNull();
    expect(document.querySelector(".navbar-photos-theme")).toBeNull();
    expect(document.documentElement.dataset.feature).toBe(feature);
    // And no other section pays for the album: its status query is only ever made on /photos.
    expect(document.querySelector(".navbar-photos-count")).toBeNull();
  });
});
