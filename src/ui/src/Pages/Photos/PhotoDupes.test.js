import { render, cleanup, screen, waitFor, fireEvent } from "@testing-library/react";
import { vi, describe, it, expect, afterEach, beforeEach } from "vitest";

// Phase 3 duplicate review UI (docs/photos-plan.md §2.6). What these pin down is the promise the
// surface makes: that a decision is a deliberate act on a named copy, that the two panes are compared
// under ONE transform (a shared zoom is the whole point of side-by-side), that the keyboard can drive
// it, and that "these are different photos" writes a rejection rather than quietly skipping — a skip
// would let the grouping pass propose the same pair forever.

global.IS_REACT_ACT_ENVIRONMENT = true;
global.matchMedia = global.matchMedia || ((q) => ({
  matches: false, media: q, onchange: null,
  addListener: vi.fn(), removeListener: vi.fn(),
  addEventListener: vi.fn(), removeEventListener: vi.fn(), dispatchEvent: vi.fn(),
}));
global.ResizeObserver = global.ResizeObserver || class { observe() {} unobserve() {} disconnect() {} };

vi.mock("antd", async () => {
  const actual = await vi.importActual("antd");
  return {
    ...actual,
    message: { success: vi.fn(), error: vi.fn(), info: vi.fn(), warning: vi.fn(), loading: vi.fn() },
  };
});

const calls = { resolve: [], reject: [], list: [] };
const ok = (body) => Promise.resolve({ ok: true, status: 200, json: () => Promise.resolve(body) });

const member = (id, overrides = {}) => ({
  card: { id, path: `Vacation/photo${id}.jpg`, kind: "Photo", width: 1600, height: 1200, thumbState: "Ready" },
  isMaster: false,
  similarity: 0.94,
  fileName: `photo${id}.jpg`,
  folder: "Vacation",
  format: "JPG",
  sizeBytes: 2 * 1048576,
  width: 1600,
  height: 1200,
  takenAt: "2011-07-04T10:00:00",
  viewUrl: `https://gateway.example/s/tok${id}/PhotoThumb`,
  ...overrides,
});

let groupsBody;
// What the SECOND and later list calls answer, when a test sets it — the next page of a queue that is
// longer than one page.
let nextGroupsBody;

vi.mock("../../MovieAPI", () => ({
  MovieAPI: {
    getPhotoDupeGroups: (args) => {
      calls.list.push(args);
      return ok(calls.list.length > 1 && nextGroupsBody ? nextGroupsBody : groupsBody);
    },
    resolvePhotoDupeGroup: (id, masterAssetId) => {
      calls.resolve.push({ id, masterAssetId });
      return ok({ id, status: "Resolved", masterAssetId, collapsed: 1 });
    },
    rejectPhotoDupeGroup: (id) => {
      calls.reject.push({ id });
      return ok({ id, status: "Rejected", members: 2 });
    },
  },
}));

const PhotoDupes = (await import("./PhotoDupes")).default;

beforeEach(() => {
  Object.keys(calls).forEach((k) => (calls[k].length = 0));
  nextGroupsBody = null;
  groupsBody = {
    total: 2,
    skip: 0,
    hasMore: false,
    dataPlane: true,
    groups: [
      {
        id: 41,
        kind: "Near",
        status: "Pending",
        members: [member(1, { isMaster: true }), member(2, { folder: "Phone Backup", sizeBytes: 1048576 })],
      },
      {
        id: 42,
        kind: "Exact",
        status: "Pending",
        members: [member(3, { isMaster: true, similarity: null }), member(4, { similarity: null })],
      },
    ],
  };
});

afterEach(cleanup);

describe("the duplicate review surface", () => {
  it("asks only for the groups a human still has to settle", async () => {
    render(<PhotoDupes />);
    await waitFor(() => expect(calls.list.length).toBe(1));
    // Variant pairs are settled by the pass and are never listed; the server enforces that, and the
    // client must not be the thing asking for them.
    expect(calls.list[0].status).toBe("pending");
  });

  it("shows each copy's folder, size and format — the merge-needed folders' whole story", async () => {
    render(<PhotoDupes />);
    await screen.findByText("photo1.jpg");

    expect(screen.getByText("photo2.jpg")).toBeTruthy();
    expect(screen.getByText("Phone Backup")).toBeTruthy();
    expect(screen.getAllByText(/1600 × 1200/).length).toBeGreaterThan(0);
    expect(screen.getAllByText(/JPG/).length).toBeGreaterThan(0);
    expect(screen.getAllByText(/94% match/).length).toBeGreaterThan(0);
  });

  it("zooms and pans BOTH copies from one transform", async () => {
    const { container } = render(<PhotoDupes />);
    await screen.findByText("photo1.jpg");

    const before = Array.from(container.querySelectorAll(".photo-dupes-stage img")).map((i) => i.style.transform);
    expect(new Set(before).size).toBe(1);

    fireEvent.click(screen.getByText("Zoom in"));
    const after = Array.from(container.querySelectorAll(".photo-dupes-stage img")).map((i) => i.style.transform);
    // Both panes moved, and they moved to the SAME place: two images at different zooms cannot be
    // compared, which is the failure this assertion exists to catch.
    expect(new Set(after).size).toBe(1);
    expect(after[0]).not.toBe(before[0]);
    expect(after[0]).toContain("scale(1.5)");
  });

  it("picking a copy names it as the master", async () => {
    render(<PhotoDupes />);
    await screen.findByText("photo1.jpg");

    fireEvent.click(screen.getAllByText("Keep this one")[1]);
    await waitFor(() => expect(calls.resolve.length).toBe(1));
    expect(calls.resolve[0]).toEqual({ id: 41, masterAssetId: 2 });
    expect(calls.reject.length).toBe(0);
  });

  it("the keyboard can choose a copy and settle the group", async () => {
    render(<PhotoDupes />);
    await screen.findByText("photo1.jpg");

    fireEvent.keyDown(window, { key: "ArrowRight" });
    fireEvent.keyDown(window, { key: "Enter" });
    await waitFor(() => expect(calls.resolve.length).toBe(1));
    expect(calls.resolve[0].masterAssetId).toBe(2);
  });

  it("marking them different writes a rejection rather than skipping", async () => {
    render(<PhotoDupes />);
    await screen.findByText("photo1.jpg");

    fireEvent.click(screen.getByText("These are different photos"));
    await waitFor(() => expect(calls.reject.length).toBe(1));
    // The rejection is the record that stops the pass re-proposing the pair. A UI that merely advanced
    // to the next group would leave the same question waiting on the next run.
    expect(calls.reject[0]).toEqual({ id: 41 });
    expect(calls.resolve.length).toBe(0);
  });

  it("a decided group leaves the queue without losing the reviewer's place", async () => {
    render(<PhotoDupes />);
    await screen.findByText("Group 1 of 2");

    fireEvent.click(screen.getByText("These are different photos"));
    await screen.findByText("Group 1 of 1");
    // The next group is now in front of them, ready.
    expect(screen.getByText("photo3.jpg")).toBeTruthy();
  });

  it("says plainly that nothing on disk changes", async () => {
    render(<PhotoDupes />);
    await screen.findByText("photo1.jpg");
    expect(screen.getByText(/stays on disk/)).toBeTruthy();
  });

  it("re-fetches when the PAGE drains, rather than claiming the queue is empty", async () => {
    // The surface asks for one page of groups and then filters it locally as decisions are made, which
    // is what keeps a decision instant. Running the PAGE out is not running the QUEUE out: a collection
    // with hundreds of pending groups showed "Nothing is waiting. Run photos-dupes" after twenty
    // decisions — the most convincing possible way to end a review session early.
    groupsBody = { ...groupsBody, total: 25 };
    nextGroupsBody = {
      total: 23,
      skip: 0,
      hasMore: true,
      dataPlane: true,
      groups: [
        {
          id: 43,
          kind: "Near",
          status: "Pending",
          members: [member(5, { isMaster: true }), member(6)],
        },
      ],
    };

    render(<PhotoDupes />);
    // The header reports what is really waiting, not the size of the page on screen.
    expect(await screen.findByText(/25 waiting/)).toBeTruthy();

    fireEvent.click(screen.getByText("These are different photos"));
    await screen.findByText("photo3.jpg");
    fireEvent.click(screen.getByText("These are different photos"));

    await waitFor(() => expect(calls.list.length).toBe(2));
    expect(await screen.findByText("photo5.jpg")).toBeTruthy();
    expect(screen.queryByText(/Nothing is waiting/)).toBeNull();
  });

  it("an empty queue explains what would fill it", async () => {
    groupsBody = { total: 0, skip: 0, hasMore: false, groups: [] };
    render(<PhotoDupes />);
    expect(await screen.findByText(/Nothing is waiting/)).toBeTruthy();
  });
});
