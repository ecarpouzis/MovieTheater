import { useEffect, useState } from "react";
import { MovieAPI } from "../MovieAPI";

// The family album's shared session data (docs/photos-plan.md §4), plus the two pure helpers that
// decide what the album's navigation offers.
//
// Why a module-level store rather than page state: the photos NAVBAR now owns the section's primary
// navigation, and the navbar is not in the photos page's React tree. Both need the same two answers
// — what the gate said, and what is waiting for a family member — and asking twice would double the
// status query against a database that has an ingest running through it. So the status and the
// people list are fetched ONCE and published to every subscriber, exactly the way
// useShowHiddenPhotos publishes the admin switch.
//
// This lives in hooks/ rather than in Pages/Photos/ on purpose: the navbar is in the entry bundle,
// and importing a photos PAGE module from it would drag the section's chunk into every visit to the
// site. Nothing here imports a photo component.

const EMPTY = {
  // loading | ready | denied | error — the same four states the page has always rendered. 401 and
  // 403 collapse into "denied" because they are the same answer to a visitor (§2.1).
  state: "loading",
  status: null,
  people: [],
  unnamed: [],
  peopleLoading: true,
};

let snapshot = EMPTY;
const listeners = new Set();
// Which username the held status belongs to, so a login/logout re-asks instead of showing the
// previous session's answer.
let statusToken;
let statusInFlight = false;
let peopleRequested = false;

function publish(patch) {
  snapshot = { ...snapshot, ...patch };
  listeners.forEach((notify) => notify(snapshot));
}

/** Re-ask the gate. Deliberately does NOT drop back to "loading": a refresh after a curation write
 *  must not blank the album the member is looking at. */
export function loadPhotosAlbumStatus(username) {
  const mine = username ?? null;
  statusToken = mine;
  statusInFlight = true;
  return MovieAPI.getPhotosStatus()
    .then((response) => {
      // A newer load (a different user) has superseded this one.
      if (statusToken !== mine) return undefined;
      if (response.status === 401 || response.status === 403) {
        publish({ state: "denied", status: null });
        return undefined;
      }
      if (!response.ok) {
        publish({ state: "error" });
        return undefined;
      }
      return response.json().then((body) => publish({ state: "ready", status: body }));
    })
    .catch(() => {
      if (statusToken === mine) publish({ state: "error" });
    })
    .finally(() => {
      statusInFlight = false;
    });
}

/** The people list (§2.8), shared with every picker: a family has tens of people, so the type-ahead
 *  filters in memory instead of asking per keystroke. */
export function loadPhotosAlbumPeople() {
  peopleRequested = true;
  return MovieAPI.getPhotoPeople()
    .then((response) => (response.ok ? response.json() : null))
    .then((body) => publish({ people: body?.people || [], unnamed: body?.unnamed || [] }))
    .catch(() => {})
    .finally(() => publish({ peopleLoading: false }));
}

/** Drops everything held. Exists for tests, which mount the page many times against different
 *  fixtures and must not inherit the previous case's answer. */
export function resetPhotosAlbum() {
  statusToken = undefined;
  statusInFlight = false;
  peopleRequested = false;
  publish(EMPTY);
}

/**
 * Subscribe to the album's session data.
 *
 * `enabled` is what keeps this inert everywhere else on the site: the navbar passes false unless the
 * user is actually on /photos, so no other section ever issues a photos request.
 */
export default function usePhotosAlbum({ enabled = true, username } = {}) {
  const [value, setValue] = useState(snapshot);

  useEffect(() => {
    listeners.add(setValue);
    setValue(snapshot);
    return () => {
      listeners.delete(setValue);
    };
  }, []);

  useEffect(() => {
    if (!enabled) return;
    const key = username ?? null;
    const settled = snapshot.state === "ready" || snapshot.state === "denied";
    if (statusToken === key && (settled || statusInFlight)) return;
    loadPhotosAlbumStatus(username);
  }, [enabled, username]);

  useEffect(() => {
    if (!enabled || value.state !== "ready" || peopleRequested) return;
    loadPhotosAlbumPeople();
  }, [enabled, value.state]);

  return {
    ...value,
    refresh: () => loadPhotosAlbumStatus(username),
    refreshPeople: loadPhotosAlbumPeople,
  };
}

// ── The album's views ────────────────────────────────────────────────────────────────────────────
// One list, read by the navbar (which links them) and by the page (which renders them), so a view
// can never appear in one and not the other.

export const PHOTO_VIEWS = [
  { key: "timeline", label: "Timeline", path: "/photos" },
  { key: "undated", label: "Undated", path: "/photos/undated" },
  { key: "albums", label: "Albums", path: "/photos/albums" },
  // §2.12. "Gallery", not "Archive": the shelf's storage meaning is that it is off the timeline, but
  // what the family opens is a room of pictures — art collections by artist, and the meme piles
  // beside them. The column is still PhotoShelf.Archive; only the sign on the door reads Gallery.
  { key: "gallery", label: "Gallery", path: "/photos/gallery" },
  { key: "folders", label: "Folders", path: "/photos/folders" },
  { key: "people", label: "People", path: "/photos/people" },
  { key: "tag", label: "Tag queue", path: "/photos/tag" },
  { key: "dupes", label: "Dupes", path: "/photos/dupes" },
  { key: "review", label: "Review", path: "/photos/review" },
];

const VIEW_BY_KEY = Object.fromEntries(PHOTO_VIEWS.map((view) => [view.key, view]));

/** Which view a /photos URL is on. Anything unrecognised is the timeline — a mistyped album path
 *  should land somewhere real rather than on an empty page. */
export function photosSection(pathname) {
  const rest = String(pathname || "").replace(/^\/photos\/?/, "").split("/")[0];
  return VIEW_BY_KEY[rest] ? rest : "timeline";
}

export function photosViewLabel(key) {
  return VIEW_BY_KEY[key]?.label ?? "Timeline";
}

/**
 * The navigation, grouped: what you browse, and what is waiting for an answer.
 *
 * The gating rules are the ones the in-page tab strip used to apply, moved here verbatim so the rail
 * and the page cannot disagree about whether a tab exists. `unnamedCount` is the number of imported
 * face clusters nobody has claimed — it opens the tag queue on its own, because naming one is the
 * highest-leverage action in the feature.
 */
export function photosNavGroups(status, unnamedCount = 0) {
  if (!status) return [];

  const reviewWaiting =
    (status.pendingHideProposals || 0) +
    (status.googleOnly || 0) +
    (status.admin ? status.quarantinedBatches || 0 : 0);
  const dupesWaiting = status.pendingDupeGroups || 0;
  const tagWaiting = (status.pendingTagSuggestions || 0) + unnamedCount;

  const browse = [
    // timelineCount is what the timeline PAGE shows (shelf split, hidden, missing and collapse all
    // applied server-side); the raw asset total quietly promised ~2,900 more than the page ever
    // rendered. The fallback keeps an older server's answer usable.
    { ...VIEW_BY_KEY.timeline, count: status.timelineCount ?? status.assets ?? null },
    ...(status.undated > 0 ? [{ ...VIEW_BY_KEY.undated, count: status.undated }] : []),
    { ...VIEW_BY_KEY.albums, count: status.albums ?? null },
    // Offered only once there is a Gallery to open (§2.12). A rail entry that leads to an empty room
    // is worse than no entry: on a site where most families will never file anything as art, the
    // default state of this section is "does not exist", and the count is what says so.
    ...(status.archiveAlbums > 0 ? [{ ...VIEW_BY_KEY.gallery, count: status.archiveAlbums }] : []),
    { ...VIEW_BY_KEY.folders, count: null },
    { ...VIEW_BY_KEY.people, count: status.people ?? null },
  ];

  const waiting = [];
  if ((status.untaggedPhotos || 0) > 0 || tagWaiting > 0) {
    waiting.push({ ...VIEW_BY_KEY.tag, count: tagWaiting || null, waiting: tagWaiting > 0 });
  }
  if (dupesWaiting > 0) {
    waiting.push({ ...VIEW_BY_KEY.dupes, count: dupesWaiting, waiting: true });
  }
  if (reviewWaiting > 0 || status.admin) {
    waiting.push({ ...VIEW_BY_KEY.review, count: reviewWaiting || null, waiting: reviewWaiting > 0 });
  }

  const groups = [{ key: "browse", label: "The album", views: browse }];
  if (waiting.length) groups.push({ key: "waiting", label: "Waiting on you", views: waiting });
  return groups;
}

/** Flat list of every view currently offered — the mobile strip, which has no room for headings. */
export function photosNavViews(status, unnamedCount = 0) {
  return photosNavGroups(status, unnamedCount).flatMap((group) => group.views);
}
