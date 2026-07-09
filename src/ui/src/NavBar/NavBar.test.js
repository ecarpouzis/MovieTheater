import { render, screen, waitFor } from "@testing-library/react";
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

// The nav panels call a spread of MovieAPI endpoints (getGenres, getMPARatings, getArcadeFilters, …)
// and grow more over time; a Proxy answers any of them with an empty OK response.
vi.mock("../MovieAPI", () => ({
  MovieAPI: new Proxy(
    {},
    // [] not null: LoginForm maps the user list straight off the response.
    { get: () => vi.fn(() => Promise.resolve({ ok: true, json: () => Promise.resolve([]) })) }
  ),
}));

// Desktop rail; the mobile drawer renders the same shared footer node.
vi.mock("../hooks/useIsMobile", () => ({ default: () => false }));

import NavBar from "./NavBar";

const userData = { username: "Eric", hasPassword: true, moviesSeen: [], moviesToWatch: [], ratings: {} };

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
