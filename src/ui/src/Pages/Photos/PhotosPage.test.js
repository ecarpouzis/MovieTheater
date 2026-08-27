import { render, cleanup, screen, waitFor, fireEvent } from "@testing-library/react";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { MemoryRouter, Route } from "react-router-dom";
import { vi, describe, it, expect, afterEach, beforeEach } from "vitest";

// Page-level tests for /photos (docs/photos-plan.md §4). What these pin down is the loop the unit
// tests beside them cannot: the gate's answer being RENDERED rather than assumed, the undated shelf
// staying a separate request, and the folder tree paging by prefix.
//
// The views are ROUTES now rather than local state, so these render at a URL and assert what came
// up. That is the same coverage plus the thing the old page could not do at all: land on a view
// directly, which is what a bookmarked or shared album link does.

global.IS_REACT_ACT_ENVIRONMENT = true;
global.matchMedia = global.matchMedia || ((q) => ({
  matches: false, media: q, onchange: null,
  addListener: vi.fn(), removeListener: vi.fn(),
  addEventListener: vi.fn(), removeEventListener: vi.fn(), dispatchEvent: vi.fn(),
}));
global.ResizeObserver = global.ResizeObserver || class { observe() {} unobserve() {} disconnect() {} };
// The justified grid lays out from a measured container width, which jsdom reports as zero — without
// this the grid renders nothing and any assertion about clicking a card passes vacuously.
Object.defineProperty(HTMLElement.prototype, "clientWidth", { configurable: true, value: 1000 });

// antd's toasts render into their own React root OUTSIDE the testing-library act() scope, so every
// success/error message lands as an "update not wrapped in act" warning during teardown. The toast
// is not what any of these tests are about — the API call is.
vi.mock("antd", async () => {
  const actual = await vi.importActual("antd");
  return {
    ...actual,
    message: { success: vi.fn(), error: vi.fn(), info: vi.fn(), warning: vi.fn(), loading: vi.fn() },
  };
});

let statusResponse;
let peopleResponse;
const timelineCalls = [];
const folderCalls = [];
const hideCalls = [];
// Every id the lightbox was asked to open. Recorded rather than asserted on the click, because the
// bug this catches is about the SHAPE of what gets handed along, not about whether a handler fired.
const assetCalls = [];
// Ids the server answers with a 404 — a deleted photo, or one hidden from the member who followed
// somebody's link. Indistinguishable by design.
const missingAssets = new Set();

const ok = (body) => Promise.resolve({ ok: true, status: 200, json: () => Promise.resolve(body) });

const card = (id, takenAt) => ({
  id,
  path: `Folder/photo${id}.jpg`,
  kind: "Photo",
  width: 400,
  height: 300,
  takenAt,
  takenAtSource: takenAt ? "Exif" : "Unknown",
  thumbState: "Ready",
  gridUrl: `https://gateway.example/s/tok${id}/PhotoThumb`,
});

vi.mock("../../MovieAPI", () => ({
  MovieAPI: {
    getPhotosStatus: () => statusResponse,
    getPhotosTimeline: (params) => {
      timelineCalls.push(params);
      const items = params.undated ? [card(90, null)] : [card(1, "2014-03-12T10:15:30")];
      return ok({ items, nextCursor: null, hasMore: false, undated: !!params.undated, dataPlane: true });
    },
    getPhotosTimelineYears: () => ok({
      years: [
        { year: 2014, count: 320 },
        { year: 2011, count: 900 },
        { year: 1997, count: 7 },
      ],
      undated: 4782,
    }),
    getPhotosFolder: (params) => {
      folderCalls.push(params);
      return ok({
        path: params.path || "",
        folders: params.path ? [] : [{ name: "Vacation 2004", count: 12 }],
        items: params.path ? [card(5, null)] : [],
        total: params.path ? 1 : 0,
        skip: 0,
        hasMore: false,
        dataPlane: true,
      });
    },
    setPhotosHidden: (ids, hidden) => {
      hideCalls.push({ ids, hidden });
      return ok({ requested: ids.length, matched: ids.length, changed: ids.length, hidden });
    },
    getPhotoAlbums: () => ok({ albums: [], dataPlane: true }),
    getPhotoAlbum: (slug) => ok({
      album: { id: 1, title: "Summer 1994", slug, description: "", rangeStart: null, rangeEnd: null },
      items: [{ card: card(50, "1994-07-04T12:00:00") }],
      hasMore: false,
      dataPlane: true,
    }),
    createPhotoAlbum: () => ok({ album: { id: 1, title: "New", slug: "new" }, added: 0 }),
    addToPhotoAlbum: () => ok({ added: 0, total: 0 }),
    getPhotosHideProposals: () => ok({ configured: true, proposals: [] }),
    getPhotosIngestBatches: () => ok({ configured: true, groups: [], quarantineActive: true, quarantinedBatches: 0 }),
    getPhotoAssetAlbums: () => ok({ albums: [] }),
    getPhotoPeople: () => peopleResponse,
    getPhotoPerson: (id) => ok({
      person: { id, name: "Subject A", birthYear: null, userId: null, immichLinked: false },
      tagCount: 1, suggestionCount: 0, firstTakenAt: null, lastTakenAt: null,
      alsoWith: [], coverUrl: null, faceCropUrl: null, dataPlane: true,
    }),
    getPhotoPersonTimeline: () => ok({
      items: [card(77, "2014-03-12T10:15:30")],
      total: 1, skip: 0, hasMore: false, dataPlane: true,
    }),
    getPhotoAssetTags: (id) => ok({ assetId: id, masterAssetId: id, redirected: false, tags: [], earliestYearHint: 0 }),
    addPhotoTags: () => ok({ person: { id: 1, name: "Subject A" }, added: 1, promoted: 0, unchanged: 0, redirectedToMasters: 0, missing: 0 }),
    getPhotoTagQueue: () => ok({ mode: "untagged", items: [], nextCursor: 0, hasMore: false, remaining: 0, total: 0 }),
    getPhotoAsset: (id) => {
      assetCalls.push(id);
      // The server answers a missing asset AND one hidden from a non-admin with the same 404, on
      // purpose — it refuses to say which. Both arrive here as "gone".
      if (missingAssets.has(id)) {
        return Promise.resolve({ ok: false, status: 404, json: () => Promise.resolve({}) });
      }
      return ok({
      card: card(id, "2014-03-12T10:15:30"),
      fileName: `photo${id}.jpg`,
      folder: "Folder",
      sizeBytes: 2097152,
      cameraMake: "TestCam",
      viewUrl: "https://gateway.example/s/view/PhotoThumb",
      zoomUrl: "https://gateway.example/s/zoom/PhotoOriginal",
      downloadUrl: "https://gateway.example/s/dl/PhotoOriginal",
      exif: { "Exif SubIFD": { "Date/Time Original": "2014:03:12 10:15:30" } },
      });
    },
  },
}));

const PhotosPage = (await import("./PhotosPage")).default;
const { saveShowHiddenPhotos } = await import("../../hooks/useShowHiddenPhotos");
const { resetPhotosAlbum, photosNavGroups } = await import("../../hooks/usePhotosAlbum");

const populated = (extra = {}) => ok({
  assets: 10, photos: 9, videos: 1, missing: 0, hidden: 0, undated: 3,
  people: 0, albums: 0, pendingDupeGroups: 0, empty: false, dataPlane: true, ...extra,
});

// Renders the album at a URL, and hands back a live read of where the router ended up so a test can
// assert on navigation as well as on what rendered.
function renderAt(route, userData) {
  const seen = { pathname: route, search: "", action: null, entries: 0 };
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  render(
    <QueryClientProvider client={client}>
    <MemoryRouter initialEntries={[route]}>
      <PhotosPage userData={userData} />
      <Route
        path="*"
        render={({ location, history }) => {
          seen.pathname = location.pathname;
          seen.search = location.search;
          seen.action = history.action;
          seen.entries = history.length;
          return null;
        }}
      />
    </MemoryRouter>
    </QueryClientProvider>
  );
  return seen;
}

beforeEach(() => {
  timelineCalls.length = 0;
  folderCalls.length = 0;
  hideCalls.length = 0;
  assetCalls.length = 0;
  missingAssets.clear();
  saveShowHiddenPhotos(false);
  statusResponse = populated();
  peopleResponse = ok({ people: [], unnamed: [], dataPlane: true });
  // The album's status/people are shared with the navbar rail through a module-level store; without
  // this, case two would inherit case one's answer.
  resetPhotosAlbum();
});

afterEach(async () => {
  cleanup();
  // Settle in-flight promises inside the test's lifetime (see PhotoCuration.test.js).
  await new Promise((resolve) => setTimeout(resolve, 0));
});

describe("PhotosPage gating", () => {
  it("renders the server's refusal rather than assuming the nav filtered", async () => {
    // A URL can be typed or bookmarked; the UI is never the gate (§2.1).
    statusResponse = Promise.resolve({ ok: false, status: 403, json: () => Promise.resolve({}) });
    renderAt("/photos", { username: "someone" });
    await screen.findByText(/limited to family members/i);
    // And it says nothing about what is inside.
    expect(screen.queryByText(/photos$/i)).not.toBeNull();
    expect(timelineCalls).toHaveLength(0);
  });

  it("treats an anonymous 401 the same as a refused 403", async () => {
    statusResponse = Promise.resolve({ ok: false, status: 401, json: () => Promise.resolve({}) });
    renderAt("/photos", {});
    await screen.findByText(/limited to family members/i);
  });

  it("refuses a deep-linked sub-view exactly as it refuses the front page", async () => {
    // The gate is checked once, for the section — a shared /photos/people/3 link cannot be a way in.
    statusResponse = Promise.resolve({ ok: false, status: 403, json: () => Promise.resolve({}) });
    renderAt("/photos/people/3", { username: "someone" });
    await screen.findByText(/limited to family members/i);
  });

  it("says the collection has not been read in yet when it is empty", async () => {
    statusResponse = ok({ assets: 0, photos: 0, videos: 0, missing: 0, hidden: 0, undated: 0, people: 0, albums: 0, pendingDupeGroups: 0, empty: true, dataPlane: true });
    renderAt("/photos", { username: "member" });
    await screen.findByText(/has not been read in yet/i);
    expect(timelineCalls).toHaveLength(0);
  });
});

describe("PhotosPage browse", () => {
  it("loads the dated timeline first and does not ask for undated items", async () => {
    renderAt("/photos", { username: "member" });
    await waitFor(() => expect(timelineCalls.length).toBeGreaterThan(0));
    expect(timelineCalls[0].undated).toBeFalsy();
  });

  it("the undated shelf is a SEPARATE request, never interleaved", async () => {
    // §2.7: date-unknown items get their own shelf rather than being scattered into the timeline.
    renderAt("/photos/undated", { username: "member" });
    await waitFor(() => expect(timelineCalls.some((c) => c.undated)).toBe(true));
    // Two distinct streams, each with its own cursor — no request ever asks for both.
    expect(timelineCalls.every((c) => c.undated)).toBe(true);
  });

  it("browses the folder tree by prefix", async () => {
    const seen = renderAt("/photos/folders", { username: "member" });
    const folder = await screen.findByText("Vacation 2004");
    fireEvent.click(folder);

    await waitFor(() => expect(folderCalls.some((c) => c.path === "Vacation 2004")).toBe(true));
    // And the position in the tree is in the URL, so it can be shared and survives a refresh.
    // Decoded before comparing: whether the history entry keeps the %20 or the space is the
    // router's business, and the folder view reads it back either way.
    expect(decodeURIComponent(seen.pathname)).toBe("/photos/folders/Vacation 2004");
  });

  it("opens a deep-linked folder without a click", async () => {
    renderAt("/photos/folders/Vacation%202004", { username: "member" });
    await waitFor(() => expect(folderCalls.some((c) => c.path === "Vacation 2004")).toBe(true));
  });

  it("no longer offers a show-hidden toggle in the page (Phase 4 moved it to the navbar, admin-only)", async () => {
    // Phase 2 put this switch here and let any member flip it. The owner's decision supersedes that:
    // hiding stays member work, but SEEING the hidden pile is admin work, and the switch that reveals
    // it lives in the navbar. Only the Select switch is left here.
    renderAt("/photos", { username: "member" });
    await waitFor(() => expect(timelineCalls.length).toBeGreaterThan(0));
    expect(timelineCalls.every((c) => !c.includeHidden)).toBe(true);
    expect(screen.getAllByRole("switch")).toHaveLength(1);
    expect(screen.queryByText(/show hidden/i)).toBeNull();
  });

  it("asks for hidden items when the navbar switch is on", async () => {
    // The page reads the same persisted value the navbar writes. It is not a gate in either place:
    // the server ignores includeHidden from a non-admin regardless of what is stored.
    saveShowHiddenPhotos(true);
    statusResponse = populated({ canShowHidden: true, admin: true });
    renderAt("/photos", { username: "operator" });
    await waitFor(() => expect(timelineCalls.some((c) => c.includeHidden)).toBe(true));
    expect(await screen.findByText(/Showing hidden photos/i)).toBeTruthy();
  });

  it("selection mode turns a card click into a batch hide", async () => {
    renderAt("/photos", { username: "member" });
    await waitFor(() => expect(timelineCalls.length).toBeGreaterThan(0));

    fireEvent.click(screen.getAllByRole("switch")[0]); // Select — the only switch left in the toolbar
    fireEvent.click(await screen.findByTitle("photo1.jpg"));
    fireEvent.click(await screen.findByText("Hide"));

    await waitFor(() => expect(hideCalls).toHaveLength(1));
    expect(hideCalls[0]).toEqual({ ids: [1], hidden: true });
  });

  it("opens a photo from a PERSON page, the same as from every other grid", async () => {
    // Every browse grid hands its onOpen the whole CARD. The people tab used to pass the
    // set-open-asset setter raw, so the card OBJECT became the open-asset id and the lightbox asked
    // the server for asset "[object Object]" — every photo on every person page was unopenable, on a
    // tab whose entire purpose is looking at one person's photographs.
    peopleResponse = ok({
      people: [{
        id: 3, name: "Subject A", birthYear: null, userId: null, immichLinked: false,
        tagCount: 1, suggestionCount: 0, coverUrl: null, faceCropUrl: null,
      }],
      unnamed: [],
      dataPlane: true,
    });
    const seen = renderAt("/photos/people", { username: "member" });

    fireEvent.click(await screen.findByText("Subject A"));
    // A person is a URL now, which is what makes "look at these" a thing one can send.
    await waitFor(() => expect(seen.pathname).toBe("/photos/people/3"));
    fireEvent.click(await screen.findByTitle("photo77.jpg"));

    await waitFor(() => expect(assetCalls).toContain(77));
    // Stated separately: a card object also "contains" nothing, and toContain(77) alone would pass
    // for a stringified id. What the lightbox needs is the number.
    expect(assetCalls.every((id) => typeof id === "number")).toBe(true);
  });

  it("a deep-linked person opens on that person, not on the list", async () => {
    peopleResponse = ok({ people: [], unnamed: [], dataPlane: true });
    renderAt("/photos/people/3", { username: "member" });
    expect(await screen.findByTitle("photo77.jpg")).toBeTruthy();
  });

  it("says so plainly when the image gateway is not configured", async () => {
    statusResponse = populated({ dataPlane: false });
    renderAt("/photos", { username: "member" });
    await screen.findByText(/image gateway is not configured/i);
  });
});

describe("the year rail (deep browse)", () => {
  it("lists the years that hold photographs, grouped by decade, with the undated shelf at the end", async () => {
    renderAt("/photos", { username: "member" });
    await screen.findByText("2011");
    expect(screen.queryByText("2010s")).not.toBeNull();
    expect(screen.queryByText("1990s")).not.toBeNull();
    // The undated shelf is reachable from the rail — the seventy-five-year collection's biggest
    // pile is the one with no year at all, and a year index that omitted it would hide it.
    expect(screen.queryByText("Undated")).not.toBeNull();
    expect(screen.queryByText("4,782")).not.toBeNull();
  });

  it("a year press seeds the keyset cursor at the following New Year with id 0", async () => {
    renderAt("/photos", { username: "member" });
    fireEvent.click(await screen.findByText("2011"));

    // The jump is the ordinary cursor, seeded: strictly-before Jan 1 2012 — no offset, no new mode.
    await waitFor(() =>
      expect(
        timelineCalls.some((c) => c.beforeTakenAt === "2012-01-01T00:00:00" && c.beforeId === 0)
      ).toBe(true)
    );
    // And the chip says where you are, with the way back.
    await screen.findByText(/Showing from/i);

    timelineCalls.length = 0;
    fireEvent.click(screen.getByText(/back to newest/i));
    await waitFor(() => expect(timelineCalls.length).toBeGreaterThan(0));
    // Newest-first again: no cursor at all on the fresh list's first page.
    expect(timelineCalls[0].beforeTakenAt).toBeUndefined();
    expect(timelineCalls[0].beforeId ?? undefined).toBeUndefined();
  });

  it("the rail's undated entry walks to the undated shelf", async () => {
    const seen = renderAt("/photos", { username: "member" });
    fireEvent.click(await screen.findByText("Undated"));
    await waitFor(() => expect(seen.pathname).toBe("/photos/undated"));
  });
});

describe("sharing one photograph", () => {
  // "Look at this one" is the most-sent link a family album has, and the lightbox used to be local
  // state that no URL could carry.

  it("opens the photograph a shared ?photo= link points at, over the view it points at", async () => {
    renderAt("/photos?photo=1", { username: "member" });

    // The lightbox is open on that asset…
    await waitFor(() => expect(assetCalls).toContain(1));
    expect(await screen.findByText("photo1.jpg")).toBeTruthy();
    // …and the view behind it is the one the path asked for, not a bare photo on an empty page.
    await waitFor(() => expect(timelineCalls.length).toBeGreaterThan(0));
  });

  it("carries the view too, so a link into an album opens there", async () => {
    renderAt("/photos/undated?photo=1", { username: "member" });
    await waitFor(() => expect(timelineCalls.some((c) => c.undated)).toBe(true));
    expect(await screen.findByText("photo1.jpg")).toBeTruthy();
  });

  it("puts the open photograph in the URL and takes it out again, without stacking history", async () => {
    const seen = renderAt("/photos", { username: "member" });
    await waitFor(() => expect(timelineCalls.length).toBeGreaterThan(0));
    const before = seen.entries;

    fireEvent.click(await screen.findByTitle("photo1.jpg"));
    await waitFor(() => expect(seen.search).toBe("?photo=1"));

    fireEvent.click(document.querySelector(".ant-modal-close"));
    await waitFor(() => expect(seen.search).toBe(""));

    // REPLACE, not PUSH: a lightbox is a look, not a place. Pushing would make Back walk out of a
    // browsing session one photograph at a time.
    expect(seen.action).toBe("REPLACE");
    expect(seen.entries).toBe(before);
  });

  it("a stale link to a photo that is gone just shows the view", async () => {
    missingAssets.add(404);
    const seen = renderAt("/photos?photo=404", { username: "member" });

    await waitFor(() => expect(assetCalls).toContain(404));
    // No apology, no toast, no dead modal — and the parameter is dropped so a reload does not retry.
    await waitFor(() => expect(seen.search).toBe(""));
    expect(screen.queryByText(/could not load this item/i)).toBeNull();
    await waitFor(() => expect(timelineCalls.length).toBeGreaterThan(0));
  });

  it("treats a photo hidden from this member exactly the same", async () => {
    // The server answers hidden-from-you with the identical 404 rather than a 403, so that following
    // a link can never reveal that there is something to be refused.
    missingAssets.add(7);
    const seen = renderAt("/photos?photo=7", { username: "member" });

    await waitFor(() => expect(seen.search).toBe(""));
    expect(screen.queryByText(/could not load this item/i)).toBeNull();
    expect(screen.queryByText(/hidden/i)).toBeNull();
  });

  it("never asks the server about a mangled photo id", async () => {
    renderAt("/photos?photo=abc", { username: "member" });
    await waitFor(() => expect(timelineCalls.length).toBeGreaterThan(0));
    expect(assetCalls).toHaveLength(0);
    expect(screen.queryByRole("dialog")).toBeNull();
  });

  it("ignores a photo id that is not a positive whole number", async () => {
    renderAt("/photos?photo=-3", { username: "member" });
    await waitFor(() => expect(timelineCalls.length).toBeGreaterThan(0));
    expect(assetCalls).toHaveLength(0);
  });
});

describe("an album page", () => {
  it("is headed by the album's own name, with the shelf above it", async () => {
    renderAt("/photos/albums/summer-1994", { username: "member" });

    // Re-queried each poll: the album plate's own <h1> is on screen first, and holding a reference to
    // that node would be asserting about a heading the page has already replaced.
    await waitFor(() => expect(screen.getByRole("heading", { level: 1 }).textContent).toBe("Summer 1994"));
    // "Albums" stays — as the eyebrow. An album is a place; the shelf it sits on is not its name.
    expect(screen.getByText("Albums")).toBeTruthy();
    // And the name is printed once, not twice.
    expect(screen.getAllByText("Summer 1994")).toHaveLength(2); // heading + breadcrumb
  });
});

describe("the album's index", () => {
  // The views the rail offers are decided by one pure function, so the rail and the page can never
  // disagree about whether a view exists. These are the gating rules the in-page tab strip used to
  // apply, asserted where they now live.
  const keys = (status, unnamedCount = 0) =>
    photosNavGroups(status, unnamedCount).flatMap((group) => group.views.map((view) => view.key));

  const base = {
    assets: 10, photos: 9, videos: 1, hidden: 0, undated: 0,
    people: 0, albums: 0, pendingDupeGroups: 0, empty: false, dataPlane: true,
  };

  it("always offers the five ways into the album", () => {
    expect(keys(base)).toEqual(["timeline", "browse", "albums", "folders", "people"]);
  });

  it("offers the undated shelf only when something is waiting for a date", () => {
    expect(keys({ ...base, undated: 3 })).toContain("undated");
    expect(keys(base)).not.toContain("undated");
  });

  it("offers review when a suggested-hide batch is waiting, and counts it", () => {
    const review = photosNavGroups({ ...base, pendingHideProposals: 2 }, 0)
      .flatMap((group) => group.views)
      .find((view) => view.key === "review");
    expect(review.count).toBe(2);
    expect(review.waiting).toBe(true);
  });

  it("keeps review out of the way when there is nothing to review", () => {
    expect(keys(base)).not.toContain("review");
  });

  it("keeps review reachable for an admin, who is the one who goes looking", () => {
    expect(keys({ ...base, admin: true })).toContain("review");
  });

  it("opens the tag queue on unnamed face clusters alone", () => {
    // Naming one is the highest-leverage action in the feature, so the queue appears for it even
    // with nothing else outstanding.
    expect(keys(base, 4)).toContain("tag");
    expect(keys(base, 0)).not.toContain("tag");
  });

  it("offers duplicate review only once the grouping pass has proposed something", () => {
    expect(keys({ ...base, pendingDupeGroups: 5 })).toContain("dupes");
    expect(keys(base)).not.toContain("dupes");
  });

  it("has nothing to offer before the gate has answered", () => {
    expect(photosNavGroups(null, 0)).toEqual([]);
  });
});
