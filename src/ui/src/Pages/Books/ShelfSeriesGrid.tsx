/**
 * The shelf's series tiles — a compact auto-fill grid — with ONE inline drawer dropped after the open
 * tile's row (the live column count is measured off the grid so the drawer never leaves a hole). The
 * standalone's `SeriesShelfGrid`, on the host's `ShelfSeriesCard` rows.
 */
import { Fragment, useEffect, useRef, useState, type RefObject } from "react";
import { hueSvg } from "../../catalog/cards/CardImage";
import { hueOf } from "../../catalog/sources/hue";
import type { ItemSummary, ShelfSeriesCard } from "./booksApi";
import { runLabel } from "./booksFormat";
import { thumbUrl } from "./booksMedia";
import ShelfDrawer from "./ShelfDrawer";
import ShelfStars from "./ShelfStars";

/** Tracks the live column count of a CSS auto-fill grid. */
export function useGridColumns(ref: RefObject<HTMLElement | null>): number {
  const [cols, setCols] = useState(1);
  useEffect(() => {
    const el = ref.current;
    if (!el) return;
    const measure = () => {
      const tpl = getComputedStyle(el).gridTemplateColumns;
      const n = tpl && tpl !== "none" ? tpl.split(" ").filter(Boolean).length : 1;
      setCols(Math.max(1, n));
    };
    measure();
    if (typeof ResizeObserver === "undefined") return;
    const ro = new ResizeObserver(measure);
    ro.observe(el);
    return () => ro.disconnect();
  }, [ref]);
  return cols;
}

const CHEVRON = (
  <svg viewBox="0 0 24 24" width="12" height="12" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round" aria-hidden="true"><path d="M6 9l6 6 6-6" /></svg>
);

function SeriesTile({ series, open, showRatings, onRate, onOpenSeries, onManage }: {
  series: ShelfSeriesCard; open: boolean; showRatings: boolean;
  onRate: (rating: number | null) => void; onOpenSeries: (seriesId: number, label: string) => void; onManage: () => void;
}) {
  const { issueCount: count, finishedCount } = series;
  const allRead = finishedCount >= count && count > 0;
  const cover = series.coverItemId != null ? thumbUrl(series.coverItemId) : null;
  const run = runLabel(series.year, series.yearEnd, series.isOngoing);
  return (
    <div className={`bs-tile${open ? " on" : ""}`}>
      <button type="button" className="bs-tile-cover" title="Open series" onClick={() => onOpenSeries(series.seriesId, series.seriesName)} aria-label={`Open ${series.seriesName}`}>
        <img src={cover ?? hueSvg(hueOf(series.seriesName), 100, 150)} alt="" loading="lazy" />
        {(allRead || finishedCount > 0) && <span className={`bs-tile-prog${allRead ? " done" : ""}`}>{allRead ? "All read" : `${finishedCount}/${count}`}</span>}
        {count > 1 && <span className="bs-tile-count">{count}</span>}
      </button>
      <button type="button" className="bs-tile-name" title={series.seriesName} onClick={() => onOpenSeries(series.seriesId, series.seriesName)}>{series.seriesName}</button>
      <div className="bs-tile-sub">
        {series.publisher && <span>{series.publisher}</span>}
        <span>{run ?? `${count} iss.`}</span>
      </div>
      {showRatings && <ShelfStars className="bs-tile-rate" value={series.rating} onSet={onRate} />}
      <button type="button" className={`bs-tile-manage${open ? " on" : ""}`} aria-expanded={open} onClick={onManage}>
        <span>{open ? "Hide issues" : "Manage issues"}</span>
        <span className="bs-tile-chev" style={{ transform: open ? "rotate(180deg)" : "none" }}>{CHEVRON}</span>
      </button>
    </div>
  );
}

export interface ShelfSeriesGridProps {
  series: ShelfSeriesCard[];
  showRatings: boolean;
  scale: number;
  ratings: Map<number, number>;
  onRateSeries: (seriesId: number, rating: number | null) => void;
  onOpenSeries: (seriesId: number, label: string) => void;
  onOpenItem: (item: ItemSummary) => void;
}

export default function ShelfSeriesGrid({ series, showRatings, scale, ratings, onRateSeries, onOpenSeries, onOpenItem }: ShelfSeriesGridProps) {
  const gridRef = useRef<HTMLDivElement>(null);
  const cols = useGridColumns(gridRef);
  const [openId, setOpenId] = useState<number | null>(null);
  const openIdx = openId == null ? -1 : series.findIndex((s) => s.seriesId === openId);
  const openSeries = openIdx >= 0 ? series[openIdx] : null;
  const drawerAfter = openIdx >= 0 ? Math.min(series.length - 1, (Math.floor(openIdx / cols) + 1) * cols - 1) : -1;

  return (
    <div className="bs-series-grid" ref={gridRef} style={{ "--bs-tile-min": `${Math.round(168 * scale)}px` } as React.CSSProperties}>
      {series.map((s, i) => (
        <Fragment key={s.seriesId}>
          <SeriesTile
            series={s}
            open={s.seriesId === openId}
            showRatings={showRatings}
            onRate={(rating) => onRateSeries(s.seriesId, rating)}
            onOpenSeries={onOpenSeries}
            onManage={() => setOpenId((o) => (o === s.seriesId ? null : s.seriesId))}
          />
          {i === drawerAfter && openSeries && (
            <ShelfDrawer series={openSeries} showRatings={showRatings} ratings={ratings} onClose={() => setOpenId(null)} onOpenSeries={onOpenSeries} onOpenItem={onOpenItem} />
          )}
        </Fragment>
      ))}
    </div>
  );
}
