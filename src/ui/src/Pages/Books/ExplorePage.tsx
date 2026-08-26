/**
 * `/books/explore` — the section's Explore tab over the host's composed payload (`/explore?kind=comic`).
 * The seed lives in the URL (`?seed=`, a push per shuffle, so Back walks the rolls) and the day's
 * default payload is held 30 minutes; a catalog change (an admin job) drops it through
 * `invalidateAfter`. Cards open the item modal; a series card opens the series modal (the single-issue
 * collapse in `openEntity`); a rail's "More →" lands on the browse with that rail's filter applied.
 */
import { keepPreviousData, useQuery } from "@tanstack/react-query";
import { useCallback, useMemo } from "react";
import { useHistory, useLocation } from "react-router-dom";
import ExploreTab from "../../catalog/explore/ExploreTab";
import type { HeroDetail } from "../../catalog/explore/HeroSpotlight";
import { mapExplore } from "../../catalog/explore/mapExplore";
import type { CardGroup, CardItem } from "../../catalog/types";
import { fetchExplore, type ItemSummary } from "./booksApi";
import { exploreWithLiveArt } from "./booksExploreArt";
import { exploreMoreHref } from "./booksExploreLinks";
import { runLabel } from "./booksFormat";
import { useMediaToken } from "./booksMedia";
import { bk } from "./booksQuery";
import { openEntity } from "./openEntity";

const RAIL_SUBTITLES: Record<string, string> = {
  "top-series": "The cream of the collection, blended from community + editorial scores",
  "collected-editions": "Omnibuses and fat trades — start a whole run in one volume",
  "top-shelf-reads": "The best-rated books on the shelf",
  suggested: "Picked from your reading history — shuffle for a fresh handful",
};
const UNSEEDED = new Set(["fresh-arrivals"]);

export function readSeed(search: string): number | undefined {
  const raw = new URLSearchParams(search).get("seed");
  if (!raw || !/^[0-9]+$/.test(raw)) return undefined;
  const n = Number(raw);
  return Number.isSafeInteger(n) && n > 0 ? n : undefined;
}

/** What the hero adds beyond the card: the series as the headline, the publisher as the byline, the run, the tags. */
export function booksHeroDetail(item: CardItem): HeroDetail {
  const raw = (item.raw ?? {}) as Partial<ItemSummary>;
  // The host's tags are "category:value"; the hero shows the value half.
  const tags = (raw.tagsCsv ?? "").split(",").map((t) => t.trim()).filter(Boolean).map((t) => (t.includes(":") ? t.slice(t.indexOf(":") + 1).trim() : t)).filter(Boolean);
  const run = runLabel(raw.seriesYearStart, raw.seriesYearEnd, raw.seriesIsOngoing);
  const meta = [run, raw.seriesIssueCount ? `${raw.seriesIssueCount} issues` : item.label].filter(Boolean) as string[];
  return {
    title: !raw.isSingleIssueSeries && raw.series ? raw.series : item.title,
    subtitle: raw.publisher ?? null,
    meta,
    tags,
  };
}

export default function ExplorePage({ kind = "comic" as const }: { kind?: "comic" | "book" }) {
  const history = useHistory();
  const location = useLocation();
  const seed = readSeed(location.search);
  const query = useQuery({
    queryKey: bk.explore(kind, seed),
    queryFn: ({ signal }) => fetchExplore(kind, seed, signal),
    staleTime: 30 * 60 * 1000,
    // A re-roll keeps the page up until the new roll lands — no flash to the skeleton.
    placeholderData: keepPreviousData,
  });
  // Covers come from the browser's live media token (the host's cached URLs can carry a dead one); the
  // token's epoch re-derives the page when it is minted or refreshed.
  const media = useMediaToken();
  const data = useMemo(() => (query.data ? exploreWithLiveArt(mapExplore(query.data)) : null), [query.data, media.epoch]);

  const onSeed = useCallback((next: number) => {
    const p = new URLSearchParams(location.search);
    p.set("seed", String(next));
    history.push({ pathname: location.pathname, search: `?${p.toString()}` });
  }, [history, location.pathname, location.search]);

  const onOpen = useCallback((item: CardItem) => openEntity(history, location, { kind: "item", id: item.id }), [history, location]);
  const onOpenGroup = useCallback((group: CardGroup, groupBy: string) => {
    if (groupBy !== "series") return;
    const card = group.items[0];
    const raw = (card?.raw ?? {}) as { issueCount?: number; cover?: { id?: number } | null };
    const single = raw.issueCount === 1 && raw.cover?.id ? { isSingleIssueSeries: true, itemId: raw.cover.id } : null;
    openEntity(history, location, { kind: "series", id: Number(group.key), single });
  }, [history, location]);

  return (
    <div className="books-explore books-surface">
      <ExploreTab
        data={data}
        loading={query.isFetching}
        error={query.error}
        onSeed={onSeed}
        onOpen={onOpen}
        onOpenGroup={onOpenGroup}
        moreHref={(href) => exploreMoreHref(href)}
        unseededRails={UNSEEDED}
        heroDetail={booksHeroDetail}
        railSubtitle={(rail) => (rail.key === "fresh-arrivals" ? `The latest ${rail.items.length} arrivals` : RAIL_SUBTITLES[rail.key])}
        emptyMessage="Nothing to explore yet — the library is still being catalogued."
      />
    </div>
  );
}
