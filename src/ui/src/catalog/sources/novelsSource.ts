/**
 * Novels (the prose books) → a flat-only `CatalogSource` over the host's `/novels` list. The facet
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
import type { ItemSummary, NovelsQuery } from "../../Pages/Books/booksApi";
import { clampAspect } from "../../Pages/Books/booksFormat";
import * as media from "../../Pages/Books/booksMedia";
import { hueSvg } from "../cards/CardImage";
import type { FacetSpec, FacetState } from "../rail/facetSpec";
import { facetStateKey } from "../rail/facetUrl";
import type { CardItem, CatalogSource, ListColumn, SortSpec, TweakExtra, ViewMode } from "../types";
import { cardKey } from "../types";
import { hueOf } from "./hue";

export const NOVELS_PAGE_SIZE = 60;
export const NOVELS_VIEWS: ViewMode[] = ["grid", "wall", "list"];

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

/** The `/novels` query for a facet state. Pure, so the URL → request mapping is testable on its own. */
export function buildNovelsQuery(state: FacetState): NovelsQuery {
  const q: NovelsQuery = {
    author: csv(state.include.authors),
    series: csv(state.include.series),
    publisher: csv(state.include.publishers),
    decade: csv(state.include.decades),
    tag: csv(state.include.tags),
    excludeTag: csv(state.exclude.tags),
    q: state.q.trim() || undefined,
  };
  if (state.ratingMin > 0) q.minRating = state.ratingMin;
  if (state.flags.unknown) q.unknown = true;
  return q;
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
  onOpen(item: CardItem): void;
}

export function createNovelsSource(o: NovelsSourceOptions): CatalogSource {
  const query = buildNovelsQuery(o.facetState);
  let knownTotal = -1;
  return {
    queryKey: `books-novels:${facetStateKey(o.facetState)}:${o.epoch ?? 0}:${o.mediaEpoch ?? 0}`,
    title: "Novels",
    itemNoun: "book",
    supports: NOVELS_VIEWS,
    groups: [],
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
    onOpen: (item) => o.onOpen(item),
  };
}
