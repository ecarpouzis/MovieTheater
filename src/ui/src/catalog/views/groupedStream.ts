import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import type { CatalogViewState } from "../state/useCatalogView";
import type { CardGroup, CardItem, CatalogSource } from "../types";
import { groupByFor } from "./flatStream";

/**
 * What the grouped views (Extended / Shelves / Newspaper) share: the two-phase protocol as the
 * client sees it. Band 0 of groups is fetched up front (its response carries `totalGroups`), the
 * engine drives the rest by band, each group's further items stream through `fetchGroupMore`,
 * and — under an alphabetical sort — the group letters give the rail its jump targets
 * (letter → first group index; band = ⌊index / GROUPS_PAGE_SIZE⌋, within = index mod it).
 */
export const GROUPS_PAGE_SIZE = 20;

export interface GroupedStream {
  queryKey: string;
  groupBy: string;
  /** The source's `dataVersion` at the time band 0 was read (see `CatalogSource.dataVersion`); a change re-reads the bands in place. */
  dataVersion: number;
  band0: CardGroup[] | null;
  totalGroups: number;
  loading: boolean;
  error: boolean;
  retry: () => void;
  fetchBand: (band: number, signal: AbortSignal) => Promise<CardGroup[]>;
  /** The next page of one group's items, or null when the source cannot page groups. */
  loadMore: ((groupKey: string, skip: number) => Promise<CardItem[]>) | null;
  /** Letter → first group index, or null when the sort is not alphabetical / the source cannot bucket. */
  letters: { letter: string; firstIndex: number }[] | null;
  open: (item: CardItem) => void;
  openGroup: ((group: CardGroup) => void) | null;
}

export function useGroupedStream(source: CatalogSource, state: CatalogViewState, perGroupTop: number): GroupedStream {
  const groupBy = groupByFor(source, state);
  const sort = state.sort;
  const queryKey = `${source.queryKey}|groups:${groupBy}|${sort}|${perGroupTop}`;
  // Data edited in place under the SAME query: band 0 and the total are re-read, the views drop their
  // cached bands, and nothing resets the window or the scroll position (the flat stream's rule).
  const dataVersion = source.dataVersion ?? 0;
  const sourceRef = useRef(source);
  sourceRef.current = source;

  const fetchBand = useCallback((band: number, signal: AbortSignal): Promise<CardGroup[]> => {
    const s = sourceRef.current;
    if (!s.fetchGroupBand) return Promise.resolve([]);
    return s.fetchGroupBand(band * GROUPS_PAGE_SIZE, GROUPS_PAGE_SIZE, perGroupTop, groupBy, sort, signal).then((gp) => gp.groups);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [queryKey, dataVersion]);

  const [band0, setBand0] = useState<CardGroup[] | null>(null);
  const [totalGroups, setTotalGroups] = useState(0);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState(false);
  const [nonce, setNonce] = useState(0);

  // A dataVersion-only re-read must not blank the view: same list, edited in place. Only a genuinely
  // different query goes back to the loading state.
  const readKeyRef = useRef(queryKey);
  useEffect(() => {
    const controller = new AbortController();
    const sameList = readKeyRef.current === queryKey;
    readKeyRef.current = queryKey;
    if (!sameList) {
      setLoading(true);
      setBand0(null);
    }
    setError(false);
    const s = sourceRef.current;
    if (!s.fetchGroupBand) { setBand0([]); setTotalGroups(0); setLoading(false); return undefined; }
    s.fetchGroupBand(0, GROUPS_PAGE_SIZE, perGroupTop, groupBy, sort, controller.signal)
      .then((gp) => {
        if (controller.signal.aborted) return;
        setBand0(gp.groups);
        setTotalGroups(gp.totalGroups);
        setLoading(false);
      })
      .catch((err: unknown) => {
        if (controller.signal.aborted || (err as { name?: string })?.name === "AbortError") return;
        setError(true);
        setLoading(false);
      });
    return () => controller.abort();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [queryKey, nonce, dataVersion]);

  const alpha = !!source.sorts.find((x) => x.value === sort)?.alpha;
  const [letters, setLetters] = useState<{ letter: string; firstIndex: number }[] | null>(null);
  useEffect(() => {
    setLetters(null);
    const s = sourceRef.current;
    if (!alpha || !s.groupLetters) return undefined;
    const controller = new AbortController();
    s.groupLetters(groupBy, sort, controller.signal)
      .then((ls) => { if (!controller.signal.aborted && ls.length) setLetters(ls); })
      .catch(() => {});
    return () => controller.abort();
  }, [source.queryKey, groupBy, sort, alpha]);

  const retry = useCallback(() => setNonce((n) => n + 1), []);
  const loadMore = useMemo(() => {
    if (!source.fetchGroupMore) return null;
    return (groupKey: string, skip: number) => sourceRef.current.fetchGroupMore!(groupKey, skip, perGroupTop, groupBy, sort).then((p) => p.items);
  }, [source.fetchGroupMore, perGroupTop, groupBy, sort]);
  const open = useCallback((item: CardItem) => sourceRef.current.onOpen(item), []);
  const openGroup = useMemo(() => (source.onOpenGroup ? (g: CardGroup) => sourceRef.current.onOpenGroup!(g, groupBy) : null), [source.onOpenGroup, groupBy]);

  return useMemo(() => ({ queryKey, groupBy, dataVersion, band0, totalGroups, loading, error, retry, fetchBand, loadMore, letters, open, openGroup }),
    [queryKey, groupBy, dataVersion, band0, totalGroups, loading, error, retry, fetchBand, loadMore, letters, open, openGroup]);
}

/** Letter buckets for the site's CatalogPager from the grouped letters (offset = group index, count = to the next letter). */
export function groupLetterBuckets(letters: { letter: string; firstIndex: number }[], totalGroups: number) {
  return letters.map((l, i) => ({
    letter: l.letter,
    offset: l.firstIndex,
    count: Math.max(0, (i + 1 < letters.length ? letters[i + 1].firstIndex : totalGroups) - l.firstIndex),
  }));
}

/** The label of a group's run: the detail's runLabel, else "N items" with the section's noun. */
export function groupRunLabel(group: CardGroup, noun = "item"): string {
  const rl = group.detail?.runLabel;
  if (typeof rl === "string" && rl) return rl;
  return `${group.totalItems.toLocaleString()} ${group.totalItems === 1 ? noun : `${noun}s`}`;
}
