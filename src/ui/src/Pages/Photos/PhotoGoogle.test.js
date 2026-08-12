import { render, cleanup, screen, waitFor, fireEvent } from "@testing-library/react";
import { vi, describe, it, expect, afterEach, beforeEach } from "vitest";

// Phase 6, the Google mesh review section (docs/photos-plan.md §2.10). What these pin down is what
// the surface PROMISES: that ignoring is the only write it can make, that the drain guard is said out
// loud rather than left for the CLI to refuse silently, and that a database which has never meshed an
// archive says so instead of drawing an empty list that reads as "Google has nothing".

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

const calls = { ignore: [], list: [] };
const ok = (body) => Promise.resolve({ ok: true, status: 200, json: () => Promise.resolve(body) });

let meshBody;
let listBody;

vi.mock("../../MovieAPI", () => ({
  MovieAPI: {
    getPhotosGoogleMesh: () => ok(meshBody),
    getPhotosGoogleOnly: (options) => {
      calls.list.push(options || {});
      return ok(listBody);
    },
    ignorePhotosGoogleItems: (ids, ignored) => {
      calls.ignore.push({ ids, ignored });
      return ok({ updated: ids.length, ignored, requested: ids.length });
    },
  },
}));

const PhotoGoogle = (await import("./PhotoGoogle")).default;

beforeEach(() => {
  Object.keys(calls).forEach((k) => (calls[k].length = 0));
  meshBody = {
    ran: true,
    total: 120,
    pending: 0,
    matched: 100,
    googleOnly: 18,
    ignored: 2,
    downloaded: 0,
    drained: true,
    byMethod: [
      { method: "name+size", count: 80 },
      { method: "sha256", count: 15 },
      { method: "phash", count: 5 },
    ],
    disagreements: [{ field: "takenAt", count: 4 }],
    disagreeingItems: 4,
  };
  listBody = {
    total: 18,
    skip: 0,
    take: 60,
    items: [
      {
        id: 501,
        fileName: "IMG_5001.jpg",
        archivePath: "Takeout/Google Photos/Photos from 2019/IMG_5001.jpg",
        takenAtUtc: "2019-07-04T16:00:00Z",
        sizeBytes: 1234,
        description: "the fireworks one",
        ignored: false,
        gridUrl: "https://gateway.example/s/tok/PhotoThumb",
        viewUrl: null,
      },
    ],
  };
});

afterEach(cleanup);

describe("Google mesh review (§2.10)", () => {
  it("shows the per-rung counts and the disagreements", async () => {
    render(<PhotoGoogle />);
    await waitFor(() => expect(screen.getByText("already in the library")).toBeTruthy());

    // The rung a match came from is the thing that makes a wrong match findable later, so it is on
    // the surface rather than only in the row.
    expect(screen.getByText("matched by pixel similarity")).toBeTruthy();
    expect(screen.getByText("matched by content hash")).toBeTruthy();
    // A disagreement is the pass's question to a human; the count is what makes a systematic problem
    // look like a cluster instead of a scattered surprise.
    expect(screen.getByText(/Google's date lost to a stronger local source/)).toBeTruthy();
  });

  it("says so when no archive has ever been meshed", async () => {
    meshBody = { ran: false };
    render(<PhotoGoogle />);
    await waitFor(() => expect(screen.getByText(/No Takeout archive has been meshed/)).toBeTruthy());
    // No list, no empty grid that would read as "Google has nothing".
    expect(screen.queryByText("Ignore")).toBeNull();
  });

  it("warns while the archive has not drained", async () => {
    meshBody = { ...meshBody, pending: 7, drained: false };
    render(<PhotoGoogle />);
    // §2.10's drain guard: the list is incomplete and the download lane will refuse, and both facts
    // belong on the screen rather than in a CLI message nobody is reading.
    await waitFor(() =>
      expect(screen.getByText(/have not been\s+matched yet/)).toBeTruthy());
  });

  it("ignoring an item is the only write, and it reloads", async () => {
    render(<PhotoGoogle />);
    await waitFor(() => expect(screen.getByText("IMG_5001.jpg")).toBeTruthy());

    fireEvent.click(screen.getByText("Ignore"));
    await waitFor(() => expect(calls.ignore.length).toBe(1));
    expect(calls.ignore[0]).toEqual({ ids: [501], ignored: true });
    // The list is re-read afterwards, so the item leaves the default view.
    await waitFor(() => expect(calls.list.length).toBeGreaterThan(1));
  });

  it("show-ignored asks the server, never filters locally", async () => {
    render(<PhotoGoogle />);
    await waitFor(() => expect(calls.list.length).toBe(1));
    expect(calls.list[0].includeIgnored).toBeFalsy();

    fireEvent.click(screen.getByLabelText("Show ignored", { exact: false }));
    await waitFor(() => expect(calls.list.some((c) => c.includeIgnored)).toBe(true));
  });

  it("draws a placeholder rather than a broken image when no thumb was emitted", async () => {
    listBody = {
      ...listBody,
      items: [{ ...listBody.items[0], gridUrl: null }],
    };
    const { container } = render(<PhotoGoogle />);
    await waitFor(() => expect(screen.getByText("IMG_5001.jpg")).toBeTruthy());
    expect(container.querySelector(".photo-google-placeholder")).toBeTruthy();
    expect(container.querySelector(".photo-google-card img")).toBeNull();
  });
});
