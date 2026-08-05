import { useCallback, useEffect, useMemo, useState } from "react";
import { useHistory, useLocation } from "react-router-dom";
import { Spin } from "antd";
import { MovieAPI } from "../../MovieAPI";
import { useMusicPlayer } from "../../Music/MusicPlayerContext";
import MusicAlbumArt from "../../Music/MusicAlbumArt";
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
// URL is the state store (arcade convention): ?view=albums|artists, ?q=, ?artist=<id>.

const PAGE_STEP = 120; // albums rendered per "Show more" — plain slicing, no virtualization yet

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

function MusicPage({ userData }) {
  const history = useHistory();
  const location = useLocation();
  const player = useMusicPlayer();

  const params = new URLSearchParams(location.search);
  const view = params.get("view") === "artists" ? "artists" : "albums";
  const q = (params.get("q") || "").trim();
  const artistParam = parseInt(params.get("artist"), 10);

  const [albums, setAlbums] = useState(null);
  const [artists, setArtists] = useState(null);
  const [songResults, setSongResults] = useState(null);
  const [artistDetail, setArtistDetail] = useState(null);
  const [openAlbumId, setOpenAlbumId] = useState(null);
  const [shown, setShown] = useState(PAGE_STEP);
  // Playlists (music-plan.md Phase 3): the shelf, plus the two modals it and the song rows drive.
  const [playlists, setPlaylists] = useState([]);
  const [pickerTracks, setPickerTracks] = useState(null); // non-null ⇒ picker open, holds what to add
  const [pickerName, setPickerName] = useState("");
  const [managePlaylistId, setManagePlaylistId] = useState(null);

  const reloadPlaylists = useCallback(() => {
    MovieAPI.getMyMusicPlaylists()
      .then((r) => (r.ok ? r.json() : []))
      .then((list) => setPlaylists(list || []))
      .catch(() => setPlaylists([]));
  }, []);

  useEffect(() => { reloadPlaylists(); }, [reloadPlaylists]);

  function openPicker(tracks, suggestedName = "") {
    setPickerTracks(tracks);
    setPickerName(suggestedName);
  }

  function playPlaylist(id) {
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
        player.playTracks(tracks, 0);
      })
      .catch(() => {});
  }

  useEffect(() => {
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
  }, []);

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
  useEffect(() => { setShown(PAGE_STEP); }, [view, q, artistParam]);

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

  if (albums === null || artists === null) {
    return (
      <div className="music-page music-page--loading">
        <Spin size="large" />
      </div>
    );
  }

  const gated = !userData?.hasPassword;

  return (
    <div className="music-page">
      {gated && (
        <div className="music-gate-note">
          Music streaming needs a password-protected account — ask the site admin.
        </div>
      )}

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
                disabled={t.requiresTranscode}
                onPlay={() => playSearchSong(i)}
                onAdd={() => openPicker([{ id: t.id, title: t.title }], t.title)}
              />
            ))}
          </div>
        </section>
      )}

      {/* My playlists — only shown once there's at least one; created from any album or song row. */}
      {playlists.length > 0 && !artistDetail && (
        <section className="music-section">
          <h2 className="music-section-head">
            Playlists <span className="music-count">{playlists.length}</span>
          </h2>
          <div className="music-playlist-grid">
            {playlists.map((p) => (
              <div className="music-playlist-card" key={p.id} data-testid="music-playlist-card">
                <div className="music-playlist-card-name" title={p.name}>{p.name}</div>
                <div className="music-playlist-card-sub" title={(p.trackTitles || []).join(", ")}>
                  {p.count} track{p.count === 1 ? "" : "s"}
                  {p.trackTitles && p.trackTitles.length > 0 ? ` · ${p.trackTitles.join(", ")}` : ""}
                </div>
                <div className="music-playlist-card-actions">
                  <button className="music-playlist-btn" disabled={p.count === 0} onClick={() => playPlaylist(p.id)}>
                    ▶ Play
                  </button>
                  <button className="music-playlist-btn" onClick={() => setManagePlaylistId(p.id)}>Manage</button>
                </div>
              </div>
            ))}
          </div>
        </section>
      )}

      {view === "artists" && !artistDetail && (
        <section className="music-section">
          <h2 className="music-section-head">Artists <span className="music-count">{filteredArtists.length}</span></h2>
          <div className="music-artist-grid">
            {filteredArtists.map((a) => (
              <button key={a.id} className="music-artist-card" onClick={() => setParam("artist", a.id)}>
                <div className="music-artist-card-name" title={a.name}>{a.name}</div>
                <div className="music-artist-card-sub">
                  {a.yearRange && <span>{a.yearRange}</span>}
                  <span>{a.albumCount} album{a.albumCount === 1 ? "" : "s"}</span>
                  <span>{a.trackCount} track{a.trackCount === 1 ? "" : "s"}</span>
                </div>
              </button>
            ))}
          </div>
        </section>
      )}

      {view === "artists" && artistDetail && !artistDetail.missing && (
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
                    disabled={t.requiresTranscode || t.missing}
                    onPlay={() => playLooseTracks(i)}
                    onAdd={() => openPicker([{ id: t.id, title: t.title }], t.title)}
                  />
                ))}
              </div>
            </>
          )}
        </section>
      )}

      {view === "albums" && (
        <section className="music-section">
          <h2 className="music-section-head">Albums <span className="music-count">{filteredAlbums.length}</span></h2>
          <div className="music-album-grid">
            {filteredAlbums.slice(0, shown).map((a) => (
              <AlbumCard key={a.id} album={a} onOpen={setOpenAlbumId} />
            ))}
          </div>
          {shown < filteredAlbums.length && (
            <button className="music-show-more" onClick={() => setShown((s) => s + PAGE_STEP)}>
              Show more ({filteredAlbums.length - shown} left)
            </button>
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
