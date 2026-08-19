import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { useHistory, useLocation } from "react-router-dom";
import { Spin } from "antd";
import { MovieAPI } from "../../MovieAPI";
import { useMusicPlayer } from "../../Music/MusicPlayerContext";
import MusicAlbumArt from "../../Music/MusicAlbumArt";
import CatalogPager, { bucketsFor } from "../../Components/CatalogPager";
import useGridWindow from "../../hooks/useGridWindow";
import MusicAlbumModal from "./MusicAlbumModal";
import LoadFailure from "../../Components/LoadFailure";
import MusicPlaylistPickerModal from "./MusicPlaylistPickerModal";
import MusicPlaylistManageModal from "./MusicPlaylistManageModal";
import MusicSongRow from "./MusicSongRow";
import "./MusicPage.css";
import "./MusicPlaylists.css";
import { formatDuration } from "../../utils/format";

// ── The music library (music-plan.md §2.6) ──────────────────────────────────
// Catalog strategy: artists (333) and albums (1.3k) load whole, once, and every view/search over
// them is client-side — the BoardGames pattern for a modest catalog. Songs are the one thing the
// client can't hold (20k+), so a live q of 2+ chars also asks the server for matching tracks.
// URL is the state store (arcade convention): ?view=artists|albums, ?q=, ?artist=<id>.
// Artists is the DEFAULT view — the shelf people browse by is the performer, not the album.
//
// ── The grid: the WHOLE list, always ────────────────────────────────────────
// The catalog is already in the browser, so the rendered list is simply all of it and useGridWindow
// mounts only the rows near the viewport (the rest is reserved by two spacers). There is no paging
// here, and deliberately no `startIndex`.
//
// There used to be. A letter jump re-anchored the rendered slice at that letter's offset, which is
// what made "tap J and you cannot scroll up into A–I" (reported 2026-08-13): the earlier artists had
// stopped existing, so there was nothing above to scroll to. Re-anchoring bought nothing the
// windowing wasn't already giving — the only reason it existed is that the arcade, which shares this
// pager, genuinely cannot render pages it has not fetched. Music can, so a jump is now what it
// always looked like: a SCROLL into a list that stays whole (useGridWindow's scrollToIndex).

const PAGE_STEP = 120; // only the pager's page-mode arithmetic; the letters mode ignores it
const NO_ITEMS = [];

// The shelves (MusicArtist.Kind). No ?kind= is the music library — the untagged rows, which is 771
// of 813 artists — and the two named shelves are where the spoken-word material lives instead of in
// the middle of it. Kept as a list rather than three branches so the rail and the headings read off
// the same table and can't disagree about what a shelf is called.
export const MUSIC_KINDS = [
  { key: "", label: "Music", noun: { artists: "Artists", albums: "Albums" } },
  { key: "comedy", label: "Comedy", noun: { artists: "Comedians", albums: "Comedy albums" } },
  { key: "audiobook", label: "Audiobooks", noun: { artists: "Authors", albums: "Audiobooks" } },
];

const kindOf = (raw) => (MUSIC_KINDS.some((k) => k.key && k.key === raw) ? raw : "");

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
  const kind = kindOf(params.get("kind"));
  const shelf = MUSIC_KINDS.find((k) => k.key === kind) || MUSIC_KINDS[0];
  const artistParam = parseInt(params.get("artist"), 10);

  const [albums, setAlbums] = useState(null);
  const [artists, setArtists] = useState(null);
  const [songResults, setSongResults] = useState(null);
  const [artistDetail, setArtistDetail] = useState(null);
  const [catalogError, setCatalogError] = useState(false);
  const [retryNonce, setRetryNonce] = useState(0);
  // The open album modal lives in the URL (?album=<id>) — the artist drill-in (?artist=) already
  // did, so the album sheet now closes on Back and survives a reload/share the same way.
  const albumParam = parseInt(params.get("album"), 10);
  const openAlbumId = Number.isInteger(albumParam) && albumParam > 0 ? albumParam : null;
  const setOpenAlbumId = (id) => setParam("album", id, { replace: id == null });
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
    // Re-fetched per shelf rather than filtered client-side: the whole point of a shelf is that its
    // rows never entered the browse catalog, and holding all 813 artists in order to hide 42 of them
    // would put the excluded material one stale filter away from the grid it was excluded from.
    setAlbums(null);
    setArtists(null);
    setCatalogError(false);
    Promise.all([
      MovieAPI.getMusicAlbums(kind).then((r) => (r.ok ? r.json() : Promise.reject(r.status))),
      MovieAPI.getMusicArtists(kind).then((r) => (r.ok ? r.json() : Promise.reject(r.status))),
    ])
      .then(([albumData, artistData]) => {
        if (!alive) return;
        setAlbums(albumData.items || []);
        setArtists(artistData || []);
      })
      .catch(() => {
        if (!alive) return;
        // NOT empty arrays: a failed fetch rendered exactly like an empty library before.
        setCatalogError(true);
      });
    return () => { alive = false; };
  }, [gated, kind, retryNonce]);

  // Server song search rides the same q, debounced a touch.
  useEffect(() => {
    if (q.length < 2) {
      setSongResults(null);
      return undefined;
    }
    const t = setTimeout(() => {
      // Scoped to the shelf, like the grid: a search from the music library that surfaced 429 comedy
      // bits would be the pollution problem back again, one input to the left.
      MovieAPI.searchMusicTracks(q, kind)
        .then((r) => (r.ok ? r.json() : { tracks: [] }))
        .then((data) => setSongResults(data.tracks || []))
        .catch(() => setSongResults([]));
    }, 250);
    return () => clearTimeout(t);
  }, [q, kind]);

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

  // The whole list, every time. `resetKey` names what makes it a DIFFERENT list — a shelf, a view or
  // a search — and pointedly not a jump, because a jump does not change the list at all any more.
  const { hostRef, gridRef, start, end, padTop, padBottom, visibleStart, scrollToIndex } =
    useGridWindow(gridItems.length, { resetKey: `${kind}:${view}:${lowerQ}` });
  const visibleItems = useMemo(
    () => gridItems.slice(start, end),
    [gridItems, start, end]
  );

  // A–Z buckets over the list as ordered by the server: artists by their sort name, albums by their
  // ARTIST's (that's what /API/Music/Albums orders on).
  const letters = useMemo(
    () => bucketsFor(gridItems, view === "artists"
      ? (a) => a.sortName || a.name
      : (a) => a.artistSortName || a.artistName),
    [gridItems, view]
  );

  // A jump is a SCROLL, not a re-slice. The list is untouched, so the letters before the one tapped
  // are still above you — which is the whole point (2026-08-13: "I tapped J and couldn't get back to
  // the artists before J").
  const jumpTo = useCallback((offset) => {
    scrollToIndex(Math.max(0, offset));
  }, [scrollToIndex]);

  function setParam(key, value, { replace = false } = {}) {
    const p = new URLSearchParams(location.search);
    if (value != null && value !== "") p.set(key, value);
    else p.delete(key);
    const search = p.toString() ? `?${p.toString()}` : "";
    // replace: closing a sheet shouldn't grow the history (Back would reopen it).
    if (replace) history.replace({ pathname: "/music", search });
    else history.push({ pathname: "/music", search });
  }

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

  if (gated) {
    return (
      <div className="music-page">
        <div className="music-gate-note">
          Music streaming needs a password-protected account — ask the site admin.
        </div>
      </div>
    );
  }

  if (catalogError) {
    return (
      <div className="music-page">
        <LoadFailure message="Couldn't load the music library." onRetry={() => setRetryNonce((n) => n + 1)} />
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
                time={formatDuration(t.durationSec)}
                disabled={!player.isPlayable(t)}
                onPlay={() => playSearchSong(i)}
                onQueue={() => player.enqueue([searchSongEntries()[i]])}
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
            {shelf.noun[view]}
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
          {/* Scrolls the same whole list; the active letter follows the grid as you scroll, and
              everything before the letter you tapped is still up there. */}
          <CatalogPager
            mode="letters"
            letters={letters}
            total={gridItems.length}
            pageSize={PAGE_STEP}
            currentIndex={visibleStart}
            onJump={jumpTo}
            itemNoun={view === "artists" ? "artist" : "album"}
          />
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
