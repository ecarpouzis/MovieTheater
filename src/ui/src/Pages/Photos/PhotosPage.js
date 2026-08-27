import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { createPortal } from "react-dom";
import useSlot, { BAR_TOOLS_SLOT } from "../../catalog/bar/useSlot";
import { Switch as AntSwitch } from "antd";
import { Route, Switch, useHistory, useLocation } from "react-router-dom";
import { MovieAPI } from "../../MovieAPI";
import PhotoTimeline from "./PhotoTimeline";
import PhotoFolders from "./PhotoFolders";
import PhotoLightbox from "./PhotoLightbox";
import PhotoAlbums from "./PhotoAlbums";
import PhotoAlbumDetail from "./PhotoAlbumDetail";
import PhotoGallery from "./PhotoGallery";
import PhotoReview from "./PhotoReview";
import PhotoDupes from "./PhotoDupes";
import PhotoPeople from "./PhotoPeople";
import PhotoTagQueue from "./PhotoTagQueue";
import PhotoSelectionBar from "./PhotoSelectionBar";
import useIsMobile from "../../hooks/useIsMobile";
import useScrollLockRestore from "../../hooks/useScrollLockRestore";
import useShowHiddenPhotos from "../../hooks/useShowHiddenPhotos";
import usePhotosAlbum, { photosSection } from "../../hooks/usePhotosAlbum";
import CatalogHost from "../../catalog/CatalogHost";
import { createPhotosSource } from "../../catalog/sources/photosSource";
import { BarSearchSlot } from "../../catalog/bar/BarSearch";
import FacetRail from "../../catalog/rail/FacetRail";
import FilterPill from "../../catalog/rail/FilterPill";
import RailChips from "../../catalog/rail/RailChips";
import SmartSearch from "../../catalog/rail/SmartSearch";
import { facetStateKey } from "../../catalog/rail/facetUrl";
import { savableSearch, useSavedSearches } from "../../catalog/rail/savedSearches";
import useFacetOptions from "../../catalog/rail/useFacetOptions";
import useFacetState from "../../catalog/rail/useFacetState";
import useRailSheet from "../../catalog/rail/useRailSheet";
import { isGroupedBrowse } from "../../catalog/state/useCatalogView";
import { PHOTOS_ENTITY_PARAMS, photosFacetSpec, photosFilterParams } from "./photosFacetSpec";
import { usePhotosResultTotal } from "./PhotosSiderRail";
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

/**
 * The three lines at the top of an album page (docs/photos-plan.md §2.12).
 *
 * Exported and pure because they are the whole of the museum treatment's LOGIC — which of two names
 * leads, and whether the second one is printed at all — and asserting on that directly beats
 * mounting a page to read an <h1>.
 *
 * The rules: the eyebrow names the shelf, so a gallery collection never claims to be a family album.
 * An artist collection puts the ARTIST in the headline, because the artist is the more useful of the
 * two things to set in 21px capitals, and drops the collection's own title to the subtitle — unless
 * they are the same words, in which case there is nothing to say twice.
 */
export function albumEyebrow(albumSlug, meta) {
  if (!albumSlug) return "Family album";
  if (meta?.shelf !== "Archive") return "Albums";
  return meta?.artistName ? "Gallery · Artist" : "Gallery";
}

export function albumHeadline(albumTitle, meta) {
  return meta?.artistName || albumTitle || "Album";
}

export function albumSubtitle(albumTitle, meta) {
  const artist = meta?.artistName;
  if (!artist || !albumTitle || artist === albumTitle) return null;
  return albumTitle;
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
  // §2.12: an album page's eyebrow says which shelf it is on, and an artist collection puts the
  // ARTIST in the 21px capitals with the album's own title beneath. Both facts belong to the album,
  // which only the detail component has loaded — so it publishes them the same way it publishes the
  // title, rather than the head re-fetching the album to draw two words.
  const [albumMeta, setAlbumMeta] = useState(null);

  // Curation state (§2.9). Selection mode is explicit: a click either opens a photo or selects it,
  // and which of those it does is never a guess about modifier keys.
  const [selecting, setSelecting] = useState(false);
  // The SectionBar's tools slot (R9 S1): the Select toggle is portaled there.
  const barToolsSlot = useSlot(BAR_TOOLS_SLOT);
  const [selected, setSelected] = useState([]);
  // Admin-only, and driven from the NAVBAR rather than from here (Phase 4 addendum). The server
  // ignores the parameter for a non-admin regardless, so this is purely what the page ASKS for.
  const [showHidden] = useShowHiddenPhotos();
  // Bumped after a STRUCTURAL change so the browse lists re-fetch rather than showing a stale answer.
  // Deliberately not bumped by ordinary curation — see `curated` below.
  const [refreshKey, setRefreshKey] = useState(0);
  // ── The facet rail's state (R9 S2c) on the /photos/browse route: the URL is the filter; the
  // sider's PhotosSiderRail reads the same URL. The Timeline root is untouched by it. ──
  const onBrowse = location.pathname.startsWith("/photos/browse");
  const facetSpec = useMemo(() => photosFacetSpec(String(refreshKey), !!showHidden), [refreshKey, showHidden]);
  const { state: facetState, actions: facetActions, activeCount } = useFacetState(facetSpec, { entityParams: PHOTOS_ENTITY_PARAMS });
  const facetLists = useFacetOptions(facetSpec, onBrowse);
  const sheet = useRailSheet();
  const facetTotal = usePhotosResultTotal(facetState, !!showHidden, onBrowse && sheet.isMobile);
  const grouped = isGroupedBrowse(location.search, "photos");
  const savedSearches = useSavedSearches("photos");
  const saveCurrent = useCallback((name) => savedSearches.save(name, savableSearch(location.search, PHOTOS_ENTITY_PARAMS)), [savedSearches, location.search]);
  const browseFilter = useMemo(() => photosFilterParams(facetState), [facetState]);
  const browseFilterKey = facetStateKey(facetState);
  // The catalog views' source (the /photos/browse route). Re-made on the hidden toggle, the rail's
  // filter and the same structural refreshes the lists re-fetch on; its open handlers come through a
  // ref set below.
  const photosOpenRef = useRef(null);
  const photosSource = useMemo(
    () => createPhotosSource({
      includeHidden: showHidden,
      filter: browseFilter,
      listKey: `${refreshKey}:${showHidden}:${browseFilterKey}`,
      onOpen: (id) => photosOpenRef.current?.photo(id),
      onOpenAlbum: (slug) => photosOpenRef.current?.album(slug),
      onOpenFolder: (path) => photosOpenRef.current?.folder(path),
    }),
    // browseFilterKey names the filter; browseFilter is its serialization.
    // eslint-disable-next-line react-hooks/exhaustive-deps
    [showHidden, refreshKey, browseFilterKey]
  );
  // What the last curation write did, for the lists to apply to the cards they are already holding.
  const [patch, setPatch] = useState(null);
  // What the grid currently has laid out, published by PhotoGrid so "Select all" does not have to
  // re-derive a list only the grid knows the shape of.
  const visibleIdsRef = useRef([]);
  const registerVisible = useCallback((ids) => {
    visibleIdsRef.current = ids;
  }, []);

  const view = photosSection(location.pathname);
  const folderPath = folderPathFromUrl(location.pathname);
  const albumSlug = albumSlugFromUrl(location.pathname);
  const openAssetId = assetIdFromUrl(location.search);

  // A modal locks the window's scroll, and on a phone that costs the reader their place in the list
  // behind it — the "closing a photo puts me back at the top" report. See the hook.
  useScrollLockRestore(!!openAssetId);

  // Leaving a view drops its selection, exactly as switching tabs used to.
  useEffect(() => {
    setSelected([]);
  }, [view]);

  // A different album (or none) means the held title is somebody else's.
  useEffect(() => {
    setAlbumTitle(null);
    setAlbumMeta(null);
  }, [albumSlug]);

  /** A structural change — an album created, a batch approved, a duplicate group settled. The lists
   *  are rebuilt, and the reader is knowingly returned to the top of one. */
  const changed = () => {
    setSelected([]);
    setRefreshKey((k) => k + 1);
    refresh();
  };

  /**
   * An ordinary curation write, applied to the cards the list is ALREADY holding (§2.9).
   *
   * This used to call `changed()`, which remounts the browse list — so sending forty photographs to
   * the gallery re-fetched the timeline from the newest photograph and dropped the reader at the top,
   * a thousand pixels above where they were working. The batch job is inherently repetitive; making
   * every round of it start with "scroll back to where I was" is what made it not worth doing.
   *
   * The counts in the header still come from the server (`refresh`), so nothing here invents a total.
   * What is patched locally is only what the write itself already told us: these ids, this flag.
   */
  const curated = (ids, changes) => {
    setSelected([]);
    if (ids?.length && changes && Object.keys(changes).length > 0) {
      setPatch((prev) => ({ seq: (prev?.seq ?? 0) + 1, ids, changes }));
    }
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

  // A Set rather than `selected.includes`: a full month selected is several hundred ids, checked once
  // per tile on every render, and the linear scan turns a scroll into a stutter.
  const selectedIds = new Set(selected);

  const selection = {
    active: selecting,
    has: (id) => selectedIds.has(id),
    toggle: (id) => setSelected((prev) => (prev.includes(id) ? prev.filter((x) => x !== id) : prev.concat(id))),
    // Turning the mode ON from a tile, which is what the corner target and press-and-hold do. The
    // switch at the top of the page is still there; it is simply no longer the ONLY way in, which it
    // was — and it scrolls away, which made "put these in an album" a scroll to the top first.
    enable: () => setSelecting(true),
    selectMany: (ids, on) =>
      setSelected((prev) => {
        if (!on) {
          const dropping = new Set(ids);
          return prev.filter((id) => !dropping.has(id));
        }
        const next = new Set(prev);
        ids.forEach((id) => next.add(id));
        return Array.from(next);
      }),
    register: registerVisible,
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
  // The catalog views' way in (the /photos/browse route): the lightbox, an album page, a folder view.
  // Read through a ref so the source (memoised on the hidden toggle + refresh) never rebuilds for a
  // fresh handler identity.
  photosOpenRef.current = {
    photo: showAsset,
    album: (slug) => history.push(`/photos/albums/${encodeURIComponent(slug)}`),
    folder: (path) => history.push(folderUrl(path)),
  };

  const makeAlbumFromFolder = async (path, name) => {
    const response = await MovieAPI.createPhotoAlbum({ title: name || path, fromFolder: path });
    if (!response.ok) return;
    const body = await response.json();
    changed();
    history.push(`/photos/albums/${encodeURIComponent(body.album.slug)}`);
  };

  // The album's counts, set as the annotation on a contact sheet: the figure in tabular mono, the
  // thing it counts in small tracked capitals underneath.
  // The curation toggle: a bar tool when the SectionBar is mounted, an in-flow toolbar otherwise.
  const selectToggle = (
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
  );

  // The gallery — and any album that lives on its shelf — is drawn on the deeper mount tone. One
  // modifier on the page root rather than a second stylesheet: it is the same room, lit differently.
  const inGallery = view === "gallery" || albumMeta?.shelf === "Archive";

  return (
    <div className={`photos-page${inGallery ? " photos-page--gallery" : ""}`}>
      {/* R9 S1: the page head and the phone tab strip are gone — the SectionBar names the view and
          carries the tabs, the counts live in the sider index. Inside an ALBUM the head stays: the
          album's name (and an artist collection's artist) is content, not chrome. */}
      {albumSlug && (
        <header className="photos-head">
          <div className="photos-head-titles">
            <p className="photos-eyebrow">{albumEyebrow(albumSlug, albumMeta)}</p>
            <h1 className="photos-title">{albumHeadline(albumTitle, albumMeta)}</h1>
            {albumSubtitle(albumTitle, albumMeta) && (
              <p className="photos-subtitle">{albumSubtitle(albumTitle, albumMeta)}</p>
            )}
          </div>
        </header>
      )}

      {!status.dataPlane && (
        <p className="photos-note">
          The image gateway is not configured on this host, so only the catalogue is shown.
        </p>
      )}

      {/* The Select toggle is a BAR tool since R9 S1 (portaled into the SectionBar's tools slot, before
          any catalog pills); the page keeps only the state note. */}
      {browsing && (barToolsSlot
        ? createPortal(selectToggle, barToolsSlot)
        : <div className="photos-toolbar">{selectToggle}</div>)}
      {/* Show-hidden used to live here as a member-visible switch. Phase 4 moved it to the navbar
          and made it admin-only: hiding is ordinary member curation, but the hidden pile is
          revealed to an operator, and the server ignores the request from anyone else. What is
          left here is the state, reported, so nobody wonders why the album looks longer. */}
      {browsing && showHidden && status.canShowHidden && (
        <p className="photos-note">Showing hidden photos (from the navbar switch).</p>
      )}

      {/* The bar docks to the bottom of the SCREEN (see PhotoSelectionBar), so it is reachable from
          wherever in the list the picking got to. It is shown for a selection made without the mode
          switch too — the corner target and press-and-hold both turn the mode on themselves. */}
      {browsing && (selecting || selected.length > 0) && (
        <PhotoSelectionBar
          ids={selected}
          active={selecting}
          people={people}
          onReloadPeople={refreshPeople}
          onCurated={curated}
          onChanged={changed}
          onClear={() => setSelected([])}
          onSelectAll={() => setSelected(visibleIdsRef.current.slice())}
          onDone={() => {
            setSelecting(false);
            setSelected([]);
          }}
        />
      )}

      <Switch>
        <Route path="/photos/browse">
          {/* Wall / List / Extended / Shelves / Newspaper / Directory over the timeline's own rows
              (/API/Photos/Browse + BrowseGroups), narrowed by the rail (R9 S2c): the SmartSearch in the
              bar on desktop (`person:Grandma`, `album:Summer`), the Filters pill + full-page sheet on
              phones, the active chips over the results. The timeline route keeps its justified grid. */}
          {!sheet.isMobile && (
            <BarSearchSlot>
              <SmartSearch spec={facetSpec} facets={facetLists.data} onAdd={facetActions.add} onText={facetActions.setText} placeholder="A place, person:Grandma, album:Summer…" />
            </BarSearchSlot>
          )}
          {sheet.isMobile && (
            <FacetRail
              variant="sheet"
              open={sheet.open}
              onClose={sheet.hide}
              spec={facetSpec}
              state={facetState}
              actions={facetActions}
              activeCount={activeCount}
              facets={facetLists.data}
              facetsLoading={facetLists.isLoading}
              total={facetTotal.data}
              grouped={grouped}
              saved={{ list: savedSearches.list, onApply: facetActions.replaceSearch, onRemove: savedSearches.remove, onSave: saveCurrent }}
            />
          )}
          <CatalogHost
            section="photos"
            source={photosSource}
            tools={sheet.isMobile ? <FilterPill count={activeCount} onClick={sheet.show} /> : null}
            beforeResults={<RailChips spec={facetSpec} state={facetState} actions={facetActions} facets={facetLists.data} activeCount={activeCount} onSave={saveCurrent} />}
          />
        </Route>

        <Route path="/photos/undated">
          <PhotoTimeline
            key={`undated-${refreshKey}-${showHidden}`}
            undated
            includeHidden={showHidden}
            onOpen={openAsset}
            selection={selection}
            patch={patch}
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
            patch={patch}
            onMakeAlbum={makeAlbumFromFolder}
          />
        </Route>

        <Route path="/photos/gallery">
          <PhotoGallery
            key={`gallery-${refreshKey}`}
            onOpenAlbum={(slug) => history.push(`/photos/albums/${encodeURIComponent(slug)}`)}
          />
        </Route>

        <Route
          path="/photos/albums/:slug"
          render={({ match }) => (
            <PhotoAlbumDetail
              slug={decodeURIComponent(match.params.slug)}
              onTitle={setAlbumTitle}
              onMeta={setAlbumMeta}
              onBack={() => {
                changed();
                // Back to the shelf this album is actually on (§2.12) — sending a gallery collection
                // "back" to the family album index would be a dead end, since it is not listed there.
                history.push(albumMeta?.shelf === "Archive" ? "/photos/gallery" : "/photos/albums");
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
            patch={patch}
          />
        </Route>
      </Switch>

      <PhotoLightbox
        assetId={openAssetId}
        onClose={() => showAsset(null)}
        // A link to a photo that is gone — or hidden from whoever followed it — drops back to the
        // view it pointed at. Nothing is announced: a stale share is not the reader's mistake.
        onUnavailable={() => showAsset(null)}
        // Same in-place patching as the batch bar: hiding a photo from the lightbox must not rebuild
        // the list the reader is standing in the middle of.
        onCurated={curated}
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
