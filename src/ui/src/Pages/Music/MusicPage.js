import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { useHistory, useLocation } from "react-router-dom";
import { Spin } from "antd";
import { MovieAPI } from "../../MovieAPI";
import { useMusicPlayer } from "../../Music/MusicPlayerContext";
import MusicAlbumArt from "../../Music/MusicAlbumArt";
import CatalogPager, { bucketsFor } from "../../Components/CatalogPager";
import useInfiniteScroll from "../../hooks/useInfiniteScroll";
import useGridWindow from "../../hooks/useGridWindow";
import MusicAlbumModal from "./MusicAlbumModal";
import MusicPlaylistPickerModal from "./MusicPlaylistPickerModal";
import MusicPlaylistManageModal from "./MusicPlaylistManageModal";
import MusicSongRow from "./MusicSongRow";
import "./MusicPage.css";
import "./MusicPlaylists.css";

// ── The music library (music-plan.md §2.6) ──────────────────────────────────
// Catalog strategy: artists (333) and albums (1.3k) load whole, once, and every view/search over
// them is client-side — the BoardGames pattern for a modest catalog. Songs are the one thing the
// client can't hold (20k+), so a live q of 2+ chars also asks the server for matching tracks.
// URL is the state store (arcade convention): ?view=artists|albums, ?q=, ?artist=<id>.
// Artists is the DEFAULT view — the shelf people browse by is the performer, not the album.
//
// The grid runs the same engine as the arcade lobby and Browse: one continuously appending list
// (useInfiniteScroll's sentinel), only the rows near the viewport mounted (useGridWindow), and an
// A–Z strip that SEEKS into it (CatalogPager) rather than paging it. The difference is where the
// pages come from — the arcade fetches them, music already has the whole catalog and just reveals
// more of it, so `loadMore` is a slice widening and a "jump" is free.

const PAGE_STEP = 120; // cards revealed per scroll-triggered append
const SENTINEL_STYLE = { height: 1 };
const NO_ITEMS = [];

function formatTime(sec) {
  if (!Number.isFinite(sec) || sec <= 0) return "";
  const m = Math.floor(sec / 60);
  const s = Math.floor(sec % 60);
  return `${m}:${s < 10 ? "0" : ""}${s}`;
}

function AlbumCard({ album, onOpen }) {
  return (
    <button className="music-album-card" onClick={() => onOpen(album.id)}>
      <MusicAlbumArt
        albumId={album.id}
        hasArt={album.hasArt}
        title={album.title}
        dominantColor={album.dominantColor}
      />
      <div className="music-album-card-title" title={album.title}>{album.title}</div>
      <div className="music-album-card-sub">
        <span className="music-album-card-artist" title={album.artistName}>{album.artistName}</span>
        {album.year != null && <span className="music-album-card-year">{album.year}</span>}
      </div>
      {album.tag && <div className="music-album-card-tag">{album.tag}</div>}
    </button>
  );
}

/** An artist wears their first album's cover (see /API/Music/Artists) — initials tile when none has art. */
function ArtistCard({ artist, onOpen }) {
  return (
    <button className="music-artist-card" onClick={() => onOpen(artist.id)}>
      <MusicAlbumArt
        albumId={artist.artAlbumId}
        hasArt={artist.hasArt}
        title={artist.name}
        dominantColor={artist.dominantColor}
      />
      <div className="music-artist-card-name" title={artist.name}>{artist.name}</div>
      <div className="music-artist-card-sub">
        {artist.yearRange && <span>{artist.yearRange}</span>}
        <span>{artist.albumCount} album{artist.albumCount === 1 ? "" : "s"}</span>
        <span>{artist.trackCount} track{artist.trackCount === 1 ? "" : "s"}</span>
      </div>
    </button>
  );
}

function MusicPage({ userData }) {
  const history = useHistory();
  const location = useLocation();
  const player = useMusicPlayer();
  // Streaming is password-only (§3.1): every /API/Music/* route sits behind the StreamingUser
  // policy. Without one, the fetches below would all 401 into an empty library, so don't make them.
  const gated = !userData?.hasPassword;

  const params = new URLSearchParams(location.search);
  const view = params.get("view") === "albums" ? "albums" : "artists";
  const q = (params.get("q") || "").trim();
  const artistParam = parseInt(params.get("artist"), 10);

  const [albums, setAlbums] = useState(null);
  const [artists, setArtists] = useState(null);
  const [songResults, setSongResults] = useState(null);
  const [artistDetail, setArtistDetail] = useState(null);
  const [openAlbumId, setOpenAlbumId] = useState(null);
  // The grid is ONE list the page seeks into: `startIndex` is the catalog index of the first card
  // rendered (a pager jump re-anchors it) and `shown` is how far past it scrolling has revealed.
  const [startIndex, setStartIndex] = useState(0);
  const [shown, setShown] = useState(PAGE_STEP);
  const sectionRef = useRef(null);
  // Playlists (music-plan.md Phase 3): the shelf, plus the two modals it and the song rows drive.
  const [playlists, setPlaylists] = useState([]);
  const [pickerTracks, setPickerTracks] = useState(null); // non-null ⇒ picker open, holds what to add
  const [pickerName, setPickerName] = useState("");
  const [managePlaylistId, setManagePlaylistId] = useState(null);

  const reloadPlaylists = useCallback(() => {
    if (gated) return;
    MovieAPI.getMyMusicPlaylists()
      .then((r) => (r.ok ? r.json() : []))
      .then((list) => setPlaylists(list || []))
      .catch(() => setPlaylists([]));
  }, [gated]);

  useEffect(() => { reloadPlaylists(); }, [reloadPlaylists]);

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

  useEffect(() => {
    if (gated) return undefined;
    let alive = true;
    Promise.all([
      MovieAPI.getMusicAlbums().then((r) => (r.ok ? r.json() : Promise.reject(r.status))),
      MovieAPI.getMusicArtists().then((r) => (r.ok ? r.json() : Promise.reject(r.status))),
    ])
      .then(([albumData, artistData]) => {
        if (!alive) return;
        setAlbums(albumData.items || []);
        setArtists(artistData || []);
      })
      .catch(() => {
        if (!alive) return;
        setAlbums([]);
        setArtists([]);
      });
    return () => { alive = false; };
  }, [gated]);

  // Server song search rides the same q, debounced a touch.
  useEffect(() => {
    if (q.length < 2) {
      setSongResults(null);
      return undefined;
    }
    const t = setTimeout(() => {
      MovieAPI.searchMusicTracks(q)
        .then((r) => (r.ok ? r.json() : { tracks: [] }))
        .then((data) => setSongResults(data.tracks || []))
        .catch(() => setSongResults([]));
    }, 250);
    return () => clearTimeout(t);
  }, [q]);

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

  // Reset paging whenever the visible set changes shape.
  useEffect(() => { setStartIndex(0); setShown(PAGE_STEP); }, [view, q, artistParam]);

  const lowerQ = q.toLowerCase();
  const filteredAlbums = useMemo(() => {
    if (!albums) return [];
    if (!lowerQ) return albums;
    return albums.filter(
      (a) => a.title.toLowerCase().includes(lowerQ) || a.artistName.toLowerCase().includes(lowerQ)
    );
  }, [albums, lowerQ]);

  const filteredArtists = useMemo(() => {
    if (!artists) return [];
    if (!lowerQ) return artists;
    return artists.filter((a) => a.name.toLowerCase().includes(lowerQ));
  }, [artists, lowerQ]);

  // The one browse list this page's scroll engine drives. Drilled into an artist there is no browse
  // grid (that view renders the artist's own, short album list), so it idles on an empty list rather
  // than windowing something nobody is looking at.
  const drilledIn = Number.isInteger(artistParam);
  const gridItems = drilledIn ? NO_ITEMS : view === "artists" ? filteredArtists : filteredAlbums;

  const loaded = Math.min(shown, Math.max(0, gridItems.length - startIndex));
  const hasMore = startIndex + loaded < gridItems.length;
  const loadMore = useCallback(() => setShown((s) => s + PAGE_STEP), []);
  const { sentinelRef, recheck } = useInfiniteScroll({
    enabled: gridItems.length > 0,
    hasMore,
    onLoadMore: loadMore,
  });
  // Re-check after an append without re-subscribing — keeps filling while the list is still shorter
  // than the viewport, or while the user sits parked at the bottom.
  useEffect(() => { recheck(); }, [loaded, recheck]);

  const { hostRef, gridRef, start, end, padTop, padBottom, visibleStart } = useGridWindow(loaded, {
    resetKey: `${view}:${lowerQ}:${startIndex}`,
  });
  const visibleItems = useMemo(
    () => gridItems.slice(startIndex + start, startIndex + end),
    [gridItems, startIndex, start, end]
  );

  // A–Z buckets over the list as ordered by the server: artists by their sort name, albums by their
  // ARTIST's (that's what /API/Music/Albums orders on).
  const letters = useMemo(
    () => bucketsFor(gridItems, view === "artists"
      ? (a) => a.sortName || a.name
      : (a) => a.artistSortName || a.artistName),
    [gridItems, view]
  );

  const jumpTo = useCallback((offset) => {
    setStartIndex(Math.max(0, offset));
    setShown(PAGE_STEP);
    sectionRef.current?.scrollIntoView({ block: "start" });
  }, []);

  function setParam(key, value) {
    const p = new URLSearchParams(location.search);
    if (value != null && value !== "") p.set(key, value);
    else p.delete(key);
    history.push({ pathname: "/music", search: p.toString() ? `?${p.toString()}` : "" });
  }

  function playSearchSong(i) {
    const tracks = songResults.map((t) => ({
      id: t.id,
      title: t.title,
      artist: t.artistName,
      album: t.albumTitle,
      albumId: t.albumId,
      durationSec: t.durationSec,
      requiresTranscode: t.requiresTranscode,
    }));
    player.playTracks(tracks, i);
  }

  function playLooseTracks(i) {
    const tracks = artistDetail.looseTracks.map((t) => ({
      id: t.id,
      title: t.title,
      artist: artistDetail.name,
      album: null,
      durationSec: t.durationSec,
      requiresTranscode: t.requiresTranscode,
      missing: t.missing,
    }));
    player.playTracks(tracks, i);
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

  if (albums === null || artists === null) {
    return (
      <div className="music-page music-page--loading">
        <Spin size="large" />
      </div>
    );
  }

  return (
    <div className="music-page">
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
                time={formatTime(t.durationSec)}
                disabled={!player.isPlayable(t)}
                onPlay={() => playSearchSong(i)}
                onAdd={() => openPicker([{ id: t.id, title: t.title }], t.title)}
              />
            ))}
          </div>
        </section>
      )}

      {/* Playlists live on their own route now (music-plan.md §2.4) — the strip that used to sit
          here grew with every playlist and pushed the library down the page. This is just the way in. */}
      {playlists.length > 0 && !drilledIn && (
        <section className="music-section">
          <h2 className="music-section-head">
            Playlists <span className="music-count">{playlists.length}</span>
            <button
              className="music-playlist-btn music-playlists-link"
              onClick={() => history.push("/music/playlists")}
            >
              Manage playlists →
            </button>
          </h2>
        </section>
      )}

      {/* The browse grid — artists or albums, same engine either way. */}
      {!drilledIn && (
        <section className="music-section" ref={sectionRef}>
          <h2 className="music-section-head">
            {view === "artists" ? "Artists" : "Albums"}
            <span className="music-count">{gridItems.length}</span>
          </h2>
          <div ref={hostRef}>
            {padTop > 0 && <div className="grid-spacer" style={{ height: padTop }} aria-hidden="true" />}
            <div className={view === "artists" ? "music-artist-grid" : "music-album-grid"} ref={gridRef}>
              {visibleItems.map((item) => (view === "artists" ? (
                <ArtistCard key={item.id} artist={item} onOpen={(id) => setParam("artist", id)} />
              ) : (
                <AlbumCard key={item.id} album={item} onOpen={setOpenAlbumId} />
              )))}
            </div>
            {padBottom > 0 && <div className="grid-spacer" style={{ height: padBottom }} aria-hidden="true" />}
          </div>
          <div ref={sentinelRef} aria-hidden="true" style={SENTINEL_STYLE} />
          {/* Seeks into the same continuous list; the active letter follows the grid as you scroll. */}
          <CatalogPager
            mode="letters"
            letters={letters}
            total={gridItems.length}
            pageSize={PAGE_STEP}
            currentIndex={startIndex + visibleStart}
            onJump={jumpTo}
            itemNoun={view === "artists" ? "artist" : "album"}
          />
        </section>
      )}

      {drilledIn && artistDetail && !artistDetail.missing && (
        <section className="music-section">
          <button className="music-back" onClick={() => setParam("artist", null)}>← All artists</button>
          <h2 className="music-section-head">
            {artistDetail.name}
            {artistDetail.yearRange && <span className="music-count">{artistDetail.yearRange}</span>}
          </h2>
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
                    time={formatTime(t.durationSec)}
                    disabled={!player.isPlayable(t)}
                    onPlay={() => playLooseTracks(i)}
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
        onDone={reloadPlaylists}
      />

      <MusicPlaylistManageModal
        open={managePlaylistId != null}
        playlistId={managePlaylistId}
        onClose={() => setManagePlaylistId(null)}
        onChanged={reloadPlaylists}
      />
    </div>
  );
}

export default MusicPage;
