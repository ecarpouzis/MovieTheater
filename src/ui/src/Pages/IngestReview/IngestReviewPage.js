import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { Input, Select, Button, Tag, Space, message, Popconfirm, Pagination, Empty, Spin, Typography, Result } from "antd";
import { MovieAPI } from "../../MovieAPI";
import "./IngestReviewPage.css";

const { Title, Text } = Typography;

// Mirrors the TitleType enum in MovieTheater.Db.
const TITLE_TYPES = ["Movie", "Short", "TvSeries", "TvMiniSeries", "TvMovie", "TvShort", "TvSpecial", "Video", "Unknown"];
const CONF_COLOR = { HIGH: "green", MEDIUM: "gold", LOW: "red", NONE: "red" };
const PROV_COLOR = { "finalsort-cache": "blue", "suggestion-api": "geekblue", "web-search": "purple", manual: "cyan" };
const PAGE_SIZE = 12;

// The on-disk folder usually carries the year — show it next to the IMDb-resolved year
// so a wrong match jumps out.
function yearFromPath(p) {
  if (!p) return "";
  const m = String(p).match(/\((\d{4})(?:\s*[-–]\s*\d{0,4})?\)/);
  return m ? m[1] : "";
}
function folderName(p) {
  if (!p) return "";
  const parts = String(p).split(/[\\/]/).filter(Boolean);
  return parts[parts.length - 1] || String(p);
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
  const [detailOpen, setDetailOpen] = useState(false);
  const [loadingDetail, setLoadingDetail] = useState(false);

  async function toggleDetail() {
    if (!detailOpen && !detail) {
      setLoadingDetail(true);
      try {
        const res = await MovieAPI.ingestReviewDetail(row.id, "misc");
        setDetail(await res.json());
      } catch {
        /* leave detail null */
      } finally {
        setLoadingDetail(false);
      }
    }
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
function ReviewCard({ row, details, onFetch, onApprove, onReject, onSave, onReclassify }) {
  const [title, setTitle] = useState(row.title || "");
  const [imdbID, setImdbID] = useState(row.imdbID || "");
  const [titleType, setTitleType] = useState(row.titleType || "Movie");
  const [working, setWorking] = useState(false);
  const [detail, setDetail] = useState(null);
  const [detailOpen, setDetailOpen] = useState(false);
  const [loadingDetail, setLoadingDetail] = useState(false);

  async function toggleDetail() {
    if (!detailOpen && !detail) {
      setLoadingDetail(true);
      try {
        const res = await MovieAPI.ingestReviewDetail(row.id, row.kind);
        setDetail(await res.json());
      } catch {
        /* leave detail null */
      } finally {
        setLoadingDetail(false);
      }
    }
    setDetailOpen((o) => !o);
  }

  // Fetch the poster + details for this id once, when the card first renders on its page.
  useEffect(() => {
    if (row.imdbID) onFetch(row.id, row.imdbID, false);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [row.id]);

  const dirty = title !== (row.title || "") || imdbID !== (row.imdbID || "") || titleType !== (row.titleType || "Movie");
  const edits = dirty ? { title, imdbID, titleType } : null;
  const d = details && details.data;
  const folderYear = yearFromPath(row.reviewSourcePath);
  const fYear = fetchedYear(d);
  const yearMismatch = folderYear && fYear && folderYear !== fYear;

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

  return (
    <div className="review-card">
      <div className="review-card-poster">
        {details && details.loading ? (
          <Spin />
        ) : d && d.posterLink ? (
          <img alt="" src={d.posterLink} />
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
          ) : d && d.title ? (
            <Text>
              IMDb resolves to <b>{d.title}</b>
              {fYear ? ` (${fYear})` : ""}
              {yearMismatch ? <Tag color="red" style={{ marginLeft: 8 }}>folder says {folderYear}</Tag> : null}
            </Text>
          ) : (
            <Text type="secondary">no lookup result for {imdbID || "—"}</Text>
          )}
        </div>

        <table className="review-card-fields">
          <tbody>
            <tr>
              <td>Title</td>
              <td>
                <Input value={title} onChange={(e) => setTitle(e.target.value)} />
              </td>
            </tr>
            <tr>
              <td>IMDb&nbsp;ID</td>
              <td>
                <Input.Group compact>
                  <Input style={{ width: "calc(100% - 110px)" }} value={imdbID} onChange={(e) => setImdbID(e.target.value)} />
                  <Button style={{ width: 110 }} onClick={() => onFetch(row.id, imdbID, true)}>
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
              <div className="rc-files">
                {(detail.files || []).length === 0 ? (
                  <Text type="secondary">no files mapped to this title</Text>
                ) : (
                  (detail.files || []).map((f, i) => (
                    <div key={i} className="rc-file">
                      <Tag color={f.role === "Primary" ? "green" : "default"}>{f.role}</Tag>
                      {f.label ? <Tag>{f.label}</Tag> : null}
                      <span className="rc-path" title={f.path}>{basename(f.path)}</span>
                    </div>
                  ))
                )}
              </div>
            ) : (
              <div className="rc-seasons">
                {(detail.seasons || []).map((s) => (
                  <div key={s.season} className="rc-season">
                    <div className="rc-season-hd">
                      Season {s.season} · {s.episodes.filter((e) => e.files && e.files.length).length}/{s.episodes.length}
                    </div>
                    {s.episodes.map((e) => {
                      const f = e.files && e.files[0];
                      const st = f ? stratOf(f.label) : null;
                      return (
                        <div key={e.episode} className={"rc-ep" + (f ? "" : " rc-ep-missing")}>
                          <span className="rc-epnum">E{e.episode}</span>
                          <span className="rc-eptitle" title={e.title || ""}>{e.title || "—"}</span>
                          {f ? (
                            <>
                              {st ? <Tag color={STRAT_COLOR[st] || "default"}>{st}</Tag> : null}
                              <span className="rc-path" title={f.path}>{basename(f.path)}</span>
                            </>
                          ) : (
                            <span className="rc-missing">no file</span>
                          )}
                        </div>
                      );
                    })}
                  </div>
                ))}
              </div>
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
  const [page, setPage] = useState(1);

  // id -> { loading, data, error } for the per-card poster/detail lookups.
  const [detailsCache, setDetailsCache] = useState({});
  const detailsRef = useRef({});
  detailsRef.current = detailsCache;

  const load = useCallback(async () => {
    setLoading(true);
    try {
      const res = await MovieAPI.ingestReviewList();
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
  }, []);

  useEffect(() => {
    load();
  }, [load]);

  const fetchDetails = useCallback(async (id, tt, force) => {
    if (!tt) return;
    const cur = detailsRef.current[id];
    if (cur && (cur.loading || (cur.data && !force))) return;
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
    } catch {
      setDetailsCache((prev) => ({ ...prev, [id]: { loading: false, error: true } }));
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
        prev.map((it) => (it.id === id ? { ...it, title: edits.title ?? it.title, imdbID: edits.imdbID ?? it.imdbID, titleType: edits.titleType ?? it.titleType } : it))
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
