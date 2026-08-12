import { useCallback, useEffect, useState } from "react";
import { Segmented, Spin, Switch } from "antd";
import { MovieAPI } from "../../MovieAPI";
import PhotoTimeline from "./PhotoTimeline";
import PhotoFolders from "./PhotoFolders";
import PhotoLightbox from "./PhotoLightbox";
import PhotoAlbums from "./PhotoAlbums";
import PhotoAlbumDetail from "./PhotoAlbumDetail";
import PhotoReview from "./PhotoReview";
import PhotoDupes from "./PhotoDupes";
import PhotoPeople from "./PhotoPeople";
import PhotoTagQueue from "./PhotoTagQueue";
import PhotoSelectionBar from "./PhotoSelectionBar";
import useShowHiddenPhotos from "../../hooks/useShowHiddenPhotos";
import "./PhotosPage.css";

// ── Family photo album (docs/photos-plan.md §4) ─────────────────────────────
// Phase 1's browse surfaces: the timeline, the date-unknown shelf it is deliberately NOT interleaved
// with (§2.7), the folder tree (§2.9), and the lightbox. Phase 2 adds curation — selection mode with
// batch hide/unhide, albums, and the review surface for suggested-hide batches and unapproved
// ingests. Phase 3 adds the duplicate review (§2.6), where a human picks which copy of a photograph
// represents it; the timeline then collapses the rest behind that pick. People are a later phase.
//
// Phase 4 adds people, tagging and the keyboard-first tag queue (§2.8) — and moves show-hidden out of
// this toolbar entirely: any member may hide a photo, but only an admin may see what was hidden, and
// the switch that reveals it now lives in the navbar (Phase 4 addendum).
//
// The nav hides /photos for non-members, but this page never assumes that filtering happened — a URL
// can be typed or bookmarked. It asks the server, and the server's 403 is what it renders. That is
// the §2.1 rule in miniature: the UI is not the gate, it just reports what the gate said.

const VIEWS = [
  { value: "timeline", label: "Timeline" },
  { value: "undated", label: "Undated" },
  { value: "folders", label: "Folders" },
  { value: "albums", label: "Albums" },
  { value: "people", label: "People" },
  { value: "tag", label: "Tag queue" },
  { value: "dupes", label: "Dupes" },
  { value: "review", label: "Review" },
];

export default function PhotosPage({ userData }) {
  const [status, setStatus] = useState(null);
  const [state, setState] = useState("loading"); // loading | ready | denied | error
  const [view, setView] = useState("timeline");
  const [openAssetId, setOpenAssetId] = useState(null);
  const [albumSlug, setAlbumSlug] = useState(null);

  // Curation state (§2.9). Selection mode is explicit: a click either opens a photo or selects it,
  // and which of those it does is never a guess about modifier keys.
  const [selecting, setSelecting] = useState(false);
  const [selected, setSelected] = useState([]);
  // Admin-only, and driven from the NAVBAR rather than from here (Phase 4 addendum). The server
  // ignores the parameter for a non-admin regardless, so this is purely what the page ASKS for.
  const [showHidden] = useShowHiddenPhotos();
  // Bumped after any curation write so the browse lists re-fetch rather than showing a stale answer.
  const [refreshKey, setRefreshKey] = useState(0);

  // The people list (§2.8). Fetched once and shared with every picker: a family has tens of people,
  // so the type-ahead filters in memory instead of asking per keystroke.
  const [people, setPeople] = useState([]);
  const [unnamed, setUnnamed] = useState([]);
  const [peopleLoading, setPeopleLoading] = useState(true);

  const loadStatus = useCallback(() => {
    return MovieAPI.getPhotosStatus()
      .then((r) => {
        // 401 (no session) and 403 (session, no membership or no password) are the same answer to
        // the visitor — the page must not hint at which of them it was.
        if (r.status === 401 || r.status === 403) {
          setState("denied");
          return null;
        }
        if (!r.ok) {
          setState("error");
          return null;
        }
        return r.json().then((body) => {
          setStatus(body);
          setState("ready");
        });
      })
      .catch(() => setState("error"));
  }, []);

  const loadPeople = useCallback(() => {
    return MovieAPI.getPhotoPeople()
      .then((r) => (r.ok ? r.json() : null))
      .then((body) => {
        setPeople(body?.people || []);
        setUnnamed(body?.unnamed || []);
      })
      .catch(() => {})
      .finally(() => setPeopleLoading(false));
  }, []);

  useEffect(() => {
    loadStatus();
  }, [loadStatus, userData?.username]);

  useEffect(() => {
    if (state === "ready") loadPeople();
  }, [state, loadPeople]);

  const changed = () => {
    setSelected([]);
    setRefreshKey((k) => k + 1);
    loadStatus();
  };

  if (state === "loading") {
    return (
      <div className="photos-page">
        <Spin size="large" />
      </div>
    );
  }

  if (state === "denied") {
    // Deliberately says nothing about what is in there.
    return (
      <div className="photos-page">
        <h1 className="photos-title">Photos</h1>
        <div className="photos-panel">
          <p className="photos-note">This area is limited to family members.</p>
        </div>
      </div>
    );
  }

  if (state === "error") {
    return (
      <div className="photos-page">
        <h1 className="photos-title">Photos</h1>
        <div className="photos-panel">
          <p className="photos-note">Could not reach the photo album just now.</p>
        </div>
      </div>
    );
  }

  const empty = status?.empty !== false;

  if (empty) {
    return (
      <div className="photos-page">
        <h1 className="photos-title">Photos</h1>
        <p className="photos-subtitle">Family photo album</p>
        <div className="photos-panel">
          <h2 className="photos-panel-head">Nothing here yet</h2>
          <p className="photos-note">
            The album is set up but the collection has not been read in yet. Nothing has been copied,
            moved or changed on disk — and nothing ever will be: everything this section does is a row
            in the database.
          </p>
        </div>
      </div>
    );
  }

  // Google-only items (§2.10) count toward the Review tab's badge for the same reason the hide
  // proposals do: they are a machine's proposal waiting on a family member's answer.
  const reviewWaiting =
    (status?.pendingHideProposals || 0) +
    (status?.googleOnly || 0) +
    (status?.admin ? status?.quarantinedBatches || 0 : 0);
  const dupesWaiting = status?.pendingDupeGroups || 0;
  const tagWaiting = (status?.pendingTagSuggestions || 0) + (unnamed.length > 0 ? unnamed.length : 0);
  const views = VIEWS.filter((v) => {
    if (v.value === "undated") return status?.undated > 0;
    // The tag queue is the manual lane first (§2.4): it appears as soon as there is anything to tag,
    // with or without a sidecar ever having run.
    if (v.value === "tag") return (status?.untaggedPhotos || 0) > 0 || tagWaiting > 0;
    // The review tab appears when there is something to review — or for an admin, who is the one who
    // would go looking for it.
    if (v.value === "review") return reviewWaiting > 0 || status?.admin;
    // Duplicate review is member-visible curation, and it appears only once the grouping pass has
    // actually proposed something.
    if (v.value === "dupes") return dupesWaiting > 0;
    return true;
  }).map((v) => {
    if (v.value === "review" && reviewWaiting > 0) return { ...v, label: `Review (${reviewWaiting})` };
    if (v.value === "dupes" && dupesWaiting > 0) return { ...v, label: `Dupes (${dupesWaiting})` };
    if (v.value === "tag" && tagWaiting > 0) return { ...v, label: `Tag queue (${tagWaiting})` };
    return v;
  });

  const selection = {
    active: selecting,
    has: (id) => selected.includes(id),
    toggle: (id) => setSelected((prev) => (prev.includes(id) ? prev.filter((x) => x !== id) : prev.concat(id))),
  };

  const browsing = view === "timeline" || view === "undated" || view === "folders";

  const makeAlbumFromFolder = async (path, name) => {
    const response = await MovieAPI.createPhotoAlbum({ title: name || path, fromFolder: path });
    if (!response.ok) return;
    const body = await response.json();
    setAlbumSlug(body.album.slug);
    setView("albums");
    changed();
  };

  return (
    <div className="photos-page">
      <div className="photos-head">
        <div>
          <h1 className="photos-title">Photos</h1>
          <p className="photos-subtitle">
            {status.photos.toLocaleString()} photos
            {status.videos > 0 ? ` · ${status.videos.toLocaleString()} videos` : ""}
            {status.undated > 0 ? ` · ${status.undated.toLocaleString()} undated` : ""}
            {status.hidden > 0 ? ` · ${status.hidden.toLocaleString()} hidden` : ""}
            {status.collapsed > 0 ? ` · ${status.collapsed.toLocaleString()} duplicate copies collapsed` : ""}
          </p>
        </div>
        <Segmented
          options={views}
          value={view}
          onChange={(next) => {
            setView(next);
            setAlbumSlug(null);
            setSelected([]);
          }}
        />
      </div>

      {!status.dataPlane && (
        <p className="photos-note">
          The image gateway is not configured on this host, so only the catalogue is shown.
        </p>
      )}

      {browsing && (
        <div className="photos-toolbar">
          <label className="photos-toggle">
            <Switch
              size="small"
              checked={selecting}
              onChange={(on) => {
                setSelecting(on);
                if (!on) setSelected([]);
              }}
            />
            Select
          </label>
          {/* Show-hidden used to live here as a member-visible switch. Phase 4 moved it to the navbar
              and made it admin-only: hiding is ordinary member curation, but the hidden pile is
              revealed to an operator, and the server ignores the request from anyone else. What is
              left here is the state, reported, so nobody wonders why the album looks longer. */}
          {showHidden && status.canShowHidden && (
            <span className="photos-note">Showing hidden photos (from the navbar switch).</span>
          )}
        </div>
      )}

      {browsing && selecting && (
        <PhotoSelectionBar
          ids={selected}
          people={people}
          onReloadPeople={loadPeople}
          onChanged={changed}
          onClear={() => setSelected([])}
        />
      )}

      {view === "folders" && (
        <PhotoFolders
          key={`folders-${refreshKey}-${showHidden}`}
          includeHidden={showHidden}
          onOpen={(item) => setOpenAssetId(item.id)}
          selection={selection}
          onMakeAlbum={makeAlbumFromFolder}
        />
      )}

      {(view === "timeline" || view === "undated") && (
        <PhotoTimeline
          key={`${view}-${refreshKey}-${showHidden}`}
          undated={view === "undated"}
          includeHidden={showHidden}
          onOpen={(item) => setOpenAssetId(item.id)}
          selection={selection}
        />
      )}

      {view === "albums" &&
        (albumSlug ? (
          <PhotoAlbumDetail
            slug={albumSlug}
            onBack={() => {
              setAlbumSlug(null);
              changed();
            }}
            onOpen={(item) => setOpenAssetId(item.id)}
          />
        ) : (
          <PhotoAlbums key={`albums-${refreshKey}`} onOpenAlbum={setAlbumSlug} />
        ))}

      {/* onOpenAsset unwraps the id, exactly as every sibling surface above does: the person page's
          grid hands its onOpen the whole CARD. Passing the setter raw stored a card OBJECT as the
          open-asset id and the lightbox then asked the server for asset "[object Object]" — every
          photo on every person page was unopenable. */}
      {view === "people" && (
        <PhotoPeople
          key={`people-${refreshKey}`}
          people={people}
          unnamed={unnamed}
          loading={peopleLoading}
          onReload={loadPeople}
          onOpenAsset={(item) => setOpenAssetId(item.id)}
          onChanged={changed}
        />
      )}

      {view === "tag" && (
        <PhotoTagQueue
          key={`tag-${refreshKey}`}
          people={people}
          onReloadPeople={loadPeople}
          onChanged={changed}
        />
      )}

      {view === "dupes" && <PhotoDupes key={`dupes-${refreshKey}`} onChanged={changed} />}

      {view === "review" && <PhotoReview key={`review-${refreshKey}`} admin={!!status.admin} onChanged={changed} />}

      <PhotoLightbox
        assetId={openAssetId}
        onClose={() => setOpenAssetId(null)}
        onChanged={changed}
        // The lightbox is the exception to the unwrapping above: its "other copies" strip already
        // hands back a plain id (member.card.id), so this one takes the setter raw.
        onOpenAsset={setOpenAssetId}
        people={people}
        onReloadPeople={loadPeople}
      />
    </div>
  );
}
