import { useCallback, useEffect, useState } from "react";
import { Button, Input, Tag, message, Popconfirm } from "antd";
import { MovieAPI } from "../../MovieAPI";
import "./FileMappingEditor.css";

function basename(p) {
  if (!p) return "";
  const parts = String(p).split(/[\\/]/).filter(Boolean);
  return parts[parts.length - 1] || String(p);
}

// Inline file-mapping editor for a LIVE movie or series, mounted in the edit modal. Reuses the same
// IngestReview Set/Remove endpoints the review tool uses (editor-gated, not pending-gated). A pasted path
// is resolved to a full on-disk path server-side (a series resolves a bare filename against its scanned
// folder listing; a movie must be a full rooted path). kind: "movie" | "series".
export default function FileMappingEditor({ id, kind }) {
  const [detail, setDetail] = useState(null);
  const [loading, setLoading] = useState(true);
  const [working, setWorking] = useState(false);
  const [moviePrimary, setMoviePrimary] = useState("");
  const [movieExtra, setMovieExtra] = useState("");
  const [settingEp, setSettingEp] = useState(null);
  const [epPath, setEpPath] = useState("");
  const [extraPath, setExtraPath] = useState("");
  const [extraSeason, setExtraSeason] = useState("");
  const [openSeasons, setOpenSeasons] = useState({});

  const refresh = useCallback(async () => {
    try {
      const res = await MovieAPI.ingestReviewDetail(id, kind);
      setDetail(await res.json());
    } catch {
      /* keep prior detail */
    } finally {
      setLoading(false);
    }
  }, [id, kind]);

  useEffect(() => { refresh(); }, [refresh]);

  // Run a file op, surface a server error message, then re-pull the detail so the UI reflects the change.
  async function run(fn, after) {
    setWorking(true);
    try {
      const res = await fn();
      if (!res.ok) {
        const b = await res.json().catch(() => ({}));
        message.error(b.message || "File update failed.");
        return false;
      }
      if (after) after();
      await refresh();
      return true;
    } finally {
      setWorking(false);
    }
  }

  const removeFile = (mediaFileId) => run(() => MovieAPI.ingestReviewRemoveFile(mediaFileId));
  const RemoveX = ({ mediaFileId, what = "file mapping" }) =>
    mediaFileId ? (
      <Popconfirm title={`Remove this ${what}?`} okText="Remove" okButtonProps={{ danger: true }} onConfirm={() => removeFile(mediaFileId)}>
        <a className="fme-x" title="remove">✕</a>
      </Popconfirm>
    ) : null;

  if (loading) return <div className="fme-loading">Loading files…</div>;
  if (!detail) return <div className="fme-loading">Couldn't load files.</div>;

  // ── MOVIE ──
  if (kind !== "series") {
    const files = (detail.files || []).filter((f) => f.path);
    const setPrimary = () => run(() => MovieAPI.ingestReviewSetFile({ targetType: "movie", targetId: id, role: "Primary", path: moviePrimary.trim() }), () => setMoviePrimary(""));
    const addExtra = () => run(() => MovieAPI.ingestReviewSetFile({ targetType: "movie", targetId: id, role: "Extra", path: movieExtra.trim() }), () => setMovieExtra(""));
    const move = (mediaFileId, action) => run(() => MovieAPI.ingestReviewMoveFile(mediaFileId, action));
    // The Primary + Parts form one ordered "feature sequence" (Extras/Variants sit outside it).
    const seq = files.filter((f) => f.role === "Primary" || f.role === "Part");
    return (
      <div className="fme">
        <div className="fme-hd">File mapping</div>
        {files.length === 0 ? <div className="fme-empty">No file mapped.</div> : files.map((f) => {
          const seqIdx = seq.findIndex((x) => x.mediaFileId === f.mediaFileId);
          const inSeq = seqIdx >= 0;
          return (
          <div className="fme-row" key={f.mediaFileId}>
            <Tag color={f.role === "Primary" ? "green" : "purple"}>{f.role}{f.role === "Part" && f.partNumber ? " " + f.partNumber : ""}</Tag>
            {f.label ? <Tag>{f.label}</Tag> : null}
            <span className="fme-path" title={f.path}>{basename(f.path)}</span>
            {inSeq && (
              <a className="fme-move" title="move up" style={{ visibility: seqIdx > 0 ? "visible" : "hidden" }}
                 onClick={() => !working && move(f.mediaFileId, "up")}>↑</a>
            )}
            {inSeq && (
              <a className="fme-move" title="move down" style={{ visibility: seqIdx < seq.length - 1 ? "visible" : "hidden" }}
                 onClick={() => !working && move(f.mediaFileId, "down")}>↓</a>
            )}
            {f.role !== "Primary" && (
              <a className="fme-move" title="make primary" onClick={() => !working && move(f.mediaFileId, "primary")}>★</a>
            )}
            <RemoveX mediaFileId={f.mediaFileId} />
          </div>
          );
        })}
        <div className="fme-add">
          <Input size="small" placeholder="paste full path (L:\…) to set the primary file" value={moviePrimary}
            onChange={(e) => setMoviePrimary(e.target.value)} onPressEnter={() => moviePrimary.trim() && setPrimary()} />
          <Button size="small" type="primary" loading={working} disabled={!moviePrimary.trim()} onClick={setPrimary}>Set primary</Button>
        </div>
        <div className="fme-add">
          <Input size="small" placeholder="add an extra (alt cut, commentary…)" value={movieExtra}
            onChange={(e) => setMovieExtra(e.target.value)} onPressEnter={() => movieExtra.trim() && addExtra()} />
          <Button size="small" loading={working} disabled={!movieExtra.trim()} onClick={addExtra}>+ extra</Button>
        </div>
      </div>
    );
  }

  // ── SERIES ──
  const seasons = detail.seasons || [];
  const seriesExtras = (detail.seriesExtras || []).filter((x) => x.path);
  const toggle = (s) => setOpenSeasons((o) => ({ ...o, [s]: !o[s] }));
  const submitEpPrimary = (episodeId) => run(() => MovieAPI.ingestReviewSetEpisodeFile(episodeId, epPath.trim() || null), () => { setSettingEp(null); setEpPath(""); });
  const submitEpExtra = (episodeId) => { if (!epPath.trim()) { message.error("Paste a path for the extra."); return; } return run(() => MovieAPI.ingestReviewSetFile({ targetType: "episode", targetId: episodeId, role: "Extra", path: epPath.trim() }), () => { setSettingEp(null); setEpPath(""); }); };
  const submitSeriesExtra = () => { if (!extraPath.trim()) { message.error("Paste a path for the extra."); return; } const season = extraSeason.trim() === "" ? null : Number(extraSeason); return run(() => MovieAPI.ingestReviewSetFile({ targetType: "series", targetId: id, seasonNumber: season, role: "Extra", path: extraPath.trim() }), () => { setExtraPath(""); setExtraSeason(""); }); };

  return (
    <div className="fme">
      <div className="fme-hd">Episode file mapping</div>
      {detail.folderListing ? (
        <details className="fme-folderdump">
          <summary>📁 on-disk folder — copy a path to paste below</summary>
          <div className="fme-folderbox">
            {detail.folderListing.split("\n").map((ln, i) => (
              <div key={i} className={ln.startsWith("[??]") ? "fme-fd-no" : ln.startsWith("[OK]") ? "fme-fd-ok" : ""}>{ln || " "}</div>
            ))}
          </div>
        </details>
      ) : null}
      {seasons.map((s) => {
        const have = s.episodes.filter((e) => (e.files || []).some((f) => f.path)).length;
        return (
          <div className="fme-season" key={s.season}>
            <button className="fme-season-hd" onClick={() => toggle(s.season)}>
              {openSeasons[s.season] ? "▾" : "▸"} {s.season === 0 ? "Specials" : "Season " + s.season} · {have}/{s.episodes.length}
            </button>
            {openSeasons[s.season] && s.episodes.map((e) => {
              const files = (e.files || []).filter((f) => f.path);
              const primary = files.find((f) => f.role === "Primary") || files[0] || null;
              const extras = files.filter((f) => f !== primary);
              const editing = settingEp === e.episodeId;
              return (
                <div className={"fme-ep" + (primary ? "" : " fme-ep-missing")} key={e.episodeId}>
                  <span className="fme-epnum">E{e.episode}</span>
                  <span className="fme-eptitle" title={e.title || ""}>{e.title || "—"}</span>
                  {editing ? (
                    <span className="fme-setfile">
                      <Input size="small" style={{ width: 240 }} value={epPath} placeholder="paste full path (blank clears primary)" onChange={(ev) => setEpPath(ev.target.value)} onPressEnter={() => submitEpPrimary(e.episodeId)} />
                      <Button size="small" type="primary" loading={working} onClick={() => submitEpPrimary(e.episodeId)}>Set</Button>
                      <Button size="small" loading={working} onClick={() => submitEpExtra(e.episodeId)}>+extra</Button>
                      <Button size="small" onClick={() => { setSettingEp(null); setEpPath(""); }}>✕</Button>
                    </span>
                  ) : (
                    <>
                      {primary ? <span className="fme-path" title={primary.path}>{basename(primary.path)}</span> : <span className="fme-missing">no file</span>}
                      <RemoveX mediaFileId={primary?.mediaFileId} />
                      <a className="fme-setlink" onClick={() => { setSettingEp(e.episodeId); setEpPath(primary ? primary.path : ""); }}>✎ set file</a>
                    </>
                  )}
                  {extras.map((x) => (
                    <span className="fme-extra" key={x.mediaFileId}>
                      <Tag color="purple">extra</Tag>
                      <span className="fme-path" title={x.path}>{basename(x.path)}</span>
                      <RemoveX mediaFileId={x.mediaFileId} what="extra" />
                    </span>
                  ))}
                </div>
              );
            })}
          </div>
        );
      })}
      <div className="fme-season">
        <div className="fme-season-hd fme-season-hd--static">Series / season extras</div>
        {seriesExtras.map((x) => (
          <div className="fme-ep" key={x.mediaFileId}>
            <Tag color="purple">extra</Tag>
            <span className="fme-path" title={x.path}>{basename(x.path)}</span>
            <RemoveX mediaFileId={x.mediaFileId} what="extra" />
          </div>
        ))}
        <span className="fme-setfile">
          <Input size="small" style={{ width: 64 }} value={extraSeason} placeholder="s#" onChange={(e) => setExtraSeason(e.target.value)} />
          <Input size="small" style={{ width: 220 }} value={extraPath} placeholder="paste extra path (making-of, special…)" onChange={(e) => setExtraPath(e.target.value)} onPressEnter={submitSeriesExtra} />
          <Button size="small" type="primary" loading={working} onClick={submitSeriesExtra}>Add extra</Button>
        </span>
      </div>
    </div>
  );
}
