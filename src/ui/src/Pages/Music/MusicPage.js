import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { useHistory, useLocation } from "react-router-dom";
import { MovieAPI } from "../../MovieAPI";
import { useMusicPlayer } from "../../Music/MusicPlayerContext";
import { AlbumCard, ArtistCard } from "./MusicCards";
import MusicAlbumModal from "./MusicAlbumModal";
import { peakOf } from "./musicPopularity";
import LoadFailure from "../../Components/LoadFailure";
import MusicPlaylistPickerModal from "./MusicPlaylistPickerModal";
import MusicPlaylistManageModal from "./MusicPlaylistManageModal";
import MusicSongRow from "./MusicSongRow";
import "./MusicPage.css";
import "./MusicPlaylists.css";
import { formatDuration } from "../../utils/format";
import { useDebouncedCallback } from "../../hooks/useDebounce";
import useIsMobile from "../../hooks/useIsMobile";
import CatalogHost, { AVAILABLE_VIEWS } from "../../catalog/CatalogHost";
import { hasFacetValue } from "../../catalog/rail/facetSpec";
import { facetStateKey } from "../../catalog/rail/facetUrl";
import useSectionRail from "../../catalog/rail/useSectionRail";
import sectionRailSurfaces from "../../catalog/rail/sectionRailSurfaces";
import { createMusicSource } from "../../catalog/sources/musicSource";
import { readCatalogDefaults, resolveViewState } from "../../catalog/state/useCatalogView";
import { FLAT_VIEWS } from "../../catalog/types";
import { MUSIC_KINDS, legacyToMusicSearch, shelfOf } from "./musicFacetSpec";
import useMusicBrowse, { MUSIC_ENTITY_PARAMS, useMusicResults } from "./useMusicShelf";

// ── The music library (music-plan.md §2.6) ──────────────────────────────────
// Catalog strategy: artists (333) and albums (1.3k) load whole, once, and every view/search over
// them is client-side — the BoardGames pattern for a modest catalog. Songs are the one thing the
// client can't hold (20k+), so a live q of 2+ chars also asks the server for matching tracks.
// URL is the state store (arcade convention): the rail's `q/f/x/y` (R9 S2c — `f=kind:` names the shelf,
// `f=artist:` / `f=tag:` / `y=` filter it), ?artist=<id> the drill, ?album=<id> the open sheet. (`?view=` is
// the catalog switcher's — Grid/Wall/Shelves… — site-wide; the old ?tab= / ?view=artists|albums and ?kind=
// links are rewritten once on arrival by `legacyToMusicSearch`.)
// "One per artist" is the DEFAULT Items mode — the shelf people browse by is the performer, not the album.
//
// ── The grid: the WHOLE list, always ────────────────────────────────────────
// The catalog is already in the browser, so the rendered list is simply all of it, paged into the
// site's ONE band engine (R9 S3) — which mounts only the rows near the viewport and holds the rest
// of the height in two spacers. There is no paging here, and deliberately no `startIndex`.
//
// There used to be. A letter jump re-anchored the rendered slice at that letter's offset, which is
// what made "tap J and you cannot scroll up into A–I" (reported 2026-08-13): the earlier artists had
// stopped existing, so there was nothing above to scroll to. Re-anchoring bought nothing the
// windowing wasn't already giving — the only reason it existed is that the arcade, which shares this
// pager, genuinely cannot render pages it has not fetched. Music can, so a jump is now what it
// always looked like: a SCROLL into a list that stays whole (the engine's `jumpToUnit`).

// The shelves (MusicArtist.Kind). No ?kind= is the music library — the untagged rows, which is 771
// of 813 artists — and the two named shelves are where the spoken-word material lives instead of in
// the middle of it. Kept as a list rather than three branches so the rail and the headings read off
// the same table and can't disagree about what a shelf is called.
export { MUSIC_KINDS };

function MusicPage({ userData }) {
  const history = useHistory();
  const location = useLocation();
  const isMobile = useIsMobile();
  const player = useMusicPlayer();
  // Streaming is password-only (§3.1): every /API/Music/* route sits behind the StreamingUser
  // policy. Without one, the fetches below would all 401 into an empty library, so don't make them.
  const gated = !userData?.hasPassword;

  const params = new URLSearchParams(location.search);
  const artistParam = parseInt(params.get("artist"), 10);

  // ── The facet rail's state (R9 S2c): the URL is the filter; the sider rail reads the same URL. ──
  // The shelf (`f=kind:`) decides what is FETCHED — stale-while-revalidate per shelf through one
  // shared React-Query resource (the sider rail reads the same rows); the last catalog renders
  // instantly on a revisit while the fresh fetch replaces it in the background.
  const browse = useMusicBrowse(userData);
  const { kind, spec } = browse;
  const shelf = shelfOf(kind);
  const rail = useSectionRail("music", spec, { entityParams: MUSIC_ENTITY_PARAMS, facetsEnabled: !gated });
  const facetState = rail.state;
  const facetActions = rail.actions;
  const q = facetState.q;
  const albums = browse.loading ? null : browse.albums;
  const artists = browse.loading ? null : browse.artists;
  const [songResults, setSongResults] = useState(null);
  const [artistDetail, setArtistDetail] = useState(null);

  // A legacy ?kind= / ?tab= / ?view=artists|albums link becomes the rail's `f=kind:` and the
  // catalog's ?items= once, in place — so the catalog switcher's own ?view= (Grid / Wall / Shelves…)
  // can never be read as a tab and the shelf picker's old param keeps working from bookmarks.
  useEffect(() => {
    const legacy = legacyToMusicSearch(location.search);
    if (legacy != null) history.replace({ pathname: "/music", search: legacy, state: location.state });
  }, [location.search, location.state, history]);

  // The open album modal lives in the URL (?album=<id>) — the artist drill-in (?artist=) already
  // did, so the album sheet now closes on Back and survives a reload/share the same way.
  const albumParam = parseInt(params.get("album"), 10);
  const openAlbumId = Number.isInteger(albumParam) && albumParam > 0 ? albumParam : null;
  const setOpenAlbumId = (id) => setParam("album", id, { replace: id == null });
  // Playlists (music-plan.md Phase 3): the two modals the song rows drive. The page itself no longer
  // HOLDS the list — its "Playlists n · Manage playlists →" head is gone (Playlists is a bar tab), so
  // fetching it here on every mount was a request nothing read. The /music/playlists route owns it.
  const [pickerTracks, setPickerTracks] = useState(null); // non-null ⇒ picker open, holds what to add
  const [pickerName, setPickerName] = useState("");
  const [managePlaylistId, setManagePlaylistId] = useState(null);

  function openPicker(tracks, suggestedName = "") {
    setPickerTracks(tracks);
    setPickerName(suggestedName);
  }

  function playPlaylist(id, { shuffle = false } = {}) {
    MovieAPI.getMusicPlaylistItems(id)
      .then((r) => (r.ok ? r.json() : Promise.reject(r.status)))
      .then((data) => {
        const tracks = (data.items || []).map((t) => ({
          id: t.id,
          title: t.title,
          artist: t.artistName,
          album: t.albumTitle,
          albumId: t.albumId,
          durationSec: t.durationSec,
          requiresTranscode: t.requiresTranscode,
          missing: t.missing,
        }));
        if (shuffle) player.shuffleTracks(tracks);
        else player.playTracks(tracks, 0);
      })
      .catch(() => {});
  }

  // (The per-shelf catalog fetch lives in useCachedResource above — re-fetched per shelf rather
  // than filtered client-side: the whole point of a shelf is that its rows never entered the
  // browse catalog, and holding all 813 artists in order to hide 42 of them would put the
  // excluded material one stale filter away from the grid it was excluded from.)

  // Server song search rides the same q, debounced a touch.
  // Scoped to the shelf, like the grid: a search from the music library that surfaced 429 comedy
  // bits would be the pollution problem back again, one input to the left.
  const searchSongs = useDebouncedCallback((term, shelf) => {
    MovieAPI.searchMusicTracks(term, shelf)
      .then((r) => (r.ok ? r.json() : { tracks: [] }))
      .then((data) => setSongResults(data.tracks || []))
      .catch(() => setSongResults([]));
  }, 250);

  useEffect(() => {
    // A q that fell below the minimum clears NOW and cancels anything armed — a late landing would
    // repopulate the list the user just emptied.
    if (q.length < 2) {
      searchSongs.cancel();
      setSongResults(null);
      return;
    }
    searchSongs(q, kind);
  }, [q, kind, searchSongs]);

  // Artist detail (albums + loose tracks) when ?artist= is present.
  useEffect(() => {
    if (!Number.isInteger(artistParam)) {
      setArtistDetail(null);
      return undefined;
    }
    let alive = true;
    setArtistDetail(null);
    MovieAPI.getMusicArtist(artistParam)
      .then((r) => (r.ok ? r.json() : Promise.reject(r.status)))
      .then((data) => alive && setArtistDetail(data))
      .catch(() => alive && setArtistDetail({ missing: true }));
    return () => { alive = false; };
  }, [artistParam]);

  // The facet state over the shelf's rows: the albums it keeps, and the artists the "one per artist"
  // grid shows (every artist of the shelf until something narrows the albums).
  const results = useMusicResults(browse, facetState);
  const filteredAlbums = results.albums;
  const filteredArtists = results.artists;

  // The one browse list this page's scroll engine drives. Drilled into an artist there is no browse
  // grid (that view renders the artist's own, short album list), so it idles on an empty list rather
  // than windowing something nobody is looking at.
  const drilledIn = Number.isInteger(artistParam);
  const listKey = `${kind}:${facetStateKey(facetState)}`;

  // ── The catalog (R9 S3: ONE engine under every view) ──────────────────────────────────────────
  // The open handlers are read through a ref so a fresh setParam never rebuilds the source.
  const openRef = useRef(null);
  openRef.current = { album: setOpenAlbumId, artist: (id) => setParam("artist", id) };
  const openAlbum = useCallback((id) => openRef.current?.album(id), []);
  const openArtist = useCallback((id) => openRef.current?.artist(id), []);
  // A group header that HAS a matching facet scopes in place and regroups by artist — one push.
  // Reached through a ref so the source's identity stays keyed on the list alone.
  const scopeRef = useRef(null);
  scopeRef.current = (patch) => {
    facetActions.apply((d) => {
      if (patch.facet && !hasFacetValue(d.include[patch.facet.key], patch.facet.value)) {
        d.include[patch.facet.key] = [...(d.include[patch.facet.key] ?? []), patch.facet.value];
      }
      if (patch.years) { d.yearMin = patch.years[0]; d.yearMax = patch.years[1]; }
    }, patch.group ? { group: patch.group } : undefined);
  };
  const scope = useCallback((patch) => scopeRef.current?.(patch), []);
  // The Grid lays THIS section's tiles into the shared bands. AlbumCard / ArtistCard are
  // MODULE-LEVEL components (the BandSlot memo law); this renderer's identity never changes, because
  // everything it varies on rides in the card item and the tweak values.
  const renderCard = useCallback((item, view) => (item.kind === "artist" ? (
    <ArtistCard artist={item.raw} onOpen={openArtist} metadata={view.metadata} hoverClass={view.hoverClass} eager={view.eager} />
  ) : (
    <AlbumCard album={item.raw} onOpen={openAlbum} metadata={view.metadata} hoverClass={view.hoverClass} eager={view.eager} />
  )), [openAlbum, openArtist]);

  const makeSource = useCallback((artistItems) => createMusicSource({
    albums: filteredAlbums,
    artists: filteredArtists,
    artistItems,
    listKey,
    renderCard,
    onOpenAlbum: openAlbum,
    onOpenArtist: openArtist,
    onScope: scope,
  }), [filteredAlbums, filteredArtists, listKey, renderCard, openAlbum, openArtist, scope]);

  // The catalog owns the sort AND the Items mode here, so the page resolves them exactly as the host
  // will — URL, then the section's remembered default. "One per artist" over a FLAT view pages the
  // ARTIST rows themselves rather than collapsing album groups to representatives: an artist with
  // only loose tracks, or one the Artist facet keeps while their albums are filtered out, has no
  // album to be represented by. The grouped views always band the albums.
  const albumSource = useMemo(() => makeSource(false), [makeSource]);
  const catalogState = useMemo(
    () => resolveViewState(location.search, readCatalogDefaults("music"), albumSource, AVAILABLE_VIEWS),
    [location.search, albumSource]
  );
  const artistItems = catalogState.items === "groups" && FLAT_VIEWS.has(catalogState.view);
  const source = useMemo(() => (artistItems ? makeSource(true) : albumSource), [artistItems, makeSource, albumSource]);

  function setParam(key, value, { replace = false } = {}) {
    const p = new URLSearchParams(location.search);
    if (value != null && value !== "") p.set(key, value);
    else p.delete(key);
    const search = p.toString() ? `?${p.toString()}` : "";
    // replace: closing a sheet shouldn't grow the history (Back would reopen it).
    if (replace) history.replace({ pathname: "/music", search });
    else history.push({ pathname: "/music", search });
  }

  // Each song list compares against ITSELF: a row's bar is its share of the loudest song in the
  // same list, so the drop it shows is the one you are actually looking at.
  const songResultPeak = useMemo(() => peakOf(songResults ?? []), [songResults]);
  const topTrackPeak = useMemo(() => peakOf(artistDetail?.topTracks ?? []), [artistDetail]);
  const loosePeak = useMemo(() => peakOf(artistDetail?.looseTracks ?? []), [artistDetail]);

  // The queue-entry shape lives in one place per list so "play from here" and "add this one to the
  // queue" can never drift into building different entries for the same track.
  function searchSongEntries() {
    return songResults.map((t) => ({
      id: t.id,
      title: t.title,
      artist: t.artistName,
      album: t.albumTitle,
      albumId: t.albumId,
      durationSec: t.durationSec,
      requiresTranscode: t.requiresTranscode,
    }));
  }

  function looseTrackEntries() {
    return artistDetail.looseTracks.map((t) => ({
      id: t.id,
      title: t.title,
      artist: artistDetail.name,
      album: null,
      durationSec: t.durationSec,
      requiresTranscode: t.requiresTranscode,
      missing: t.missing,
    }));
  }

  function playSearchSong(i) {
    player.playTracks(searchSongEntries(), i);
  }

  function playLooseTracks(i) {
    player.playTracks(looseTrackEntries(), i);
  }

  // The artist's best-known songs, as a playable queue. Separate from looseTrackEntries because
  // these come off REAL albums and carry that context — the row says "— Hunky Dory" and the play
  // bar has to agree with it.
  function topTrackEntries() {
    return (artistDetail.topTracks ?? []).map((t) => ({
      id: t.id,
      title: t.title,
      artist: artistDetail.name,
      album: t.albumTitle,
      albumId: t.albumId,
      durationSec: t.durationSec,
      requiresTranscode: t.requiresTranscode,
      missing: t.missing,
    }));
  }

  function playTopTracks(i) {
    player.playTracks(topTrackEntries(), i);
  }

  if (gated) {
    return (
      <div className="music-page">
        <div className="music-gate-note">
          Music streaming needs a password-protected account — ask the site admin.
        </div>
      </div>
    );
  }

  if (browse.error) {
    return (
      <div className="music-page">
        <LoadFailure message="Couldn't load the music library." onRetry={browse.refresh} />
      </div>
    );
  }

  if (albums === null || artists === null) {
    // First-paint skeleton in the real tile-grid layout — the site-wide convention (movies,
    // boardgames, arcade), instead of a lone spinner.
    return (
      <div className="music-page" aria-hidden="true">
        <section className="music-section">
          <div className="music-album-grid">
            {Array.from({ length: 12 }).map((_, i) => (
              <div className="music-album-card music-album-card--skeleton" key={i}>
                <div className="music-album-tile skeleton-block" />
                <div className="music-skel-line music-skel-line--title skeleton-block" />
                <div className="music-skel-line skeleton-block" />
              </div>
            ))}
          </div>
        </section>
      </div>
    );
  }

  // The rail itself is the sider's MusicSiderRail, which carries the count on its head line — and on
  // a phone that sider IS the drawer, so nothing phone-shaped is left for the page. Its share: the
  // chips over the results and, on desktop, the bar's SmartSearch.
  const { chips, surfaces } = sectionRailSurfaces(rail, isMobile, {
    placeholder: "A song, artist:Bush, tag:Live…",
  });

  return (
    <div className="music-page">
      {surfaces}
      {/* Song results (server search) come first: they're the most specific match for a query. */}
      {songResults && songResults.length > 0 && (
        <section className="music-section">
          <h2 className="music-section-head">Songs</h2>
          <div className="music-song-list">
            {songResults.map((t, i) => (
              <MusicSongRow
                key={t.id}
                no="▶"
                title={t.title}
                meta={`${t.artistName}${t.albumTitle ? ` — ${t.albumTitle}` : ""}`}
                time={formatDuration(t.durationSec)}
                popularity={t}
                popularityPeak={songResultPeak}
                disabled={!player.isPlayable(t)}
                onPlay={() => playSearchSong(i)}
                onQueue={() => player.enqueue([searchSongEntries()[i]])}
                onAdd={() => openPicker([{ id: t.id, title: t.title }], t.title)}
              />
            ))}
          </div>
        </section>
      )}
      {/* Playlists are a BAR TAB (the canvas: Explore · Browse · Playlists · Now playing · Admin),
          and the canvas's Browse goes chips → grid with nothing between. The "Playlists n · Manage
          playlists →" head that used to sit here was a third door to the same route, and on a phone
          it pushed the library a screen down. The route and its modals stay; the door is gone. */}

      {/* The browse grid — artists or albums, same engine either way. */}
      {!drilledIn && (
        <section className="music-section">
          {/* No section head here since R9 S1: the SectionBar names the page, and the count belongs
              to the rail. The Songs head above is a content section and stays. */}
          {/* The letter strip is the package's now, over the same whole list: a jump is a scroll,
              so everything before the letter you tapped is still up there. */}
          <CatalogHost section="music" source={source} beforeResults={chips} />
        </section>
      )}

      {drilledIn && artistDetail && !artistDetail.missing && (
        <section className="music-section">
          <button className="music-back" onClick={() => setParam("artist", null)}>
            ← All {shelf.noun.artists.toLowerCase()}
          </button>
          <h2 className="music-section-head">
            {artistDetail.name}
            {artistDetail.yearRange && <span className="music-count">{artistDetail.yearRange}</span>}
          </h2>
          {/* "Which songs of theirs are the well-known ones" — an answer that cuts ACROSS the
              records below, so it cannot live in the album grid. Above the grid for the reason the
              Songs results sit above the browse grid: it is the more specific answer, and on an
              artist with forty albums it would otherwise be a scroll away. Absent entirely until the
              enrich pass has reached this artist, which is the honest empty state. */}
          {artistDetail.topTracks?.length > 0 && (
            <>
              <h3 className="music-subhead">Most popular</h3>
              <div className="music-song-list">
                {artistDetail.topTracks.map((t, i) => (
                  <MusicSongRow
                    key={t.id}
                    no="▶"
                    title={t.title}
                    meta={t.albumTitle || undefined}
                    time={formatDuration(t.durationSec)}
                    popularity={t}
                    popularityPeak={topTrackPeak}
                    disabled={!player.isPlayable(t)}
                    onPlay={() => playTopTracks(i)}
                    onQueue={() => player.enqueue([topTrackEntries()[i]])}
                    onAdd={() => openPicker([{ id: t.id, title: t.title }], t.title)}
                  />
                ))}
              </div>
              <h3 className="music-subhead">Albums</h3>
            </>
          )}
          <div className="music-album-grid">
            {artistDetail.albums.map((a) => (
              <AlbumCard
                key={a.id}
                album={{ ...a, artistName: artistDetail.name }}
                onOpen={setOpenAlbumId}
              />
            ))}
          </div>
          {artistDetail.looseTracks.length > 0 && (
            <>
              <h3 className="music-subhead">Loose tracks</h3>
              <div className="music-song-list">
                {artistDetail.looseTracks.map((t, i) => (
                  <MusicSongRow
                    key={t.id}
                    no="▶"
                    title={t.title}
                    time={formatDuration(t.durationSec)}
                    popularity={t}
                    popularityPeak={loosePeak}
                    disabled={!player.isPlayable(t)}
                    onPlay={() => playLooseTracks(i)}
                    onQueue={() => player.enqueue([looseTrackEntries()[i]])}
                    onAdd={() => openPicker([{ id: t.id, title: t.title }], t.title)}
                  />
                ))}
              </div>
            </>
          )}
        </section>
      )}

      <MusicAlbumModal albumId={openAlbumId} onClose={() => setOpenAlbumId(null)} onAddToPlaylist={openPicker} />

      <MusicPlaylistPickerModal
        open={pickerTracks != null}
        tracks={pickerTracks || []}
        defaultName={pickerName}
        onClose={() => setPickerTracks(null)}
      />

      <MusicPlaylistManageModal
        open={managePlaylistId != null}
        playlistId={managePlaylistId}
        onClose={() => setManagePlaylistId(null)}
      />
    </div>
  );
}

export default MusicPage;
