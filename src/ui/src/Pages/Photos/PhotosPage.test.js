import { render, cleanup, screen, waitFor, fireEvent } from "@testing-library/react";
import { vi, describe, it, expect, afterEach, beforeEach } from "vitest";

// Page-level tests for /photos (docs/photos-plan.md §4). What these pin down is the loop the unit
// tests beside them cannot: the gate's answer being RENDERED rather than assumed, the undated shelf
// staying a separate request, and the folder tree paging by prefix.

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

const populated = (extra = {}) => ok({
  assets: 10, photos: 9, videos: 1, missing: 0, hidden: 0, undated: 3,
  people: 0, albums: 0, pendingDupeGroups: 0, empty: false, dataPlane: true, ...extra,
});

beforeEach(() => {
  timelineCalls.length = 0;
  folderCalls.length = 0;
  hideCalls.length = 0;
  assetCalls.length = 0;
  saveShowHiddenPhotos(false);
  statusResponse = populated();
  peopleResponse = ok({ people: [], unnamed: [], dataPlane: true });
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
    render(<PhotosPage userData={{ username: "someone" }} />);
    await screen.findByText(/limited to family members/i);
    // And it says nothing about what is inside.
    expect(screen.queryByText(/photos$/i)).not.toBeNull();
    expect(timelineCalls).toHaveLength(0);
  });

  it("treats an anonymous 401 the same as a refused 403", async () => {
    statusResponse = Promise.resolve({ ok: false, status: 401, json: () => Promise.resolve({}) });
    render(<PhotosPage userData={{}} />);
    await screen.findByText(/limited to family members/i);
  });

  it("says the collection has not been read in yet when it is empty", async () => {
    statusResponse = ok({ assets: 0, photos: 0, videos: 0, missing: 0, hidden: 0, undated: 0, people: 0, albums: 0, pendingDupeGroups: 0, empty: true, dataPlane: true });
    render(<PhotosPage userData={{ username: "member" }} />);
    await screen.findByText(/has not been read in yet/i);
    expect(timelineCalls).toHaveLength(0);
  });
});

describe("PhotosPage browse", () => {
  it("loads the dated timeline first and does not ask for undated items", async () => {
    render(<PhotosPage userData={{ username: "member" }} />);
    await waitFor(() => expect(timelineCalls.length).toBeGreaterThan(0));
    expect(timelineCalls[0].undated).toBeFalsy();
  });

  it("the undated shelf is a SEPARATE request, never interleaved", async () => {
    // §2.7: date-unknown items get their own shelf rather than being scattered into the timeline.
    render(<PhotosPage userData={{ username: "member" }} />);
    await waitFor(() => expect(timelineCalls.length).toBeGreaterThan(0));

    fireEvent.click(screen.getByText("Undated"));
    await waitFor(() => expect(timelineCalls.some((c) => c.undated)).toBe(true));
    // Two distinct streams, each with its own cursor — no request ever asks for both.
    expect(timelineCalls.every((c) => typeof c.undated === "boolean" || c.undated === undefined)).toBe(true);
    expect(timelineCalls.filter((c) => c.undated).length).toBeGreaterThan(0);
    expect(timelineCalls.filter((c) => !c.undated).length).toBeGreaterThan(0);
  });

  it("hides the undated shelf when nothing is waiting for a date", async () => {
    statusResponse = populated({ undated: 0 });
    render(<PhotosPage userData={{ username: "member" }} />);
    await waitFor(() => expect(timelineCalls.length).toBeGreaterThan(0));
    expect(screen.queryByText("Undated")).toBeNull();
  });

  it("browses the folder tree by prefix", async () => {
    render(<PhotosPage userData={{ username: "member" }} />);
    await waitFor(() => expect(timelineCalls.length).toBeGreaterThan(0));

    fireEvent.click(screen.getByText("Folders"));
    const folder = await screen.findByText("Vacation 2004");
    fireEvent.click(folder);

    await waitFor(() => expect(folderCalls.some((c) => c.path === "Vacation 2004")).toBe(true));
  });

  it("no longer offers a show-hidden toggle in the page (Phase 4 moved it to the navbar, admin-only)", async () => {
    // Phase 2 put this switch here and let any member flip it. The owner's decision supersedes that:
    // hiding stays member work, but SEEING the hidden pile is admin work, and the switch that reveals
    // it lives in the navbar. Only the Select switch is left here.
    render(<PhotosPage userData={{ username: "member" }} />);
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
    render(<PhotosPage userData={{ username: "operator" }} />);
    await waitFor(() => expect(timelineCalls.some((c) => c.includeHidden)).toBe(true));
    expect(await screen.findByText(/Showing hidden photos/i)).toBeTruthy();
  });

  it("selection mode turns a card click into a batch hide", async () => {
    render(<PhotosPage userData={{ username: "member" }} />);
    await waitFor(() => expect(timelineCalls.length).toBeGreaterThan(0));

    fireEvent.click(screen.getAllByRole("switch")[0]); // Select — the only switch left in the toolbar
    fireEvent.click(await screen.findByTitle("photo1.jpg"));
    fireEvent.click(await screen.findByText("Hide"));

    await waitFor(() => expect(hideCalls).toHaveLength(1));
    expect(hideCalls[0]).toEqual({ ids: [1], hidden: true });
  });

  it("offers the review tab when a suggested-hide batch is waiting", async () => {
    statusResponse = populated({ pendingHideProposals: 2 });
    render(<PhotosPage userData={{ username: "member" }} />);
    expect(await screen.findByText("Review (2)")).toBeTruthy();
  });

  it("keeps the review tab out of the way when there is nothing to review", async () => {
    render(<PhotosPage userData={{ username: "member" }} />);
    await waitFor(() => expect(timelineCalls.length).toBeGreaterThan(0));
    expect(screen.queryByText(/^Review/)).toBeNull();
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
    render(<PhotosPage userData={{ username: "member" }} />);
    await waitFor(() => expect(timelineCalls.length).toBeGreaterThan(0));

    fireEvent.click(screen.getByText("People"));
    fireEvent.click(await screen.findByText("Subject A"));
    fireEvent.click(await screen.findByTitle("photo77.jpg"));

    await waitFor(() => expect(assetCalls).toContain(77));
    // Stated separately: a card object also "contains" nothing, and toContain(77) alone would pass
    // for a stringified id. What the lightbox needs is the number.
    expect(assetCalls.every((id) => typeof id === "number")).toBe(true);
  });

  it("says so plainly when the image gateway is not configured", async () => {
    statusResponse = populated({ dataPlane: false });
    render(<PhotosPage userData={{ username: "member" }} />);
    await screen.findByText(/image gateway is not configured/i);
  });
});
