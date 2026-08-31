/**
 * Novels (the prose books) → a `CatalogSource` over the host's `/novels` list. The facet
 * state (from the URL, through the Novels facet spec) is the scope: every included value of a facet
 * joins that facet's CSV param (the host ORs within a param and ANDs across), excluded TAGS ride
 * `excludeTag`, the rating floor is `minRating`, the "unknown" flag asks for the no-metadata pile.
 *
 * The host cannot exclude by author/series/publisher/decade — those excludes are dropped here rather
 * than guessed at; the Novels facet spec offers no "−" on them.
 *
 * Covers come with the page (`covers`, host-minted URLs) so no media token is needed to draw a band;
 * `maturity` rides beside it and becomes a badge. A card opens the item modal (`?item=`), whose
 * "Read now" is the EPUB reader — the standalone's Books view jumped straight into the reader because
 * it had no book modal; the section has one now, and the site's rule is that a card opens its detail.
 */
import * as api from "../../Pages/Books/booksApi";
import type { BrowseGroupItem, ItemSummary, NovelBrowseFilter, NovelsQuery } from "../../Pages/Books/booksApi";
import { clampAspect } from "../../Pages/Books/booksFormat";
import * as media from "../../Pages/Books/booksMedia";
import { hueSvg } from "../cards/CardImage";
import type { FacetSpec, FacetState } from "../rail/facetSpec";
import { facetStateKey } from "../rail/facetUrl";
import type { CardGroup, CardItem, CardPage, CatalogSource, GroupPage, GroupSpec, ListColumn, SortSpec, TweakExtra, ViewMode } from "../types";
import { cardKey } from "../types";
import { hueOf } from "./hue";

export const NOVELS_PAGE_SIZE = 60;
export const NOVELS_VIEWS: ViewMode[] = ["grid", "wall", "list"];
export const NOVELS_GROUPED_VIEWS: ViewMode[] = ["grid", "wall", "list", "extended", "shelf", "newspaper"];

/**
 * The four axes a prose shelf has — and they are exactly the four facets `/novels/facets` offers, so a
 * shelf and its chip always describe the same set. Collection and franchise are comic structures a novel
 * has no row in; `artist` is the comic art credit.
 */
export const NOVELS_GROUPS: GroupSpec[] = [
  { value: "series", label: "Series", one: "series" },
  { value: "author", label: "Author", one: "author" },
  { value: "publisher", label: "Publisher", one: "publisher" },
  { value: "decade", label: "Decade", one: "decade" },
];

/**
 * The Group pill's axes, as a reading of the DEPLOYED host — the same discipline `booksGroupsFor` uses,
 * for a worse failure. An old host does not reject `book.author=`, it IGNORES it: the grouped view would
 * page the WHOLE library under a rail full of active chips. So grouping is off unless the host says it
 * applies the novels filter (`bookFilters`), and then it is the axes that host advertised.
 */
export function novelsGroupsFor(groupAxes: string[] | null | undefined, bookFilters: boolean | undefined): GroupSpec[] {
  if (!bookFilters) return [];
  if (!groupAxes?.length) return NOVELS_GROUPS;
  const advertised = new Set(groupAxes.map((a) => a.trim().toLowerCase()));
  const offered = NOVELS_GROUPS.filter((g) => advertised.has(g.value));
  return offered.length ? offered : NOVELS_GROUPS;
}

/** The host's five orders (`NovelsController.Sorted`); the default is the shelf order by author. */
export const NOVELS_SORTS: (SortSpec & { orderby: string | null })[] = [
  { value: "author", label: "Author", orderby: null, alpha: true },
  { value: "title", label: "Title", orderby: "title", alpha: true },
  { value: "rating", label: "Top rated", orderby: "rating" },
  { value: "newest", label: "Newest", orderby: "newest" },
  { value: "oldest", label: "Oldest", orderby: "oldest" },
];

export const MATURITY_LABEL: Record<number, string> = { 0: "All ages", 1: "Teen", 2: "Mature", 3: "18+" };

/** The first name of the host's creators line ("A, B" or "A; B"). */
export function firstAuthor(creatorsCsv: string | null | undefined): string | undefined {
  if (!creatorsCsv) return undefined;
  const first = creatorsCsv.split(/[;,]/)[0]?.trim();
  return first || undefined;
}

const csv = (values: (string | number)[] | undefined): string | undefined => (values && values.length ? values.map(String).join(",") : undefined);

/**
 * The facet state as the eight filter values BOTH surfaces take — `/novels` flat and `/browse/*` grouped
 * (where they ride a `book.` prefix). One definition, so a reader switching the View pill cannot find a
 * book present in Grid and absent in Shelves; the host halves are one `NovelFilters` for the same reason.
 */
export function novelsBrowseFilter(state: FacetState): NovelBrowseFilter {
  const f: NovelBrowseFilter = {
    author: csv(state.include.authors),
    series: csv(state.include.series),
    publisher: csv(state.include.publishers),
    decade: csv(state.include.decades),
    tag: csv(state.include.tags),
    excludeTag: csv(state.exclude.tags),
  };
  if (state.ratingMin > 0) f.minRating = state.ratingMin;
  if (state.flags.unknown) f.unknown = true;
  return f;
}

/** The `/novels` query for a facet state. Pure, so the URL → request mapping is testable on its own. */
export function buildNovelsQuery(state: FacetState): NovelsQuery {
  return { ...novelsBrowseFilter(state), q: state.q.trim() || undefined };
}

/**
 * A group head → a `CardGroup`. A series head carries the reader's own mark (the host returns it) and
 * names its publisher; every other axis is its own kicker.
 */
export function toNovelGroup(g: BrowseGroupItem, groupBy: string): CardGroup {
  const items = g.items.map((row) => ({ ...toNovelCard(row), groupKey: g.key }));
  const detail: CardGroup["detail"] = { kicker: groupBy === "series" ? g.items[0]?.publisher ?? "Series" : groupBy };
  const mark = g.userMeta ?? undefined;
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

export function toNovelCard(row: ItemSummary, covers?: Record<string, string | null>, maturity?: Record<string, number | null>): CardItem {
  const title = row.title ?? row.fileName;
  const hue = hueOf(row.series ?? title);
  const cover = covers?.[String(row.id)] ?? media.thumbUrl(row.id);
  const badges: CardItem["badges"] = [];
  if (row.rating != null) badges.push({ label: `★ ${(row.rating / 10).toFixed(1)}`, tone: "rating", title: "Rating" });
  const m = maturity?.[String(row.id)];
  if (m != null && MATURITY_LABEL[m]) badges.push({ label: MATURITY_LABEL[m], tone: m >= 2 ? "system" : "neutral", title: "Maturity" });
  return {
    kind: "book",
    id: row.id,
    key: cardKey("book", row.id),
    title,
    subtitle: firstAuthor(row.creatorsCsv) ?? row.publisher ?? undefined,
    label: row.year ? String(row.year) : undefined,
    year: row.year ?? undefined,
    aspect: clampAspect(row.coverAspect),
    imageUrl: cover ?? hueSvg(hue, 100, 150),
    imageThumbUrl: cover ?? undefined,
    hue,
    rating: row.rating ?? undefined,
    sortKey: firstAuthor(row.creatorsCsv) ?? title,
    badges,
    raw: row,
  };
}

const rawOf = (i: CardItem) => (i.raw ?? {}) as ItemSummary;

export const NOVELS_LIST_COLUMNS: ListColumn[] = [
  { key: "title", label: "Title", width: "2fr", value: (i) => i.title },
  { key: "author", label: "Author", width: "1.2fr", value: (i) => firstAuthor(rawOf(i).creatorsCsv) },
  { key: "series", label: "Series", width: "1.2fr", value: (i) => rawOf(i).series },
  { key: "publisher", label: "Publisher", width: "1fr", value: (i) => rawOf(i).publisher },
  { key: "year", label: "Year", width: "64px", mono: true, value: (i) => i.label },
  { key: "rating", label: "Rating", width: "64px", mono: true, align: "right", value: (i) => (rawOf(i).rating != null ? (rawOf(i).rating! / 10).toFixed(1) : null) },
];

export interface NovelsSourceOptions {
  facetState: FacetState;
  spec: FacetSpec;
  epoch?: number;
  mediaEpoch?: number;
  tweakExtras?: TweakExtra[];
  /** `groupAxes` from `/browse/facets?kind=book` — the axes the deployed host can answer. */
  groupAxes?: string[] | null;
  /** `bookFilters` from the same payload: false or absent keeps this section flat. See `novelsGroupsFor`. */
  bookFilters?: boolean;
  onOpen(item: CardItem): void;
  /** A series header opens the series modal — the same one the Books section opens. */
  onOpenSeries?(seriesId: number, label: string): void;
  /** Scope in place: an author / publisher / decade header applied as a filter, one push. */
  onScope?(patch: { facet?: { key: string; value: string }; group?: string }): void;
}

export function createNovelsSource(o: NovelsSourceOptions): CatalogSource {
  const query = buildNovelsQuery(o.facetState);
  // The grouped endpoints take the same eight values under `book.`, plus the text, which they run
  // through the identical FTS the flat list does.
  const grouped = { kind: "book" as const, q: query.q, book: novelsBrowseFilter(o.facetState) };
  const groups = novelsGroupsFor(o.groupAxes, o.bookFilters);
  const groupable = groups.length > 0;
  let knownTotal = -1;
  return {
    queryKey: `books-novels:${facetStateKey(o.facetState)}:${o.epoch ?? 0}:${o.mediaEpoch ?? 0}`,
    title: "Novels",
    itemNoun: "book",
    supports: groupable ? NOVELS_GROUPED_VIEWS : NOVELS_VIEWS,
    groups,
    // "Books" / "One per series" — the collapsed label names the axis (`GroupSpec.one`), never a constant.
    itemsModes: groupable ? ["items", "groups"] : undefined,
    itemsLabels: { items: "Books" },
    defaultGroup: groupable ? "series" : undefined,
    sorts: NOVELS_SORTS.map(({ value, label, alpha }) => ({ value, label, alpha })),
    // The flat strip's buckets under the two alphabetical sorts (R9 S0) — without them the site's
    // CatalogPager shows page numbers here.
    letters: async (sort, signal) => {
      const orderby = (NOVELS_SORTS.find((s) => s.value === sort) ?? NOVELS_SORTS[0]).orderby ?? undefined;
      return (await api.fetchNovelLetters({ ...query, orderby }, signal)).letters;
    },
    listColumns: NOVELS_LIST_COLUMNS,
    tweakExtras: o.tweakExtras,
    defaultView: "grid",
    defaultSort: "author",
    pageSize: NOVELS_PAGE_SIZE,
    defaultAspect: 0.66,
    fetchFlatBand: async (skip, top, sort, signal) => {
      const orderby = (NOVELS_SORTS.find((s) => s.value === sort) ?? NOVELS_SORTS[0]).orderby ?? undefined;
      const r = await api.fetchNovels({ ...query, skip, top, orderby }, signal);
      if (r.total >= 0) knownTotal = r.total;
      return { items: r.items.map((row) => toNovelCard(row, r.covers, r.maturity)), total: knownTotal };
    },
    // The five sorts are the SAME tokens `/browse` names its item sorts with (title / rating / newest /
    // oldest, and the default), so a band inside a shelf is in the order the flat list would have used.
    fetchGroupBand: groupable
      ? async (groupsSkip, groupsTop, perGroupTop, groupBy, sort, signal): Promise<GroupPage> => {
          const r = await api.fetchGroups({ ...grouped, groupBy, groupsSkip, groupsTop, perGroupTop, orderby: bandOrderby(sort) }, signal);
          return { groups: r.groups.map((g) => toNovelGroup(g, groupBy)), totalGroups: r.totalGroups };
        }
      : undefined,
    fetchGroupMore: groupable
      ? async (groupKey, skip, top, groupBy, sort, signal): Promise<CardPage> => {
          const r = await api.fetchGroupItems(groupBy, groupKey, { ...grouped, skip, top, orderby: bandOrderby(sort) }, signal);
          return { items: r.items.map((row) => ({ ...toNovelCard(row), groupKey })), total: r.total };
        }
      : undefined,
    groupLetters: groupable
      ? async (groupBy, _sort, signal) => (await api.fetchGroupLetters({ ...grouped, groupBy }, signal)).letters
      : undefined,
    onOpen: (item) => o.onOpen(item),
    onOpenGroup: groupable
      ? (group, groupBy) => {
          // A series header opens the series modal (a book series is a real Series row since R9); every
          // other axis is a FACET this rail already has, so its header scopes in place and drops the
          // grouping to series — the Books section's `handlePickGroup` behaviour, one push.
          if (groupBy === "series") { o.onOpenSeries?.(Number(group.key), group.label); return; }
          const key = GROUP_FACET[groupBy];
          if (!key) return;
          // The decade FACET is spelled "1990s" (what /novels/facets hands back); the group KEY is the
          // bare decade, so it is re-spelled here rather than being sent as a chip nothing matches.
          o.onScope?.({ facet: { key, value: groupBy === "decade" ? `${group.key}s` : group.key }, group: "series" });
        }
      : undefined,
  };
}

/** Which facet an axis header becomes; `series` opens its modal instead, so it is not here. */
const GROUP_FACET: Record<string, string> = { author: "authors", publisher: "publishers", decade: "decades" };

const bandOrderby = (sort: string | null | undefined): string | undefined =>
  (NOVELS_SORTS.find((s) => s.value === sort) ?? NOVELS_SORTS[0]).orderby ?? undefined;
