/**
 * The shelf's inline issue manager: one series' run in reading order (`/browse/series/{id}/run`) with
 * a done-tick per issue from `/shelf/series/{id}/progress`. A tick is `lastPage: -1` (the ONE Finished
 * signal) and un-ticking resets the position — the same two calls the item modal makes — and every
 * write names what it made stale through `invalidateAfter`, so the tile's progress chip and the
 * Read tab's counts follow without a reload. Ratings (Read tab only) come from the page's marks map.
 */
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import type { ItemSummary, ShelfSeriesCard } from "./booksApi";
import { fetchSeriesProgress, fetchSeriesRun, markFinished, putItemMark, resetPosition } from "./booksApi";
import { dateLabel } from "./booksFormat";
import { bk, invalidateAfter } from "./booksQuery";
import { CoverThumb } from "./RelatedStrip";
import ShelfStars from "./ShelfStars";

export interface ShelfDrawerProps {
  series: ShelfSeriesCard;
  showRatings: boolean;
  /** Item id → the caller's 0–100 rating, from the page's marks list. */
  ratings: Map<number, number>;
  onClose: () => void;
  onOpenSeries: (seriesId: number, label: string) => void;
  onOpenItem: (item: ItemSummary) => void;
}

export default function ShelfDrawer({ series, showRatings, ratings, onClose, onOpenSeries, onOpenItem }: ShelfDrawerProps) {
  const qc = useQueryClient();
  const run = useQuery({ queryKey: bk.seriesRun(series.seriesId), queryFn: ({ signal }) => fetchSeriesRun(series.seriesId, signal), staleTime: 5 * 60 * 1000 });
  const progress = useQuery({ queryKey: bk.seriesProgress(series.seriesId), queryFn: ({ signal }) => fetchSeriesProgress(series.seriesId, signal) });
  const finished = new Set(progress.data?.finishedIds ?? []);

  const toggle = useMutation({
    mutationFn: async ({ id, done }: { id: number; done: boolean }) => (done ? resetPosition(id) : markFinished(id)),
    onSettled: (_r, _e, v) => invalidateAfter(qc, { kind: "position", itemId: v.id }),
  });
  const rate = useMutation({
    mutationFn: async ({ id, rating }: { id: number; rating: number | null }) => putItemMark(id, { rating }),
    onSettled: (_r, _e, v) => invalidateAfter(qc, { kind: "itemMark", itemId: v.id }),
  });

  const total = progress.data?.total ?? series.issueCount;
  const done = progress.data?.finishedCount ?? series.finishedCount;
  const rows = run.data?.items ?? [];

  return (
    <div className="bs-drawer" role="region" aria-label={`${series.seriesName} issues`}>
      <div className="bs-drawer-head">
        <button type="button" className="bs-drawer-title" onClick={() => onOpenSeries(series.seriesId, series.seriesName)}>{series.seriesName}</button>
        <span className="bs-drawer-sub">{done >= total && total > 0 ? `All ${total} read` : `${done}/${total} read`}</span>
        <button type="button" className="bs-drawer-close" onClick={onClose} aria-label="Close issue manager">✕</button>
      </div>
      <div className="bs-drawer-issues">
        {run.isLoading ? (
          <div className="bs-issues-note">Loading issues…</div>
        ) : run.isError ? (
          <div className="bs-issues-note">The run could not be loaded.</div>
        ) : rows.length === 0 ? (
          <div className="bs-issues-note">No issues found for this series.</div>
        ) : rows.map(({ item, readingOrder }) => {
          const isDone = finished.has(item.id);
          const num = readingOrder?.readNumber ?? readingOrder?.readIndex;
          const label = dateLabel(item.year, item.month, item.datePrecision);
          return (
            <div key={item.id} className="bs-issue">
              <button
                type="button"
                className={`bs-issue-check${isDone ? " on" : ""}`}
                title={isDone ? "Mark as unread" : "Mark as read"}
                aria-pressed={isDone}
                aria-label={`${isDone ? "Mark as unread" : "Mark as read"}: ${item.title ?? item.fileName}`}
                disabled={toggle.isPending}
                onClick={() => toggle.mutate({ id: item.id, done: isDone })}
              >
                <svg viewBox="0 0 24 24" width="13" height="13" fill="none" stroke="currentColor" strokeWidth="2.4" strokeLinecap="round" strokeLinejoin="round" aria-hidden="true"><path d="M5 12l5 5L20 6" /></svg>
              </button>
              <button type="button" className="bs-issue-cover" onClick={() => onOpenItem(item)} title="Open issue" aria-label={`Open ${item.title ?? item.fileName}`}>
                <CoverThumb item={item} />
              </button>
              <button type="button" className="bs-issue-title" onClick={() => onOpenItem(item)}>
                {num != null && <span className="bs-issue-num">#{num}</span>}
                <span className="bs-issue-name">{item.title ?? item.fileName}</span>
              </button>
              {showRatings && (
                <ShelfStars className="bs-issue-rate" value={ratings.get(item.id) ?? null} onSet={(rating) => rate.mutate({ id: item.id, rating })} />
              )}
              {label && <span className="bs-issue-date">{label}</span>}
            </div>
          );
        })}
      </div>
    </div>
  );
}
