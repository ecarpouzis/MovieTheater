import { useState } from "react";
import { render, cleanup, screen, waitFor, fireEvent } from "@testing-library/react";
import { vi, describe, it, expect, afterEach, beforeEach } from "vitest";

// Phase 4 people (docs/photos-plan.md §2.8). What these pin down is the split the whole flow rests
// on: a NAMED row is a person, and a row with an empty name is an imported face cluster that must
// never appear as a nameless person — naming one is the highest-leverage act in the feature, and
// mapping one onto somebody who already exists must merge rather than create a twin.

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

const calls = { create: [], update: [], merge: [], person: [], timeline: [] };
const ok = (body) => Promise.resolve({ ok: true, status: 200, json: () => Promise.resolve(body) });

let detail;

vi.mock("../../MovieAPI", () => ({
  MovieAPI: {
    createPhotoPerson: (body) => {
      calls.create.push(body);
      return ok({ person: { id: 9, name: body.name }, created: true });
    },
    updatePhotoPerson: (id, body) => {
      calls.update.push({ id, body });
      return ok({ person: { id, name: body.name }, named: true });
    },
    mergePhotoPerson: (id, intoPersonId) => {
      calls.merge.push({ id, intoPersonId });
      return ok({ merged: true, moved: 12, dropped: 0, into: { id: intoPersonId, name: "Subject A" } });
    },
    deletePhotoPerson: () => ok({ deleted: true, tagsRemoved: 0 }),
    getPhotoPerson: (id) => {
      calls.person.push(id);
      return ok(detail);
    },
    getPhotoPersonTimeline: (id, args) => {
      calls.timeline.push({ id, ...args });
      return ok({
        items: [
          {
            id: 1, path: "Vacation/one.jpg", kind: "Photo", width: 400, height: 300,
            takenAt: "2011-07-04T10:00:00", takenAtSource: "Exif", thumbState: "Ready",
            gridUrl: "https://gateway.example/s/tok1/PhotoThumb",
          },
        ],
        total: 1, skip: 0, hasMore: false, dataPlane: true,
      });
    },
  },
}));

const PhotoPeople = (await import("./PhotoPeople")).default;

const person = (id, name, extra = {}) => ({
  id, name, birthYear: null, userId: null, immichLinked: false,
  tagCount: 4, suggestionCount: 0, coverUrl: null, faceCropUrl: null, ...extra,
});

const cluster = (id, faces) => person(id, "", { tagCount: 0, suggestionCount: faces, immichLinked: true });

beforeEach(() => {
  Object.keys(calls).forEach((k) => (calls[k].length = 0));
  detail = {
    person: { id: 3, name: "Subject A", birthYear: 1990 },
    tagCount: 4,
    suggestionCount: 0,
    firstTakenAt: "2011-07-04T10:00:00",
    lastTakenAt: "2014-01-01T10:00:00",
    alsoWith: [{ id: 4, name: "Subject B", count: 3 }],
    coverUrl: null,
    faceCropUrl: null,
    dataPlane: true,
  };
});

afterEach(async () => {
  cleanup();
  await new Promise((resolve) => setTimeout(resolve, 0));
});

describe("the people list", () => {
  it("lists named people, and lists unnamed clusters SEPARATELY", async () => {
    render(<PhotoPeople people={[person(3, "Subject A")]} unnamed={[cluster(8, 42)]} loading={false} />);

    expect(screen.getByText("Subject A")).toBeTruthy();
    // An unnamed cluster is a queue item, not a person: it must never read as a nameless someone.
    expect(screen.getByText("Unnamed group")).toBeTruthy();
    expect(screen.getByText(/42 faces/)).toBeTruthy();
  });

  it("says out loud that names live here and not in the sidecar", async () => {
    render(<PhotoPeople people={[]} unnamed={[cluster(8, 5)]} loading={false} />);
    expect(screen.getByText(/names live here, not there/i)).toBeTruthy();
  });

  it("adds a person with an optional birth year", async () => {
    render(<PhotoPeople people={[]} unnamed={[]} loading={false} />);
    fireEvent.click(screen.getByText("Add a person"));

    const inputs = document.querySelectorAll(".photo-person-editor input");
    fireEvent.change(inputs[0], { target: { value: "Subject C" } });
    fireEvent.change(inputs[1], { target: { value: "1978" } });
    fireEvent.click(screen.getByText("Save"));

    await waitFor(() => expect(calls.create).toHaveLength(1));
    expect(calls.create[0]).toEqual({ name: "Subject C", birthYear: 1978 });
  });

  it("says a birth year is only ever a hint", async () => {
    render(<PhotoPeople people={[]} unnamed={[]} loading={false} />);
    fireEvent.click(screen.getByText("Add a person"));
    // §2.7: the implied bound is shown to a human and never written by a machine.
    expect(screen.getByText(/never writes a date by itself/i)).toBeTruthy();
  });

  it("naming a cluster renames the row the suggestions already point at", async () => {
    render(<PhotoPeople people={[]} unnamed={[cluster(8, 42)]} loading={false} />);
    fireEvent.click(screen.getByText("Name or map"));

    const input = document.querySelector(".photo-album-new input");
    fireEvent.change(input, { target: { value: "Subject D" } });
    fireEvent.click(screen.getByText("Name"));

    await waitFor(() => expect(calls.update).toHaveLength(1));
    // No tag rows are rewritten: they were always pointed here, so one rename fans them out.
    expect(calls.update[0]).toEqual({ id: 8, body: { name: "Subject D" } });
    expect(calls.create).toHaveLength(0);
  });

  it("mapping a cluster onto an existing person MERGES rather than creating a twin", async () => {
    render(<PhotoPeople people={[person(3, "Subject A")]} unnamed={[cluster(8, 42)]} loading={false} />);
    fireEvent.click(screen.getByText("Name or map"));
    // Picked from the modal's own list: the same name is also on the card behind it, and a query that
    // could not tell them apart would be asserting nothing about which one was clicked.
    await waitFor(() => expect(document.querySelectorAll(".photo-album-choice").length).toBe(1));
    fireEvent.click(document.querySelector(".photo-album-choice"));

    await waitFor(() => expect(calls.merge).toHaveLength(1));
    expect(calls.merge[0]).toEqual({ id: 8, intoPersonId: 3 });
  });
});

// Which person is open is a ROUTE now (/photos/people/:id) rather than component state, so the
// parent owns it. This harness is the smallest stand-in for PhotosPage's router: it holds the id and
// hands it back down, which is exactly what the route does.
function PeopleHarness(props) {
  const [personId, setPersonId] = useState(null);
  return (
    <PhotoPeople
      {...props}
      personId={personId}
      onOpenPerson={setPersonId}
      onBackToPeople={() => setPersonId(null)}
    />
  );
}

describe("a person page", () => {
  it("shows their photos and who else is in them", async () => {
    render(<PeopleHarness people={[person(3, "Subject A")]} unnamed={[]} loading={false} onOpenAsset={() => {}} />);
    fireEvent.click(screen.getByText("Subject A"));

    await waitFor(() => expect(calls.person).toEqual([3]));
    expect(await screen.findByText(/Also with/)).toBeTruthy();
    // The co-occurrence chip carries the count, which is what makes it worth clicking.
    expect(screen.getByText("3")).toBeTruthy();
  });

  it("a co-occurrence chip navigates to that person", async () => {
    render(<PeopleHarness people={[person(3, "Subject A")]} unnamed={[]} loading={false} onOpenAsset={() => {}} />);
    fireEvent.click(screen.getByText("Subject A"));
    await screen.findByText(/Also with/);

    fireEvent.click(screen.getByText(/Subject B/));
    await waitFor(() => expect(calls.person).toEqual([3, 4]));
  });
});
