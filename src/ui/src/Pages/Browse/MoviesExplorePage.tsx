/**
 * `/movies/explore` — the Movies/TV section's landing (R9 S7), composed SPA-side by
 * `composeMoviesExplore` out of endpoints the section already served.
 *
 * Cheapness is the design, not an afterthought:
 *  - every rail is its own React Query with a sensible `staleTime`, so returning to the tab redraws
 *    from cache and the page never refetches because a param moved;
 *  - the two EXPENSIVE queries (the franchise group index and the franchise run) are gated on
 *    `useExploreDepth` — a landing that is opened and left alone never asks for them;
 *  - rails below the fold do not mount their covers until they are approached (`ExploreTab`'s
 *    `LazyRail`);
 *  - the seed lives in the URL, so Shuffle is a real history step and Back walks the rolls.
 *
 * A card opens the section's own sheet HERE (`?title=<kind>:<id>` — the same param the browse uses,
 * so the modal, its Back-closes behaviour and its links are identical). A GROUP card (a franchise)
 * goes to the browse with `f=franchise:<value>` — the rail URL contract.
 */
import { useQuery } from "@tanstack/react-query";
import { Suspense, lazy, useCallback, useMemo } from "react";
import { useHistory, useLocation } from "react-router-dom";
import ExploreTab from "../../catalog/explore/ExploreTab";
import { FACET_GROUP_KINDS } from "../../catalog/explore/mapExplore";
import { useExploreDepth } from "../../catalog/explore/useNearViewport";
import type { CardGroup, CardItem } from "../../catalog/types";
import useChannelLineup from "../Tv/useChannelLineup";
import {
  MOVIES_MORE,
  MOVIES_UNSEEDED_RAILS,
  composeMoviesExplore,
  moviesFacetHref,
  type ContinueRow,
  type FranchiseGroupRow,
  type FranchiseRailDto,
  type LineupChannel,
  type RecommendationRow,
} from "./moviesExplore";
import type { MovieCardRow } from "../../catalog/sources/moviesSource";

const MovieModal = lazy(() => import("./MovieModal"));

const RAIL_SUBTITLES: Record<string, string> = {
  continue: "Pick up where you stopped — on any device",
  "now-on-tv": "The channels are running right now",
  "for-you": "From your ratings and what you have watched",
  recent: "The newest arrivals on the shelf",
  franchises: "A whole franchise, in one place",
  random: "A shuffled handful of the library — roll again for more",
};

const TYPES = "Movies,Series";

async function getJson<T>(url: string, signal?: AbortSignal): Promise<T> {
  const r = await fetch(url, { signal });
  if (!r.ok) throw new Error(`${url} → ${r.status}`);
  return (await r.json()) as T;
}

export function readSeed(search: string): number {
  const raw = new URLSearchParams(search).get("seed");
  if (raw && /^[0-9]{1,9}$/.test(raw)) {
    const n = Number(raw);
    if (Number.isSafeInteger(n) && n > 0) return n;
  }
  return 0;
}

/** `?title=<kind>:<id>` — the browse's own open-title param, so the sheet behaves identically here. */
export function titleFromSearch(search: string): { kind: "movie" | "series" | "misc"; id: number } | null {
  const m = /^(movie|series|misc):([0-9]+)$/.exec(new URLSearchParams(search).get("title") || "");
  if (!m) return null;
  const id = Number(m[2]);
  return Number.isSafeInteger(id) && id > 0 ? { kind: m[1] as "movie" | "series" | "misc", id } : null;
}

export interface MoviesExplorePageProps {
  userData?: { hasPassword?: boolean; userName?: string; username?: string; [k: string]: unknown } | null;
  setUserData?: (u: unknown) => void;
}

export default function MoviesExplorePage({ userData, setUserData }: MoviesExplorePageProps) {
  const history = useHistory();
  const location = useLocation();
  const seed = readSeed(location.search);
  const deep = useExploreDepth();
  const signedIn = !!userData;
  const streaming = !!userData?.hasPassword;

  // One seeded page of the shuffle feeds BOTH the hero and the "something else" grid — one query,
  // two rails, and Shuffle re-rolls them together.
  const random = useQuery({
    queryKey: ["movies", "explore", "random", seed],
    queryFn: ({ signal }) => getJson<{ movies?: MovieCardRow[] }>(`/API/Browse?types=${TYPES}&sort=random&seed=${seed}&page=1&pageSize=30`, signal),
    staleTime: 10 * 60 * 1000,
  });
  const recent = useQuery({
    queryKey: ["movies", "explore", "recent"],
    queryFn: ({ signal }) => getJson<{ movies?: MovieCardRow[] }>(`/API/Browse?types=${TYPES}&sort=added&page=1&pageSize=24`, signal),
    staleTime: 10 * 60 * 1000,
  });
  const continueWatching = useQuery({
    queryKey: ["movies", "explore", "continue"],
    queryFn: ({ signal }) => getJson<{ items?: ContinueRow[] }>("/API/ContinueWatching?take=12", signal),
    enabled: signedIn,
    staleTime: 60 * 1000,
  });
  const recommendations = useQuery({
    queryKey: ["movies", "explore", "for-you"],
    queryFn: ({ signal }) => getJson<{ items?: RecommendationRow[] }>("/API/Recommendations?take=18", signal),
    enabled: signedIn,
    staleTime: 30 * 60 * 1000,
  });
  // The franchise INDEX is the one heavy read on this page (it builds the group index server-side —
  // exactly what the catalog warmer keeps hot), so it waits until the reader has actually moved.
  const franchises = useQuery({
    queryKey: ["movies", "explore", "franchises"],
    queryFn: ({ signal }) => getJson<{ groups?: FranchiseGroupRow[] }>(
      `/API/BrowseGroups?types=${TYPES}&groupBy=franchise&groupsSkip=0&groupsTop=18&perGroupTop=1&sort=alpha`, signal),
    enabled: deep,
    staleTime: 30 * 60 * 1000,
  });
  const anchor = random.data?.movies?.[0];
  const franchiseRun = useQuery({
    queryKey: ["movies", "explore", "franchise-run", anchor?.kind ?? "movie", anchor?.id ?? 0],
    queryFn: ({ signal }) => getJson<FranchiseRailDto>(`/API/GetFranchiseRail?id=${anchor!.id}&kind=${anchor!.kind ?? "movie"}`, signal),
    enabled: deep && !!anchor?.id,
    staleTime: 30 * 60 * 1000,
  });
  // The homepage rail's lineup, reused verbatim (localStorage-seeded, user-independent). No poll:
  // Explore is a landing, not the guide.
  const { lineup } = useChannelLineup({ poll: false }) as { lineup: LineupChannel[] | null };

  const data = useMemo(() => composeMoviesExplore({
    random: random.data?.movies,
    recent: recent.data?.movies,
    continueWatching: continueWatching.data?.items,
    recommendations: recommendations.data?.items,
    franchiseGroups: franchises.data?.groups,
    franchiseRun: franchiseRun.data,
    lineup: streaming ? lineup : null,
    seed: seed || undefined,
  }), [random.data, recent.data, continueWatching.data, recommendations.data, franchises.data, franchiseRun.data, lineup, streaming, seed]);

  const ready = !random.isPending || !!random.data;
  const onSeed = useCallback((next: number) => {
    const p = new URLSearchParams(location.search);
    p.set("seed", String(next));
    p.delete("title");
    history.push({ pathname: location.pathname, search: `?${p.toString()}` });
  }, [history, location.pathname, location.search]);

  const onOpen = useCallback((item: CardItem) => {
    // A live channel is not a title — it tunes.
    if (item.kind === "channel") { history.push(`/tv/${item.id}`); return; }
    const p = new URLSearchParams(location.search);
    p.set("title", `${item.kind}:${item.id}`);
    history.push({ pathname: location.pathname, search: `?${p.toString()}` });
  }, [history, location.pathname, location.search]);

  const onOpenGroup = useCallback((group: CardGroup, groupBy: string) => {
    const href = moviesFacetHref(groupBy === "person" ? "actor" : groupBy, group.key);
    if (href) history.push(href);
  }, [history]);

  const closeTitle = useCallback(() => {
    const loc = history.location;
    const p = new URLSearchParams(loc.search);
    if (!p.has("title")) return;
    p.delete("title");
    const s = p.toString();
    history.replace({ pathname: loc.pathname, search: s ? `?${s}` : "" });
  }, [history]);

  const open = titleFromSearch(location.search);
  const browse = useCallback((mode: string, value: string) => {
    const href = moviesFacetHref(mode, value);
    if (href) history.push(href);
  }, [history]);

  return (
    <div className="movies-explore">
      <ExploreTab
        data={ready ? data : null}
        loading={random.isFetching || recent.isFetching}
        error={random.error && !random.data ? random.error : undefined}
        onSeed={onSeed}
        onOpen={onOpen}
        onOpenGroup={onOpenGroup}
        groupKinds={FACET_GROUP_KINDS}
        moreHref={(href) => href || null}
        unseededRails={MOVIES_UNSEEDED_RAILS}
        railSubtitle={(rail) => RAIL_SUBTITLES[rail.key]}
        heroEyebrow="From the library"
        emptyMessage="Nothing to explore yet — the library is still being catalogued."
      />
      {/* Explore holds no list of its own, so the three list-editing hooks the browse hands the
          sheet (remove-on-untoggle, the row patch, the playlist picker) have nothing to act on. */}
      <Suspense fallback={null}>
        <MovieModal
          movieId={open?.id ?? null}
          kind={open?.kind ?? "movie"}
          open={open != null}
          onClose={closeTitle}
          actorSearch={(name: string) => browse("actor", name)}
          onBrowse={browse}
          onOpenTitle={(id: number, kind = "movie") => onOpen({ kind, id } as CardItem)}
          userData={userData}
          setUserData={setUserData}
          onToggleViewing={undefined}
          onMovieUpdated={undefined}
          onAddToPlaylist={undefined}
        />
      </Suspense>
    </div>
  );
}

export { MOVIES_MORE };
