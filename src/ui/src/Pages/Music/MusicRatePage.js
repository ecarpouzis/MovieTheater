import { useEffect, useMemo, useRef, useState } from "react";
import { Button, Input, Segmented, Slider, Spin, message } from "antd";
import LoadFailure from "../../Components/LoadFailure";
import MusicAlbumArt from "../../Music/MusicAlbumArt";
import { MovieAPI } from "../../MovieAPI";
import { useDebouncedCallback } from "../../hooks/useDebounce";
import { useMusicShelf } from "./useMusicShelf";
import "./MusicPage.css";
import "./MusicRate.css";

/**
 * `/music/rate` — the MEMBER surface for scoring records, where the movies have `/rate` (R9 S10).
 *
 * It is deliberately NOT a port of the movies' Rate page. That one is a drag-RANK: you order titles
 * against each other and anchor lines turn the order into scores, which works because a film is a
 * thing you saw once and can place. Records are not ranked against each other by anyone; they are
 * scored one at a time, over years, and the useful bulk surface is simply "show me the shelf and let
 * me put numbers on it". So this is a list with a slider per row — the same control the album sheet
 * offers, at volume.
 *
 * <p>It is also not an admin tab. `/music/admin` is an Overview REPORT because every music job is a
 * CLI; a rating is somebody's opinion and there is no operator-shaped job hiding in it.</p>
 *
 * Three rules it keeps:
 *   * **The shelf costs nothing.** `useMusicShelf` is the SAME React-Query copy the browse and the
 *     sider rail hold, so arriving from the browse paints from memory.
 *   * **Bounded rendering, no second engine.** 2,921 albums cannot all be rows. There is no
 *     `CatalogHost` here and the site's law is that nobody re-rolls a windowed grid, so this shows a
 *     bounded page and a "Show more" — which also nudges you to search, the faster way to find the
 *     record you meant.
 *   * **Bounded writes, driven from here.** Edits queue and flush in capped chunks against
 *     `POST /API/Music/Rating` (the server refuses more than 200), the loop living in this page with
 *     a no-progress break — never one request that must survive to the end.
 */

const PAGE = 60;
const SAVE_DEBOUNCE_MS = 700;
/** The server caps a call at 200; stay well under and drive the loop here. */
const CHUNK = 100;

export default function MusicRatePage({ userData }) {
  const hasPassword = !!userData?.hasPassword;
  const shelf = useMusicShelf("music", hasPassword);
  const [ratings, setRatings] = useState(null);
  const [failed, setFailed] = useState(false);
  const [q, setQ] = useState("");
  const [scope, setScope] = useState("all");
  const [shown, setShown] = useState(PAGE);

  // Album id → the value we still owe the server. A ref, not state: a drag must not re-render the
  // whole list, and the flush reads whatever the last tick left here.
  const pending = useRef(new Map());
  const [saving, setSaving] = useState(false);

  useEffect(() => {
    if (!hasPassword) return undefined;
    let alive = true;
    MovieAPI.getMusicRating()
      .then((r) => (r.ok ? r.json() : Promise.reject(r.status)))
      .then((d) => {
        if (!alive) return;
        const map = {};
        for (const row of d?.ratings ?? []) map[row.albumId] = row.score;
        setRatings(map);
      })
      .catch(() => alive && setFailed(true));
    return () => { alive = false; };
  }, [hasPassword]);

  const flush = async () => {
    if (pending.current.size === 0) return;
    setSaving(true);
    try {
      let guard = 0;
      while (pending.current.size > 0) {
        const batch = [...pending.current.entries()].slice(0, CHUNK);
        const res = await MovieAPI.setMusicRatings(batch.map(([albumId, value]) => ({ albumId, value })));
        if (!res.ok) throw new Error(String(res.status));
        for (const [albumId] of batch) pending.current.delete(albumId);
        // No-progress break: a batch that removed nothing would spin forever.
        guard += 1;
        if (guard > 60) break;
      }
    } catch {
      message.error("Couldn't save your ratings — they are still on screen, try again.");
    } finally {
      setSaving(false);
    }
  };
  const queueFlush = useDebouncedCallback(flush, SAVE_DEBOUNCE_MS);

  // A page that unmounts mid-debounce must not drop what it owes.
  useEffect(() => () => { queueFlush.cancel(); flush(); /* eslint-disable-line react-hooks/exhaustive-deps */ }, []);

  const setScore = (albumId, value) => {
    setRatings((prev) => {
      const next = { ...(prev ?? {}) };
      if (value == null) delete next[albumId];
      else next[albumId] = value;
      return next;
    });
    pending.current.set(albumId, value);
    queueFlush();
  };

  const rows = useMemo(() => {
    const term = q.trim().toLowerCase();
    return shelf.albums.filter((a) => {
      if (term && !`${a.title ?? ""} ${a.artistName ?? ""}`.toLowerCase().includes(term)) return false;
      const rated = ratings != null && a.id in ratings;
      return scope === "all" || (scope === "rated" ? rated : !rated);
    });
  }, [shelf.albums, ratings, q, scope]);

  useEffect(() => { setShown(PAGE); }, [q, scope]);

  if (!hasPassword) {
    return (
      <div className="music-rate">
        <LoadFailure message="Rating the shelf needs a password-verified session." />
      </div>
    );
  }
  if (shelf.error || failed) {
    return (
      <div className="music-rate">
        <LoadFailure message="Couldn't load the shelf." onRetry={shelf.refresh} />
      </div>
    );
  }
  if (shelf.loading || ratings == null) {
    return <div className="music-rate music-rate--loading"><Spin /></div>;
  }

  const ratedCount = Object.keys(ratings).length;

  return (
    <div className="music-rate">
      <div className="music-rate-head">
        <div className="music-rate-head-line">
          <h1 className="music-rate-title">Rate the shelf</h1>
          <span className="music-rate-count">
            {ratedCount} of {shelf.albums.length} rated{saving ? " · saving…" : ""}
          </span>
        </div>
        <p className="music-rate-blurb">
          0 is a real score; clearing a rating removes it entirely. Your scores feed the
          Top&nbsp;rated order and the rail&apos;s rating floor.
        </p>
        <div className="music-rate-controls">
          <Input.Search
            allowClear
            placeholder="An album or an artist…"
            value={q}
            onChange={(e) => setQ(e.target.value)}
            className="music-rate-search"
          />
          <Segmented
            value={scope}
            onChange={setScope}
            options={[
              { label: "All", value: "all" },
              { label: "Rated", value: "rated" },
              { label: "Not rated", value: "unrated" },
            ]}
          />
        </div>
      </div>

      {rows.length === 0 && <p className="music-rate-empty">Nothing on the shelf matches that.</p>}

      <div className="music-rate-list">
        {rows.slice(0, shown).map((a) => (
          <MusicRateRow key={a.id} album={a} score={ratings[a.id]} onChange={setScore} />
        ))}
      </div>

      {rows.length > shown && (
        <div className="music-rate-more">
          <Button onClick={() => setShown((n) => n + PAGE)}>
            Show {Math.min(PAGE, rows.length - shown)} more of {rows.length - shown}
          </Button>
        </div>
      )}
    </div>
  );
}

/**
 * One record. The slider commits on RELEASE — one gesture is one queued write, not one per drag
 * frame — and the local value tracks the drag so the number under the thumb moves with it.
 */
function MusicRateRow({ album, score, onChange }) {
  const rated = typeof score === "number";
  const [value, setValue] = useState(rated ? score : 0);
  useEffect(() => { setValue(rated ? score : 0); }, [album.id, score, rated]);

  return (
    <div className={`music-rate-row${rated ? " music-rate-row--rated" : ""}`}>
      <span className="music-rate-art">
        <MusicAlbumArt albumId={album.id} hasArt={album.hasArt} title={album.title} dominantColor={album.dominantColor} thumb />
      </span>
      <span className="music-rate-names">
        <span className="music-rate-album" title={album.title}>{album.title}</span>
        <span className="music-rate-artist">
          {album.artistName}
          {album.year != null && ` · ${album.year}`}
          {album.genres?.length > 0 && ` · ${album.genres.slice(0, 2).join(", ")}`}
        </span>
      </span>
      <Slider
        className="music-rate-slider"
        min={0}
        max={100}
        value={value}
        onChange={setValue}
        onChangeComplete={(v) => onChange(album.id, v)}
      />
      <span className="music-rate-score">{rated ? value : "—"}</span>
      <button
        type="button"
        className="music-rate-clear"
        title={rated ? "Remove your rating" : "Not rated"}
        disabled={!rated}
        onClick={() => onChange(album.id, null)}
      >
        Clear
      </button>
    </div>
  );
}
