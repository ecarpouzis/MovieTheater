/**
 * Books → `CatalogSource`. The facet state (from the URL) is the scope: flat bands page
 * `/odata/catalog` (the count on band 0, from the `X-Total-Count` header — the package's first header
 * read), grouped views ride `/browse/groups` (heads → bands, `userMeta` → `userMark`, the AI card →
 * `detail`), "more of one group" `/browse/groups/{by}/{key}/items`, the letter rail
 * `/browse/group-letters`, and the Directory walks the real folder tree (`/library/comic/folders`,
 * children by `parentId`, a folder's items through `/odata/catalog?directory=`, shadow duplicates
 * badged rather than hidden).
 *
 * Opening: a card opens the item modal; a series header opens the series modal (a single-issue
 * series collapses to its item — the one rule, in `openEntity`); every other group header scopes in
 * place, the standalone's `handlePickGroup`: collection/publisher/franchise become a filter and the
 * grouping drops to series, a decade becomes a year range.
 */
import * as api from "../../Pages/Books/booksApi";
import type { BrowseGroupItem, ItemSummary } from "../../Pages/Books/booksApi";
import { clampAspect, dateLabel, runLabel } from "../../Pages/Books/booksFormat";
import * as media from "../../Pages/Books/booksMedia";
import { groupMarkOverride } from "../../Pages/Books/booksQuery";
import { hueSvg } from "../cards/CardImage";
import type { FacetSpec, FacetState } from "../rail/facetSpec";
import { facetStateKey } from "../rail/facetUrl";
import type { CardGroup, CardItem, CardPage, CatalogSource, DirectoryNode, GroupPage, GroupSpec, ListColumn, TweakExtra, ViewMode } from "../types";
import { cardKey } from "../types";
import { BOOKS_SORTS, buildBooksQuery, flatOrderby, groupedOrderby } from "./booksOData";
import { hueOf } from "./hue";

export const BOOKS_GROUPS: GroupSpec[] = [
  { value: "collection", label: "Collection" },
  { value: "series", label: "Series" },
  { value: "publisher", label: "Publisher" },
  { value: "decade", label: "Decade" },
  { value: "franchise", label: "Franchise" },
];

export const BOOKS_PAGE_SIZE = 48;
const ALL_VIEWS: ViewMode[] = ["grid", "wall", "list", "extended", "shelf", "newspaper", "directory"];
const DIRECTORY_PAGE = 200;

export function toBookCard(row: ItemSummary): CardItem {
  const title = row.title ?? row.fileName;
  const hue = hueOf(row.series ?? row.publisher ?? title);
  const label = dateLabel(row.year, row.month, row.datePrecision);
  const badges: CardItem["badges"] = [];
  if (row.rating != null) badges.push({ label: `★ ${(row.rating / 10).toFixed(1)}`, tone: "rating", title: "Rating" });
  if (row.isExcluded) badges.push({ label: "duplicate", tone: "neutral", title: "Shadow duplicate — hidden from the catalog" });
  const thumb = media.thumbUrl(row.id);
  return {
    kind: row.kind === "book" ? "book" : "comic",
    id: row.id,
    key: cardKey(row.kind === "book" ? "book" : "comic", row.id),
    title,
    subtitle: row.isSingleIssueSeries ? undefined : row.series ?? undefined,
    label: label || undefined,
    year: row.year ?? undefined,
    aspect: clampAspect(row.coverAspect),
    imageUrl: thumb ?? hueSvg(hue, 100, 150),
    imageThumbUrl: thumb ?? undefined,
    hue,
    rating: row.rating ?? undefined,
    sortKey: row.series ?? title,
    badges,
    raw: row,
  };
}

const rawOf = (i: CardItem) => (i.raw ?? {}) as ItemSummary;

export const BOOKS_LIST_COLUMNS: ListColumn[] = [
  { key: "title", label: "Title", width: "2fr", value: (i) => i.title },
  { key: "series", label: "Series", width: "1.4fr", value: (i) => rawOf(i).series },
  { key: "publisher", label: "Publisher", width: "1fr", value: (i) => rawOf(i).publisher },
  { key: "date", label: "Date", width: "80px", mono: true, value: (i) => i.label },
  { key: "pages", label: "Pages", width: "64px", mono: true, align: "right", value: (i) => rawOf(i).pageCount },
  { key: "rating", label: "Rating", width: "64px", mono: true, align: "right", value: (i) => (rawOf(i).rating != null ? (rawOf(i).rating! / 10).toFixed(1) : null) },
  { key: "size", label: "Size", width: "80px", mono: true, align: "right", value: (i) => `${(rawOf(i).fileSize / 1048576).toFixed(0)} MB` },
];

export function toBookGroup(g: BrowseGroupItem, groupBy: string): CardGroup {
  const items = g.items.map((row) => ({ ...toBookCard(row), groupKey: g.key }));
  const first = g.items[0];
  const detail: CardGroup["detail"] = {};
  if (groupBy === "series" && first) {
    const run = runLabel(first.seriesYearStart, first.seriesYearEnd, first.seriesIsOngoing);
    if (run) detail.runLabel = run;
    if (first.publisher) detail.kicker = first.publisher;
  } else {
    detail.kicker = groupBy;
  }
  if (g.groupDetail?.aiSynopsis) detail.synopsis = g.groupDetail.aiSynopsis;
  if (g.groupDetail?.aiTags?.length) detail.tags = g.groupDetail.aiTags.map((t) => t.slice(t.indexOf(":") + 1));
  const groupType = groupBy === "franchise" ? null : groupBy;
  const override = groupType ? groupMarkOverride(groupType, g.key) : undefined;
  const mark = override ?? g.userMeta ?? undefined;
  return {
    key: g.key,
    label: g.label,
    totalItems: g.totalItems,
    renderTotal: g.renderTotal ?? g.totalItems,
    items,
    detail,
    userMark: mark ? { isRead: mark.isRead, wantToRead: mark.wantToRead, isFavorite: mark.isFavorite, rating: mark.rating, notes: mark.notes } : undefined,
  };
}

export interface BooksSourceOptions {
  facetState: FacetState;
  spec: FacetSpec;
  /** Bumped when an admin job changes the catalog — drops every band. */
  epoch?: number;
  /** Re-mint epoch of the media token: URLs are rebuilt when it changes. */
  mediaEpoch?: number;
  tweakExtras?: TweakExtra[];
  onOpen(item: CardItem): void;
  onOpenSeries(seriesId: number, label: string, single?: { isSingleIssueSeries: boolean; itemId: number } | null): void;
  /** Scope in place: a group header applied as a filter. */
  onScope(patch: { facet?: { key: string; value: string | number }; years?: [number, number]; group?: string }): void;
}

export function createBooksSource(o: BooksSourceOptions): CatalogSource {
  const parts = buildBooksQuery(o.facetState, o.spec);
  const q = o.facetState.q.trim() || undefined;
  let knownTotal = -1;

  const common = { q, filter: parts.filter, kind: "comic" as const, readOnly: parts.readOnly, wantToReadOnly: parts.wantToReadOnly, exact: parts.exact };

  const fetchGroupMore = async (groupKey: string, skip: number, top: number, groupBy: string, sort: string, signal?: AbortSignal): Promise<CardPage> => {
    const r = await api.fetchGroupItems(groupBy, groupKey, { ...common, skip, top, orderby: groupedOrderby(sort, groupBy) }, signal);
    return { items: r.items.map((row) => ({ ...toBookCard(row), groupKey })), total: r.total };
  };

  const directory = {
    roots: async (signal?: AbortSignal): Promise<DirectoryNode[]> => (await api.fetchLibraryFolders("comic", null, signal)).map(folderNode),
    children: async (id: string, signal?: AbortSignal): Promise<DirectoryNode[]> => (await api.fetchLibraryFolders("comic", Number(id), signal)).map(folderNode),
    items: async (id: string, skip: number, top: number, signal?: AbortSignal): Promise<CardPage> => {
      const r = await api.fetchCatalog({ directory: Number(id), orderby: "fileName asc", skip, top: Math.min(top, DIRECTORY_PAGE), count: skip === 0 }, signal);
      return { items: r.items.map(toBookCard), total: r.total };
    },
  };

  return {
    queryKey: `books:${facetStateKey(o.facetState)}:${o.epoch ?? 0}:${o.mediaEpoch ?? 0}`,
    title: "Books",
    itemNoun: "book",
    groupNoun: "series",
    supports: ALL_VIEWS,
    groups: BOOKS_GROUPS,
    sorts: BOOKS_SORTS.map(({ value, label, alpha }) => ({ value, label, alpha })),
    itemsModes: ["items", "groups"],
    itemsLabels: { items: "Comics", groups: "Series" },
    listColumns: BOOKS_LIST_COLUMNS,
    directory,
    tweakExtras: o.tweakExtras,
    defaultView: "extended",
    defaultGroup: "collection",
    defaultSort: "series",
    pageSize: BOOKS_PAGE_SIZE,
    defaultAspect: 0.66,
    fetchFlatBand: async (skip, top, sort, signal) => {
      const r = await api.fetchCatalog({ ...common, orderby: flatOrderby(sort), skip, top, count: skip === 0 }, signal);
      if (r.total >= 0) knownTotal = r.total;
      return { items: r.items.map(toBookCard), total: knownTotal };
    },
    fetchGroupBand: async (groupsSkip, groupsTop, perGroupTop, groupBy, sort, signal): Promise<GroupPage> => {
      const r = await api.fetchGroups({ ...common, groupBy, groupsSkip, groupsTop, perGroupTop, orderby: groupedOrderby(sort, groupBy) }, signal);
      return { groups: r.groups.map((g) => toBookGroup(g, groupBy)), totalGroups: r.totalGroups };
    },
    fetchGroupMore,
    groupLetters: async (groupBy, _sort, signal) => (await api.fetchGroupLetters({ ...common, groupBy }, signal)).letters,
    onOpen: (item) => o.onOpen(item),
    onOpenGroup: (group, groupBy) => {
      switch (groupBy) {
        case "series": {
          const first = group.items[0];
          const single = group.totalItems === 1 && first ? { isSingleIssueSeries: !!rawOf(first).isSingleIssueSeries, itemId: first.id } : null;
          o.onOpenSeries(Number(group.key), group.label, single);
          return;
        }
        case "collection": o.onScope({ facet: { key: "collections", value: Number(group.key) }, group: "series" }); return;
        case "publisher": o.onScope({ facet: { key: "publishers", value: group.key }, group: "series" }); return;
        case "franchise": o.onScope({ facet: { key: "franchises", value: group.key }, group: "series" }); return;
        case "decade": {
          const y = Number(group.key);
          if (Number.isFinite(y)) o.onScope({ years: [y, y + 9], group: "series" });
          return;
        }
        default: return;
      }
    },
  };
}

function folderNode(f: api.FolderNode): DirectoryNode {
  return {
    id: String(f.id),
    label: f.name ?? f.path ?? String(f.id),
    count: f.descendantItemCount,
    imageUrl: f.iconUrl ?? media.folderIconUrl(f.id) ?? undefined,
    hue: hueOf(f.name ?? String(f.id)),
    hasChildren: f.directChildCount > 0,
  };
}
