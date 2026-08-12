import { render, cleanup, screen, waitFor, fireEvent } from "@testing-library/react";
import { vi, describe, it, expect, afterEach, beforeEach } from "vitest";

// Phase 2 curation UI (docs/photos-plan.md §2.9): selection mode, the batch actions, albums and the
// review surface. What these pin down is the promise the UI makes about what a click DOES — that
// selecting is a mode rather than a modifier-key guess, that a partial reorder sends only what was
// picked, and that the review surface's accept is the ONLY thing that ever applies a proposal.

global.IS_REACT_ACT_ENVIRONMENT = true;
global.matchMedia = global.matchMedia || ((q) => ({
  matches: false, media: q, onchange: null,
  addListener: vi.fn(), removeListener: vi.fn(),
  addEventListener: vi.fn(), removeEventListener: vi.fn(), dispatchEvent: vi.fn(),
}));
global.ResizeObserver = global.ResizeObserver || class { observe() {} unobserve() {} disconnect() {} };
// The justified grid lays out from a measured container width; jsdom reports zero for every element,
// which would render an empty grid and make every assertion below vacuously pass.
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

const calls = {
  hide: [],
  reorder: [],
  createAlbum: [],
  addToAlbum: [],
  remove: [],
  decide: [],
  approve: [],
};

const ok = (body) => Promise.resolve({ ok: true, status: 200, json: () => Promise.resolve(body) });

const card = (id, extra = {}) => ({
  id,
  path: `Folder/photo${id}.jpg`,
  kind: "Photo",
  width: 400,
  height: 300,
  takenAt: "2014-03-12T10:15:30",
  takenAtSource: "Exif",
  thumbState: "Ready",
  hidden: false,
  gridUrl: `https://gateway.example/s/tok${id}/PhotoThumb`,
  ...extra,
});

let albumsBody;
let albumBody;
let proposalsBody;
let batchesBody;

vi.mock("../../MovieAPI", () => ({
  MovieAPI: {
    setPhotosHidden: (ids, hidden) => {
      calls.hide.push({ ids, hidden });
      return ok({ requested: ids.length, matched: ids.length, changed: ids.length, hidden });
    },
    getPhotoAlbums: () => ok(albumsBody),
    getPhotoAlbum: () => ok(albumBody),
    createPhotoAlbum: (body) => {
      calls.createAlbum.push(body);
      return ok({ album: { id: 9, title: body.title, slug: "new-album" }, added: body.assetIds?.length ?? 0 });
    },
    addToPhotoAlbum: (id, body) => {
      calls.addToAlbum.push({ id, ...body });
      return ok({ added: body.assetIds?.length ?? 0, total: 5 });
    },
    removeFromPhotoAlbum: (id, assetIds) => {
      calls.remove.push({ id, assetIds });
      return ok({ removed: assetIds.length, total: 0 });
    },
    reorderPhotoAlbum: (id, assetIds) => {
      calls.reorder.push({ id, assetIds });
      return ok({ ordered: 3, ignored: 0, total: 3 });
    },
    updatePhotoAlbum: () => ok({ album: albumBody.album }),
    deletePhotoAlbum: () => ok({ deleted: true, entriesRemoved: 0 }),
    getPhotosHideProposals: () => ok(proposalsBody),
    decidePhotosHideProposal: (batchId, decision) => {
      calls.decide.push({ batchId, decision });
      return ok({ batchId, status: decision === "accept" ? "accepted" : "rejected", applied: decision === "accept" ? 12 : 0 });
    },
    getPhotosIngestBatches: () => ok(batchesBody),
    approvePhotosIngestBatches: (body) => {
      calls.approve.push(body);
      return ok({ approved: 1, batches: 1 });
    },
  },
}));

const PhotoGrid = (await import("./PhotoGrid")).default;
const PhotoSelectionBar = (await import("./PhotoSelectionBar")).default;
const PhotoAlbums = (await import("./PhotoAlbums")).default;
const PhotoAlbumDetail = (await import("./PhotoAlbumDetail")).default;
const PhotoReview = (await import("./PhotoReview")).default;

beforeEach(() => {
  Object.keys(calls).forEach((k) => (calls[k].length = 0));
  albumsBody = {
    albums: [
      { id: 1, title: "The Trip", slug: "the-trip", count: 12, coverUrl: null, rangeStart: "2015-08-01T00:00:00" },
    ],
    dataPlane: true,
  };
  albumBody = {
    album: { id: 1, title: "The Trip", slug: "the-trip", description: null, rangeStart: null, rangeEnd: null },
    items: [
      { entryId: 11, sortOrder: 0, caption: null, card: card(1) },
      { entryId: 12, sortOrder: 1, caption: null, card: card(2) },
      { entryId: 13, sortOrder: 2, caption: null, card: card(3) },
    ],
    total: 3,
    skip: 0,
    hasMore: false,
    dataPlane: true,
  };
  proposalsBody = {
    configured: true,
    proposals: [
      {
        batchId: "hide-20260812-100000",
        createdUtc: "2026-08-12T10:00:00Z",
        status: "pending",
        complete: true,
        count: 12,
        rules: { "screenshot-folder": 10, "tiny-image": 2 },
        samplePaths: ["Screenshots/one.jpg"],
      },
    ],
  };
  batchesBody = {
    configured: true,
    quarantineActive: true,
    quarantinedBatches: 3,
    groups: [
      {
        groupKey: "photos-20260812",
        batchIds: ["photos-20260812-100000", "photos-20260812-101500", "photos-20260812-103000"],
        count: 420,
        firstSeenUtc: "2026-08-12T10:00:00Z",
        approved: false,
      },
      { groupKey: "photos-20260101", batchIds: ["photos-20260101-000000"], count: 9, firstSeenUtc: "2026-01-01T00:00:00Z", approved: true },
    ],
  };
});

afterEach(async () => {
  cleanup();
  // Let the in-flight fetch promises and antd's toast render settle INSIDE the test's lifetime.
  // Without this they resolve during teardown, where their console output has nowhere to go and
  // vitest reports it as an unhandled error.
  await new Promise((resolve) => setTimeout(resolve, 0));
});

describe("selection mode", () => {
  const selectionFor = (state) => ({
    active: true,
    has: (id) => state.ids.includes(id),
    toggle: (id) => state.ids.push(id),
  });

  it("a click SELECTS instead of opening once selection mode is on", async () => {
    const state = { ids: [] };
    const onOpen = vi.fn();
    render(<PhotoGrid items={[card(1), card(2)]} groupBySection={false} onOpen={onOpen} selection={selectionFor(state)} />);

    // The grid places rows on an animation frame once it has measured its width.
    fireEvent.click(await screen.findByTitle("photo1.jpg"));
    expect(state.ids).toEqual([1]);
    // One mode, no modifier keys to discover: selecting never also opens the lightbox.
    expect(onOpen).not.toHaveBeenCalled();
  });

  it("opens the lightbox when selection mode is off", async () => {
    const onOpen = vi.fn();
    render(<PhotoGrid items={[card(1)]} groupBySection={false} onOpen={onOpen} />);
    fireEvent.click(await screen.findByTitle("photo1.jpg"));
    expect(onOpen).toHaveBeenCalledTimes(1);
  });

  it("badges a collapsed duplicate instead of hiding it from the folder view", async () => {
    // §2.6: a copy the timeline collapses is still on disk and still in the folder view. The badge is
    // how the folder view says "this is one of three" without ever suggesting a file went missing.
    render(
      <PhotoGrid
        items={[card(1, { group: { id: 7, kind: "Exact", status: "Pending", size: 3, isMaster: false, collapsed: true } })]}
        groupBySection={false}
      />
    );
    const badge = await screen.findByText("×3");
    expect(badge.className).toContain("collapsed");
    expect(badge.getAttribute("title")).toMatch(/another one represents it/);
  });

  it("badges a hidden photo instead of dimming it away", async () => {
    // §2.9: the folder view shows hidden items. A hidden photo is still on disk, so the mark must
    // read as curation rather than as damage.
    render(<PhotoGrid items={[card(1, { hidden: true })]} groupBySection={false} />);
    expect(await screen.findByText("hidden")).toBeTruthy();
  });

  it("the batch action hides the selection and never deletes anything", async () => {
    const onChanged = vi.fn();
    render(<PhotoSelectionBar ids={[4, 5, 6]} onChanged={onChanged} onClear={() => {}} />);

    fireEvent.click(screen.getByText("Hide"));
    await waitFor(() => expect(calls.hide).toHaveLength(1));
    expect(calls.hide[0]).toEqual({ ids: [4, 5, 6], hidden: true });
    // There is no delete action in this bar, because there is no delete in this vertical.
    expect(screen.queryByText(/delete/i)).toBeNull();
    await waitFor(() => expect(onChanged).toHaveBeenCalled());
  });

  it("unhide is the same edit in the other direction", async () => {
    render(<PhotoSelectionBar ids={[4]} onChanged={() => {}} onClear={() => {}} />);
    fireEvent.click(screen.getByText("Unhide"));
    await waitFor(() => expect(calls.hide[0]).toEqual({ ids: [4], hidden: false }));
  });

  it("creates an album from the selection", async () => {
    render(<PhotoSelectionBar ids={[7, 8]} onChanged={() => {}} onClear={() => {}} />);
    fireEvent.click(screen.getByText("Add to album"));

    const input = await screen.findByPlaceholderText("New album title");
    fireEvent.change(input, { target: { value: "Beach Day" } });
    fireEvent.click(screen.getByText("Create"));

    await waitFor(() => expect(calls.createAlbum).toHaveLength(1));
    expect(calls.createAlbum[0]).toMatchObject({ title: "Beach Day", assetIds: [7, 8] });
  });

  it("adds the selection to an existing album", async () => {
    render(<PhotoSelectionBar ids={[7]} onChanged={() => {}} onClear={() => {}} />);
    fireEvent.click(screen.getByText("Add to album"));

    fireEvent.click(await screen.findByText("The Trip"));
    await waitFor(() => expect(calls.addToAlbum).toHaveLength(1));
    expect(calls.addToAlbum[0]).toMatchObject({ id: 1, assetIds: [7] });
  });
});

describe("albums", () => {
  it("lists albums with their counts and hand-set range", async () => {
    render(<PhotoAlbums onOpenAlbum={() => {}} />);
    await screen.findByText("The Trip");
    expect(screen.getByText(/12 items/)).toBeTruthy();
    expect(screen.getByText(/2015-08-01/)).toBeTruthy();
  });

  it("creates an empty album from the index", async () => {
    render(<PhotoAlbums onOpenAlbum={() => {}} />);
    await screen.findByText("The Trip");

    fireEvent.change(screen.getByPlaceholderText("New album title"), { target: { value: "Christmas" } });
    fireEvent.click(screen.getByText("Create album"));
    await waitFor(() => expect(calls.createAlbum).toHaveLength(1));
    expect(calls.createAlbum[0].title).toBe("Christmas");
    // The slug is minted server-side; nothing here invents a URL.
    expect(calls.createAlbum[0].slug).toBeUndefined();
  });

  it("sends a PARTIAL reorder — only the cards that were picked", async () => {
    render(<PhotoAlbumDetail slug="the-trip" onBack={() => {}} onOpen={() => {}} />);
    // Selecting inside an album is what reordering is expressed through: pick, then move.
    fireEvent.click(await screen.findByText("Select"));
    fireEvent.click(await screen.findByTitle("photo3.jpg"));
    fireEvent.click(await screen.findByText("Move to front"));

    await waitFor(() => expect(calls.reorder).toHaveLength(1));
    expect(calls.reorder[0].assetIds).toEqual([3]);
  });

  it("removes photos from an album without touching the photos", async () => {
    render(<PhotoAlbumDetail slug="the-trip" onBack={() => {}} onOpen={() => {}} />);
    fireEvent.click(await screen.findByText("Select"));
    fireEvent.click(await screen.findByTitle("photo1.jpg"));
    fireEvent.click(await screen.findByText("Remove from album"));

    await waitFor(() => expect(calls.remove).toHaveLength(1));
    expect(calls.remove[0]).toEqual({ id: 1, assetIds: [1] });
  });

  it("says plainly that deleting an album leaves the photos alone", async () => {
    render(<PhotoAlbumDetail slug="the-trip" onBack={() => {}} onOpen={() => {}} />);
    fireEvent.click(await screen.findByText("Delete album"));
    // A confirmation, and one that tells the truth about what is at risk (nothing on disk).
    expect(await screen.findByText(/photos stay exactly where they are/i)).toBeTruthy();
  });
});

describe("review surface", () => {
  it("shows a proposal by RULE and count rather than ten thousand file names", async () => {
    render(<PhotoReview admin={false} onChanged={() => {}} />);
    await screen.findByText("hide-20260812-100000");
    expect(screen.getByText("screenshot-folder")).toBeTruthy();
    expect(screen.getByText("10")).toBeTruthy();
    expect(screen.getByText("12 photos")).toBeTruthy();
  });

  it("accepting the batch is the only thing that applies it", async () => {
    render(<PhotoReview admin={false} onChanged={() => {}} />);
    fireEvent.click(await screen.findByText("Hide all of these"));
    await waitFor(() => expect(calls.decide).toHaveLength(1));
    expect(calls.decide[0]).toEqual({ batchId: "hide-20260812-100000", decision: "accept" });
  });

  it("rejecting hides nothing", async () => {
    render(<PhotoReview admin={false} onChanged={() => {}} />);
    fireEvent.click(await screen.findByText("Reject"));
    await waitFor(() => expect(calls.decide).toHaveLength(1));
    expect(calls.decide[0].decision).toBe("reject");
    expect(calls.hide).toHaveLength(0);
  });

  it("keeps the ingest-batch list to admins", async () => {
    render(<PhotoReview admin={false} onChanged={() => {}} />);
    await screen.findByText("hide-20260812-100000");
    expect(screen.queryByText("New ingests")).toBeNull();
  });

  it("groups a chunked walk's markers into one thing to approve", async () => {
    render(<PhotoReview admin onChanged={() => {}} />);
    await screen.findByText("New ingests");

    // Three markers from one night's chunked walk, reviewed as the single ingest it actually was.
    expect(screen.getByText("photos-20260812")).toBeTruthy();
    expect(screen.getByText(/3 batch markers/)).toBeTruthy();
    // An already-approved group is not offered again.
    expect(screen.queryByText("photos-20260101")).toBeNull();

    fireEvent.click(screen.getByText("Approve into the timeline"));
    await waitFor(() => expect(calls.approve).toHaveLength(1));
    expect(calls.approve[0]).toEqual({ groupKey: "photos-20260812", batchIds: undefined });
  });

  it("says so when the unreviewed backlog turned quarantine off", async () => {
    batchesBody.quarantineActive = false;
    batchesBody.quarantinedBatches = 900;
    render(<PhotoReview admin onChanged={() => {}} />);
    expect(await screen.findByText(/Too many unreviewed ingests/)).toBeTruthy();
  });
});
