import { memo, useCallback, useEffect, useLayoutEffect, useMemo, useRef, useState, type CSSProperties, type ReactNode } from "react";
import CatalogPager from "../pager/CatalogPager";
import Card, { cardWidth } from "../cards/Card";
import InfiniteBands, { type InfiniteBandsHandle } from "../engine/InfiniteBands";
import { prefixSums, spacerWidths, useHorizontalWindow } from "../engine/horizontalWindow";
import { NO_GROUP } from "../state/useCatalogView";
import type { CardGroup, CardItem } from "../types";
import FlatCardStream from "./FlatCardStream";
import { GRID_BASE_CELL } from "./GridView";
import { StreamEmpty, StreamFailed, StreamLoading } from "./StreamStates";
import type { ViewProps } from "./ViewProps";
import { GROUPS_PAGE_SIZE, groupLetterBuckets, groupRunLabel, useGroupedStream } from "./groupedStream";

/**
 * Extended — every group as a horizontally-scrolling strip of cards under its header, the strips
 * streamed by the band engine. Each strip remembers its scroll position across band recycling,
 * shows edge chevrons only where it can scroll, and pulls the group's next page when the reader
 * nears its right edge. Ungrouped, it is simply the Grid.
 *
 * A strip is windowed sideways the way a Shelves plank is (`engine/horizontalWindow.ts`): it
 * reserves its full run's width up front and mounts only the cards within half a scrollport of
 * the visible ones, exact-width spacers standing in for the rest. A band mount is therefore
 * ~20 strips × a screenful, not 20 × 48 — the band-mount long task the instruments measured on
 * Arcade was the 960-card commit — and a strip grown by "more" pages stays bounded.
 */
export const EXTENDED_PER_GROUP = 48;
const STRIP_RATIO = 184 / 220;
/** `.bx-strip`'s flex gap and left padding (catalog-grouped.css) — the window's geometry reads them. */
export const STRIP_GAP = 14;
const STRIP_PAD_LEFT = 2;
/** A strip at or under this many cards mounts whole; past it the window pays for itself. */
export const STRIP_VIRT_THRESHOLD = 16;
export const STRIP_VIRT_SLACK = 4;
/** Half a scrollport of mounted headroom either side of the visible cards. Module-level: the hook keys on its identity. */
const stripKeep = (clientWidth: number) => Math.round(clientWidth * 0.5);
/** Band 0's first strip is above the fold: this many of its covers load eagerly. */
const STRIP_EAGER = 8;

function ChevronIcon({ dir }: { dir: "l" | "r" }) {
  return (
    <svg width="15" height="15" viewBox="0 0 16 16" fill="none" stroke="currentColor" strokeWidth="1.8" strokeLinecap="round" strokeLinejoin="round" aria-hidden="true">
      {dir === "l" ? <polyline points="10,3 5,8 10,13" /> : <polyline points="6,3 11,8 6,13" />}
    </svg>
  );
}

function GroupIcon() {
  return (
    <svg className="bx-group-icon" width="12" height="12" viewBox="0 0 16 16" fill="none" stroke="currentColor" strokeWidth="1.7" strokeLinecap="round" aria-hidden="true">
      <circle cx="7" cy="7" r="4.5" /><line x1="10.5" y1="10.5" x2="14" y2="14" />
    </svg>
  );
}

/**
 * A horizontally-scrolling strip of cards with edge chevrons (mounted only where it can scroll that
 * way), remembered scroll position (the store outlives band recycling), a near-end trigger, and the
 * engine's horizontal window over its run: `items` are laid out at `cardWidth` and only the slice
 * near the scrollport is mounted, lead/tail spacers holding the rest of the width. `trailing` (the
 * "more" button) sits after the tail spacer, at the run's true end.
 */
export function Strip({ items, coverH, metadata, hoverClass, onOpen, eagerCount = 0, groupKey, scrollStore, onNearEnd, trailing }: {
  items: CardItem[]; coverH: number; metadata: ViewProps["metadata"]; hoverClass: string; onOpen: (i: CardItem) => void; eagerCount?: number;
  groupKey: string; scrollStore: Map<string, number>; onNearEnd?: () => void; trailing?: ReactNode;
}) {
  const ref = useRef<HTMLDivElement>(null);
  const [edges, setEdges] = useState({ left: false, right: false });
  const update = useCallback(() => {
    const el = ref.current;
    if (!el) return;
    const left = el.scrollLeft > 1;
    const right = el.scrollLeft + el.clientWidth < el.scrollWidth - 1;
    setEdges((prev) => (prev.left === left && prev.right === right ? prev : { left, right }));
  }, []);
  // The restore runs BEFORE the window's layout effect (declaration order), so the first measured
  // window is taken at the remembered scrollLeft, not at 0.
  useLayoutEffect(() => {
    const el = ref.current;
    if (!el) return;
    const saved = scrollStore.get(groupKey);
    if (saved) el.scrollLeft = saved;
    update();
  }, [groupKey, scrollStore, update]);
  const n = items.length;
  const widths = useMemo(() => items.map((item) => cardWidth(item, coverH, { metadata })), [items, coverH, metadata]);
  const prefix = useMemo(() => prefixSums(widths), [widths]);
  const win = useHorizontalWindow(ref, { prefix, n, gap: STRIP_GAP, padLeft: STRIP_PAD_LEFT, keepPx: stripKeep, slack: STRIP_VIRT_SLACK, threshold: STRIP_VIRT_THRESHOLD });
  const start = win ? Math.min(win.start, n) : 0;
  const end = win ? Math.max(start, Math.min(win.end, n)) : n;
  const spacers = spacerWidths(prefix, n, STRIP_GAP, start, end);
  const onScroll = () => {
    const el = ref.current;
    if (!el) return;
    scrollStore.set(groupKey, el.scrollLeft);
    if (onNearEnd && el.scrollLeft + el.clientWidth > el.scrollWidth - 600) onNearEnd();
    update();
  };
  useEffect(() => {
    update();
    const el = ref.current;
    if (!el || typeof ResizeObserver === "undefined") return undefined;
    const ro = new ResizeObserver(update);
    ro.observe(el);
    return () => ro.disconnect();
  }, [update, n, coverH]);
  const scroll = (dir: -1 | 1) => { const el = ref.current; if (el) el.scrollBy({ left: dir * el.clientWidth * 0.8, behavior: "smooth" }); };
  return (
    <div className="bx-strip-wrap" style={{ "--cover-h": `${coverH}px` } as CSSProperties}>
      {edges.left && <button type="button" className="bx-strip-nav bx-strip-nav-l" aria-label="Scroll left" onClick={() => scroll(-1)}><ChevronIcon dir="l" /></button>}
      <div className="bx-strip" ref={ref} onScroll={onScroll} data-mounted={win ? `${start}-${end}` : undefined}>
        {start > 0 && <div className="bx-strip-spacer" aria-hidden="true" style={{ flex: `0 0 ${spacers.lead}px` }} />}
        {items.slice(start, end).map((item, i) => (
          <Card key={item.key} item={item} cellH={coverH} metadata={metadata} hoverClass={hoverClass} eager={start + i < eagerCount} onOpen={onOpen} />
        ))}
        {end < n && <div className="bx-strip-spacer" aria-hidden="true" style={{ flex: `0 0 ${spacers.tail}px` }} />}
        {trailing}
      </div>
      {edges.right && <button type="button" className="bx-strip-nav bx-strip-nav-r" aria-label="Scroll right" onClick={() => scroll(1)}><ChevronIcon dir="r" /></button>}
    </div>
  );
}

type Extra = { items: CardItem[]; loading: boolean; hasMore: boolean };

/**
 * One group: header + strip. Memoized per GROUP so a "more" page landing for one group re-renders
 * that group alone — `extras` is one object for the whole view, and a per-band memo alone re-drew
 * every strip in every mounted band on each page.
 */
function GroupSection({ g, extra, stripH, metadata, hoverClass, noun, groupNoun, eagerCount, onOpen, onOpenGroup, loadMore, perGroupCap, onLoadMore, scrollStore }: {
  g: CardGroup; extra: Extra | undefined; stripH: number; metadata: ViewProps["metadata"]; hoverClass: string; noun: string; groupNoun: string; eagerCount: number;
  onOpen: (i: CardItem) => void; onOpenGroup: ((g: CardGroup) => void) | null;
  loadMore: ((groupKey: string, skip: number) => Promise<CardItem[]>) | null; perGroupCap: number;
  onLoadMore: (groupKey: string, count: number) => void; scrollStore: Map<string, number>;
}) {
  const all = useMemo(() => (extra ? [...g.items, ...extra.items] : g.items), [g.items, extra]);
  const showMore = !!loadMore && (extra ? extra.hasMore : g.items.length >= perGroupCap) && all.length < g.totalItems;
  const loadingMore = extra?.loading ?? false;
  return (
    <section className="bx-group" data-group-key={g.key}>
      <header className="bx-group-head">
        <h3 className={`bx-group-name${onOpenGroup ? " bx-clickable" : ""}`} onClick={onOpenGroup ? () => onOpenGroup(g) : undefined}>{g.label}</h3>
        {onOpenGroup && (
          <button type="button" className="bx-group-browse" title={`Open ${groupNoun.replace(/s$/, "")}`} onClick={() => onOpenGroup(g)}><GroupIcon /></button>
        )}
        <span className="bx-group-meta">{groupRunLabel(g, noun)}</span>
        <span className="bx-group-rule" />
      </header>
      <Strip items={all} coverH={stripH} metadata={metadata} hoverClass={hoverClass} onOpen={onOpen} eagerCount={eagerCount}
        groupKey={g.key} scrollStore={scrollStore}
        onNearEnd={showMore && !loadingMore ? () => onLoadMore(g.key, all.length) : undefined}
        trailing={showMore ? (
          <button type="button" className="bx-strip-more" disabled={loadingMore} onClick={() => onLoadMore(g.key, all.length)}>
            {loadingMore ? "…" : "more →"}
          </button>
        ) : null}
      />
    </section>
  );
}
const GroupSectionMemo = memo(GroupSection);

/** One band of groups: a section per group, with per-group "more" that survives band recycling. */
function GroupBand({ groups, band, cellH, metadata, hoverClass, noun, groupNoun, onOpen, onOpenGroup, loadMore, perGroupCap, extras, onLoadMore, scrollStore }: {
  groups: CardGroup[]; band: number; cellH: number; metadata: ViewProps["metadata"]; hoverClass: string; noun: string; groupNoun: string;
  onOpen: (i: CardItem) => void; onOpenGroup: ((g: CardGroup) => void) | null;
  loadMore: ((groupKey: string, skip: number) => Promise<CardItem[]>) | null; perGroupCap: number;
  extras: Record<string, Extra>; onLoadMore: (groupKey: string, count: number) => void; scrollStore: Map<string, number>;
}) {
  const stripH = Math.round(cellH * STRIP_RATIO);
  return (
    <div className="bx-groups">
      {groups.map((g, i) => (
        <GroupSectionMemo key={g.key} g={g} extra={extras[g.key]} stripH={stripH} metadata={metadata} hoverClass={hoverClass} noun={noun} groupNoun={groupNoun}
          eagerCount={band === 0 && i === 0 ? STRIP_EAGER : 0}
          onOpen={onOpen} onOpenGroup={onOpenGroup} loadMore={loadMore} perGroupCap={perGroupCap} onLoadMore={onLoadMore} scrollStore={scrollStore} />
      ))}
    </div>
  );
}
const GroupBandMemo = memo(GroupBand);

export default function ExtendedView(props: ViewProps) {
  const { source, state, coverScale } = props;
  if (state.group === NO_GROUP || !source.fetchGroupBand) {
    return <FlatCardStream {...props} variant="grid" cellH={Math.round(GRID_BASE_CELL * coverScale)} perBand={source.pageSize ?? 48} />;
  }
  return <ExtendedGrouped {...props} />;
}

function ExtendedGrouped({ source, state, coverScale, metadata, hoverClass }: ViewProps) {
  const cellH = Math.round(GRID_BASE_CELL * coverScale);
  const stream = useGroupedStream(source, state, EXTENDED_PER_GROUP);
  const engineRef = useRef<InfiniteBandsHandle>(null);
  const [spyUnit, setSpyUnit] = useState(0);
  const onSpy = useCallback((unit: number) => setSpyUnit(unit), []);
  const scrollStoreRef = useRef(new Map<string, number>());
  const [extras, setExtras] = useState<Record<string, Extra>>({});
  const inFlightRef = useRef(new Set<string>());
  useEffect(() => { setExtras({}); inFlightRef.current.clear(); scrollStoreRef.current.clear(); }, [stream.queryKey]);
  // Data changed under the same query: the "more" pages re-read (the engine re-reads its bands via dataVersion).
  useEffect(() => { setExtras({}); inFlightRef.current.clear(); }, [stream.dataVersion]);

  const onLoadMore = useCallback(async (groupKey: string, count: number) => {
    const lm = stream.loadMore;
    if (!lm || inFlightRef.current.has(groupKey)) return;
    inFlightRef.current.add(groupKey);
    setExtras((prev) => ({ ...prev, [groupKey]: { items: prev[groupKey]?.items ?? [], loading: true, hasMore: true } }));
    try {
      const more = await lm(groupKey, count);
      setExtras((prev) => ({ ...prev, [groupKey]: { items: [...(prev[groupKey]?.items ?? []), ...more], loading: false, hasMore: more.length >= EXTENDED_PER_GROUP } }));
    } catch {
      setExtras((prev) => ({ ...prev, [groupKey]: { items: prev[groupKey]?.items ?? [], loading: false, hasMore: true } }));
    } finally {
      inFlightRef.current.delete(groupKey);
    }
  }, [stream.loadMore]);

  // Wheel over a strip = horizontal browse, but only while a card in that strip is hovered (the
  // visible "engaged" state) or a browse is already running; a vertical page scroll always falls
  // through and clears engagement, so the page can never be trapped. Non-passive capture is what
  // preventDefault needs; the handler is O(1).
  const rootRef = useRef<HTMLDivElement>(null);
  useEffect(() => {
    const root = rootRef.current;
    if (!root) return undefined;
    let hoverCard: HTMLElement | null = null;
    const engaged = new Set<Element>();
    const timers = new Map<Element, ReturnType<typeof setTimeout>>();
    const onOver = (e: Event) => { hoverCard = (e.target as HTMLElement)?.closest?.(".bx-strip .bx-card") as HTMLElement | null; };
    const onOut = (e: Event) => { const c = (e.target as HTMLElement)?.closest?.(".bx-strip .bx-card"); if (c && c === hoverCard) hoverCard = null; };
    const onWheel = (e: WheelEvent) => {
      const strip = (e.target as HTMLElement)?.closest?.(".bx-strip") as HTMLElement | null;
      if (!strip) return;
      if (Math.abs(e.deltaX) > Math.abs(e.deltaY)) return;
      const on = engaged.has(strip) || (hoverCard != null && hoverCard.closest(".bx-strip") === strip);
      if (!on || strip.scrollWidth <= strip.clientWidth + 1) return;
      const step = e.deltaMode === 1 ? e.deltaY * 16 : e.deltaY;
      const atStart = strip.scrollLeft <= 0;
      const atEnd = strip.scrollLeft >= strip.scrollWidth - strip.clientWidth - 1;
      if ((step < 0 && atStart) || (step > 0 && atEnd)) { engaged.delete(strip); return; }
      e.preventDefault();
      engaged.add(strip);
      strip.scrollLeft += step;
      const t = timers.get(strip);
      if (t) clearTimeout(t);
      timers.set(strip, setTimeout(() => { if (!hoverCard || hoverCard.closest(".bx-strip") !== strip) engaged.delete(strip); }, 150));
    };
    const onPageScroll = () => { engaged.clear(); };
    root.addEventListener("pointerover", onOver);
    root.addEventListener("pointerout", onOut);
    root.addEventListener("wheel", onWheel, { capture: true, passive: false });
    window.addEventListener("scroll", onPageScroll, { passive: true, capture: true });
    return () => {
      root.removeEventListener("pointerover", onOver);
      root.removeEventListener("pointerout", onOut);
      root.removeEventListener("wheel", onWheel, { capture: true });
      window.removeEventListener("scroll", onPageScroll, { capture: true });
      timers.forEach((t) => clearTimeout(t));
    };
  }, []);

  const noun = source.itemNoun ?? "item";
  const groupNoun = source.groupNoun ?? "groups";
  const renderBand = useCallback((groups: CardGroup[], band: number) => (
    <GroupBandMemo
      groups={groups} band={band} cellH={cellH} metadata={metadata} hoverClass={hoverClass} noun={noun} groupNoun={groupNoun}
      onOpen={stream.open} onOpenGroup={stream.openGroup} loadMore={stream.loadMore} perGroupCap={EXTENDED_PER_GROUP}
      extras={extras} onLoadMore={onLoadMore} scrollStore={scrollStoreRef.current}
    />
  ), [cellH, metadata, hoverClass, noun, groupNoun, stream.open, stream.openGroup, stream.loadMore, extras, onLoadMore]);

  if (stream.loading && !stream.band0) return <StreamLoading />;
  if (stream.error && !stream.band0) return <StreamFailed onRetry={stream.retry} />;
  if (!stream.band0 || stream.band0.length === 0) return <StreamEmpty source={source} />;

  const letters = stream.letters ? groupLetterBuckets(stream.letters, stream.totalGroups) : null;
  return (
    <div ref={rootRef} className="bx-extended">
      <InfiniteBands<CardGroup>
        ref={engineRef}
        key="extended-groups"
        total={stream.totalGroups}
        perBand={GROUPS_PAGE_SIZE}
        band0={stream.band0}
        queryKey={stream.queryKey}
        dataVersion={stream.dataVersion}
        fetchBand={stream.fetchBand}
        estBandHeight={GROUPS_PAGE_SIZE * (Math.round(cellH * STRIP_RATIO) + 110)}
        spy={letters ? "unit" : "band"}
        onSpy={onSpy}
        renderBand={renderBand}
      />
      {stream.totalGroups > GROUPS_PAGE_SIZE && (
        <CatalogPager
          mode={letters ? "letters" : "pages"}
          letters={letters}
          total={stream.totalGroups}
          pageSize={GROUPS_PAGE_SIZE}
          currentIndex={spyUnit}
          disabled={false}
          onJump={(offset: number) => engineRef.current?.jumpToUnit(offset)}
          itemNoun={groupNoun.replace(/s$/, "")}
        />
      )}
    </div>
  );
}
