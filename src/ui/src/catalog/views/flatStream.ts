import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { NO_GROUP, type CatalogViewState } from "../state/useCatalogView";
import type { CardGroup, CardItem, CatalogSource, LetterBucket } from "../types";

/**
 * What the flat views (Grid / Wall / List) share: band 0 fetched up front (it is the response that
 * carries `total`), then a band fetcher the engine drives for the rest. The "Items: one per group"
 * mode swaps the flat query for the grouped one with `perGroupTop = 1` and flattens each group to
 * its representative card — the standalone site's `repSeries` shape.
 */
export interface FlatStream {
  /** Identity of THIS stream — changes with the source's filters, the sort, the items mode, the band size. */
  queryKey: string;
  band0: CardItem[] | null;
  total: number;
  loading: boolean;
  error: boolean;
  retry: () => void;
  fetchBand: (band: number, signal: AbortSignal) => Promise<CardItem[]>;
  /** The card's open action: a representative opens its group, anything else opens itself. */
  open: (item: CardItem) => void;
}

/** One card standing for a whole group: the group's first item, re-labelled. */
export function representative(group: CardGroup): CardItem | null {
  const first = group.items[0];
  if (!first) return null;
  return {
    ...first,
    key: `group:${group.key}:${first.key}`,
    title: group.label,
    label: group.detail && typeof group.detail.runLabel === "string" ? group.detail.runLabel : `${group.totalItems.toLocaleString()} ${group.totalItems === 1 ? "item" : "items"}`,
    count: group.totalItems,
    groupKey: group.key,
    group,
  };
}

export function groupByFor(source: CatalogSource, state: CatalogViewState): string {
  if (state.group !== NO_GROUP) return state.group;
  return source.defaultGroup ?? source.groups[0]?.value ?? "";
}

export function useFlatStream(source: CatalogSource, state: CatalogViewState, perBand: number): FlatStream {
  const repsMode = state.items === "groups" && !!source.fetchGroupBand;
  const groupBy = groupByFor(source, state);
  const sort = state.sort;
  const queryKey = `${source.queryKey}|${sort}|${repsMode ? `reps:${groupBy}` : "items"}|${perBand}`;

  const sourceRef = useRef(source);
  sourceRef.current = source;

  const fetchBand = useCallback((band: number, signal: AbortSignal): Promise<CardItem[]> => {
    const s = sourceRef.current;
    if (repsMode && s.fetchGroupBand) {
      return s.fetchGroupBand(band * perBand, perBand, 1, groupBy, sort, signal)
        .then((gp) => gp.groups.map(representative).filter((c): c is CardItem => c != null));
    }
    return s.fetchFlatBand(band * perBand, perBand, sort, signal).then((p) => p.items);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [queryKey]);

  const [band0, setBand0] = useState<CardItem[] | null>(null);
  const [total, setTotal] = useState(0);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState(false);
  const [nonce, setNonce] = useState(0);

  useEffect(() => {
    const controller = new AbortController();
    setLoading(true);
    setError(false);
    setBand0(null);
    const s = sourceRef.current;
    const first = repsMode && s.fetchGroupBand
      ? s.fetchGroupBand(0, perBand, 1, groupBy, sort, controller.signal)
        .then((gp) => ({ items: gp.groups.map(representative).filter((c): c is CardItem => c != null), total: gp.totalGroups }))
      : s.fetchFlatBand(0, perBand, sort, controller.signal);
    first
      .then((page) => {
        if (controller.signal.aborted) return;
        setBand0(page.items);
        // A source that cannot count (total -1) gets exactly what it returned: one band, no more.
        setTotal(page.total >= 0 ? page.total : page.items.length);
        setLoading(false);
      })
      .catch((err: unknown) => {
        if (controller.signal.aborted || (err as { name?: string })?.name === "AbortError") return;
        setError(true);
        setLoading(false);
      });
    return () => controller.abort();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [queryKey, nonce]);

  const retry = useCallback(() => setNonce((n) => n + 1), []);
  const open = useCallback((item: CardItem) => {
    const s = sourceRef.current;
    if (item.group && s.onOpenGroup) s.onOpenGroup(item.group);
    else s.onOpen(item);
  }, []);

  return useMemo(() => ({ queryKey, band0, total, loading, error, retry, fetchBand, open }),
    [queryKey, band0, total, loading, error, retry, fetchBand, open]);
}

/**
 * Letter buckets for the pager, when the sort is alphabetical and the source can bucket. In the
 * representative mode the buckets come from the grouped order (`groupLetters`), with counts
 * derived from consecutive first indices.
 */
export function usePagerLetters(source: CatalogSource, state: CatalogViewState, total: number): LetterBucket[] | null {
  const alpha = !!source.sorts.find((s) => s.value === state.sort)?.alpha;
  const repsMode = state.items === "groups" && !!source.fetchGroupBand;
  const groupBy = groupByFor(source, state);
  const wanted = alpha && (repsMode ? !!source.groupLetters : !!source.letters);
  const key = `${source.queryKey}|${state.sort}|${repsMode ? `reps:${groupBy}` : "items"}`;
  const [letters, setLetters] = useState<LetterBucket[] | null>(null);
  const sourceRef = useRef(source);
  sourceRef.current = source;
  useEffect(() => {
    setLetters(null);
    if (!wanted) return undefined;
    const controller = new AbortController();
    const s = sourceRef.current;
    const p: Promise<LetterBucket[]> = repsMode && s.groupLetters
      ? s.groupLetters(groupBy, state.sort, controller.signal).then((ls) =>
        ls.map((l, i) => ({ letter: l.letter, offset: l.firstIndex, count: Math.max(0, (i + 1 < ls.length ? ls[i + 1].firstIndex : total) - l.firstIndex) })))
      : s.letters!(state.sort, controller.signal);
    p.then((ls) => { if (!controller.signal.aborted && ls.length) setLetters(ls); }).catch(() => {});
    return () => controller.abort();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [key, wanted, total]);
  return letters;
}
