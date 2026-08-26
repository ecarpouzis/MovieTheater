/**
 * A `CatalogSource` over an array the section already holds in memory (Boardgames' cached OData
 * catalog, Music's cached albums). Paging, grouping, letters and the directory are all slices and
 * walks over that array — instant, abort-free — so the views behave exactly as they do over a
 * server source without the section growing a grouped endpoint it has no need for.
 *
 * The section owns filtering and (usually) sorting: it hands over the list it is already showing
 * plus a `currentSort` naming the order that list is in. A sort with a `compare` is applied here
 * instead (a section whose grid has no sort control of its own).
 */
import { bucketsFor } from "../../Components/CatalogPager";
import type { CardGroup, CardItem, CardPage, CatalogSource, DirectoryNode, GroupPage, GroupSpec, LetterBucket, ListColumn, SortSpec, TweakExtra, ViewMode } from "../types";

export interface GroupKey {
  key: string;
  label: string;
}

export interface ClientGrouper extends GroupSpec {
  /** Every group an item belongs to (an album has one artist; a boardgame several publishers). Nothing = the item is left out of this grouping. */
  keysOf(item: CardItem): GroupKey[] | GroupKey | null | undefined;
  /** Head order: by label (default), by key descending (decades, newest first), or by size. */
  order?: "label" | "keyDesc" | "count";
  /** Per-group detail for the Newspaper (synopsis / byline / kicker / tags), computed once from the group's cards. */
  detail?(key: GroupKey, items: CardItem[]): CardGroup["detail"];
}

export interface ClientSort extends SortSpec {
  /** Comparator over cards; omitted = the incoming order (the section sorted already). */
  compare?: (a: CardItem, b: CardItem) => number;
  /** What the letter strip buckets on under this sort (default: the card's `sortKey`, else its title). */
  letterKey?: (item: CardItem) => string;
}

export interface ClientSourceOptions {
  queryKey: string;
  title?: string;
  itemNoun?: string;
  groupNoun?: string;
  /** Labels for the Items pill, e.g. { items: "Games", groups: "One per group" }. */
  itemsLabels?: CatalogSource["itemsLabels"];
  items: CardItem[];
  groups?: ClientGrouper[];
  sorts: ClientSort[];
  currentSort?: string;
  defaultGroup?: string;
  /** Which grouping the Directory view walks (its roots); none = no directory. */
  directoryGroup?: string;
  listColumns?: ListColumn[];
  defaultAspect?: number;
  pageSize?: number;
  tweakExtras?: TweakExtra[];
  onOpen(item: CardItem): void;
  onOpenGroup?(group: CardGroup, groupBy: string): void;
}

export const ALL_VIEWS: ViewMode[] = ["grid", "wall", "list", "extended", "shelf", "newspaper", "directory"];
export const FLAT_ONLY_VIEWS: ViewMode[] = ["grid", "wall", "list"];

interface Grouped {
  heads: CardGroup[];
  byKey: Map<string, CardGroup>;
}

const collator = new Intl.Collator(undefined, { sensitivity: "base", numeric: true });

function withGroupKey(items: CardItem[], key: string): CardItem[] {
  return items.map((i) => (i.groupKey === key ? i : { ...i, groupKey: key }));
}

export function createClientSource(o: ClientSourceOptions): CatalogSource {
  const sorts = o.sorts.length ? o.sorts : [{ value: "default", label: "Default" }];
  const sortCache = new Map<string, CardItem[]>();
  const sorted = (sort: string): CardItem[] => {
    const spec = sorts.find((s) => s.value === sort) ?? sorts[0];
    const cached = sortCache.get(spec.value);
    if (cached) return cached;
    const arr = spec.compare ? [...o.items].sort(spec.compare) : o.items;
    sortCache.set(spec.value, arr);
    return arr;
  };

  const groupers = o.groups ?? [];
  const groupCache = new Map<string, Grouped>();
  const grouped = (groupBy: string, sort: string): Grouped => {
    const cacheKey = `${groupBy}|${sort}`;
    const hit = groupCache.get(cacheKey);
    if (hit) return hit;
    const grouper = groupers.find((g) => g.value === groupBy);
    const byKey = new Map<string, CardGroup>();
    if (grouper) {
      for (const item of sorted(sort)) {
        const raw = grouper.keysOf(item);
        const keys = raw == null ? [] : Array.isArray(raw) ? raw : [raw];
        for (const k of keys) {
          if (!k || !k.key) continue;
          let g = byKey.get(k.key);
          if (!g) {
            g = { key: k.key, label: k.label || k.key, totalItems: 0, renderTotal: 0, items: [] };
            byKey.set(k.key, g);
          }
          g.items.push(item);
        }
      }
    }
    const heads = [...byKey.values()];
    for (const g of heads) {
      g.totalItems = g.items.length;
      g.renderTotal = g.items.length;
      if (grouper?.detail) g.detail = grouper.detail({ key: g.key, label: g.label }, g.items);
    }
    const order = grouper?.order ?? "label";
    if (order === "keyDesc") heads.sort((a, b) => collator.compare(b.key, a.key));
    else if (order === "count") heads.sort((a, b) => b.totalItems - a.totalItems || collator.compare(a.label, b.label));
    else heads.sort((a, b) => collator.compare(a.label, b.label));
    const result = { heads, byKey };
    groupCache.set(cacheKey, result);
    return result;
  };

  const currentSort = o.currentSort ?? sorts[0].value;

  const fetchGroupMore = async (groupKey: string, skip: number, top: number, groupBy: string, sort: string): Promise<CardPage> => {
    const g = grouped(groupBy, sort).byKey.get(groupKey);
    if (!g) return { items: [], total: 0 };
    return { items: withGroupKey(g.items.slice(skip, skip + top), g.key), total: g.items.length };
  };

  const directory = o.directoryGroup && groupers.some((g) => g.value === o.directoryGroup)
    ? {
        roots: async (): Promise<DirectoryNode[]> =>
          grouped(o.directoryGroup!, currentSort).heads.map((g) => {
            const rep = g.items[0];
            return { id: g.key, label: g.label, count: g.totalItems, imageUrl: rep?.imageThumbUrl ?? rep?.imageUrl, hue: rep?.hue };
          }),
        children: async () => [],
        items: (id: string, skip: number, top: number) => fetchGroupMore(id, skip, top, o.directoryGroup!, currentSort),
      }
    : undefined;

  const groupable = groupers.length > 0;
  return {
    queryKey: o.queryKey,
    title: o.title,
    itemNoun: o.itemNoun,
    groupNoun: o.groupNoun,
    supports: groupable ? ALL_VIEWS : FLAT_ONLY_VIEWS,
    groups: groupers.map(({ value, label }) => ({ value, label })),
    sorts: sorts.map(({ value, label, alpha }) => ({ value, label, alpha })),
    currentSort: o.currentSort,
    itemsModes: groupable ? ["items", "groups"] : undefined,
    itemsLabels: o.itemsLabels,
    listColumns: o.listColumns,
    directory,
    defaultGroup: o.defaultGroup,
    defaultAspect: o.defaultAspect,
    pageSize: o.pageSize,
    tweakExtras: o.tweakExtras,
    fetchFlatBand: async (skip, top, sort) => {
      const arr = sorted(sort);
      return { items: arr.slice(skip, skip + top), total: arr.length };
    },
    fetchGroupBand: groupable
      ? async (groupsSkip, groupsTop, perGroupTop, groupBy, sort): Promise<GroupPage> => {
          const g = grouped(groupBy, sort);
          return {
            groups: g.heads.slice(groupsSkip, groupsSkip + groupsTop).map((h) => ({ ...h, items: withGroupKey(h.items.slice(0, perGroupTop), h.key) })),
            totalGroups: g.heads.length,
          };
        }
      : undefined,
    fetchGroupMore: groupable ? fetchGroupMore : undefined,
    letters: async (sort): Promise<LetterBucket[]> => {
      const spec = sorts.find((s) => s.value === sort) ?? sorts[0];
      const keyOf = spec.letterKey ?? ((i: CardItem) => i.sortKey ?? i.title);
      return bucketsFor(sorted(spec.value), keyOf) as LetterBucket[];
    },
    groupLetters: groupable
      ? async (groupBy, sort) => (bucketsFor(grouped(groupBy, sort).heads, (g: CardGroup) => g.label) as LetterBucket[]).map((b) => ({ letter: b.letter, firstIndex: b.offset }))
      : undefined,
    onOpen: o.onOpen,
    onOpenGroup: o.onOpenGroup,
  };
}
