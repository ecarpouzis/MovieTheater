import { fireEvent, render, screen } from "@testing-library/react";
import { MemoryRouter, Route } from "react-router-dom";
import SectionBar from "./SectionBar";
import { barHidden, isExploreRoute, sectionFor, tabIsActive, tabsFor } from "./sections";

/**
 * R9 S1: the one content-top bar. Tabs come from the table and are REMOVED (never disabled) when
 * they don't apply to the user; the active tab follows the pathname; the tools/search slots exist
 * for pages to portal into; the screening rooms show no bar.
 */
function renderAt(path: string, userData: Record<string, unknown> | null = null, theme = "light") {
  const toggleTheme = vi.fn();
  render(
    <MemoryRouter initialEntries={[path]}>
      <SectionBar userData={userData} theme={theme} toggleTheme={toggleTheme} />
      <Route path="*" render={({ location }) => <div data-testid="where">{location.pathname}</div>} />
    </MemoryRouter>,
  );
  return { toggleTheme };
}

describe("catalog/bar — sections table", () => {
  it("resolves a section by prefix, movies as the fallback, and hides on the screening rooms", () => {
    expect(sectionFor("/books/shelf").key).toBe("books");
    expect(sectionFor("/channels").key).toBe("tv");
    expect(sectionFor("/").key).toBe("movies");
    // R9 S6 moved the movie tools under one shell; `/review-ingest` still resolves (App redirects
    // it into the tab), and the shell's own path resolves to the same section.
    expect(sectionFor("/review-ingest").key).toBe("movies");
    expect(sectionFor("/movies/admin").key).toBe("movies");
    expect(sectionFor("/channels/admin").key).toBe("tv");
    expect(sectionFor("/photos/admin").key).toBe("photos");
    expect(barHidden("/watch/12")).toBe(true);
    expect(barHidden("/tv/3")).toBe(true);
    expect(barHidden("/arcade/room/ABCD")).toBe(true);
    expect(barHidden("/arcade")).toBe(false);
  });

  it("removes tabs that don't apply to the user, and an exact tab is only active on its own path", () => {
    const books = sectionFor("/books");
    expect(tabsFor(books, null).map((t) => t.key)).toEqual([]);
    const member = { booksAccess: true, hasPassword: true };
    expect(tabsFor(books, member).map((t) => t.key)).toEqual(["explore", "browse", "shelf", "novels", "kids"]);
    expect(tabsFor(books, { ...member, isAdmin: true }).map((t) => t.key)).toContain("admin");
    const browse = books.tabs.find((t) => t.key === "browse")!;
    expect(tabIsActive(browse, "/books")).toBe(true);
    expect(tabIsActive(browse, "/books/explore")).toBe(false);
    const shelf = books.tabs.find((t) => t.key === "shelf")!;
    expect(tabIsActive(shelf, "/books/shelf")).toBe(true);
  });
});

describe("catalog/bar — the Explore landings (R9 S7)", () => {
  it("every browsable section offers an Explore tab", () => {
    for (const [path, expected] of [
      ["/", "/movies/explore"],
      ["/music", "/music/explore"],
      ["/arcade", "/arcade/explore"],
      ["/boardgames", "/boardgames/explore"],
      ["/photos", "/photos/explore"],
    ] as const) {
      const tab = sectionFor(path).tabs.find((t) => t.key === "explore");
      expect(tab?.path).toBe(expected);
    }
    // TV deliberately has none: /channels IS the EPG, so an Explore of "now + favourites" would be
    // a second copy of that page. See docs/catalog.md → "The Explore kit".
    expect(sectionFor("/channels").tabs.find((t) => t.key === "explore")).toBeUndefined();
  });

  it("knows an Explore route, so the facet rails can hide where they have nothing to filter", () => {
    expect(isExploreRoute("/movies/explore")).toBe(true);
    expect(isExploreRoute("/music/explore")).toBe(true);
    expect(isExploreRoute("/boardgames/explore")).toBe(true);
    expect(isExploreRoute("/photos/explore")).toBe(true);
    expect(isExploreRoute("/books/explore")).toBe(true);
    expect(isExploreRoute("/")).toBe(false);
    expect(isExploreRoute("/music")).toBe(false);
    expect(isExploreRoute("/boardgames")).toBe(false);
  });
});

describe("catalog/bar — SectionBar", () => {
  it("renders the section's tabs with the current one active, the slots, and the theme toggle", () => {
    const { toggleTheme } = renderAt("/photos/browse", { isAdmin: true });
    const nav = screen.getByRole("navigation", { name: "Photos sections" });
    expect(nav).toBeTruthy();
    const active = screen.getByRole("button", { name: "Browse" });
    expect(active.getAttribute("aria-current")).toBe("page");
    expect(screen.getByRole("button", { name: "Admin" }).className).toContain("sbar-tab--admin");
    expect(document.getElementById("section-bar-tools")).not.toBeNull();
    expect(document.getElementById("section-bar-search")).not.toBeNull();
    fireEvent.click(screen.getByRole("button", { name: /switch to dark theme/i }));
    expect(toggleTheme).toHaveBeenCalledTimes(1);
  });

  it("navigates on a tab click and stays put on the active one", () => {
    renderAt("/music", { hasPassword: true });
    fireEvent.click(screen.getByRole("button", { name: "Playlists" }));
    expect(screen.getByTestId("where").textContent).toBe("/music/playlists");
    fireEvent.click(screen.getByRole("button", { name: "Playlists" }));
    expect(screen.getByTestId("where").textContent).toBe("/music/playlists");
  });

  it("shows nothing on a screening room", () => {
    renderAt("/watch/12");
    expect(screen.queryByRole("navigation")).toBeNull();
    expect(document.getElementById("section-bar-tools")).toBeNull();
  });
});
