import { render, act, cleanup } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { vi, describe, it, expect, beforeEach, afterEach } from "vitest";

// ── Browse rides the ONE catalog engine (R9 S3) ─────────────────────────────────────────────────
// URL-based infinite searches (the Type-scope browse, title/person/genre/franchise) are modelled as
// fixed slots from the first response — InfiniteBands' sparse bands — so the CatalogPager
// quick-scroll strip can seek anywhere and scrolling works in both directions. Id-list (Seen/Want)
// searches ride the SAME engine over a dense client source: their list is user-sized and a
// Seen/Want removal edits the array in place, which a sparse page map cannot express. This file
// pins the seams of that split. (The assertions moved from `.card-list` — the retired CardList
// shell — to `.bx-grid--movies`, the Grid's own wrap; the behaviour they pin is unchanged.)

global.IS_REACT_ACT_ENVIRONMENT = true;
global.ResizeObserver = global.ResizeObserver || class { observe() {} unobserve() {} disconnect() {} };
global.matchMedia = global.matchMedia || ((query) => ({
  matches: false, media: query, onchange: null,
  addListener: vi.fn(), removeListener: vi.fn(),
  addEventListener: vi.fn(), removeEventListener: vi.fn(), dispatchEvent: vi.fn(),
}));

vi.mock("./NowOnTvRail", () => ({ default: () => null }));
vi.mock("./MovieModal", () => ({ default: () => null }));
vi.mock("../Tv/PlaylistPickerModal", () => ({ default: () => null }));

import Browse from "./Browse";

const TOTAL = 600; // 10 pages of 60
const PAGE_SIZE = 60;

const movieAt = (i) => ({
  id: i + 1, kind: "movie", title: `Movie ${i}`, posterVersion: 1,
  plotFull: "", topCast: "", rating: null, runtime: null, imdbRating: null,
});

const ok = (body) => Promise.resolve({ ok: true, status: 200, json: () => Promise.resolve(body) });

let requests;
beforeEach(() => {
  requests = [];
  global.fetch = vi.fn((url) => {
    const u = new URL(String(url), "http://localhost");
    requests.push(u.pathname + u.search);
    if (u.pathname === "/API/BrowseFacets") {
      return ok({ types: [], genres: [], franchises: [], mpa: [], decades: [], tags: {}, total: TOTAL });
    }
    if (u.pathname === "/API/BrowseLetters") {
      return ok({
        total: TOTAL,
        letters: [
          { letter: "A", count: 300, offset: 0 },
          { letter: "M", count: 300, offset: 300 },
        ],
      });
    }
    if (u.pathname === "/API/GetMoviesByType") {
      const page = Number(u.searchParams.get("page") || 1);
      const skip = (page - 1) * PAGE_SIZE;
      return ok({
        totalCount: page === 1 ? TOTAL : -1,
        page,
        pageSize: PAGE_SIZE,
        movies: Array.from({ length: Math.min(PAGE_SIZE, TOTAL - skip) }, (_, n) => movieAt(skip + n)),
      });
    }
    if (u.pathname === "/API/GetMoviesByIds") {
      return ok({ totalCount: 3, page: 1, pageSize: PAGE_SIZE, movies: [movieAt(0), movieAt(1), movieAt(2)] });
    }
    return ok({});
  });
  window.localStorage.clear();
});
afterEach(() => { cleanup(); vi.clearAllMocks(); });

async function frames(n = 14) {
  for (let i = 0; i < n; i += 1) {
    // eslint-disable-next-line no-await-in-loop
    await act(async () => { await Promise.resolve(); });
  }
}

// Shaped like what useMovieSearch emits: the search names its OWN letter source, so Browse doesn't
// have to reverse-engineer one out of the browse URL (which is what kept the strip pinned to the
// unfiltered Type-scope browse). No lettersUrl = no strip, and the pager falls back to page numbers.
const urlSearch = {
  url: `/API/GetMoviesByType?type=Movies&sort=alpha`,
  lettersUrl: `/API/BrowseLetters?type=Movies`,
  titleTypes: ["Movies"],
  sort: "alpha",
  infinite: true,
};

function mount(search) {
  // The facet rail's hooks (options, count) ride React Query; index.js provides the client for the app.
  return render(
    <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
      <MemoryRouter initialEntries={["/"]}>
        <Browse search={search} userData={null} setUserData={() => {}} isAuthReady simpleStyle={false} />
      </MemoryRouter>
    </QueryClientProvider>
  );
}

describe("the sparse browse catalog", () => {
  it("fetches page 1 and renders real cards for it", async () => {
    const { container } = mount(urlSearch);
    await frames();
    expect(requests.some((r) => r.startsWith("/API/GetMoviesByType") && r.includes("page=1"))).toBe(true);
    expect(container.querySelector(".bx-grid--movies")).toBeTruthy();
    expect(container.textContent).toContain("Movie 0");
  });

  it("shows the letter quick-scroll strip for the alphabetical Type-scope browse", async () => {
    const { container } = mount(urlSearch);
    await frames();
    expect(requests.some((r) => r.startsWith("/API/BrowseLetters"))).toBe(true);
    const pager = container.querySelector(".catalog-pager");
    expect(pager).toBeTruthy();
    expect(pager.textContent).toContain("A");
    expect(pager.textContent).toContain("M");
  });

  it("mounts no bottom sentinel — the window pump owns fetching, not a scroll sentinel", async () => {
    const { container } = mount(urlSearch);
    await frames();
    // Nothing in the catalog stream is driven by a 1px bottom sentinel any more, on either path.
    expect(container.querySelector('div[aria-hidden="true"][style*="height: 1px"]')).toBeNull();
  });

  it("pages id-list (Seen/Want) searches into the SAME grid — dense, chunked, no sentinel", async () => {
    const { container } = mount({ movieIds: [1, 2, 3], infinite: true });
    await frames();
    // Chunked, never "fetch everything in one request": a paged POST, bounded by the row cap.
    expect(requests.some((r) => r.startsWith("/API/GetMoviesByIds") && r.includes("page=1"))).toBe(true);
    // It is the catalog Grid now — so Seen/Want finally has the pills, the tweaks and the strip.
    expect(container.querySelector(".bx-grid--movies")).toBeTruthy();
    expect(container.textContent).toContain("Movie 0");
    // Three titles is under one band: no pager to seek with.
    expect(container.querySelector(".catalog-pager")).toBeNull();
  });

  it("asks for no letters under a non-alphabetical sort (page numbers instead)", async () => {
    const { container } = mount({ ...urlSearch, url: `/API/GetMoviesByType?type=Movies&sort=imdb`, lettersUrl: undefined, sort: "imdb" });
    await frames();
    expect(requests.some((r) => r.startsWith("/API/BrowseLetters"))).toBe(false);
    // Pages-mode pager still renders (600 titles = 10 pages).
    const pager = container.querySelector(".catalog-pager");
    expect(pager).toBeTruthy();
  });
});
