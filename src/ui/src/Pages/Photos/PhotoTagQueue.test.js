import { render, cleanup, screen, waitFor, fireEvent } from "@testing-library/react";
import { vi, describe, it, expect, afterEach, beforeEach } from "vitest";

// Phase 4's tag queue (docs/photos-plan.md §2.8): "keyboard-first review of untagged/suggested
// photos". What these pin down is the promise the surface makes — that a decision is a deliberate act
// on a named face, that the KEYBOARD alone can drive it (a queue that needs a mouse for hundreds of
// decisions is a queue nobody finishes), that a refusal WRITES a rejection rather than skipping (a
// skip would let the next sync propose the same face forever), and that the manual lane works with no
// sidecar anywhere.

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

const calls = { queue: [], confirm: [], reject: [], add: [] };
const ok = (body) => Promise.resolve({ ok: true, status: 200, json: () => Promise.resolve(body) });

let queues;
// Lets one test hold the READ-AHEAD page open (the request the queue fires as the reviewer nears the
// end) so several keystrokes can land while it is still in flight — which is the only way to observe
// a missing in-flight guard.
let holdReadAhead;
let releaseReadAhead;
let readAheadPage;

vi.mock("../../MovieAPI", () => ({
  MovieAPI: {
    getPhotoTagQueue: (args) => {
      calls.queue.push(args);
      if (holdReadAhead && args.afterId > 0) {
        return new Promise((resolve) => {
          releaseReadAhead = () =>
            resolve({ ok: true, status: 200, json: () => Promise.resolve(readAheadPage) });
        });
      }
      return ok(queues[args.mode || "suggested"]);
    },
    confirmPhotoTag: (tagId) => {
      calls.confirm.push(tagId);
      return ok({ id: tagId, source: "Confirmed" });
    },
    rejectPhotoTag: (tagId) => {
      calls.reject.push(tagId);
      return ok({ id: tagId, source: "Rejected" });
    },
    addPhotoTags: (body) => {
      calls.add.push(body);
      return ok({
        person: { id: 3, name: body.name || "Subject A" },
        added: 1, promoted: 0, unchanged: 0, redirectedToMasters: 0, missing: 0,
      });
    },
  },
}));

const PhotoTagQueue = (await import("./PhotoTagQueue")).default;

const item = (id, tags) => ({
  card: { id, path: `Vacation/photo${id}.jpg`, kind: "Photo", width: 1600, height: 1200, thumbState: "Ready" },
  viewUrl: `https://gateway.example/s/tok${id}/PhotoThumb`,
  tags,
});

const suggestion = (tagId, name, overrides = {}) => ({
  id: tagId,
  personId: 7,
  name,
  unnamed: false,
  source: "Suggested",
  confidence: 0.91,
  box: { x: 0.2, y: 0.1, w: 0.3, h: 0.25 },
  faceCropUrl: null,
  ...overrides,
});

const people = [{ id: 3, name: "Subject A", tagCount: 4 }];

beforeEach(() => {
  Object.keys(calls).forEach((k) => (calls[k].length = 0));
  holdReadAhead = false;
  releaseReadAhead = null;
  readAheadPage = null;
  queues = {
    suggested: {
      mode: "suggested",
      items: [item(1, [suggestion(11, "Subject A")]), item(2, [suggestion(12, "Subject A")])],
      nextCursor: 2, hasMore: false, remaining: 0, total: 2,
    },
    untagged: {
      mode: "untagged",
      items: [item(5, []), item(6, [])],
      nextCursor: 6, hasMore: false, remaining: 0, total: 2,
    },
  };
});

afterEach(async () => {
  cleanup();
  await new Promise((resolve) => setTimeout(resolve, 0));
});

describe("the tag queue", () => {
  it("opens on the MANUAL lane, which works with no sidecar deployed at all", async () => {
    render(<PhotoTagQueue people={people} />);
    await waitFor(() => expect(calls.queue.length).toBeGreaterThan(0));
    // §2.4's posture as a UI shape: hand-tagging is the feature, suggestions are the accelerator.
    expect(calls.queue[0].mode).toBe("untagged");
  });

  it("the keyboard alone accepts a suggestion", async () => {
    render(<PhotoTagQueue people={people} />);
    fireEvent.click(await screen.findByText("Suggestions"));
    await screen.findByText(/Subject A · 91%/);

    fireEvent.keyDown(window, { key: "y" });
    await waitFor(() => expect(calls.confirm).toEqual([11]));
    expect(calls.reject).toHaveLength(0);
  });

  it("the keyboard alone refuses one — and that WRITES a rejection rather than skipping", async () => {
    render(<PhotoTagQueue people={people} />);
    fireEvent.click(await screen.findByText("Suggestions"));
    await screen.findByText(/Subject A · 91%/);

    fireEvent.keyDown(window, { key: "n" });
    // A UI that merely advanced would leave the same face proposed on every future sync. The
    // rejection is the record that stops it.
    await waitFor(() => expect(calls.reject).toEqual([11]));
    expect(calls.confirm).toHaveLength(0);
  });

  it("skipping moves on without deciding anything", async () => {
    render(<PhotoTagQueue people={people} />);
    fireEvent.click(await screen.findByText("Suggestions"));
    await screen.findByText("Vacation/photo1.jpg");

    fireEvent.keyDown(window, { key: "s" });
    await screen.findByText("Vacation/photo2.jpg");
    expect(calls.confirm).toHaveLength(0);
    expect(calls.reject).toHaveLength(0);
  });

  it("arrow keys walk the queue in both directions", async () => {
    render(<PhotoTagQueue people={people} />);
    fireEvent.click(await screen.findByText("Suggestions"));
    await screen.findByText("Vacation/photo1.jpg");

    fireEvent.keyDown(window, { key: "ArrowRight" });
    await screen.findByText("Vacation/photo2.jpg");
    fireEvent.keyDown(window, { key: "ArrowLeft" });
    await screen.findByText("Vacation/photo1.jpg");
  });

  it("a decided suggestion leaves the card without moving the reviewer", async () => {
    render(<PhotoTagQueue people={people} />);
    fireEvent.click(await screen.findByText("Suggestions"));
    await screen.findByText(/Subject A · 91%/);

    fireEvent.click(screen.getByText("Yes (Y)"));
    await waitFor(() => expect(screen.queryByText(/Subject A · 91%/)).toBeNull());
    // Still on the same photograph: a queue that jumps after every keystroke cannot be worked at speed.
    expect(screen.getByText("Vacation/photo1.jpg")).toBeTruthy();
  });

  it("typing a name tags by hand and advances", async () => {
    render(<PhotoTagQueue people={people} />);
    await screen.findByText("Vacation/photo5.jpg");

    const input = screen.getByLabelText("Tag someone…");
    fireEvent.change(input, { target: { value: "Subject A" } });
    fireEvent.keyDown(input, { key: "Enter" });

    await waitFor(() => expect(calls.add).toHaveLength(1));
    expect(calls.add[0].assetIds).toEqual([5]);
    expect(calls.add[0].familyPersonId).toBe(3);
    await screen.findByText("Vacation/photo6.jpg");
  });

  it("a name the list does not have is offered as an addition", async () => {
    render(<PhotoTagQueue people={people} />);
    await screen.findByText("Vacation/photo5.jpg");

    const input = screen.getByLabelText("Tag someone…");
    fireEvent.change(input, { target: { value: "Subject Z" } });
    fireEvent.click(screen.getByText(/Add “Subject Z”/));

    await waitFor(() => expect(calls.add).toHaveLength(1));
    // One round trip: the server creates the person and writes the tag together.
    expect(calls.add[0].familyPersonId).toBeUndefined();
    expect(calls.add[0].name).toBe("Subject Z");
  });

  it("typing into the picker does not fire the queue's single-key shortcuts", async () => {
    render(<PhotoTagQueue people={people} />);
    fireEvent.click(await screen.findByText("Suggestions"));
    await screen.findByText(/Subject A · 91%/);

    const input = screen.getByLabelText("Tag someone…");
    fireEvent.change(input, { target: { value: "Ny" } });
    fireEvent.keyDown(input, { key: "n" });
    fireEvent.keyDown(input, { key: "y" });
    // Otherwise typing a name containing "n" would silently refuse the suggestion on screen.
    expect(calls.reject).toHaveLength(0);
    expect(calls.confirm).toHaveLength(0);
  });

  it("draws the face box over our OWN image when no crop was cached", async () => {
    const { container } = render(<PhotoTagQueue people={people} />);
    fireEvent.click(await screen.findByText("Suggestions"));
    await screen.findByText(/Subject A · 91%/);

    // §2.4's degradation, visible: the box fractions live on the tag row precisely so the queue keeps
    // working with the sidecar unreachable or thrown away.
    const box = container.querySelector(".photo-face-box");
    expect(box).not.toBeNull();
    expect(box.style.left).toBe("20%");
    expect(box.style.width).toBe("30%");
  });

  it("an empty suggestions lane says the sidecar is optional", async () => {
    queues.suggested = { mode: "suggested", items: [], nextCursor: 0, hasMore: false, remaining: 0, total: 0 };
    render(<PhotoTagQueue people={people} />);
    fireEvent.click(await screen.findByText("Suggestions"));
    expect(await screen.findByText(/everything here works without it/i)).toBeTruthy();
  });

  it("rapid advance fires ONE read-ahead page, not one per keystroke", async () => {
    // The read-ahead effect re-runs on every change to `index`, but the cursor only moves when a
    // response comes BACK. Holding a key down under a slow round trip therefore fired the same page
    // request several times and appended the same cards several times — the reviewer meets photos
    // they already decided on, and the position counter stops meaning anything. The in-flight guard
    // (the one PhotoFolders and PhotoAlbumDetail already use) is what makes it one page.
    holdReadAhead = true;
    readAheadPage = {
      mode: "untagged",
      items: [item(10, []), item(11, [])],
      nextCursor: 11, hasMore: false, remaining: 0, total: 7,
    };
    queues.untagged = {
      mode: "untagged",
      // Five, so the reach-ahead threshold (four left) is crossed by the FIRST keystroke rather than
      // on load — the initial fetch and the read-ahead have to be distinguishable.
      items: [item(5, []), item(6, []), item(7, []), item(8, []), item(9, [])],
      nextCursor: 9, hasMore: true, remaining: 2, total: 7,
    };

    render(<PhotoTagQueue people={people} />);
    await screen.findByText("Vacation/photo5.jpg");
    const readAheads = () => calls.queue.filter((c) => c.afterId > 0).length;
    expect(readAheads()).toBe(0);

    fireEvent.keyDown(window, { key: "ArrowRight" });
    await waitFor(() => expect(readAheads()).toBe(1));

    // Three more keystrokes while the page is still in flight. Each re-runs the effect and each is a
    // duplicate request without the guard.
    fireEvent.keyDown(window, { key: "ArrowRight" });
    fireEvent.keyDown(window, { key: "ArrowRight" });
    fireEvent.keyDown(window, { key: "ArrowRight" });
    await new Promise((resolve) => setTimeout(resolve, 0));
    expect(readAheads()).toBe(1);

    // And the one page appends once: seven cards, not seven plus three duplicates.
    releaseReadAhead();
    await waitFor(() => expect(screen.getByText(/of 7$/)).toBeTruthy());
  });

  it("stops reaching ahead once the server says there are no more pages", async () => {
    // `nextCursor` is the LAST ID on the page and stays non-zero at the end of the queue, so a
    // read-ahead keyed on the cursor alone re-requested the final page and appended the same
    // photographs again — "2 of 2" quietly became "2 of 4". `hasMore` is the server's actual answer,
    // and this is also what made the surface racy: those spurious pages were in flight while the
    // reviewer was switching lanes.
    render(<PhotoTagQueue people={people} />);
    await screen.findByText("Vacation/photo5.jpg");
    await new Promise((resolve) => setTimeout(resolve, 0));

    expect(calls.queue.filter((c) => c.afterId > 0)).toHaveLength(0);
    expect(screen.getByText(/of 2$/)).toBeTruthy();
  });

  it("switching lanes while a read-ahead is in flight still loads the new lane", async () => {
    // The in-flight guard used to swallow the lane change, leaving `state` on "loading" and the
    // surface on a spinner that never resolved. A read-ahead is skippable; a reviewer asking for a
    // different queue is not.
    holdReadAhead = true;
    readAheadPage = {
      mode: "untagged",
      items: [item(10, [])],
      nextCursor: 10, hasMore: false, remaining: 0, total: 6,
    };
    queues.untagged = {
      mode: "untagged",
      items: [item(5, []), item(6, []), item(7, []), item(8, []), item(9, [])],
      nextCursor: 9, hasMore: true, remaining: 1, total: 6,
    };

    render(<PhotoTagQueue people={people} />);
    await screen.findByText("Vacation/photo5.jpg");
    fireEvent.keyDown(window, { key: "ArrowRight" });
    await waitFor(() => expect(calls.queue.filter((c) => c.afterId > 0)).toHaveLength(1));

    fireEvent.click(screen.getByText("Suggestions"));
    expect(await screen.findByText(/Subject A · 91%/)).toBeTruthy();

    // And when the superseded page finally lands it does not push the old lane's photos into the
    // new one.
    releaseReadAhead();
    await new Promise((resolve) => setTimeout(resolve, 0));
    expect(screen.queryByText("Vacation/photo10.jpg")).toBeNull();
    expect(screen.getByText("Vacation/photo1.jpg")).toBeTruthy();
  });

  it("an unnamed cluster reads as a group, never as a blank name", async () => {
    queues.suggested.items = [item(1, [suggestion(11, "", { unnamed: true })])];
    render(<PhotoTagQueue people={people} />);
    fireEvent.click(await screen.findByText("Suggestions"));
    expect(await screen.findByText(/Unnamed face group/)).toBeTruthy();
  });
});
