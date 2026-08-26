/**
 * React Query for the Books section: the key factory every hook uses, and the ONE invalidation map.
 * The standalone's rule stands — the default `staleTime` is minutes, so every write must name what it
 * made stale; a write that forgets shows the old answer until the next visit.
 *
 * The catalog sources stay query-free (they page through the band engine); React Query serves the
 * modals, the shelf, Explore, Kids, Novels and the admin.
 */
import type { QueryClient } from "@tanstack/react-query";

export const bk = {
  all: ["books"] as const,
  item: (id: number) => ["books", "item", id] as const,
  itemMark: (id: number) => ["books", "item-mark", id] as const,
  facets: (kind: string) => ["books", "facets", kind] as const,
  facetOptions: (field: string, q: string, skip: number) => ["books", "facet-options", field, q, skip] as const,
  seriesRun: (id: number) => ["books", "series-run", id] as const,
  seriesRating: (id: number) => ["books", "series-rating", id] as const,
  seriesGroup: (id: number) => ["books", "series-group", id] as const,
  groupMark: (type: string, key: string) => ["books", "group-mark", type, key] as const,
  position: (id: number) => ["books", "position", id] as const,
  history: (status: string) => ["books", "history", status] as const,
  itemMarks: (kind: string) => ["books", "item-marks", kind] as const,
  shelfSeries: (kind: string) => ["books", "shelf-series", kind] as const,
  seriesProgress: (id: number) => ["books", "series-progress", id] as const,
  shelf: (which: string) => ["books", "shelf", which] as const,
  suggestions: (count: number, seed?: number) => ["books", "suggestions", count, seed ?? 0] as const,
  explore: (kind: string, seed?: number) => ["books", "explore", kind, seed ?? 0] as const,
  exploreKids: (seed?: number) => ["books", "explore-kids", seed ?? 0] as const,
  novels: (params: unknown) => ["books", "novels", params] as const,
  novelFacets: () => ["books", "novel-facets"] as const,
  kidsBrowse: () => ["books", "kids-browse"] as const,
  epubSpine: (id: number) => ["books", "epub-spine", id] as const,
  epubToc: (id: number) => ["books", "epub-toc", id] as const,
  admin: (...parts: (string | number)[]) => ["books", "admin", ...parts] as const,
  index: () => ["books", "index"] as const,
};

export type BooksEvent =
  | { kind: "position"; itemId: number }
  | { kind: "itemMark"; itemId: number }
  | { kind: "groupMark"; groupType: string; groupKey: string }
  | { kind: "catalog" };

const bump = (qc: QueryClient, key: readonly unknown[]) => qc.invalidateQueries({ queryKey: key });

/** What a write made stale. Read this like a table; add a row when a new write lands. */
export async function invalidateAfter(qc: QueryClient, event: BooksEvent): Promise<void> {
  switch (event.kind) {
    case "position":
      await Promise.all([
        bump(qc, bk.position(event.itemId)),
        bump(qc, bk.itemMark(event.itemId)),
        bump(qc, ["books", "history"]),
        bump(qc, bk.shelf("continue")),
        bump(qc, bk.shelf("last-opened")),
        bump(qc, bk.itemMarks("read")),
        bump(qc, ["books", "shelf-series"]),
        bump(qc, ["books", "series-progress"]),
        bump(qc, bk.index()),
      ]);
      return;
    case "itemMark":
      await Promise.all([
        bump(qc, bk.itemMark(event.itemId)),
        bump(qc, bk.position(event.itemId)),
        bump(qc, ["books", "item-marks"]),
        bump(qc, ["books", "suggestions"]),
      ]);
      return;
    case "groupMark":
      await Promise.all([
        bump(qc, bk.groupMark(event.groupType, event.groupKey)),
        bump(qc, ["books", "shelf-series"]),
        bump(qc, ["books", "series-progress"]),
        bump(qc, ["books", "history"]),
        bump(qc, ["books", "item-marks"]),
        bump(qc, ["books", "suggestions"]),
        bump(qc, bk.index()),
      ]);
      return;
    case "catalog":
      await Promise.all([
        bump(qc, ["books", "facets"]),
        bump(qc, ["books", "facet-options"]),
        bump(qc, ["books", "explore"]),
        bump(qc, ["books", "explore-kids"]),
        bump(qc, bk.novelFacets()),
        bump(qc, bk.index()),
      ]);
      return;
    default:
      return;
  }
}

/**
 * The group marks a band showed come from the server's `userMeta`; a toggle in the series modal must
 * show on that band without dropping it. The source folds these overrides into `CardGroup.userMark`
 * on the next band mount — a cheap in-memory patch, keyed by `groupType|groupKey`.
 */
const overrides = new Map<string, { isRead: boolean; wantToRead: boolean; isFavorite: boolean; rating: number | null; notes: string | null }>();
let overridesEpoch = 0;

export function setGroupMarkOverride(groupType: string, groupKey: string, mark: { isRead: boolean; wantToRead: boolean; isFavorite: boolean; rating: number | null; notes: string | null }): void {
  overrides.set(`${groupType}|${groupKey}`, mark);
  overridesEpoch += 1;
}

export function groupMarkOverride(groupType: string, groupKey: string) {
  return overrides.get(`${groupType}|${groupKey}`);
}

export function groupMarkOverridesEpoch(): number {
  return overridesEpoch;
}
