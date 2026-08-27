/**
 * `/books/shelf?tab=last-opened|read|want|suggested` — the reader's own shelves. Last opened is the
 * host's `/shelf/last-opened` with a hover ✕ (optimistic, then `hideFromHistory`); Read and Want are
 * series tiles (`/shelf/series?kind=`) over the standalone items (`/marks/items?kind=`, an item being
 * standalone when it has no series or is a single-issue series — any issue of a real series is its
 * series tile, never a loose card too); Suggested is `/suggestions?count=48`, asked for only on its
 * tab. Ratings show on Read only.
 *
 * The cover size is a device tweak (`books-shelf`) reached the way every other view reaches one:
 * the ⚙ in the section bar's tools slot (R9 S5). The page's own "Size" slider in the header — the
 * last bespoke tweak control on the site — is gone.
 */
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useCallback, useMemo, type CSSProperties } from "react";
import { useHistory, useLocation } from "react-router-dom";
import PageTweaksTool from "../../catalog/tweaks/PageTweaksTool";
import useTweaks from "../../catalog/tweaks/useTweaks";
import type { ItemMark, ItemSummary } from "./booksApi";
import { fetchItemMarks, fetchLastOpened, fetchShelfSeries, fetchSuggestions, hideFromHistory, putGroupMark, putItemMark } from "./booksApi";
import { dateLabel } from "./booksFormat";
import { bk, invalidateAfter, setGroupMarkOverride } from "./booksQuery";
import { openEntity } from "./openEntity";
import { CoverThumb } from "./RelatedStrip";
import ShelfSeriesGrid from "./ShelfSeriesGrid";
import ShelfStars from "./ShelfStars";
import { firstAuthor } from "../../catalog/sources/novelsSource";
import "./css/books-shelf.css";

export type ShelfTab = "last-opened" | "read" | "want" | "suggested";
export const SHELF_TABS: ShelfTab[] = ["last-opened", "read", "want", "suggested"];
const LIST_TOP = 200;

const ICONS: Record<ShelfTab, string> = {
  "last-opened": "M12 7v5l3 2 M12 21a9 9 0 1 0 0-18 9 9 0 0 0 0 18z",
  read: "M9 12l2 2 4-4 M12 21a9 9 0 1 0 0-18 9 9 0 0 0 0 18z",
  want: "M6 4h12v16l-6-4-6 4z",
  suggested: "M12 4v16M4 12h16",
};
const META: Record<ShelfTab, { label: string; empty: [string, string] }> = {
  "last-opened": { label: "Last opened", empty: ["◷", "Comics you open will collect here so you can pick up where you left off."] },
  read: { label: "Read", empty: ["✓", "Mark comics or whole series as read to build your reading history."] },
  want: { label: "Want to read", empty: ["＋", "Add comics or series to your reading list from any detail view."] },
  suggested: { label: "Suggested", empty: ["✦", "Shelve a few comics and we'll suggest more like them."] },
};

export function readShelfTab(search: string): ShelfTab {
  const t = new URLSearchParams(search).get("tab");
  return (SHELF_TABS as string[]).includes(t ?? "") ? (t as ShelfTab) : "last-opened";
}

/** Standalone = no series, or a single-issue series (the catalog collapses it to one entity). */
export const isStandalone = (c: ItemSummary) => c.seriesId == null || c.isSingleIssueSeries;

function ShelfItemCard({ item, scale, showRatings, rating, onRate, onOpen, onRemove }: {
  item: ItemSummary; scale: number; showRatings: boolean; rating: number | null;
  onRate: (rating: number | null) => void; onOpen: (item: ItemSummary) => void; onRemove?: (item: ItemSummary) => void;
}) {
  const h = Math.round(200 * scale);
  const w = Math.round(h * (item.coverAspect && item.coverAspect > 0 ? Math.min(1.6, Math.max(0.35, item.coverAspect)) : 0.66));
  const author = item.kind === "book" ? firstAuthor(item.creatorsCsv) : undefined;
  const label = dateLabel(item.year, item.month, item.datePrecision);
  return (
    <div className="bs-card bx-hover-lift" role="button" tabIndex={0} onClick={() => onOpen(item)} onKeyDown={(e) => { if (e.key === "Enter" || e.key === " ") { e.preventDefault(); onOpen(item); } }} aria-label={item.title ?? item.fileName}>
      <div className="bs-card-cover bx-cover" style={{ height: h, width: w }}>
        <CoverThumb item={item} />
        {onRemove && (
          <button type="button" className="bs-card-remove" title="Remove from Last opened" aria-label={`Remove ${item.title ?? item.fileName} from Last opened`} onClick={(e) => { e.stopPropagation(); onRemove(item); }}>
            <svg viewBox="0 0 24 24" width="13" height="13" fill="none" stroke="currentColor" strokeWidth="2.2" strokeLinecap="round" strokeLinejoin="round" aria-hidden="true"><path d="M6 6l12 12M18 6L6 18" /></svg>
          </button>
        )}
      </div>
      <div className="bs-card-meta" style={{ width: w, minWidth: 100 }}>
        <div className="bs-card-title">{item.title ?? item.fileName}</div>
        {author && <div className="bs-card-author">{author}</div>}
        {label && <div className="bs-card-date">{label}</div>}
        {showRatings && <ShelfStars className="bs-card-rate" value={rating} onSet={onRate} />}
      </div>
    </div>
  );
}

export default function ShelfPage() {
  const history = useHistory();
  const location = useLocation();
  const qc = useQueryClient();
  const tab = readShelfTab(location.search);
  const { coverScale } = useTweaks("books-shelf");
  const scale = coverScale("grid");

  const lastOpened = useQuery({ queryKey: bk.shelf("last-opened"), queryFn: ({ signal }) => fetchLastOpened(0, LIST_TOP, signal) });
  const readSeries = useQuery({ queryKey: bk.shelfSeries("read"), queryFn: ({ signal }) => fetchShelfSeries("read", 0, LIST_TOP, signal) });
  const wantSeries = useQuery({ queryKey: bk.shelfSeries("want"), queryFn: ({ signal }) => fetchShelfSeries("want", 0, LIST_TOP, signal) });
  const readItems = useQuery({ queryKey: bk.itemMarks("read"), queryFn: ({ signal }) => fetchItemMarks("read", 0, LIST_TOP, signal) });
  const wantItems = useQuery({ queryKey: bk.itemMarks("want"), queryFn: ({ signal }) => fetchItemMarks("want", 0, LIST_TOP, signal) });
  const suggested = useQuery({ queryKey: bk.suggestions(48), queryFn: ({ signal }) => fetchSuggestions(48, undefined, signal), enabled: tab === "suggested", staleTime: 0, retry: false });

  const marksToItems = (marks: ItemMark[] | undefined) => (marks ?? []).map((m) => m.item).filter((i): i is ItemSummary => !!i);
  const lastItems = useMemo(() => (lastOpened.data?.entries ?? []).map((e) => e.item).filter((i): i is ItemSummary => !!i), [lastOpened.data]);
  const readAll = useMemo(() => marksToItems(readItems.data?.entries), [readItems.data]);
  const standaloneRead = useMemo(() => readAll.filter(isStandalone), [readAll]);
  const standaloneWant = useMemo(() => marksToItems(wantItems.data?.entries).filter(isStandalone), [wantItems.data]);
  const ratings = useMemo(() => {
    const m = new Map<number, number>();
    for (const e of readItems.data?.entries ?? []) if (e.rating != null) m.set(e.itemId, e.rating);
    return m;
  }, [readItems.data]);

  const counts: Record<ShelfTab, number> = {
    "last-opened": lastOpened.data?.totalCount ?? lastItems.length,
    read: (readSeries.data?.series.length ?? 0) + standaloneRead.length,
    want: (wantSeries.data?.series.length ?? 0) + standaloneWant.length,
    suggested: suggested.data?.items.length ?? 0,
  };
  const activeSeries = tab === "read" ? readSeries.data?.series ?? [] : tab === "want" ? wantSeries.data?.series ?? [] : [];
  const activeItems: ItemSummary[] = tab === "last-opened" ? lastItems : tab === "read" ? standaloneRead : tab === "want" ? standaloneWant : suggested.data?.items ?? [];
  const showRatings = tab === "read";
  const loading = tab === "suggested" ? suggested.isLoading : tab === "last-opened" ? lastOpened.isLoading : readSeries.isLoading || readItems.isLoading || wantSeries.isLoading || wantItems.isLoading;
  const isEmpty = !loading && activeSeries.length === 0 && activeItems.length === 0;

  const setTab = (next: ShelfTab) => {
    const p = new URLSearchParams(location.search);
    p.set("tab", next);
    history.push({ pathname: location.pathname, search: `?${p.toString()}` });
  };
  const onOpenItem = useCallback((item: ItemSummary) => openEntity(history, location, { kind: "item", id: item.id }), [history, location]);
  const onOpenSeries = useCallback((seriesId: number) => openEntity(history, location, { kind: "series", id: seriesId }), [history, location]);

  const remove = useMutation({
    mutationFn: (id: number) => hideFromHistory(id),
    onMutate: (id) => {
      qc.setQueryData(bk.shelf("last-opened"), (old: typeof lastOpened.data) => (old ? { ...old, totalCount: Math.max(0, old.totalCount - 1), entries: old.entries.filter((e) => e.itemId !== id) } : old));
    },
    onSettled: () => Promise.all([qc.invalidateQueries({ queryKey: bk.shelf("last-opened") }), qc.invalidateQueries({ queryKey: ["books", "history"] })]),
  });
  const rateItem = useMutation({
    mutationFn: ({ id, rating }: { id: number; rating: number | null }) => putItemMark(id, { rating }),
    onSettled: (_r, _e, v) => invalidateAfter(qc, { kind: "itemMark", itemId: v.id }),
  });
  const rateSeries = useMutation({
    mutationFn: ({ id, rating }: { id: number; rating: number | null }) => putGroupMark("series", String(id), { rating }),
    onSuccess: (r, v) => setGroupMarkOverride("series", String(v.id), { isRead: r.mark.isRead, wantToRead: r.mark.wantToRead, isFavorite: r.mark.isFavorite, rating: r.mark.rating, notes: r.mark.notes }),
    onSettled: (_r, _e, v) => invalidateAfter(qc, { kind: "groupMark", groupType: "series", groupKey: String(v.id) }),
  });

  const meta = META[tab];
  const singleLabel = tab === "last-opened" ? "Recently opened" : "Single issues & books";

  return (
    <div className="bookshelf books-surface">
      {/* The page's ⚙, in the ONE tools slot — the shelf draws its own cards, so cover size is the
          only standard row that reaches them (a control that does not apply is removed, not shown). */}
      <PageTweaksTool section="books-shelf" view="grid" rows={{ hover: false, rounded: false, metadata: false }} footNote="Remembered on this device for your shelf." />
      <header className="bs-head">
        <div>
          <div className="bs-eyebrow">Your library</div>
          <h1 className="bs-title">Shelf</h1>
        </div>
        <div className="bs-head-right">
          {counts[tab] > 0 && (
            <div className="bs-headcount"><b>{counts[tab]}</b> {counts[tab] === 1 ? "item" : "items"}<span className="bs-headcount-sub"> · {meta.label.toLowerCase()}</span></div>
          )}
        </div>
      </header>

      <div className="bs-segbar" role="tablist" aria-label="Shelves">
        {SHELF_TABS.map((t) => (
          <button key={t} type="button" role="tab" aria-selected={t === tab} className={`bs-seg${t === tab ? " on" : ""}`} onClick={() => setTab(t)}>
            <svg viewBox="0 0 24 24" width="15" height="15" fill="none" stroke="currentColor" strokeWidth="1.7" strokeLinecap="round" strokeLinejoin="round" aria-hidden="true"><path d={ICONS[t]} /></svg>
            <span>{META[t].label}</span>
            {counts[t] > 0 && <span className="bs-seg-num">{counts[t]}</span>}
          </button>
        ))}
      </div>

      {loading ? (
        <div className="bs-note" aria-busy="true">Loading your shelf…</div>
      ) : isEmpty ? (
        <div className="bs-empty">
          <span className="bs-empty-mark">{meta.empty[0]}</span>
          <div className="bs-empty-text">
            <div className="bs-empty-title">Nothing in {meta.label.toLowerCase()} yet</div>
            <div>{meta.empty[1]}</div>
          </div>
        </div>
      ) : (
        <div className="bs-grid">
          {tab === "suggested" && <div className="bs-grid-note">Based on the comics on your shelves</div>}
          {activeSeries.length > 0 && (
            <section className="bs-section">
              <div className="bs-sec-head"><span className="bs-sec-label">Series</span><span className="bs-sec-count">{activeSeries.length}</span><span className="bs-sec-rule" /></div>
              <ShelfSeriesGrid
                series={activeSeries}
                showRatings={showRatings}
                scale={scale}
                ratings={ratings}
                onRateSeries={(id, rating) => rateSeries.mutate({ id, rating })}
                onOpenSeries={onOpenSeries}
                onOpenItem={onOpenItem}
              />
            </section>
          )}
          {activeItems.length > 0 && (
            <section className="bs-section">
              {activeSeries.length > 0 && (
                <div className="bs-sec-head"><span className="bs-sec-label">{singleLabel}</span><span className="bs-sec-count">{activeItems.length}</span><span className="bs-sec-rule" /></div>
              )}
              <div className="bs-cards" style={{ "--cell": `${Math.round(200 * scale)}px` } as CSSProperties}>
                {activeItems.map((item) => (
                  <ShelfItemCard
                    key={item.id}
                    item={item}
                    scale={scale}
                    showRatings={showRatings}
                    rating={ratings.get(item.id) ?? null}
                    onRate={(rating) => rateItem.mutate({ id: item.id, rating })}
                    onOpen={onOpenItem}
                    onRemove={tab === "last-opened" ? (i) => remove.mutate(i.id) : undefined}
                  />
                ))}
              </div>
            </section>
          )}
        </div>
      )}
    </div>
  );
}
