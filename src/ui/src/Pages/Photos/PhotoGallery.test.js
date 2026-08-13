import { render, cleanup, screen, waitFor } from "@testing-library/react";
import { MemoryRouter, Route } from "react-router-dom";
import { vi, describe, it, expect, afterEach, beforeEach } from "vitest";

// The Gallery (docs/photos-plan.md §2.12) — the section the owner asked for when they said the art
// and meme piles "are not album material … put them in another section".
//
// What these pin down is the half of the feature that lives in the browser: the rail entry appearing
// only once there is a gallery to open, the index leading with artist collections, the album head
// choosing between two names, the plaque's filename cleanup, and the selection-bar action existing at
// all. The server-side exclusions are pinned in PhotoShelfTests.cs; nothing here restates them.

global.IS_REACT_ACT_ENVIRONMENT = true;
global.matchMedia = global.matchMedia || ((q) => ({
  matches: false, media: q, onchange: null,
  addListener: vi.fn(), removeListener: vi.fn(),
  addEventListener: vi.fn(), removeEventListener: vi.fn(), dispatchEvent: vi.fn(),
}));
global.ResizeObserver = global.ResizeObserver || class { observe() {} unobserve() {} disconnect() {} };
Object.defineProperty(HTMLElement.prototype, "clientWidth", { configurable: true, value: 1000 });

vi.mock("antd", async () => {
  const actual = await vi.importActual("antd");
  return {
    ...actual,
    message: { success: vi.fn(), error: vi.fn(), info: vi.fn(), warning: vi.fn(), loading: vi.fn() },
  };
});

let statusResponse;
let galleryAlbums;
let albumBySlug;
const shelfCalls = [];

const ok = (body) => Promise.resolve({ ok: true, status: 200, json: () => Promise.resolve(body) });

const card = (id, path) => ({
  id,
  path,
  kind: "Photo",
  width: 400,
  height: 300,
  takenAt: null,
  takenAtSource: "Unknown",
  shelf: "Archive",
  thumbState: "Ready",
  gridUrl: `https://gateway.example/s/tok${id}/PhotoThumb`,
});

vi.mock("../../MovieAPI", () => ({
  MovieAPI: {
    getPhotosStatus: () => statusResponse,
    getPhotoPeople: () => ok({ people: [], unnamed: [], dataPlane: true }),
    getPhotosTimeline: () => ok({ items: [], nextCursor: null, hasMore: false, dataPlane: true }),
    getPhotosFolder: () => ok({ path: "", folders: [], items: [], total: 0, skip: 0, hasMore: false, dataPlane: true }),
    getPhotoAlbums: () => ok({ albums: [], shelf: "Timeline", dataPlane: true }),
    getPhotoGallery: () => ok({ albums: galleryAlbums, shelf: "Archive", dataPlane: true }),
    getPhotoAlbum: (slug) => ok(albumBySlug[slug]),
    updatePhotoAlbum: () => ok({ album: {} }),
    createPhotoAlbum: () => ok({ album: { id: 9, title: "New", slug: "new" }, added: 0 }),
    addToPhotoAlbum: () => ok({ added: 0, total: 0 }),
    getPhotoAssetAlbums: () => ok({ albums: [] }),
    getPhotoAsset: (id) => ok({ card: card(id, "Misc Pics/x.jpg"), fileName: "x.jpg", folder: "Misc Pics" }),
    getPhotosHideProposals: () => ok({ configured: true, proposals: [] }),
    getPhotosIngestBatches: () => ok({ configured: true, groups: [], quarantineActive: true, quarantinedBatches: 0 }),
    setPhotosHidden: () => ok({ changed: 0 }),
    setPhotosShelf: (ids, shelf) => {
      shelfCalls.push({ ids, shelf });
      return ok({ requested: ids.length, matched: ids.length, changed: ids.length, groupMembersIncluded: 0, shelf });
    },
  },
}));

const PhotosPage = (await import("./PhotosPage")).default;
const { albumEyebrow, albumHeadline, albumSubtitle } = await import("./PhotosPage");
const { plaqueTitle } = await import("./PhotoGrid");
const { resetPhotosAlbum, photosNavGroups } = await import("../../hooks/usePhotosAlbum");

const populated = (extra = {}) => ok({
  assets: 40, photos: 40, videos: 0, missing: 0, hidden: 0, undated: 2,
  people: 0, albums: 1, archived: 8, archiveAlbums: 2, artistCollections: 1,
  pendingDupeGroups: 0, empty: false, dataPlane: true, ...extra,
});

const album = (slug, title, shelf, artistName, items = []) => ({
  album: {
    id: 1, title, slug, description: "", rangeStart: null, rangeEnd: null,
    sortOrder: 0, shelf, artistName,
  },
  items: items.map((path, i) => ({ entryId: i, sortOrder: i, caption: null, card: card(100 + i, path) })),
  total: items.length,
  skip: 0,
  hasMore: false,
  dataPlane: true,
});

function renderAt(route) {
  const seen = { pathname: route };
  render(
    <MemoryRouter initialEntries={[route]}>
      <PhotosPage userData={{ username: "member" }} />
      <Route path="*" render={({ location }) => { seen.pathname = location.pathname; return null; }} />
    </MemoryRouter>
  );
  return seen;
}

beforeEach(() => {
  shelfCalls.length = 0;
  statusResponse = populated();
  galleryAlbums = [
    { id: 2, title: "Beksinski", slug: "beksinski", artistName: "Beksinski", shelf: "Archive", count: 153, coverUrl: null },
    { id: 3, title: "Brom collection", slug: "brom", artistName: "Brom", shelf: "Archive", count: 261, coverUrl: null },
    { id: 4, title: "SA Misc", slug: "sa-misc", artistName: null, shelf: "Archive", count: 1608, coverUrl: null },
  ];
  albumBySlug = {
    beksinski: album("beksinski", "Beksinski", "Archive", "Beksinski", ["Misc Pics/Misc Pics/Beksinski/a_dark_room.jpg"]),
    "sa-misc": album("sa-misc", "SA Misc", "Archive", null, ["Misc Pics/SAMisc/x.jpg"]),
    wedding: album("wedding", "Wedding", "Timeline", null, ["Wedding/w1.jpg"]),
  };
  resetPhotosAlbum();
});

afterEach(cleanup);

// ── The rail ──────────────────────────────────────────────────────────────────────────────────

describe("the gallery's place in the rail", () => {
  it("is offered once there is a gallery, with its count", () => {
    const browse = photosNavGroups({ assets: 40, albums: 1, archiveAlbums: 2, people: 0 })[0];
    const gallery = browse.views.find((v) => v.key === "gallery");
    expect(gallery).toBeTruthy();
    expect(gallery.path).toBe("/photos/gallery");
    expect(gallery.count).toBe(2);
    // It sits under "The album" beside the family index, not in "Waiting on you": nothing here is
    // waiting for a decision — it is a place to browse.
    expect(browse.key).toBe("browse");
  });

  it("is absent when nothing has been filed there", () => {
    // The default state of this section, on a site where most families will never file anything as
    // art. A rail entry that leads to an empty room is worse than no entry.
    const browse = photosNavGroups({ assets: 40, albums: 1, archiveAlbums: 0, people: 0 })[0];
    expect(browse.views.some((v) => v.key === "gallery")).toBe(false);
  });

  it("does not put gallery collections on the family album index", () => {
    // Two counts that ADD UP to the album table rather than double-counting part of it.
    const browse = photosNavGroups({ assets: 40, albums: 1, archiveAlbums: 2, people: 0 })[0];
    expect(browse.views.find((v) => v.key === "albums").count).toBe(1);
  });
});

// ── The index ─────────────────────────────────────────────────────────────────────────────────

describe("the gallery index", () => {
  it("renders artist collections above the plain ones", async () => {
    renderAt("/photos/gallery");

    await waitFor(() => expect(screen.getByText("Artists")).toBeTruthy());
    expect(screen.getByText("Collections")).toBeTruthy();

    // The artist's NAME leads the card, and the collection's own title drops beneath it only where
    // the two differ — "Beksinski" titled "Beksinski" is not announced twice.
    const cards = document.querySelectorAll(".photo-album-card");
    expect(cards.length).toBe(3);
    expect(cards[0].classList.contains("is-artist")).toBe(true);
    expect(cards[0].textContent).toContain("Beksinski");
    expect(cards[0].querySelector(".photo-album-card-sub")).toBeNull();
    expect(cards[1].querySelector(".photo-album-card-sub").textContent).toBe("Brom collection");
    // …and the plain pile is not dressed as one.
    expect(cards[2].classList.contains("is-artist")).toBe(false);
  });

  it("does not draw the Collections heading when there is nothing to distinguish it from", async () => {
    galleryAlbums = [galleryAlbums[2]];
    renderAt("/photos/gallery");

    await waitFor(() => expect(screen.getByText("SA Misc")).toBeTruthy());
    expect(screen.queryByText("Artists")).toBeNull();
    expect(screen.queryByText("Collections")).toBeNull();
  });

  it("lights the page as a gallery", async () => {
    renderAt("/photos/gallery");
    await waitFor(() => expect(document.querySelector(".photos-page--gallery")).toBeTruthy());
  });
});

// ── The album head ────────────────────────────────────────────────────────────────────────────

describe("an album's three lines", () => {
  it("names the shelf in the eyebrow", () => {
    expect(albumEyebrow(null, null)).toBe("Family album");
    expect(albumEyebrow("wedding", { shelf: "Timeline" })).toBe("Albums");
    expect(albumEyebrow("sa-misc", { shelf: "Archive" })).toBe("Gallery");
    expect(albumEyebrow("beksinski", { shelf: "Archive", artistName: "Beksinski" })).toBe("Gallery · Artist");
  });

  it("puts the artist in the headline and the album title beneath — or nothing, when they agree", () => {
    const artist = { shelf: "Archive", artistName: "Brom" };
    expect(albumHeadline("Brom collection", artist)).toBe("Brom");
    expect(albumSubtitle("Brom collection", artist)).toBe("Brom collection");

    const same = { shelf: "Archive", artistName: "Beksinski" };
    expect(albumHeadline("Beksinski", same)).toBe("Beksinski");
    expect(albumSubtitle("Beksinski", same)).toBeNull();

    // A plain album is untouched by any of it.
    expect(albumHeadline("Wedding", { shelf: "Timeline" })).toBe("Wedding");
    expect(albumSubtitle("Wedding", { shelf: "Timeline" })).toBeNull();
  });

  it("draws an artist collection's page as a wall and a plain album's as an album", async () => {
    renderAt("/photos/albums/beksinski");
    await waitFor(() => expect(screen.getByText("Gallery · Artist")).toBeTruthy());
    expect(document.querySelector(".photo-grid--gallery")).toBeTruthy();
    expect(document.querySelector(".photos-page--gallery")).toBeTruthy();
    // The plaque: a filename made presentable, and the artist beside it.
    await waitFor(() => expect(document.querySelector(".photo-tile-plaque")).toBeTruthy());
    expect(document.querySelector(".photo-tile-plaque-title").textContent).toBe("a dark room");
    expect(document.querySelector(".photo-tile-plaque-artist").textContent).toBe("Beksinski");

    cleanup();
    resetPhotosAlbum();
    renderAt("/photos/albums/wedding");
    await waitFor(() => expect(screen.getByText("Albums")).toBeTruthy());
    // A family album keeps the dense contact-sheet packing and gets no plaques.
    expect(document.querySelector(".photo-grid--gallery")).toBeNull();
    expect(document.querySelector(".photo-tile-plaque")).toBeNull();
  });

  it("keeps the ordinary album URL for a gallery collection", async () => {
    // A deep link minted before any of this existed resolves exactly as it did.
    const seen = renderAt("/photos/albums/sa-misc");
    await waitFor(() => expect(screen.getByText("Gallery")).toBeTruthy());
    expect(seen.pathname).toBe("/photos/albums/sa-misc");
  });
});

// ── The plaque's one decision ─────────────────────────────────────────────────────────────────

describe("plaqueTitle", () => {
  it("makes a filename presentable without inventing a title", () => {
    expect(plaqueTitle("a_dark_room.jpg")).toBe("a dark room");
    expect(plaqueTitle("untitled-07.png")).toBe("untitled 07");
    expect(plaqueTitle("no extension")).toBe("no extension");
    // Deliberately NOT title-cased or otherwise "improved": these names came off the internet and out
    // of scanners, and rewriting one would be inventing a title nobody recorded.
    expect(plaqueTitle("THE_TROUT.JPG")).toBe("THE TROUT");
    // A name that is nothing but an extension has no presentable form; the filename stands.
    expect(plaqueTitle(".jpg")).toBe(".jpg");
    expect(plaqueTitle("")).toBe("");
  });
});

// ── The member action ─────────────────────────────────────────────────────────────────────────

describe("sending photos to the gallery", () => {
  it("offers both directions in selection mode and asks the server for the named shelf", async () => {
    const { default: PhotoSelectionBar } = await import("./PhotoSelectionBar");
    render(<PhotoSelectionBar ids={[4, 5]} onChanged={() => {}} onClear={() => {}} />);

    const send = screen.getByText("Send to gallery");
    const back = screen.getByText("Return to timeline");
    expect(send).toBeTruthy();
    expect(back).toBeTruthy();

    send.click();
    await waitFor(() => expect(shelfCalls.length).toBe(1));
    expect(shelfCalls[0]).toEqual({ ids: [4, 5], shelf: "Archive" });

    back.click();
    await waitFor(() => expect(shelfCalls.length).toBe(2));
    expect(shelfCalls[1].shelf).toBe("Timeline");
  });
});
