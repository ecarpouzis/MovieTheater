/**
 * "Ways in" for the comics Explore — composed in the BROWSER over the host's existing browse routes,
 * so it needs nothing new of the BooksHost (which deploys separately, by hand). The host's composed
 * `/explore` payload stays the page's spine; this rail is spliced in after its first rail.
 *
 * | Axis | Doors from | Covers from |
 * |---|---|---|
 * | Publisher / Franchise | `/browse/facets` — the twelve biggest by count | one `/browse/groups?singleGroupKey=` per door (the host's group heads are alphabetical, so "the biggest twelve" cannot be one request) |
 * | Decade | `/browse/groups?groupBy=decade` — one request, every decade | the same answer |
 *
 * Only the ACTIVE axis is fetched, each cached half an hour. Covers come from the live media token
 * (`withLiveArt`), the rule every Books surface follows.
 */
import { distinctCovers } from "../../catalog/explore/composeExplore";
import { facetHref } from "../../catalog/rail/facetUrl";
import { toBookCard } from "../../catalog/sources/booksSource";
import type { ExploreDoor } from "../../catalog/types";
import { fetchGroups, type BrowseFacetsResult, type BrowseGroupItem, type ItemSummary } from "./booksApi";
import { withLiveArt } from "./booksExploreArt";

export const BOOK_DOOR_AXES = [
  { key: "publisher", label: "Publisher" },
  { key: "franchise", label: "Franchise" },
  { key: "decade", label: "Decade" },
] as const;
export type BookDoorAxis = (typeof BOOK_DOOR_AXES)[number]["key"];

/** How many doors a count-ordered axis offers (two rows on a wide screen). */
export const BOOK_DOORS_TAKE = 12;
const DOOR_MIN = 5;

/** The browse a door opens: the facet chip, or the year range for a decade. */
export function bookDoorHref(axis: string, key: string): string {
  if (axis === "decade") {
    const d = Number(key);
    return Number.isFinite(d) ? `/books?y=${d}-${d + 9}` : "/books";
  }
  return facetHref("/books", [[axis, key]]);
}

/** The biggest values of a count-ordered axis, from the facet lists the browse rail already reads. */
export function topFacetValues(facets: BrowseFacetsResult | null | undefined, axis: BookDoorAxis, take = BOOK_DOORS_TAKE): { value: string; count: number }[] {
  // Publishers travel by NAME (the facet spec's rule: the projection has no publisher id).
  const list = axis === "publisher"
    ? (facets?.publishers ?? []).map((p) => ({ value: p.name, count: p.count }))
    : axis === "franchise" ? (facets?.franchises ?? []) : (facets?.decades ?? []);
  return list
    .map((o) => ({ value: String(o.value ?? "").trim(), count: Number(o.count ?? 0) }))
    .filter((o) => o.value && o.count >= DOOR_MIN)
    .sort((a, b) => b.count - a.count || a.value.localeCompare(b.value))
    .slice(0, take);
}

const coverCandidates = (items: readonly ItemSummary[]) =>
  items.map((it) => withLiveArt(toBookCard(it))).map((c) => ({ src: c.imageThumbUrl ?? c.imageUrl, hue: c.hue }));

/** Groups (each with its best-rated members) as doors, with covers made distinct across the axis. */
export function bookDoors(axis: string, groups: readonly Pick<BrowseGroupItem, "key" | "label" | "totalItems" | "items">[]): ExploreDoor[] {
  const list = groups.filter((g) => g.key && g.totalItems >= DOOR_MIN);
  const covers = distinctCovers(list, (g) => coverCandidates(g.items ?? []));
  return list.map((g, i) => ({ key: g.key, label: g.label || g.key, count: g.totalItems, href: bookDoorHref(axis, g.key), covers: covers[i] }));
}

/** Fetch one axis's doors. Decades are one request; the count-ordered axes one small request per door. */
export async function fetchBookDoors(axis: BookDoorAxis, facets: BrowseFacetsResult | null | undefined, signal?: AbortSignal): Promise<ExploreDoor[]> {
  if (axis === "decade") {
    const r = await fetchGroups({ groupBy: "decade", groupsTop: 30, perGroupTop: 8, orderby: "rating", kind: "comic" }, signal);
    const chronological = (r?.groups ?? []).slice().sort((a, b) => Number(a.key) - Number(b.key));
    return bookDoors(axis, chronological);
  }
  const top = topFacetValues(facets, axis);
  const groups = await Promise.all(top.map(async (t) => {
    const r = await fetchGroups({ groupBy: axis, singleGroupKey: t.value, groupsTop: 1, perGroupTop: 8, orderby: "rating", kind: "comic" }, signal).catch(() => null);
    const g = r?.groups?.[0];
    return { key: t.value, label: g?.label || t.value, totalItems: g?.totalItems ?? t.count, items: g?.items ?? [] };
  }));
  return bookDoors(axis, groups);
}
