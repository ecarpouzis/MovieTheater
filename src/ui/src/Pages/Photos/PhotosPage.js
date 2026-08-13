import { useEffect, useState } from "react";
import { Switch as AntSwitch } from "antd";
import { Route, Switch, useHistory, useLocation } from "react-router-dom";
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
import useIsMobile from "../../hooks/useIsMobile";
import useShowHiddenPhotos from "../../hooks/useShowHiddenPhotos";
import usePhotosAlbum, { photosNavViews, photosSection, photosViewLabel } from "../../hooks/usePhotosAlbum";
import "./PhotosPage.css";

// ── Family photo album (docs/photos-plan.md §4) ─────────────────────────────
// Phase 1's browse surfaces: the timeline, the date-unknown shelf it is deliberately NOT interleaved
// with (§2.7), the folder tree (§2.9), and the lightbox. Phase 2 adds curation — selection mode with
// batch hide/unhide, albums, and the review surface for suggested-hide batches and unapproved
// ingests. Phase 3 adds the duplicate review (§2.6), where a human picks which copy of a photograph
// represents it; the timeline then collapses the rest behind that pick. Phase 4 adds people, tagging
// and the keyboard-first tag queue (§2.8), and moves show-hidden to the navbar, admin-only.
//
// The views used to be local state, which meant an album could not be linked to and a refresh always
// landed back on the timeline. They are ROUTES now — /photos/albums/:slug, /photos/people/:id,
// /photos/folders/<path> — and the navbar rail links them. Nothing about what a view DOES changed;
// only where the answer to "which view" is stored.
//
// The nav hides /photos for non-members, but this page never assumes that filtering happened — a URL
// can be typed or bookmarked. It asks the server, and the server's 403 is what it renders. That is
// the §2.1 rule in miniature: the UI is not the gate, it just reports what the gate said.

/** The folder view keeps its position in the URL, one path segment per folder, so a folder deep in a
 *  device dump can be linked to. Each segment is encoded on its own — a folder name may contain any
 *  character the filesystem allows, including the "/" that separates them here. */
function folderPathFromUrl(pathname) {
  const rest = String(pathname || "").replace(/^\/photos\/folders\/?/, "");
  if (!rest) return "";
  return rest
    .split("/")
    .filter(Boolean)
    .map((segment) => {
      try {
        return decodeURIComponent(segment);
      } catch {
        return segment;
      }
    })
    .join("/");
}

function folderUrl(path) {
  if (!path) return "/photos/folders";
  const encoded = path.split("/").filter(Boolean).map(encodeURIComponent).join("/");
  return `/photos/folders/${encoded}`;
}

/** The album slug a /photos/albums/<slug> URL is on, or null anywhere else. */
function albumSlugFromUrl(pathname) {
  const match = /^\/photos\/albums\/([^/]+)/.exec(String(pathname || ""));
  if (!match) return null;
  try {
    return decodeURIComponent(match[1]);
  } catch {
    return match[1];
  }
}

/**
 * The open photograph, read out of ?photo=<id>.
 *
 * A family album's most-sent link is "look at THIS one", and until now the lightbox was local state
 * that no URL could carry. Anything that is not a plain positive integer is treated as no photo at
 * all — a mangled link asks the server for nothing rather than for "abc".
 */
function assetIdFromUrl(search) {
  const raw = new URLSearchParams(search).get("photo");
  if (!raw || !/^[0-9]+$/.test(raw)) return null;
  const id = Number(raw);
  return Number.isSafeInteger(id) && id > 0 ? id : null;
}

export default function PhotosPage({ userData }) {
  const history = useHistory();
  const location = useLocation();
  const isMobile = useIsMobile();

  // The gate's answer and the people list, shared with the navbar rail so one status request feeds
  // both (see hooks/usePhotosAlbum.js).
  const { state, status, people, unnamed, peopleLoading, refresh, refreshPeople } = usePhotosAlbum({
    username: userData?.username,
  });

  // The album detail page's title, lifted so the page's own <h1> can carry it — an album is a place,
  // and "Albums" is the shelf it sits on rather than its name.
  const [albumTitle, setAlbumTitle] = useState(null);

  // Curation state (§2.9). Selection mode is explicit: a click either opens a photo or selects it,
  // and which of those it does is never a guess about modifier keys.
  const [selecting, setSelecting] = useState(false);
  const [selected, setSelected] = useState([]);
  // Admin-only, and driven from the NAVBAR rather than from here (Phase 4 addendum). The server
  // ignores the parameter for a non-admin regardless, so this is purely what the page ASKS for.
  const [showHidden] = useShowHiddenPhotos();
  // Bumped after any curation write so the browse lists re-fetch rather than showing a stale answer.
  const [refreshKey, setRefreshKey] = useState(0);

  const view = photosSection(location.pathname);
  const folderPath = folderPathFromUrl(location.pathname);
  const albumSlug = albumSlugFromUrl(location.pathname);
  const openAssetId = assetIdFromUrl(location.search);

  // Leaving a view drops its selection, exactly as switching tabs used to.
  useEffect(() => {
    setSelected([]);
  }, [view]);

  // A different album (or none) means the held title is somebody else's.
  useEffect(() => {
    setAlbumTitle(null);
  }, [albumSlug]);

  const changed = () => {
    setSelected([]);
    setRefreshKey((k) => k + 1);
    refresh();
  };

  if (state === "loading") {
    return (
      <div className="photos-page photos-page--plate">
        <AlbumPlate eyebrow="Family album" title="Opening the album">
          <PhotoMountSkeleton />
        </AlbumPlate>
      </div>
    );
  }

  if (state === "denied") {
    // Deliberately says nothing about what is in there.
    return (
      <div className="photos-page photos-page--plate">
        <AlbumPlate eyebrow="Family album" title="Photos">
          <p className="photos-note">This area is limited to family members.</p>
        </AlbumPlate>
      </div>
    );
  }

  if (state === "error") {
    return (
      <div className="photos-page photos-page--plate">
        <AlbumPlate eyebrow="Family album" title="Photos">
          <p className="photos-note">Could not reach the photo album just now.</p>
          <button type="button" className="photos-button photos-button--stamp" onClick={refresh}>
            Try again
          </button>
        </AlbumPlate>
      </div>
    );
  }

  const empty = status?.empty !== false;

  if (empty) {
    return (
      <div className="photos-page photos-page--plate">
        <AlbumPlate eyebrow="Family album" title="Nothing here yet">
          <p className="photos-note">
            The album is set up but the collection has not been read in yet. Nothing has been copied,
            moved or changed on disk — and nothing ever will be: everything this section does is a row
            in the database.
          </p>
        </AlbumPlate>
      </div>
    );
  }

  const selection = {
    active: selecting,
    has: (id) => selected.includes(id),
    toggle: (id) => setSelected((prev) => (prev.includes(id) ? prev.filter((x) => x !== id) : prev.concat(id))),
  };

  const browsing = view === "timeline" || view === "undated" || view === "folders";

  // Opening and closing a photograph REPLACES the URL rather than pushing one. The lightbox is a
  // look, not a place: pushing would make Back walk out of a browsing session one photo at a time,
  // which is the behaviour every gallery that does it gets complained about for. Replace keeps the
  // link shareable and keeps Back meaning "the view I came from".
  const showAsset = (id) => {
    const params = new URLSearchParams(location.search);
    if (id == null) params.delete("photo");
    else params.set("photo", String(id));
    const search = params.toString();
    history.replace({ pathname: location.pathname, search: search ? `?${search}` : "" });
  };
  const openAsset = (item) => showAsset(item.id);

  const makeAlbumFromFolder = async (path, name) => {
    const response = await MovieAPI.createPhotoAlbum({ title: name || path, fromFolder: path });
    if (!response.ok) return;
    const body = await response.json();
    changed();
    history.push(`/photos/albums/${encodeURIComponent(body.album.slug)}`);
  };

  // The album's counts, set as the annotation on a contact sheet: the figure in tabular mono, the
  // thing it counts in small tracked capitals underneath.
  const facts = [
    ["photos", status.photos],
    ["videos", status.videos],
    ["undated", status.undated],
    ["hidden", status.hidden],
    ["copies collapsed", status.collapsed],
  ].filter(([, value]) => value > 0);

  return (
    <div className="photos-page">
      <header className="photos-head">
        <div className="photos-head-titles">
          {/* Inside an album the eyebrow becomes the shelf and the heading becomes the album — the
              name a family gave it is the more useful of the two things to put in 21px capitals. */}
          <p className="photos-eyebrow">{albumSlug ? "Albums" : "Family album"}</p>
          <h1 className="photos-title">{albumSlug ? albumTitle || "Album" : photosViewLabel(view)}</h1>
        </div>
        <ul className="photos-facts">
          {facts.map(([label, value]) => (
            <li className="photos-fact" key={label}>
              <span className="photos-fact-value">{value.toLocaleString()}</span>
              <span className="photos-fact-label">{label}</span>
            </li>
          ))}
        </ul>
      </header>

      {/* The rail carries the album's index. On a phone the rail is behind a hamburger, so the same
          list rides along the top of the page instead — one navigation, two shapes. */}
      {isMobile && (
        <nav className="photos-tabs" aria-label="Album sections">
          {photosNavViews(status, unnamed.length).map((item) => (
            <button
              key={item.key}
              type="button"
              className={`photos-tab${view === item.key ? " is-active" : ""}`}
              aria-current={view === item.key ? "page" : undefined}
              onClick={() => history.push(item.path)}
            >
              {item.label}
              {item.count != null && <span className="photos-tab-count">{item.count.toLocaleString()}</span>}
            </button>
          ))}
        </nav>
      )}

      {!status.dataPlane && (
        <p className="photos-note">
          The image gateway is not configured on this host, so only the catalogue is shown.
        </p>
      )}

      {browsing && (
        <div className="photos-toolbar">
          <label className="photos-toggle">
            <AntSwitch
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
          onReloadPeople={refreshPeople}
          onChanged={changed}
          onClear={() => setSelected([])}
        />
      )}

      <Switch>
        <Route path="/photos/undated">
          <PhotoTimeline
            key={`undated-${refreshKey}-${showHidden}`}
            undated
            includeHidden={showHidden}
            onOpen={openAsset}
            selection={selection}
          />
        </Route>

        <Route path="/photos/folders">
          <PhotoFolders
            key={`folders-${refreshKey}-${showHidden}`}
            path={folderPath}
            onNavigate={(next) => history.push(folderUrl(next))}
            includeHidden={showHidden}
            onOpen={openAsset}
            selection={selection}
            onMakeAlbum={makeAlbumFromFolder}
          />
        </Route>

        <Route
          path="/photos/albums/:slug"
          render={({ match }) => (
            <PhotoAlbumDetail
              slug={decodeURIComponent(match.params.slug)}
              onTitle={setAlbumTitle}
              onBack={() => {
                changed();
                history.push("/photos/albums");
              }}
              onOpen={openAsset}
            />
          )}
        />

        <Route path="/photos/albums">
          <PhotoAlbums
            key={`albums-${refreshKey}`}
            onOpenAlbum={(slug) => history.push(`/photos/albums/${encodeURIComponent(slug)}`)}
          />
        </Route>

        {/* onOpenAsset unwraps the id, exactly as every sibling surface above does: the person page's
            grid hands its onOpen the whole CARD. Passing the setter raw stored a card OBJECT as the
            open-asset id and the lightbox then asked the server for asset "[object Object]" — every
            photo on every person page was unopenable. */}
        <Route
          path="/photos/people/:id"
          render={({ match }) => (
            <PhotoPeople
              key={`people-${refreshKey}`}
              personId={Number(match.params.id)}
              onOpenPerson={(id) => history.push(`/photos/people/${id}`)}
              onBackToPeople={() => history.push("/photos/people")}
              people={people}
              unnamed={unnamed}
              loading={peopleLoading}
              onReload={refreshPeople}
              onOpenAsset={openAsset}
              onChanged={changed}
            />
          )}
        />

        <Route path="/photos/people">
          <PhotoPeople
            key={`people-${refreshKey}`}
            onOpenPerson={(id) => history.push(`/photos/people/${id}`)}
            onBackToPeople={() => history.push("/photos/people")}
            people={people}
            unnamed={unnamed}
            loading={peopleLoading}
            onReload={refreshPeople}
            onOpenAsset={openAsset}
            onChanged={changed}
          />
        </Route>

        <Route path="/photos/tag">
          <PhotoTagQueue key={`tag-${refreshKey}`} people={people} onReloadPeople={refreshPeople} onChanged={changed} />
        </Route>

        <Route path="/photos/dupes">
          <PhotoDupes key={`dupes-${refreshKey}`} onChanged={changed} />
        </Route>

        <Route path="/photos/review">
          <PhotoReview key={`review-${refreshKey}`} admin={!!status.admin} onChanged={changed} />
        </Route>

        <Route path="/photos">
          <PhotoTimeline
            key={`timeline-${refreshKey}-${showHidden}`}
            includeHidden={showHidden}
            onOpen={openAsset}
            selection={selection}
          />
        </Route>
      </Switch>

      <PhotoLightbox
        assetId={openAssetId}
        onClose={() => showAsset(null)}
        // A link to a photo that is gone — or hidden from whoever followed it — drops back to the
        // view it pointed at. Nothing is announced: a stale share is not the reader's mistake.
        onUnavailable={() => showAsset(null)}
        onChanged={changed}
        // The lightbox is the exception to the unwrapping above: its "other copies" strip already
        // hands back a plain id (member.card.id), so this one takes it raw.
        onOpenAsset={showAsset}
        people={people}
        onReloadPeople={refreshPeople}
      />
    </div>
  );
}

/** The album's first page — what a brand-new family member sees before there is anything to look at.
 *  Deliberately the same plate for "opening", "not for you", "broken" and "empty": four different
 *  sentences, one piece of furniture, so none of them reads as an error screen. */
function AlbumPlate({ eyebrow, title, children }) {
  return (
    <div className="photos-plate">
      <p className="photos-eyebrow">{eyebrow}</p>
      <h1 className="photos-plate-title">{title}</h1>
      {children}
    </div>
  );
}

/** Three empty mounts where the photographs will be. A spinner says "wait"; empty mounts say what
 *  is coming, which is the more useful thing to say to somebody opening a family album. */
function PhotoMountSkeleton() {
  return (
    <div className="photos-mounts" aria-hidden="true">
      <span className="photos-mount" />
      <span className="photos-mount" />
      <span className="photos-mount" />
    </div>
  );
}
