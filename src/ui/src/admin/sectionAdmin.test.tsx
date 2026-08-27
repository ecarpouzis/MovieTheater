import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { cleanup, render, screen, waitFor } from "@testing-library/react";
import { MemoryRouter, Route } from "react-router-dom";
import { SECTIONS, tabsFor } from "../catalog/bar/sections";
import { NOT_ALIASED, PHOTOS_ADMIN_ALIASES, SITE_ADMIN_ALIASES } from "./aliases";

// One route test per section (R9 S6): the section's `/…/admin` shows the tabs it is supposed to
// show for an operator, and refuses a plain member with the shell's plate. Only the ACTIVE tab's
// body mounts, so these render the Overview and nothing heavier.

global.IS_REACT_ACT_ENVIRONMENT = true;
(global as unknown as { matchMedia: unknown }).matchMedia = (global as unknown as { matchMedia?: unknown }).matchMedia || ((q: string) => ({
  matches: false, media: q, onchange: null, addListener: vi.fn(), removeListener: vi.fn(), addEventListener: vi.fn(), removeEventListener: vi.fn(), dispatchEvent: vi.fn(),
}));
(global as unknown as { ResizeObserver: unknown }).ResizeObserver = (global as unknown as { ResizeObserver?: unknown }).ResizeObserver || class { observe() {} unobserve() {} disconnect() {} };
(global as unknown as { IntersectionObserver: unknown }).IntersectionObserver = (global as unknown as { IntersectionObserver?: unknown }).IntersectionObserver || class { observe() {} unobserve() {} disconnect() {} takeRecords() { return []; } };
class NoopEventSource { onopen = null; onerror = null; addEventListener() {} close() {} }
vi.stubGlobal("EventSource", NoopEventSource);

// Every admin Overview reads existing endpoints; an empty-but-OK answer is enough to draw the page.
const BODIES: Record<string, unknown> = {
  "/API/GetTotalMovieCount": { totalCount: 4242, success: true },
  "/API/Admin/IngestReview/List": { items: [], byConfidence: [], byType: [], batches: [] },
  "/API/Admin/IngestReview/SyncCandidates": { items: [], counts: { upgrades: 0, newTitles: 0, unclassified: 0 }, seriesGroups: [] },
  "/API/Admin/Users": [],
  "/API/Channel/Admin/List": [],
  "/API/Channel/Playlist/Mine": [],
  "/API/Arcade/Filters": { total: 0, systems: [], regions: [], variants: [] },
  "/API/Arcade/Rooms": [],
  "/API/Arcade/HostStatus": { degraded: false, stale: false, kind: "ok" },
  "/API/Music/Albums": { items: [] },
  "/API/Music/Artists": [],
  "/API/Music/Capabilities": { streamingConfigured: true, transcodeEnabled: true, fmp4Enabled: true },
  "/odata/Boardgames": { value: [] },
  "/API/Boardgames/Facets": [],
};

beforeEach(() => {
  vi.stubGlobal("fetch", vi.fn(async (url: string) => {
    const key = Object.keys(BODIES).find((k) => String(url).startsWith(k));
    return { ok: true, status: 200, headers: { get: () => null }, json: async () => BODIES[key ?? ""] ?? {}, text: async () => "" };
  }));
  try { window.localStorage.clear(); } catch { /* private mode */ }
});
afterEach(() => { cleanup(); vi.unstubAllGlobals(); });

const ADMIN = { username: "eric", isAdmin: true, canEditMovies: true, hasPassword: true, familyAlbum: true };
const MEMBER = { username: "guest", isAdmin: false, canEditMovies: false, hasPassword: true };

function mount(path: string, node: React.ReactNode) {
  const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <QueryClientProvider client={qc}>
      <MemoryRouter initialEntries={[path]}>
        <Route path={path.split("?")[0]}>{node}</Route>
      </MemoryRouter>
    </QueryClientProvider>
  );
}

const tabNames = () => screen.queryAllByRole("tab").map((t) => t.textContent?.trim());

describe("the bar's Admin tab points at each section's admin shell", () => {
  it("names /<section>/admin for every section that has one, and hides it from a member", () => {
    const paths: Record<string, string | undefined> = {};
    for (const s of SECTIONS) {
      paths[s.key] = tabsFor(s, ADMIN).find((t) => t.key === "admin")?.path;
      expect(tabsFor(s, MEMBER).some((t) => t.key === "admin")).toBe(false);
    }
    expect(paths).toEqual({
      movies: "/movies/admin",
      tv: "/channels/admin",
      arcade: "/arcade/admin",
      music: "/music/admin",
      photos: "/photos/admin",
      boardgames: "/boardgames/admin",
      // Books is gated on membership too, so an admin without Books access has no tab there.
      books: undefined,
    });
    expect(tabsFor(SECTIONS.find((s) => s.key === "books")!, { ...ADMIN, booksAccess: true }).find((t) => t.key === "admin")?.path).toBe("/books/admin");
  });
});

describe("the routes the tools used to own", () => {
  // Ported from the old per-page routes: each of these WAS a page, and is a tab now. The pairs are
  // the redirects App and PhotosPage render, so a bookmark anyone kept still lands.
  it("each redirects to the tab that now holds it", () => {
    expect(SITE_ADMIN_ALIASES).toEqual([
      { from: "/insert", to: "/movies/admin?tab=insert" },
      { from: "/batchinsert", to: "/movies/admin?tab=batch-insert" },
      { from: "/review-ingest", to: "/movies/admin?tab=review-ingest" },
      { from: "/boardgames/batchinsert", to: "/boardgames/admin?tab=batchinsert" },
    ]);
    expect(PHOTOS_ADMIN_ALIASES).toEqual([
      { from: "/photos/tag", to: "/photos/admin?tab=tag" },
      { from: "/photos/dupes", to: "/photos/admin?tab=dupes" },
      { from: "/photos/review", to: "/photos/admin?tab=review" },
      { from: "/photos/google", to: "/photos/admin?tab=google" },
    ]);
    // A member surface must never be redirected into an admin-gated shell.
    expect(NOT_ALIASED).toContain("/rate");
    for (const a of [...SITE_ADMIN_ALIASES, ...PHOTOS_ADMIN_ALIASES]) expect(NOT_ALIASED).not.toContain(a.from);
  });
});

describe("/movies/admin", () => {
  it("lists the movie tools for an editor", async () => {
    const { default: MoviesAdminPage } = await import("../Pages/Admin/MoviesAdminPage");
    mount("/movies/admin", <MoviesAdminPage userData={ADMIN} setUserData={() => {}} />);
    await waitFor(() => expect(tabNames()).toEqual(["Overview", "Review ingest", "Insert", "Batch insert", "Users", "Rate"]));
  });

  it("hides Users from an editor who is not an admin, and refuses a plain member", async () => {
    const { default: MoviesAdminPage } = await import("../Pages/Admin/MoviesAdminPage");
    mount("/movies/admin", <MoviesAdminPage userData={{ ...MEMBER, canEditMovies: true }} setUserData={() => {}} />);
    await waitFor(() => expect(tabNames()).not.toContain("Users"));
    cleanup();
    mount("/movies/admin", <MoviesAdminPage userData={MEMBER} setUserData={() => {}} />);
    expect(screen.getByText("Administrators only")).toBeTruthy();
    expect(tabNames()).toEqual([]);
  });
}, 30000);

describe("/channels/admin", () => {
  it("lists the TV tools for an editor and refuses a member", async () => {
    const { default: TvAdminPage } = await import("../Pages/Tv/TvAdminPage");
    mount("/channels/admin", <TvAdminPage userData={ADMIN} />);
    await waitFor(() => expect(tabNames()).toEqual(["Overview", "Channels", "Playlists"]));
    cleanup();
    mount("/channels/admin", <TvAdminPage userData={MEMBER} />);
    expect(screen.getByText("Administrators only")).toBeTruthy();
  });
}, 30000);

describe("/arcade/admin", () => {
  it("lists the arcade tools for an admin and refuses a member", async () => {
    const { default: ArcadeAdminPage } = await import("../Pages/Arcade/ArcadeAdminPage");
    mount("/arcade/admin", <ArcadeAdminPage userData={ADMIN} />);
    await waitFor(() => expect(tabNames()).toEqual(["Overview", "Game config", "Saves vault", "RetroAchievements"]));
    cleanup();
    mount("/arcade/admin", <ArcadeAdminPage userData={MEMBER} />);
    expect(screen.getByText("Administrators only")).toBeTruthy();
  });
}, 30000);

describe("/music/admin", () => {
  it("is an Overview and nothing else — Music has no operator tools on the site", async () => {
    const { default: MusicAdminPage } = await import("../Pages/Music/MusicAdminPage");
    mount("/music/admin", <MusicAdminPage userData={ADMIN} />);
    await waitFor(() => expect(tabNames()).toEqual(["Overview"]));
    cleanup();
    mount("/music/admin", <MusicAdminPage userData={MEMBER} />);
    expect(screen.getByText("Administrators only")).toBeTruthy();
  });
}, 30000);

describe("/boardgames/admin", () => {
  it("lists the collection tools for an editor and refuses a member", async () => {
    const { default: BoardgamesAdminPage } = await import("../Pages/BoardGames/BoardgamesAdminPage");
    mount("/boardgames/admin", <BoardgamesAdminPage userData={ADMIN} />);
    await waitFor(() => expect(tabNames()).toEqual(["Overview", "Batch insert"]));
    cleanup();
    mount("/boardgames/admin", <BoardgamesAdminPage userData={MEMBER} />);
    expect(screen.getByText("Administrators only")).toBeTruthy();
  });
}, 30000);

describe("/photos/admin", () => {
  it("lists the curation tools for an admin and refuses a member", async () => {
    const { default: PhotosAdminPage } = await import("../Pages/Photos/PhotosAdminPage");
    mount("/photos/admin", <PhotosAdminPage status={{ admin: true, photos: 10, videos: 2 }} people={[]} refreshPeople={() => {}} changed={() => {}} refreshKey={0} />);
    await waitFor(() => expect(tabNames()).toEqual(["Overview", "Review", "Dupes", "Tag queue", "Google"]));
    cleanup();
    mount("/photos/admin", <PhotosAdminPage status={{ admin: false }} people={[]} refreshPeople={() => {}} changed={() => {}} refreshKey={0} />);
    expect(screen.getByText("Administrators only")).toBeTruthy();
  });
}, 30000);
