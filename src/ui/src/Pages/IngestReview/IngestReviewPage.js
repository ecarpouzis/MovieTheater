import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { Input, Select, Button, Tag, Space, message, Popconfirm, Pagination, Empty, Spin, Typography, Result } from "antd";
import { MovieAPI } from "../../MovieAPI";
import "./IngestReviewPage.css";

const { Title, Text } = Typography;

// Mirrors the TitleType enum in MovieTheater.Db.
const TITLE_TYPES = ["Movie", "Short", "TvSeries", "TvMiniSeries", "TvMovie", "TvShort", "TvSpecial", "Video", "Unknown"];

// A lookup's coarse TitleType ("Movie"/"TvSeries" from OMDB) — apply only if it's a real, known value.
function mapLookupType(tt) {
  if (!tt || tt === "Unknown") return null;
  return TITLE_TYPES.includes(tt) ? tt : null;
}
const CONF_COLOR = { HIGH: "green", MEDIUM: "gold", LOW: "red", NONE: "red" };
const PROV_COLOR = { "finalsort-cache": "blue", "suggestion-api": "geekblue", "web-search": "purple", manual: "cyan" };
const PAGE_SIZE = 12;

function folderName(p) {
  if (!p) return "";
  const parts = String(p).split(/[\\/]/).filter(Boolean);
  return parts[parts.length - 1] || String(p);
}
// The on-disk folder carries the year — show it next to the IMDb-resolved year so a wrong match
// jumps out. Read it from the IMMEDIATE folder (the title's own folder), NOT the whole path: a
// movie inside a collection ("…/Zootopia (2016-2025)/Zootopia 2 (2025)") would otherwise pick up
// the collection's range-start (1937 from "!Animated Movies (1937-2012)") and false-mismatch.
function yearFromPath(p) {
  if (!p) return "";
  const m = folderName(p).match(/\((\d{4})(?:\s*[-–]\s*\d{0,4})?\)/);
  return m ? m[1] : "";
}
function fetchedYear(d) {
  if (!d) return "";
  if (d.releaseDate) {
    const y = new Date(d.releaseDate).getFullYear();
    if (!Number.isNaN(y)) return String(y);
  }
  return "";
}

// Color the episode-match strategy so the position-based ones (absolute/combined) stand out for scrutiny.
const STRAT_COLOR = { se: "green", title: "blue", year: "geekblue", seasonep: "blue", folderseason: "cyan", single: "cyan", combined: "orange", absolute: "orange" };
function basename(p) {
  if (!p) return "";
  const parts = String(p).split(/[\\/]/).filter(Boolean);
  return parts[parts.length - 1] || String(p);
}
function stratOf(label) {
  return label && label.startsWith("match:") ? label.slice(6) : null;
}

// Extras (MiscVideos) attached to this title via RelatedMovieId/RelatedSeriesId. Approved extras are
// read-only here; a PENDING extra can be deleted inline (e.g. a file that's since been mapped to a real
// episode, leaving a stale misc duplicate) — onDelete rejects the misc row the same as the misc queue.
function RelatedExtras({ items, onDelete }) {
  if (!items || !items.length) return null;
  return (
    <div className="rc-related-misc">
      <div className="rc-season-hd">Attached extras (misc videos) · {items.length}</div>
      {items.map((m) => (
        <div key={m.id} className="rc-ep">
          <Tag color="geekblue">{m.category || "misc"}</Tag>
          <span className="rc-eptitle" title={m.title}>
            {m.title}{m.year ? ` (${m.year})` : ""}{m.collectionName ? ` — ${m.collectionName}` : ""}
          </span>
          {m.pending ? <Tag color="gold">pending review</Tag> : null}
          {(m.files || []).map((f, i) => (
            <span key={i} className="rc-path" title={f.path}>{basename(f.path)}</span>
          ))}
          {m.pending && onDelete ? (
            <Popconfirm
              title="Delete this pending extra (misc video + its file rows)?"
              okText="Delete"
              okButtonProps={{ danger: true }}
              onConfirm={() => onDelete(m.id)}
            >
              <a className="rc-ep-rm" title="delete pending extra">✕</a>
            </Popconfirm>
          ) : null}
        </div>
      ))}
    </div>
  );
}

// MiscVideo has its own id sequence, so identify list rows by (kind, id) — never a bare id.
function uidOf(it) {
  return (it.kind || "movie") + ":" + it.id;
}

// The reasons a reviewer should look harder at a row — playability gaps, the scraper's own
// uncertainty flag, and low match confidence. Drives both the per-card badges and the
// Concern filter so "anything we're not confident about" is one click away.
function concernsOf(it) {
  const c = [];
  if (it.isSeries) {
    if (!it.episodeHave) c.push("nofile");
    else if (!it.episodePlayable) c.push("unplayable");
  } else {
    if (!it.fileCount) c.push("nofile");
    else if (!it.playableCount) c.push("unplayable");
    if (it.missingCount > 0) c.push("missing");
  }
  if (it.imdbNeedsReview) c.push("imdb");
  const cf = (it.reviewConfidence || "").toUpperCase();
  if (cf === "LOW" || cf === "NONE") c.push("lowconf");
  return c;
}

// The "needs attention" buckets the Concern dropdown filters on (a row may sit in several).
const CONCERN_FILTERS = {
  ATTENTION: (c) => c.length > 0,
  UNPLAYABLE: (c) => c.includes("nofile") || c.includes("unplayable"),
  MISSING: (c) => c.includes("missing"),
  IMDB: (c) => c.includes("imdb"),
  LOWCONF: (c) => c.includes("lowconf"),
};

// Render the playability / IMDb concern badges for a card (confidence + provenance are shown
// separately). No badges = nothing structurally wrong with the row.
function ConcernTags({ row }) {
  const tags = [];
  if (row.isSeries) {
    if (!row.episodeHave) tags.push(["red", "no episodes mapped"]);
    else if (!row.episodePlayable) tags.push(["orange", "not synced — unplayable"]);
  } else {
    if (!row.fileCount) tags.push(["red", "no file"]);
    else if (!row.playableCount) tags.push(["orange", "not synced — unplayable"]);
    if (row.missingCount > 0) tags.push(["gold", `${row.missingCount} file${row.missingCount === 1 ? "" : "s"} missing`]);
  }
  if (row.imdbNeedsReview) tags.push(["red", row.imdbReviewReason ? `IMDb: ${row.imdbReviewReason}` : "IMDb needs review"]);
  return tags.map(([color, label], i) => (
    <Tag key={i} color={color}>
      {label}
    </Tag>
  ));
}

// A misc-video review card: no poster / IMDb editor (it has no tt). Shows category, the related
// title (a workprint's film, a short's series), its files, Approve/Reject, and reclassify-back.
function MiscReviewCard({ row, onApprove, onReject, onReclassify }) {
  const [working, setWorking] = useState(false);
  const [detail, setDetail] = useState(null);
  const [detailOpen, setDetailOpen] = useState(true); // files are shown open by default — no click to expand
  const [loadingDetail, setLoadingDetail] = useState(true);

  // Load this misc video's files from the DB on open (no external lookup) so they're visible immediately.
  useEffect(() => {
    let cancelled = false;
    (async () => {
      try {
        const res = await MovieAPI.ingestReviewDetail(row.id, "misc");
        const data = await res.json();
        if (!cancelled) setDetail(data);
      } catch {
        /* leave detail null */
      } finally {
        if (!cancelled) setLoadingDetail(false);
      }
    })();
    return () => { cancelled = true; };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [row.id]);

  function toggleDetail() {
    setDetailOpen((o) => !o);
  }
  async function act(fn) {
    setWorking(true);
    try {
      await fn();
    } finally {
      setWorking(false);
    }
  }

  return (
    <div className="review-card">
      <div className="review-card-poster">
        <div className="review-card-noposter">
          misc
          <br />
          video
        </div>
      </div>
      <div className="review-card-body">
        <div className="review-card-tags">
          <Tag color="volcano">MiscVideo</Tag>
          {row.category ? <Tag color="geekblue">{row.category}</Tag> : null}
          {row.collectionName ? <Tag color="purple">{row.collectionName}</Tag> : null}
          <ConcernTags row={row} />
        </div>

        <div className="review-card-folder" title={row.reviewSourcePath || ""}>
          📁 {folderName(row.reviewSourcePath)}
        </div>

        <div className="review-card-imdbsays">
          <Text strong>{row.title}</Text>
          {row.relatedTitle ? (
            <Text type="secondary">
              {" "}
              — related to <b>{row.relatedTitle}</b>
            </Text>
          ) : (
            <Text type="secondary"> — standalone</Text>
          )}
        </div>

        <div className="review-card-summary">
          <a onClick={toggleDetail}>
            📄 {row.fileCount} file{row.fileCount === 1 ? "" : "s"} {detailOpen ? "▲ hide" : "▼ check files"}
          </a>
        </div>

        <Space wrap>
          <Button type="primary" loading={working} onClick={() => act(() => onApprove(row))}>
            Approve
          </Button>
          <Popconfirm title="Delete this misc video and its file rows?" okText="Reject" okButtonProps={{ danger: true }} onConfirm={() => act(() => onReject(row))}>
            <Button danger disabled={working}>
              Reject
            </Button>
          </Popconfirm>
          <Button disabled={working} onClick={() => act(() => onReclassify(row, "movie"))}>
            → Movie
          </Button>
          <Button disabled={working} onClick={() => act(() => onReclassify(row, "series"))}>
            → Series
          </Button>
        </Space>

        {detailOpen && (
          <div className="review-card-detail">
            {loadingDetail ? (
              <Spin size="small" />
            ) : !detail ? (
              <Text type="secondary">no detail loaded</Text>
            ) : (
              <div className="rc-files">
                {(detail.files || []).length === 0 ? (
                  <Text type="secondary">no files mapped to this misc video</Text>
                ) : (
                  (detail.files || []).map((f, i) => (
                    <div key={i} className="rc-file">
                      <Tag color={f.role === "Primary" ? "green" : "default"}>{f.role}</Tag>
                      {f.label ? <Tag>{f.label}</Tag> : null}
                      <span className="rc-path" title={f.path}>
                        {basename(f.path)}
                      </span>
                    </div>
                  ))
                )}
              </div>
            )}
          </div>
        )}
      </div>
    </div>
  );
}

// One BatchInsert-style review card: poster + the IMDb lookup cross-check + editable
// Title / IMDb id / Type, with Approve / Reject. Edits are saved automatically on Approve.
function ReviewCard({ row, details, onFetch, onApprove, onReject, onSave, onReclassify, onAcknowledge }) {
  const [title, setTitle] = useState(row.title || "");
  const [simpleTitle, setSimpleTitle] = useState(row.simpleTitle || "");
  const [year, setYear] = useState(row.year ?? "");
  const [imdbID, setImdbID] = useState(row.imdbID || "");
  const [titleType, setTitleType] = useState(row.titleType || "Movie");
  const [posterUrl, setPosterUrl] = useState(row.posterLink || "");
  const [working, setWorking] = useState(false);
  const [detail, setDetail] = useState(null);
  const [detailOpen, setDetailOpen] = useState(true); // files are shown open by default — no click to expand
  const [loadingDetail, setLoadingDetail] = useState(true);
  const [settingEp, setSettingEp] = useState(null); // episodeId whose file is being assigned
  const [epPath, setEpPath] = useState("");
  const [extraPath, setExtraPath] = useState("");   // series/season-level Extra path
  const [extraSeason, setExtraSeason] = useState(""); // optional season to scope a series Extra

  async function refreshDetail() {
    try {
      const res = await MovieAPI.ingestReviewDetail(row.id, row.kind);
      setDetail(await res.json());
    } catch {
      /* leave detail as-is */
    }
  }

  async function runFileOp(fn, after) {
    setWorking(true);
    try {
      const res = await fn();
      if (!res.ok) {
        const b = await res.json().catch(() => ({}));
        message.error(b.message || "File update failed.");
        return false;
      }
      if (after) after();
      await refreshDetail();
      return true;
    } finally {
      setWorking(false);
    }
  }

  // Set/clear the episode's PRIMARY file (blank path clears it).
  const submitEpFile = (episodeId) =>
    runFileOp(() => MovieAPI.ingestReviewSetEpisodeFile(episodeId, epPath.trim() || null),
      () => { setSettingEp(null); setEpPath(""); });

  // Add an EXTRA file to the episode (alongside its Primary).
  const submitEpExtra = (episodeId) => {
    if (!epPath.trim()) { message.error("Paste a path for the extra."); return; }
    return runFileOp(() => MovieAPI.ingestReviewSetFile({ targetType: "episode", targetId: episodeId, role: "Extra", path: epPath.trim() }),
      () => { setSettingEp(null); setEpPath(""); });
  };

  // Add a SERIES/SEASON-level Extra (attaches to the series' Extras holder).
  const submitSeriesExtra = () => {
    if (!extraPath.trim()) { message.error("Paste a path for the extra."); return; }
    const season = extraSeason.trim() === "" ? null : Number(extraSeason);
    return runFileOp(() => MovieAPI.ingestReviewSetFile({ targetType: "series", targetId: row.id, seasonNumber: season, role: "Extra", path: extraPath.trim() }),
      () => { setExtraPath(""); setExtraSeason(""); });
  };

  const removeFile = (mediaFileId) => runFileOp(() => MovieAPI.ingestReviewRemoveFile(mediaFileId));

  // Delete a pending attached extra (misc video) shown on this card — e.g. a file that's since been mapped
  // to a real episode, leaving a stale misc duplicate. Rejects it (parent drops its standalone misc card),
  // then refreshes this card's detail so it falls off the "attached extras" list too.
  const deleteRelatedExtra = async (miscId) => {
    setWorking(true);
    try {
      const ok = await onReject({ kind: "misc", id: miscId });
      if (ok) await refreshDetail();
    } finally {
      setWorking(false);
    }
  };

  function toggleDetail() {
    setDetailOpen((o) => !o);
  }

  // On open, load the file / episode detail straight from the DB (no external lookup) so every card shows
  // its mapped files immediately. The IMDb cross-check below reads the stored scraped title — also no live
  // call. A live OMDB/IMDb lookup now happens only when the reviewer clicks "Re-lookup".
  useEffect(() => {
    let cancelled = false;
    (async () => {
      try {
        const res = await MovieAPI.ingestReviewDetail(row.id, row.kind);
        const data = await res.json();
        if (!cancelled) setDetail(data);
      } catch {
        /* leave detail null */
      } finally {
        if (!cancelled) setLoadingDetail(false);
      }
    })();
    return () => { cancelled = true; };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [row.id]);

  // IMDb's year is the reliable source (project rule), so when the lookup resolves a year it takes
  // PRIORITY over our stored/ingest year — pre-fill the field with it so it's visible and gets applied
  // on Save/Approve. Only the original ingest value is overwritten; a year you've hand-edited is kept.
  useEffect(() => {
    const fy = fetchedYear(details && details.data);
    if (fy) setYear((prev) => (prev === "" || String(prev) === String(row.year ?? "") ? String(fy) : prev));
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [details]);

  const d = details && details.data;
  // The poster to fetch on save: a user-typed/overridden URL, else — for a row that has no poster yet —
  // the one the lookup found. An already-postered row left untouched sends nothing (no needless re-download).
  const trimmedPoster = (posterUrl || "").trim();
  let posterToSave = null;
  if (trimmedPoster && trimmedPoster !== (row.posterLink || "")) posterToSave = trimmedPoster;
  else if (!row.posterLink && d && d.posterLink) posterToSave = d.posterLink;

  const fieldsDirty =
    title !== (row.title || "") ||
    simpleTitle !== (row.simpleTitle || "") ||
    String(year ?? "") !== String(row.year ?? "") ||
    imdbID !== (row.imdbID || "") ||
    titleType !== (row.titleType || "Movie");
  const dirty = fieldsDirty || !!posterToSave;
  const edits = dirty ? { title, simpleTitle, year, imdbID, titleType, posterLink: posterToSave } : null;
  const folderYear = yearFromPath(row.reviewSourcePath);
  const fYear = fetchedYear(d);
  // The IMDb cross-check title: the stored scraped title by default (no live call), upgraded to a fresh
  // live result only after the reviewer clicks "Re-lookup".
  const liveTitle = d && d.title ? d.title : null;
  const imdbCrossTitle = liveTitle || row.imdbScrapedTitle || null;
  // The on-disk folder year vs the year we'll store. A match is a strong "this resolution is right"
  // signal; a mismatch (read from the immediate folder) is worth a closer look. Compares against a fresh
  // live year if present, else the stored/edited year — so the check works on open without a lookup.
  const storedYear = year != null && year !== "" ? String(year) : "";
  const compareYear = fYear || storedYear;
  const yearConfirmed = folderYear && compareYear && folderYear === compareYear;
  const yearMismatch = folderYear && compareYear && folderYear !== compareYear;

  // Re-lookup pulls fresh IMDb/OMDB data for the current id and fills the editable fields the user
  // would otherwise hand-copy: Title, Year, Type, and the Poster URL. SimpleTitle is left alone
  // (it's a hand-curated sort key). The poster is downloaded when the row is saved/approved.
  async function relookup() {
    setWorking(true);
    try {
      const data = await onFetch(row.id, imdbID, true);
      if (data) {
        if (data.title) setTitle(data.title);
        const fy = fetchedYear(data);
        if (fy) setYear(fy);
        const lt = mapLookupType(data.titleType);
        if (lt) setTitleType(lt);
        if (data.posterLink) setPosterUrl(data.posterLink);
      }
    } finally {
      setWorking(false);
    }
  }

  async function approve() {
    setWorking(true);
    try {
      if (edits) {
        const saved = await onSave(row.id, edits, row.kind);
        if (!saved) return;
      }
      await onApprove(row);
    } finally {
      setWorking(false);
    }
  }

  async function reject() {
    setWorking(true);
    try {
      await onReject(row);
    } finally {
      setWorking(false);
    }
  }

  async function doReclassify(toKind) {
    setWorking(true);
    try {
      await onReclassify(row, toKind);
    } finally {
      setWorking(false);
    }
  }

  async function acknowledge() {
    setWorking(true);
    try {
      await onAcknowledge(row);
    } finally {
      setWorking(false);
    }
  }

  return (
    <div className="review-card">
      <div className="review-card-poster">
        {details && details.loading && !posterUrl ? (
          <Spin />
        ) : posterUrl || (d && d.posterLink) ? (
          <img alt="" src={posterUrl || d.posterLink} />
        ) : (
          <div className="review-card-noposter">no&nbsp;poster</div>
        )}
      </div>

      <div className="review-card-body">
        <div className="review-card-tags">
          <Tag color={CONF_COLOR[(row.reviewConfidence || "").toUpperCase()] || "default"}>{row.reviewConfidence || "?"}</Tag>
          <Tag color={PROV_COLOR[row.reviewProvenance] || "default"}>{row.reviewProvenance || "manual"}</Tag>
          <Tag>{titleType}</Tag>
          <ConcernTags row={row} />
        </div>

        <div className="review-card-folder" title={row.reviewSourcePath || ""}>
          📁 {folderName(row.reviewSourcePath)}
        </div>

        <div className="review-card-summary">
          <a onClick={toggleDetail}>
            {row.isSeries
              ? `📺 episodes ${row.episodeHave}/${row.episodeTotal} mapped${
                  row.episodePlayable < row.episodeHave ? ` · ${row.episodePlayable} playable` : ""
                }`
              : `📄 ${row.fileCount} file${row.fileCount === 1 ? "" : "s"}${
                  row.playableCount < row.fileCount ? ` · ${row.playableCount} playable` : ""
                }`}{" "}
            {detailOpen ? "▲ hide" : "▼ check matches"}
          </a>
        </div>

        <div className="review-card-imdbsays">
          {details && details.loading ? (
            <Text type="secondary">looking up {imdbID}…</Text>
          ) : imdbCrossTitle ? (
            <Text>
              IMDb{liveTitle ? " resolves to" : ":"} <b>{imdbCrossTitle}</b>
              {(liveTitle ? fYear : storedYear) ? ` (${liveTitle ? fYear : storedYear})` : ""}
              {!liveTitle ? <Text type="secondary"> · stored</Text> : null}
              {yearConfirmed ? <Tag color="green" style={{ marginLeft: 8 }}>✓ year {folderYear}</Tag> : null}
              {yearMismatch ? <Tag color="red" style={{ marginLeft: 8 }}>folder says {folderYear}</Tag> : null}
            </Text>
          ) : imdbID ? (
            <Text type="secondary">no stored IMDb data for {imdbID} — “Re-lookup” to fetch live</Text>
          ) : (
            <Text type="secondary">no IMDb id</Text>
          )}
        </div>

        {detail && detail.meta && (detail.meta.plot || (detail.meta.genres || []).length || (detail.meta.cast || []).length) ? (
          <div className="review-card-meta">
            <div className="rc-meta-line">
              {detail.meta.year ? <span>{detail.meta.year}</span> : null}
              {detail.meta.runtimeMinutes ? <span>· {detail.meta.runtimeMinutes} min</span> : detail.meta.runtime ? <span>· {detail.meta.runtime}</span> : null}
              {detail.meta.mpaa ? <span>· {detail.meta.mpaa}</span> : null}
              {detail.meta.imdbRating ? <span>· ⭐ {detail.meta.imdbRating}</span> : null}
              {detail.meta.rtTomatometer != null ? <span>· 🍅 {detail.meta.rtTomatometer}%</span> : null}
              {(detail.meta.genres || []).length ? <span>· {detail.meta.genres.join(", ")}</span> : null}
            </div>
            {(detail.meta.directors || []).length ? <div className="rc-meta-line"><b>Dir:</b> {detail.meta.directors.join(", ")}</div> : null}
            {(detail.meta.cast || []).length ? <div className="rc-meta-line"><b>Cast:</b> {detail.meta.cast.join(", ")}</div> : null}
            {detail.meta.plot ? <div className="rc-meta-plot">{detail.meta.plot}</div> : null}
          </div>
        ) : null}

        <table className="review-card-fields">
          <tbody>
            <tr>
              <td>Title</td>
              <td>
                <Input value={title} onChange={(e) => setTitle(e.target.value)} />
              </td>
            </tr>
            <tr>
              <td>Simple&nbsp;Title</td>
              <td>
                <Input value={simpleTitle} onChange={(e) => setSimpleTitle(e.target.value)} placeholder="sort / search key" />
              </td>
            </tr>
            <tr>
              <td>Year</td>
              <td>
                <Input style={{ width: 110 }} value={year} onChange={(e) => setYear(e.target.value.replace(/[^\d]/g, ""))} maxLength={4} />
              </td>
            </tr>
            <tr>
              <td>IMDb&nbsp;ID</td>
              <td>
                <Input.Group compact>
                  <Input style={{ width: "calc(100% - 110px)" }} value={imdbID} onChange={(e) => setImdbID(e.target.value)} />
                  <Button style={{ width: 110 }} loading={working} onClick={relookup}>
                    Re-lookup
                  </Button>
                </Input.Group>
                {imdbID ? (
                  <a className="review-card-imdblink" href={`https://www.imdb.com/title/${imdbID}/`} target="_blank" rel="noopener noreferrer">
                    open on IMDb ↗
                  </a>
                ) : null}
              </td>
            </tr>
            <tr>
              <td>Type</td>
              <td>
                <Select
                  value={titleType}
                  style={{ width: 200 }}
                  onChange={setTitleType}
                  options={TITLE_TYPES.map((t) => ({ value: t, label: t }))}
                />
              </td>
            </tr>
            <tr>
              <td>Poster&nbsp;URL</td>
              <td>
                <Input value={posterUrl} onChange={(e) => setPosterUrl(e.target.value)} placeholder="https://… (fetched + saved on approve)" />
              </td>
            </tr>
          </tbody>
        </table>

        <Space wrap>
          <Button type="primary" loading={working} onClick={approve}>
            {dirty ? "Save & Approve" : "Approve"}
          </Button>
          <Popconfirm title="Delete this title from the database?" okText="Reject" okButtonProps={{ danger: true }} onConfirm={reject}>
            <Button danger disabled={working}>
              Reject
            </Button>
          </Popconfirm>
          {/* Live (already-approved) oddity rows get an Acknowledge instead of being re-approved:
              it just records that the file oddity was seen, so the row stops surfacing. */}
          {!row.reviewBatch && onAcknowledge && (
            <Button disabled={working} onClick={acknowledge}>
              Acknowledge oddity
            </Button>
          )}
          {dirty ? (
            <Button disabled={working} onClick={() => onSave(row.id, edits, row.kind)}>
              Save edits
            </Button>
          ) : null}
          {row.kind === "movie" && (
            <Popconfirm
              title="Reclassify as a TV series? Its metadata + cast/genre move to the Series table; map its episodes after a re-scrape."
              okText="Make Series"
              onConfirm={() => doReclassify("series")}
            >
              <Button disabled={working}>→ Series</Button>
            </Popconfirm>
          )}
          {row.kind === "series" && (
            <Popconfirm
              title="Reclassify as a movie? Its metadata moves to the Movie table and its episodes are dropped (the disk files are untouched)."
              okText="Make Movie"
              onConfirm={() => doReclassify("movie")}
            >
              <Button disabled={working}>→ Movie</Button>
            </Popconfirm>
          )}
          <Popconfirm
            title={
              row.kind === "series"
                ? "Reclassify as a misc video (no IMDb id)? Its episodes are dropped; the disk files are untouched."
                : "Reclassify as a misc video (no IMDb id)? Its files are kept; the Movie row is removed."
            }
            okText="Make Misc Video"
            onConfirm={() => doReclassify("misc")}
          >
            <Button disabled={working}>→ Misc Video</Button>
          </Popconfirm>
        </Space>

        {detailOpen && (
          <div className="review-card-detail">
            {loadingDetail ? (
              <Spin size="small" />
            ) : !detail ? (
              <Text type="secondary">no detail loaded</Text>
            ) : detail.kind === "movie" ? (
              <>
                <div className="rc-files">
                  {(detail.files || []).length === 0 ? (
                    <Text type="secondary">no files mapped to this title</Text>
                  ) : (
                    (detail.files || []).map((f, i) => (
                      <div key={i} className="rc-file">
                        <Tag color={f.role === "Primary" ? "green" : "default"}>{f.role}</Tag>
                        {f.isPlayable ? (
                          <Tag color="green">streamable</Tag>
                        ) : f.missing ? (
                          <Tag color="gold">missing</Tag>
                        ) : (
                          <Tag color="orange">mapped · not synced</Tag>
                        )}
                        {f.label ? <Tag>{f.label}</Tag> : null}
                        <span className="rc-path" title={f.path}>{basename(f.path)}</span>
                      </div>
                    ))
                  )}
                </div>
                <RelatedExtras items={detail.relatedMisc} onDelete={deleteRelatedExtra} />
              </>
            ) : (
              <>
                {detail.folderListing ? (
                  <div className="rc-folderdump">
                    <div className="rc-folderdump-hd">
                      📁 on-disk folder — <span className="rc-fd-tag rc-fd-ok">[OK] mapped</span>{" "}
                      <span className="rc-fd-tag rc-fd-no">[??] NOT captured</span> (select a path, then "set file" on an episode)
                    </div>
                    {/* Colorized per-line so unmapped ([??]) files stand out at a glance; still plain text you can select/copy. */}
                    <div className="rc-folderdump-box">
                      {detail.folderListing.split("\n").map((ln, i) => {
                        const cls = ln.startsWith("[??]")
                          ? "rc-fd-line rc-fd-no"
                          : ln.startsWith("[OK]")
                          ? "rc-fd-line rc-fd-ok"
                          : "rc-fd-line";
                        return (
                          <div key={i} className={cls}>
                            {ln || " "}
                          </div>
                        );
                      })}
                    </div>
                  </div>
                ) : (
                  <Text type="secondary">No folder scan yet — run <code>scan-series-folders</code>.</Text>
                )}
                <div className="rc-seasons">
                  {(detail.seasons || []).map((s) => (
                    <div key={s.season} className="rc-season">
                      <div className="rc-season-hd">
                        Season {s.season} · {s.episodes.filter((e) => e.files && e.files.length).length}/{s.episodes.length}
                      </div>
                      {s.episodes.map((e) => {
                        const files = e.files || [];
                        const primary = files.find((x) => x.role === "Primary") || files[0] || null;
                        const extras = files.filter((x) => x !== primary);
                        const st = primary ? stratOf(primary.label) : null;
                        const editing = settingEp === e.episodeId;
                        return (
                          <div key={e.episode} className={"rc-ep" + (primary ? "" : " rc-ep-missing")}>
                            <span className="rc-epnum">E{e.episode}</span>
                            <span className="rc-eptitle" title={e.title || ""}>{e.title || "—"}</span>
                            {editing ? (
                              <span className="rc-ep-setfile">
                                <Input
                                  size="small"
                                  style={{ width: 280 }}
                                  value={epPath}
                                  placeholder="paste full path (blank clears primary)"
                                  onChange={(ev) => setEpPath(ev.target.value)}
                                  onPressEnter={() => submitEpFile(e.episodeId)}
                                />
                                <Button size="small" type="primary" loading={working} onClick={() => submitEpFile(e.episodeId)}>Set primary</Button>
                                <Button size="small" loading={working} onClick={() => submitEpExtra(e.episodeId)}>+ extra</Button>
                                <Button size="small" onClick={() => { setSettingEp(null); setEpPath(""); }}>✕</Button>
                              </span>
                            ) : (
                              <>
                                {primary ? (
                                  <>
                                    {st ? <Tag color={STRAT_COLOR[st] || "default"}>{st}</Tag> : null}
                                    <span className="rc-path" title={primary.path}>{basename(primary.path)}</span>
                                    {primary.mediaFileId ? <a className="rc-ep-rm" title="remove file" onClick={() => removeFile(primary.mediaFileId)}>✕</a> : null}
                                  </>
                                ) : (
                                  <span className="rc-missing">no file</span>
                                )}
                                <a className="rc-ep-setlink" onClick={() => { setSettingEp(e.episodeId); setEpPath(primary ? primary.path : ""); }}>✎ set file</a>
                              </>
                            )}
                            {extras.map((x) => (
                              <span key={x.mediaFileId} className="rc-extra">
                                <Tag color="purple">extra</Tag>
                                <span className="rc-path" title={x.path}>{basename(x.path)}</span>
                                <a className="rc-ep-rm" title="remove extra" onClick={() => removeFile(x.mediaFileId)}>✕</a>
                              </span>
                            ))}
                          </div>
                        );
                      })}
                    </div>
                  ))}
                </div>
                {/* Series / season-level extras — making-ofs & specials not tied to a single episode. */}
                <div className="rc-season">
                  <div className="rc-season-hd">Series / season extras</div>
                  {(detail.seriesExtras || []).map((x) => {
                    const sm = (x.label || "").match(/:s(\d+)/);
                    return (
                      <div key={x.mediaFileId} className="rc-ep">
                        <Tag color="purple">extra</Tag>
                        {sm ? <span className="rc-epnum">S{sm[1]}</span> : <span className="rc-epnum">—</span>}
                        <span className="rc-path" title={x.path}>{basename(x.path)}</span>
                        <a className="rc-ep-rm" title="remove extra" onClick={() => removeFile(x.mediaFileId)}>✕</a>
                      </div>
                    );
                  })}
                  <span className="rc-ep-setfile">
                    <Input size="small" style={{ width: 80 }} value={extraSeason} placeholder="season#" onChange={(ev) => setExtraSeason(ev.target.value)} />
                    <Input size="small" style={{ width: 260 }} value={extraPath} placeholder="paste extra path (making-of, special…)" onChange={(ev) => setExtraPath(ev.target.value)} onPressEnter={submitSeriesExtra} />
                    <Button size="small" type="primary" loading={working} onClick={submitSeriesExtra}>Add extra</Button>
                  </span>
                </div>
                <RelatedExtras items={detail.relatedMisc} onDelete={deleteRelatedExtra} />
              </>
            )}
          </div>
        )}
      </div>
    </div>
  );
}

export default function IngestReviewPage({ userData }) {
  const [loading, setLoading] = useState(true);
  const [forbidden, setForbidden] = useState(false);
  const [items, setItems] = useState([]);
  const [meta, setMeta] = useState({ byConfidence: [], byType: [], batches: [] });

  const [search, setSearch] = useState("");
  const [confFilter, setConfFilter] = useState("ALL");
  const [typeFilter, setTypeFilter] = useState("ALL");
  const [concernFilter, setConcernFilter] = useState("ALL");
  const [scope, setScope] = useState("batch"); // "batch" = open ingest batch; "gaps" = all series w/ unmapped episodes
  const [page, setPage] = useState(1);

  // id -> { loading, data, error } for the per-card poster/detail lookups.
  const [detailsCache, setDetailsCache] = useState({});
  const detailsRef = useRef({});
  detailsRef.current = detailsCache;

  const load = useCallback(async () => {
    setLoading(true);
    try {
      const res = await MovieAPI.ingestReviewList(scope);
      if (res.status === 401 || res.status === 403) {
        setForbidden(true);
        return;
      }
      const data = await res.json();
      setItems(data.items || []);
      setMeta({ byConfidence: data.byConfidence || [], byType: data.byType || [], batches: data.batches || [] });
    } catch {
      message.error("Failed to load the review queue.");
    } finally {
      setLoading(false);
    }
  }, [scope]);

  useEffect(() => {
    load();
  }, [load]);

  // Returns the looked-up record (or null) so Re-lookup can populate the editable fields from it.
  const fetchDetails = useCallback(async (id, tt, force) => {
    if (!tt) return null;
    const cur = detailsRef.current[id];
    if (cur && (cur.loading || (cur.data && !force))) return cur.data || null;
    setDetailsCache((prev) => ({ ...prev, [id]: { loading: true } }));
    try {
      let data = await MovieAPI.omdbLookupImdbID(tt);
      if (!data || !data.title) {
        // OMDB missed (often anime/foreign) — fall back to the IMDb API client.
        try {
          data = await MovieAPI.imdbApiLookupImdbId(tt);
        } catch {
          /* keep the OMDB (possibly empty) result */
        }
      }
      setDetailsCache((prev) => ({ ...prev, [id]: { loading: false, data } }));
      return data || null;
    } catch {
      setDetailsCache((prev) => ({ ...prev, [id]: { loading: false, error: true } }));
      return null;
    }
  }, []);

  const filtered = useMemo(() => {
    const q = search.trim().toLowerCase();
    const concernTest = CONCERN_FILTERS[concernFilter];
    return items.filter((it) => {
      if (confFilter !== "ALL" && (it.reviewConfidence || "").toUpperCase() !== confFilter) return false;
      if (typeFilter !== "ALL" && it.titleType !== typeFilter) return false;
      if (concernTest && !concernTest(concernsOf(it))) return false;
      if (q) {
        const hay = `${it.title || ""} ${it.imdbID || ""} ${it.reviewSourcePath || ""}`.toLowerCase();
        if (!hay.includes(q)) return false;
      }
      return true;
    });
  }, [items, search, confFilter, typeFilter, concernFilter]);

  // How many rows have any concern at all — drives the header chip and is the count behind
  // the "Needs attention" filter.
  const attentionCount = useMemo(() => items.filter((it) => concernsOf(it).length > 0).length, [items]);

  useEffect(() => {
    setPage(1);
  }, [search, confFilter, typeFilter, concernFilter]);

  const pageItems = useMemo(() => filtered.slice((page - 1) * PAGE_SIZE, page * PAGE_SIZE), [filtered, page]);

  function dropItems(list) {
    const set = new Set(list.map(uidOf));
    setItems((prev) => prev.filter((it) => !set.has(uidOf(it))));
  }
  // Split selected rows by kind — movie / series / misc all have separate id sequences.
  function splitKinds(list) {
    return {
      ids: list.filter((it) => it.kind === "movie").map((it) => it.id),
      seriesIds: list.filter((it) => it.kind === "series").map((it) => it.id),
      miscIds: list.filter((it) => it.kind === "misc").map((it) => it.id),
    };
  }

  const approve = useCallback(async (itemOrArr) => {
    const list = Array.isArray(itemOrArr) ? itemOrArr : [itemOrArr];
    if (!list.length) return false;
    const { ids, seriesIds, miscIds } = splitKinds(list);
    try {
      const res = await MovieAPI.ingestReviewApprove(ids, seriesIds, miscIds);
      const data = await res.json();
      dropItems(list);
      if (list.length > 1) message.success(`Approved ${data.approved} title(s) into the library.`);
      return true;
    } catch {
      message.error("Approve failed.");
      return false;
    }
  }, []);

  const reject = useCallback(async (itemOrArr) => {
    const list = Array.isArray(itemOrArr) ? itemOrArr : [itemOrArr];
    if (!list.length) return false;
    const { ids, seriesIds, miscIds } = splitKinds(list);
    try {
      const res = await MovieAPI.ingestReviewReject(ids, seriesIds, miscIds);
      const data = await res.json();
      dropItems(list);
      if (list.length > 1) message.success(`Rejected (deleted) ${data.rejected} title(s).`);
      return true;
    } catch {
      message.error("Reject failed.");
      return false;
    }
  }, []);

  // Acknowledge a live title's file oddity (records it as reviewed; nothing else changes) and drop it
  // from the list. Movies/series only — misc has no acknowledge flag.
  const acknowledge = useCallback(async (item) => {
    try {
      const res = await MovieAPI.ingestReviewAcknowledgeOddity(item.id, item.kind);
      if (!res.ok) {
        message.error("Acknowledge failed.");
        return false;
      }
      dropItems([item]);
      return true;
    } catch {
      message.error("Acknowledge failed.");
      return false;
    }
  }, []);

  // Move a row between movie / series / misc, then re-fetch so it reappears in its new form.
  const reclassify = useCallback(
    async (item, toKind, extra) => {
      try {
        const res = await MovieAPI.ingestReviewReclassify({ id: item.id, fromKind: item.kind, toKind, ...(extra || {}) });
        if (!res.ok) {
          const b = await res.json().catch(() => ({}));
          message.error(b.message || "Reclassify failed.");
          return false;
        }
        message.success(`Reclassified "${item.title}" → ${toKind}.`);
        await load();
        return true;
      } catch {
        message.error("Reclassify failed.");
        return false;
      }
    },
    [load]
  );

  const save = useCallback(async (id, edits, kind) => {
    try {
      const res = await MovieAPI.ingestReviewUpdate({ id, kind, ...edits });
      if (!res.ok) {
        const body = await res.json().catch(() => ({}));
        message.error(body.message || "Update failed.");
        return false;
      }
      setItems((prev) =>
        prev.map((it) =>
          it.id === id && (it.kind || "movie") === (kind || "movie")
            ? {
                ...it,
                title: edits.title ?? it.title,
                simpleTitle: edits.simpleTitle ?? it.simpleTitle,
                year: edits.year != null && edits.year !== "" ? Number(edits.year) : it.year,
                imdbID: edits.imdbID ?? it.imdbID,
                titleType: edits.titleType ?? it.titleType,
                posterLink: edits.posterLink ?? it.posterLink,
              }
            : it
        )
      );
      // The id may have changed — refresh the lookup.
      if (edits.imdbID) fetchDetails(id, edits.imdbID, true);
      return true;
    } catch {
      message.error("Update failed.");
      return false;
    }
  }, [fetchDetails]);

  if (forbidden) {
    return <Result status="403" title="Editors only" subTitle="The library review queue requires movie-edit permission." />;
  }
  if (userData && userData.canEditMovies === false) {
    return <Result status="403" title="Editors only" subTitle="Ask an admin to grant you movie-edit permission." />;
  }

  return (
    <div className="ingest-review-page">
      <div className="ingest-review-header">
        <Title level={3} style={{ marginBottom: 4 }}>
          Library ingest review
        </Title>
        <Text type="secondary">
          {items.length} title(s) pending — quarantined from browse until you approve them. Reject deletes the row entirely.
        </Text>
        <div className="ingest-review-chips">
          {attentionCount > 0 && (
            <Tag color="warning" style={{ cursor: "pointer" }} onClick={() => setConcernFilter("ATTENTION")}>
              ⚠ {attentionCount} need attention
            </Tag>
          )}
          {meta.byConfidence.map((c) => (
            <Tag key={c.confidence} color={CONF_COLOR[(c.confidence || "").toUpperCase()] || "default"}>
              {c.confidence}: {c.count}
            </Tag>
          ))}
          {meta.byType.map((t) => (
            <Tag key={t.type}>
              {t.type}: {t.count}
            </Tag>
          ))}
        </div>
      </div>

      <div className="ingest-review-toolbar">
        <Input.Search placeholder="Search title / id / folder" allowClear onChange={(e) => setSearch(e.target.value)} style={{ width: 260 }} />
        <Select
          value={scope}
          onChange={setScope}
          style={{ width: 230 }}
          options={[
            { value: "batch", label: "Open ingest batch" },
            { value: "gaps", label: "All series with gaps" },
            { value: "oddities", label: "Live titles with file oddities" },
          ]}
        />
        <Select
          value={confFilter}
          onChange={setConfFilter}
          style={{ width: 160 }}
          options={[
            { value: "ALL", label: "All confidence" },
            { value: "HIGH", label: "HIGH" },
            { value: "MEDIUM", label: "MEDIUM" },
            { value: "LOW", label: "LOW" },
            { value: "NONE", label: "NONE" },
          ]}
        />
        <Select
          value={typeFilter}
          onChange={setTypeFilter}
          style={{ width: 160 }}
          options={[{ value: "ALL", label: "All types" }, ...TITLE_TYPES.map((t) => ({ value: t, label: t })), { value: "MiscVideo", label: "MiscVideo" }]}
        />
        <Select
          value={concernFilter}
          onChange={setConcernFilter}
          style={{ width: 190 }}
          options={[
            { value: "ALL", label: "All rows" },
            { value: "ATTENTION", label: `⚠ Needs attention (${attentionCount})` },
            { value: "UNPLAYABLE", label: "No / unplayable file" },
            { value: "MISSING", label: "Missing files" },
            { value: "IMDB", label: "IMDb flagged" },
            { value: "LOWCONF", label: "Low confidence" },
          ]}
        />
        <Popconfirm
          title={`Approve all ${filtered.length} shown title(s) into the library?`}
          okText="Approve all"
          disabled={!filtered.length}
          onConfirm={() => approve(filtered)}
        >
          <Button type="primary" disabled={!filtered.length}>
            Approve all shown ({filtered.length})
          </Button>
        </Popconfirm>
        <Button onClick={load}>Refresh</Button>
        <Popconfirm
          title="Fetch posters (from IMDb via OMDB) for every approved movie/series that has none?"
          okText="Backfill posters"
          onConfirm={async () => {
            const hide = message.loading("Backfilling posters…", 0);
            try {
              const res = await MovieAPI.ingestReviewBackfillPosters();
              const data = await res.json().catch(() => ({}));
              hide();
              if (res.ok) message.success(`Posters fetched for ${data.got ?? 0} of ${data.attempted ?? 0} title(s).`);
              else message.error(data.message || "Backfill failed.");
            } catch {
              hide();
              message.error("Backfill failed.");
            }
          }}
        >
          <Button>Backfill posters</Button>
        </Popconfirm>
        <Popconfirm
          title="Repair series posters? Series share an id space with movies; this gives every series its own poster file. Runs in chunks until done — keep this tab open."
          okText="Migrate series posters"
          onConfirm={async () => {
            let hide = message.loading("Migrating series posters…", 0);
            const totals = { copied: 0, refetched: 0, skipped: 0, movieRestored: 0, failed: 0 };
            try {
              let afterId = 0;
              // Drive the cursor-based endpoint to completion; each chunk is bounded so it can't time out.
              // Stop on done, or if a chunk makes no progress (safety against an unexpected stall).
              for (let guard = 0; guard < 1000; guard++) {
                const res = await MovieAPI.ingestReviewMigrateSeriesPosters(afterId, 40);
                const data = await res.json().catch(() => ({}));
                if (!res.ok) {
                  hide();
                  message.error(data.message || `Migration failed at id ${afterId}.`);
                  return;
                }
                totals.copied += data.copied ?? 0;
                totals.refetched += data.refetched ?? 0;
                totals.skipped += data.skipped ?? 0;
                totals.movieRestored += data.movieRestored ?? 0;
                totals.failed += data.failed ?? 0;
                if (data.done || data.processed === 0) break;
                afterId = data.nextAfterId;
                hide();
                hide = message.loading(`Migrating series posters… ${data.remaining ?? 0} remaining`, 0);
              }
              hide();
              message.success(
                `Series posters done: ${totals.copied} copied, ${totals.refetched} re-fetched, ${totals.skipped} already ok, ${totals.movieRestored} movie poster(s) restored (${totals.failed} no poster found).`
              );
            } catch {
              hide();
              message.error("Migration failed.");
            }
          }}
        >
          <Button>Migrate series posters</Button>
        </Popconfirm>
      </div>

      {loading ? (
        <div className="ingest-review-loading">
          <Spin size="large" />
        </div>
      ) : filtered.length === 0 ? (
        <Empty description={items.length ? "Nothing matches the current filter." : "Review queue is empty."} />
      ) : (
        <>
          <div className="ingest-review-cards">
            {pageItems.map((row) =>
              row.kind === "misc" ? (
                <MiscReviewCard key={uidOf(row)} row={row} onApprove={approve} onReject={reject} onReclassify={reclassify} />
              ) : (
                <ReviewCard
                  key={uidOf(row)}
                  row={row}
                  details={detailsCache[row.id]}
                  onFetch={fetchDetails}
                  onApprove={approve}
                  onReject={reject}
                  onSave={save}
                  onReclassify={reclassify}
                  onAcknowledge={acknowledge}
                />
              )
            )}
          </div>
          <div className="ingest-review-pager">
            <Pagination current={page} pageSize={PAGE_SIZE} total={filtered.length} onChange={setPage} showSizeChanger={false} />
          </div>
        </>
      )}
    </div>
  );
}
