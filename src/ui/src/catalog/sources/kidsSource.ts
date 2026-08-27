/**
 * The kids browse → a grouped-only `CatalogSource`: one shelf per kid-clear series (plus the trailing
 * "Books" shelf), over `/kids/browse`. The set is bounded by construction on the host (at most 160
 * shelves × 40 issues), so it is loaded ONCE per source and every band, letter and "more" is a slice
 * of that — the standalone's kids browse did the same. Two orders: the host's "best first" (series
 * rating, then size) and A–Z, whose letter rail is computed here from the shelf labels. A shelf that
 * holds more than the 40 it came with pages the rest from `/kids/series/{id}/items`.
 *
 * No user marks, no ratings badges, no excludes: a child's shelf is covers and names.
 */
import { bucketsFor } from "../pager/CatalogPager";
import * as api from "../../Pages/Books/booksApi";
import type { BrowseGroupItem, ItemSummary } from "../../Pages/Books/booksApi";
import { clampAspect } from "../../Pages/Books/booksFormat";
import * as media from "../../Pages/Books/booksMedia";
import { hueSvg } from "../cards/CardImage";
import type { CardGroup, CardItem, CardPage, CatalogSource, GroupPage, LetterBucket, ViewMode } from "../types";
import { cardKey } from "../types";
import { hueOf } from "./hue";

export const KIDS_VIEWS: ViewMode[] = ["shelf", "extended"];
export const KIDS_MAX_SERIES = 160;
export const KIDS_PER_SERIES = 40;
export const KIDS_BOOKS_KEY = "books";

const collator = new Intl.Collator(undefined, { sensitivity: "base", numeric: true });

export function toKidCard(row: ItemSummary, covers?: Record<string, string | null>): CardItem {
  const title = row.title ?? row.fileName;
  const hue = hueOf(row.series ?? title);
  const cover = covers?.[String(row.id)] ?? media.thumbUrl(row.id);
  return {
    kind: row.kind === "book" ? "book" : "comic",
    id: row.id,
    key: cardKey(row.kind === "book" ? "book" : "comic", row.id),
    title,
    subtitle: row.kind === "book" ? undefined : row.series ?? undefined,
    label: row.year ? String(row.year) : undefined,
    year: row.year ?? undefined,
    aspect: clampAspect(row.coverAspect),
    imageUrl: cover ?? hueSvg(hue, 100, 150),
    imageThumbUrl: cover ?? undefined,
    hue,
    sortKey: row.series ?? title,
    raw: row,
  };
}

export function toKidGroup(g: BrowseGroupItem, covers?: Record<string, string | null>): CardGroup {
  const items = g.items.map((row) => ({ ...toKidCard(row, covers), groupKey: g.key }));
  const first = g.items[0];
  const detail: CardGroup["detail"] = {};
  if (first?.publisher && g.key !== KIDS_BOOKS_KEY) detail.kicker = first.publisher;
  return { key: g.key, label: g.label, totalItems: g.totalItems, renderTotal: g.totalItems, items, detail };
}

export interface KidsSourceOptions {
  epoch?: number;
  mediaEpoch?: number;
  onOpen(item: CardItem): void;
  onOpenGroup?(group: CardGroup, groupBy: string): void;
}

interface Loaded { best: CardGroup[]; alpha: CardGroup[]; byKey: Map<string, CardGroup> }

export function createKidsSource(o: KidsSourceOptions): CatalogSource {
  let loaded: Promise<Loaded> | null = null;
  const load = (signal?: AbortSignal): Promise<Loaded> => {
    if (loaded) return loaded;
    loaded = api.fetchKidsBrowse({ groupsSkip: 0, groupsTop: KIDS_MAX_SERIES, perGroupTop: KIDS_PER_SERIES }, signal).then((r) => {
      const best = r.groups.map((g) => toKidGroup(g, r.covers));
      const alpha = [...best].sort((a, b) => collator.compare(a.label, b.label));
      return { best, alpha, byKey: new Map(best.map((g) => [g.key, g])) };
    }).catch((e) => { loaded = null; throw e; });
    return loaded;
  };
  const ordered = (l: Loaded, sort: string) => (sort === "alpha" ? l.alpha : l.best);

  const fetchGroupMore = async (groupKey: string, skip: number, top: number, _groupBy: string, _sort: string, signal?: AbortSignal): Promise<CardPage> => {
    const l = await load(signal);
    const g = l.byKey.get(groupKey);
    if (!g) return { items: [], total: 0 };
    const have = g.items;
    if (skip + top <= have.length || groupKey === KIDS_BOOKS_KEY || have.length >= g.totalItems) {
      return { items: have.slice(skip, skip + top), total: g.totalItems };
    }
    // Past what the shelf came with: page the rest from the host (its order is the same — by id).
    const r = await api.fetchKidsSeriesItems(Number(groupKey), skip, top, undefined, signal);
    return { items: r.items.map((row) => ({ ...toKidCard(row, r.covers), groupKey })), total: r.total };
  };

  return {
    queryKey: `books-kids:${o.epoch ?? 0}:${o.mediaEpoch ?? 0}`,
    title: "Kids",
    itemNoun: "comic",
    groupNoun: "shelves",
    supports: KIDS_VIEWS,
    groups: [{ value: "series", label: "Series" }],
    sorts: [
      { value: "best", label: "Best first" },
      { value: "alpha", label: "A–Z", alpha: true },
    ],
    defaultView: "shelf",
    defaultGroup: "series",
    defaultSort: "best",
    defaultAspect: 0.66,
    fetchFlatBand: async (skip, top, sort, signal) => {
      const l = await load(signal);
      const all = ordered(l, sort).flatMap((g) => g.items);
      return { items: all.slice(skip, skip + top), total: all.length };
    },
    fetchGroupBand: async (groupsSkip, groupsTop, perGroupTop, _groupBy, sort, signal): Promise<GroupPage> => {
      const l = await load(signal);
      const heads = ordered(l, sort);
      return {
        groups: heads.slice(groupsSkip, groupsSkip + groupsTop).map((g) => ({ ...g, items: g.items.slice(0, perGroupTop) })),
        totalGroups: heads.length,
      };
    },
    fetchGroupMore,
    groupLetters: async (_groupBy, sort, signal) => {
      const l = await load(signal);
      return (bucketsFor(ordered(l, sort), (g: CardGroup) => g.label) as LetterBucket[]).map((b) => ({ letter: b.letter, firstIndex: b.offset }));
    },
    onOpen: (item) => o.onOpen(item),
    onOpenGroup: o.onOpenGroup,
  };
}
