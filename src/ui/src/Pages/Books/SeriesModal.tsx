/**
 * The series modal (`?series=<id>`): the standalone's GroupDetailModal over the host's run
 * (`/browse/series/{id}/run` — every issue with its reading-order and containment rows), the group
 * head (`/browse/groups?singleGroupKey=`) for the label, count, the caller's mark and the AI card, the
 * library rating, and the first issue's detail for the series-level description legs.
 *
 * "Mark read" on a series fans out to its issues on the host in batches; the client re-PUTs while
 * `issuesRemaining` > 0, with a no-progress break. A toggle here shows on the browse band it came
 * from through `setGroupMarkOverride` (no band refetch) and invalidates the shelf/history queries.
 */
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useEffect, useMemo, useState } from "react";
import { useHistory, useLocation } from "react-router-dom";
import { hueOf } from "../../catalog/sources/hue";
import { fetchGroups, fetchItem, fetchSeriesLibraryRating, fetchSeriesProgress, fetchSeriesRun, putGroupMark, type GroupUserMark, type SeriesRunRow } from "./booksApi";
import { clampAspect, plural, runLabel, seriesSynopsisFor, stripHtml } from "./booksFormat";
import { directoryHref, facetHref } from "./booksLinks";
import BooksModalShell, { ICON, Icon } from "./BooksModalShell";
import { bk, invalidateAfter, setGroupMarkOverride } from "./booksQuery";
import CoverStack from "./CoverStack";
import { closeEntity, openEntity } from "./openEntity";
import { RateTen } from "./RatingStars";
import ReadingList from "./ReadingList";

const EMPTY_MARK: GroupUserMark = { isRead: false, wantToRead: false, isFavorite: false, rating: null, notes: null };
const FANOUT_MAX_ROUNDS = 40;

/** Re-PUT until the host reports nothing remaining; stop on a round that made no progress. */
export async function driveGroupMark(key: string, body: Parameters<typeof putGroupMark>[2]): Promise<GroupUserMark> {
  let last = -1;
  let result = await putGroupMark("series", key, body);
  for (let round = 0; round < FANOUT_MAX_ROUNDS && result.issuesRemaining > 0; round += 1) {
    if (result.issuesRemaining === last) break;
    last = result.issuesRemaining;
    result = await putGroupMark("series", key, {});
  }
  const m = result.mark;
  return { isRead: m.isRead, wantToRead: m.wantToRead, isFavorite: m.isFavorite, rating: m.rating, notes: m.notes };
}

export interface SeriesModalProps { seriesId: number; isKid?: boolean }

export default function SeriesModal({ seriesId, isKid = false }: SeriesModalProps) {
  const history = useHistory();
  const location = useLocation();
  const qc = useQueryClient();
  const key = String(seriesId);

  const head = useQuery({
    queryKey: bk.seriesGroup(seriesId),
    queryFn: () => fetchGroups({ groupBy: "series", singleGroupKey: key, perGroupTop: 1 }),
    staleTime: 60 * 1000,
  });
  const run = useQuery({ queryKey: bk.seriesRun(seriesId), queryFn: () => fetchSeriesRun(seriesId), staleTime: 5 * 60 * 1000 });
  const libRating = useQuery({ queryKey: bk.seriesRating(seriesId), queryFn: () => fetchSeriesLibraryRating(seriesId), staleTime: 5 * 60 * 1000 });
  const progress = useQuery({ queryKey: bk.seriesProgress(seriesId), queryFn: () => fetchSeriesProgress(seriesId), staleTime: 60 * 1000 });
  const rows = useMemo(() => run.data?.items ?? [], [run.data]);
  const firstId = rows[0]?.item.id;
  const firstDetail = useQuery({ queryKey: [...bk.item(firstId ?? 0), "about"], queryFn: () => fetchItem(firstId!), enabled: firstId != null, staleTime: 5 * 60 * 1000 });

  const group = head.data?.groups[0] ?? null;
  const gone = head.isSuccess && !group && run.isSuccess && rows.length === 0;
  const serverMark = group?.userMeta ?? EMPTY_MARK;
  const [mark, setMark] = useState<GroupUserMark>(serverMark);
  useEffect(() => { setMark(serverMark); }, [serverMark]);
  const [notes, setNotes] = useState(serverMark.notes ?? "");
  useEffect(() => { setNotes(serverMark.notes ?? ""); }, [serverMark.notes]);

  const write = useMutation({
    mutationFn: (body: Parameters<typeof putGroupMark>[2]) => driveGroupMark(key, body),
    onSuccess: (next) => { setMark(next); setGroupMarkOverride("series", key, next); },
    onSettled: () => invalidateAfter(qc, { kind: "groupMark", groupType: "series", groupKey: key }),
  });

  const label = group?.label ?? rows[0]?.item.series ?? "Series";
  const total = group?.totalItems || run.data?.total || rows.length;
  const agg = useMemo(() => aggregate(rows), [rows]);
  const detail = group?.groupDetail ?? null;
  const about = seriesSynopsisFor(firstDetail.data ?? null, detail?.aiSynopsis);
  const yearSpan = runLabel(rows[0]?.item.seriesYearStart ?? agg.yearMin, rows[0]?.item.seriesYearEnd ?? agg.yearMax, rows[0]?.item.seriesIsOngoing);
  const publisherHue = hueOf(agg.publishers[0] ?? label);
  const tagsByCategory = useMemo(() => {
    const acc: Record<string, string[]> = {};
    for (const t of detail?.aiTags ?? []) {
      const sep = t.indexOf(":");
      const cat = sep > 0 ? t.slice(0, sep) : "other";
      (acc[cat] ??= []).push(sep > 0 ? t.slice(sep + 1) : t);
    }
    return acc;
  }, [detail?.aiTags]);

  const close = () => closeEntity(history, location);
  const go = (href: string) => history.push(href);
  const openRow = (r: SeriesRunRow) => openEntity(history, location, { kind: "item", id: r.item.id });
  const finished = useMemo(() => new Set(progress.data?.finishedIds ?? []), [progress.data]);

  return (
    <BooksModalShell open onClose={close} ariaLabel={label} variant="series">
      {gone || head.isError ? (
        <div className="cm-loading">This series is not available.</div>
      ) : head.isLoading || run.isLoading ? (
        <div className="cm-loading">Loading…</div>
      ) : (
        <div className="cm-grid">
          <div className="cm-left">
            {rows.length > 0 && <CoverStack items={rows.map((r) => r.item)} count={total} aspect={clampAspect(rows[0].item.coverAspect)} />}
            <div className="cm-actions">
              {!isKid && (
                <button type="button" className="cm-btn cm-btn-primary" onClick={() => go(facetHref("series", seriesId))}>
                  <Icon d={ICON.grid} /><span>Browse this series</span>
                </button>
              )}
              {!isKid && agg.folderId != null && (
                <button type="button" className="cm-btn" onClick={() => go(directoryHref(agg.folderId!))}>
                  <Icon d={ICON.folder} /><span>Browse this folder</span>
                </button>
              )}
              <button type="button" className={`cm-btn${mark.wantToRead ? " on" : ""}`} onClick={() => write.mutate({ wantToRead: !mark.wantToRead })} disabled={write.isPending} data-testid="series-want">
                <Icon d={ICON.bookmark} fill={mark.wantToRead} /><span>{mark.wantToRead ? "On your list" : "Want to read"}</span>
              </button>
              <button type="button" className={`cm-btn${mark.isRead ? " on" : ""}`} onClick={() => write.mutate({ isRead: !mark.isRead })} disabled={write.isPending} data-testid="series-marked">
                <Icon d={ICON.check} /><span>{mark.isRead ? "Series read" : write.isPending ? "Marking…" : "Mark read"}</span>
              </button>
            </div>
          </div>
          <div className="cm-right">
            <div className="cm-kindrow">
              <span className="cm-kind cm-kind-series" style={{ background: `oklch(0.56 0.16 ${publisherHue})` }}><Icon d={ICON.layers} /> Series</span>
              <span className="cm-kind-count">{plural(total, "book")}{yearSpan ? ` · ${yearSpan}` : ""}</span>
            </div>
            <h2 className="cm-title">{label}</h2>

            {agg.publishers.length > 0 && (
              <button type="button" className="cm-pub" onClick={isKid ? undefined : () => go(facetHref("publisher", agg.publishers[0]))} disabled={isKid} title={`Browse ${agg.publishers[0]}`}>
                <span className="cm-pub-swatch" style={{ background: `oklch(0.74 0.15 ${publisherHue})` }} />
                {agg.publishers[0]}{agg.publishers.length > 1 ? ` + ${agg.publishers.length - 1} more` : ""}
              </button>
            )}

            {agg.creators.length > 0 && (
              <div className="cm-credits">
                <div><span className="cm-k">Creators</span>{agg.creators.slice(0, 4).map((p, i) => (
                  <span key={p}>{i > 0 && <span className="cm-sep">, </span>}<button type="button" className="cm-link" onClick={isKid ? undefined : () => go(facetHref("author", p))} disabled={isKid}>{p}</button></span>
                ))}</div>
              </div>
            )}

            <div className="cm-stats">
              {libRating.data?.rating != null ? (
                <><span className="cm-librating" title={libRating.data.note ?? "Library score (no rationale recorded)"}>Library <strong>{libRating.data.rating}</strong></span><span className="cm-dot">·</span></>
              ) : detail?.aiRating != null ? (
                <><span>AI {(detail.aiRating / 10).toFixed(1)}/10</span><span className="cm-dot">·</span></>
              ) : null}
              <span>{plural(total, "book")}</span>
              {yearSpan && <><span className="cm-dot">·</span><span>{yearSpan}</span></>}
              {agg.pages > 0 && total <= rows.length && <><span className="cm-dot">·</span><span>{agg.pages.toLocaleString()} pp total</span></>}
              {progress.data && <><span className="cm-dot">·</span><span>{progress.data.finishedCount} / {progress.data.total} read</span></>}
            </div>

            {agg.genres.length > 0 && (
              <div className="cm-tags">
                {agg.genres.map((g) => <button key={g} type="button" className="cm-tag" onClick={isKid ? undefined : () => go(facetHref("tag", g))} disabled={isKid}>{g}</button>)}
              </div>
            )}

            {about.text && (
              <section className="cm-relsec">
                <h3 className="cm-h3">About this series</h3>
                <p className="cm-synopsis" style={{ marginTop: 0 }}>{about.text}</p>
                {about.isAi && detail?.aiKnownSeries === false && <p className="cm-ai-note">AI-inferred from publisher context</p>}
              </section>
            )}

            {Object.keys(tagsByCategory).length > 0 && (
              <section className="cm-relsec">
                <h3 className="cm-h3">Tags</h3>
                <div className="cm-tagcats">
                  {Object.entries(tagsByCategory).map(([cat, values]) => (
                    <div key={cat}>
                      <p className="cm-tagcat-label">{cat}</p>
                      <div className="cm-tags" style={{ marginTop: 0 }}>
                        {values.map((v) => <button key={v} type="button" className="cm-tag" onClick={isKid ? undefined : () => go(facetHref("tag", v))} disabled={isKid}>{v}</button>)}
                      </div>
                    </div>
                  ))}
                </div>
              </section>
            )}

            {rows.length > 0 && <ReadingList rows={rows} total={run.data?.total ?? rows.length} finishedIds={finished} onOpen={openRow} />}

            <section className="cm-relsec">
              <h3 className="cm-h3">Your rating</h3>
              <RateTen value={mark.rating} onChange={(v) => write.mutate({ rating: v })} disabled={write.isPending} />
            </section>

            <section className="cm-relsec">
              <h3 className="cm-h3">Notes</h3>
              <textarea className="cm-notes" value={notes} onChange={(e) => setNotes(e.target.value)} onBlur={() => { if (notes !== (mark.notes ?? "")) write.mutate({ notes }); }} placeholder="Add notes about this series…" rows={3} />
            </section>
          </div>
        </div>
      )}
    </BooksModalShell>
  );
}

function aggregate(rows: SeriesRunRow[]) {
  const tally = (get: (r: SeriesRunRow) => string[]) => {
    const m = new Map<string, number>();
    for (const r of rows) for (const x of get(r)) if (x) m.set(x, (m.get(x) ?? 0) + 1);
    return [...m.entries()].sort((a, b) => b[1] - a[1]).map((e) => e[0]);
  };
  const split = (csv: string | null) => (csv ?? "").split(/[,;]/).map((s) => s.trim()).filter(Boolean);
  const years = rows.map((r) => r.item.year).filter((y): y is number => !!y && y > 0);
  const folders = tally((r) => [String(r.item.folderId)]);
  return {
    publishers: tally((r) => (r.item.publisher ? [r.item.publisher] : [])),
    creators: tally((r) => split(r.item.creatorsCsv)),
    genres: tally((r) => split(r.item.tagsCsv)).slice(0, 10),
    yearMin: years.length ? Math.min(...years) : null,
    yearMax: years.length ? Math.max(...years) : null,
    pages: rows.reduce((s, r) => s + (r.item.pageCount ?? 0), 0),
    folderId: folders.length ? Number(folders[0]) : null,
    about: stripHtml(""),
  };
}
