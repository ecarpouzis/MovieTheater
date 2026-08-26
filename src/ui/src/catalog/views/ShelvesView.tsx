import { startTransition, useCallback, useEffect, useMemo, useRef, useState } from "react";
import CatalogPager from "../../Components/CatalogPager";
import type { CardGroup, CardItem } from "../types";
import { StreamEmpty, StreamFailed, StreamLoading } from "./StreamStates";
import type { ViewProps } from "./ViewProps";
import { GROUPS_PAGE_SIZE, groupLetterBuckets, useGroupedStream } from "./groupedStream";
import ShelvesLayout, { type PendingJump, type ShelfSlot } from "./shelves/ShelvesLayout";

/**
 * Shelves — the bookcase: every group a shelf of spines, bands of shelves streamed, recycled and
 * jumped to by the site's pager (letters under an alphabetical sort). The data side of the
 * standalone's ShelvesInfinite over the grouped stream; ShelvesLayout is the interaction engine.
 */
export const SHELVES_PER_GROUP = 36;

export default function ShelvesView({ source, state, coverScale }: ViewProps) {
  const stream = useGroupedStream(source, state, SHELVES_PER_GROUP);
  const totalBands = Math.max(1, Math.ceil(stream.totalGroups / GROUPS_PAGE_SIZE));

  // Bands beyond the first, fetched on demand; the render window = which bands are mounted real.
  const [extraBands, setExtraBands] = useState<Record<number, CardGroup[]>>({});
  const loadingRef = useRef(new Set<number>());
  const [renderBands, setRenderBands] = useState<Set<number>>(() => new Set([0]));
  const heightsRef = useRef(new Map<number, number>());
  const [avgBandHeight, setAvgBandHeight] = useState(700);
  const [extras, setExtras] = useState<Record<string, CardItem[]>>({});
  const exhaustedRef = useRef(new Set<string>());
  const moreLoadingRef = useRef(new Set<string>());

  useEffect(() => {
    setExtraBands({}); setRenderBands(new Set([0])); loadingRef.current.clear();
    heightsRef.current.clear(); setAvgBandHeight(700); setExtras({}); exhaustedRef.current.clear(); moreLoadingRef.current.clear();
    setPendingJump(null); setActive({ band: 0, within: 0 });
  }, [stream.queryKey]);

  const ensureBand = useCallback((i: number) => {
    if (i <= 0 || i >= totalBands || loadingRef.current.has(i) || extraBands[i]) return;
    loadingRef.current.add(i);
    const key = stream.queryKey;
    stream.fetchBand(i, new AbortController().signal)
      .then((groups) => { if (key !== stream.queryKey) return; startTransition(() => setExtraBands((prev) => ({ ...prev, [i]: groups }))); })
      .catch(() => {})
      .finally(() => loadingRef.current.delete(i));
  }, [stream, totalBands, extraBands]);
  // A band mount is ~720 books: a transition, so a scroll in flight keeps its frames.
  const requestBand = useCallback((i: number) => {
    if (i < 0 || i >= totalBands) return;
    ensureBand(i);
    startTransition(() => setRenderBands((prev) => (prev.has(i) ? prev : new Set(prev).add(i))));
  }, [ensureBand, totalBands]);
  const releaseBand = useCallback((i: number) => {
    setRenderBands((prev) => { if (!prev.has(i)) return prev; const next = new Set(prev); next.delete(i); return next; });
  }, []);
  const onBandHeight = useCallback((band: number, h: number) => {
    if (h <= 0) return;
    heightsRef.current.set(band, h);
    let sum = 0; heightsRef.current.forEach((v) => { sum += v; });
    const avg = Math.round(sum / heightsRef.current.size);
    setAvgBandHeight((prev) => (Math.abs(prev - avg) > 24 ? avg : prev));
  }, []);

  const onLoadMore = useCallback((groupKey: string, skip: number) => {
    const lm = stream.loadMore;
    if (!lm || moreLoadingRef.current.has(groupKey) || exhaustedRef.current.has(groupKey)) return;
    moreLoadingRef.current.add(groupKey);
    lm(groupKey, skip)
      .then((items) => {
        if (items.length < SHELVES_PER_GROUP) exhaustedRef.current.add(groupKey);
        if (items.length) setExtras((prev) => ({ ...prev, [groupKey]: [...(prev[groupKey] ?? []), ...items] }));
      })
      .catch(() => {})
      .finally(() => moreLoadingRef.current.delete(groupKey));
  }, [stream.loadMore]);

  const slots: ShelfSlot[] = useMemo(() => {
    const arr: ShelfSlot[] = [];
    for (let i = 0; i < totalBands; i += 1) {
      const groups = i === 0 ? stream.band0 ?? undefined : extraBands[i];
      if (groups && groups.length && renderBands.has(i)) arr.push({ band: i, groups });
      else arr.push({ band: i, height: heightsRef.current.get(i) ?? avgBandHeight });
    }
    return arr;
  }, [totalBands, stream.band0, extraBands, renderBands, avgBandHeight]);

  const [active, setActive] = useState({ band: 0, within: 0 });
  const onActiveChange = useCallback((band: number, within: number) => setActive({ band, within }), []);
  const [pendingJump, setPendingJump] = useState<PendingJump | null>(null);
  const nonceRef = useRef(0);
  const jumpToUnit = useCallback((unit: number) => {
    const band = Math.floor(unit / GROUPS_PAGE_SIZE);
    requestBand(band);
    setPendingJump({ band, within: unit % GROUPS_PAGE_SIZE, nonce: ++nonceRef.current });
  }, [requestBand]);
  const onJumpHandled = useCallback(() => setPendingJump(null), []);

  const noun = source.itemNoun ?? "item";
  if (stream.loading && !stream.band0) return <StreamLoading />;
  if (stream.error && !stream.band0) return <StreamFailed onRetry={stream.retry} />;
  if (!stream.band0 || stream.band0.length === 0) return <StreamEmpty noun={noun} />;

  const letters = stream.letters ? groupLetterBuckets(stream.letters, stream.totalGroups) : null;
  const currentIndex = active.band * GROUPS_PAGE_SIZE + active.within;
  return (
    <div className="bx-bookcase">
      <div className="bx-shelved">
        <ShelvesLayout
          slots={slots} extras={extras} scale={coverScale} noun={noun}
          onOpen={stream.open} onOpenGroup={stream.openGroup} onLoadMore={stream.loadMore ? onLoadMore : null}
          onNeedBand={requestBand} onBandFar={releaseBand} onBandHeight={onBandHeight}
          onActiveChange={onActiveChange} pendingJump={pendingJump} onJumpHandled={onJumpHandled}
        />
      </div>
      {stream.totalGroups > GROUPS_PAGE_SIZE && (
        <CatalogPager
          mode={letters ? "letters" : "pages"}
          letters={letters}
          total={stream.totalGroups}
          pageSize={GROUPS_PAGE_SIZE}
          currentIndex={currentIndex}
          disabled={false}
          onJump={jumpToUnit}
          itemNoun={(source.groupNoun ?? "groups").replace(/s$/, "")}
        />
      )}
    </div>
  );
}
