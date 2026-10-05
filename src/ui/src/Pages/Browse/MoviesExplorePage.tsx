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
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { Suspense, lazy, useCallback, useMemo, useState } from "react";
import { useHistory, useLocation } from "react-router-dom";
import ExploreTab from "../../catalog/explore/ExploreTab";
import { FACET_GROUP_KINDS } from "../../catalog/explore/mapExplore";
import { useExploreDepth } from "../../catalog/explore/useNearViewport";
import type { CardGroup, CardItem } from "../../catalog/types";
import useChannelLineup from "../Tv/useChannelLineup";
import type { HeroDetail } from "../../catalog/explore/HeroSpotlight";
import {
  MOVIE_DOOR_AXES,
  MOVIES_MORE,
  MOVIES_UNSEEDED_RAILS,
  composeMoviesExplore,
  moviesFacetHref,
  pickColumnDecade,
  pickColumnMood,
  type MovieDoorAxis,
  type ContinueRow,
  type FranchiseGroupRow,
  type FranchiseRailDto,
  type LineupChannel,
  type RecommendationRow,
} from "./moviesExplore";
import type { MovieCardRow } from "../../catalog/sources/moviesSource";

const MovieModal = lazy(() => import("./MovieModal"));

const RAIL_SUBTITLES: Record<string, string> = {
  continue: "Pick up where you stopped, on any device",
  "now-on-tv": "Every channel is playing something right now",
  "ways-in": "Start from a feeling, a place or a decade",
  "for-you": "Based on your ratings and what you've watched",
  suggested: "Newest first",
  recent: "The newest arrivals on the shelf",
  top: "By IMDb rating, across every film on the shelf",
  quick: "Three short lists, rolled fresh with every shuffle",
  franchises: "A whole franchise in one place",
  random: "A shuffled handful of the library",
};

/** The few fields of `/API/GetMovie` / `/API/GetSeries`'s `data` the marquee reads. */
interface TitleDetail { plot?: string | null; plotFull?: string | null; runtime?: string | null; genre?: string | null; director?: string | null; rating?: string | null; releaseDate?: string | null }

/** A title's detail as the marquee's lines: the plot, the facts, the genres. */
export function titleHeroDetail(item: CardItem, d: TitleDetail): HeroDetail {
  // OMDB-era rows spell "unknown" as the literal "N/A" — treat it as absent, never print it.
  const known = (v: string | null | undefined) => (v && v.trim() && v.trim().toUpperCase() !== "N/A" ? v.trim() : null);
  const year = item.year ?? (d.releaseDate ? new Date(d.releaseDate).getFullYear() : undefined);
  const director = known(d.director);
  const meta = [year ? String(year) : null, known(d.runtime), known(d.rating), director ? `Directed by ${director}` : null]
    .filter((x): x is string => !!x);
  const tags = (known(d.genre) ?? "").split(",").map((g) => g.trim()).filter(Boolean);
  return { synopsis: known(d.plot) || known(d.plotFull), meta, tags };
}

const TYPES = "Movies,Series";
/** The head of the viewer's Suggested list the rail shows (the endpoint materializes every id it is given). */
const SUGGESTED_RAIL_SIZE = 20;

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
  const queryClient = useQueryClient();
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
  // What friends suggested (the Suggested feature): the head of the viewer's own id list, newest first,
  // resolved to cards. Keyed on the ids themselves so a new suggestion refreshes it; only the head is
  // sent because the endpoint materializes every id it is given. `pageSize=0` answers alphabetically,
  // so the head's own order is restored client-side.
  const suggestedIds = useMemo(() => ((userData?.moviesSuggested as number[] | undefined) ?? []).slice(0, SUGGESTED_RAIL_SIZE), [userData?.moviesSuggested]);
  const suggested = useQuery({
    queryKey: ["movies", "explore", "suggested", suggestedIds.join(",")],
    queryFn: async ({ signal }) => {
      const r = await fetch("/API/GetMoviesByIds", { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify(suggestedIds), signal });
      if (!r.ok) throw new Error(`GetMoviesByIds → ${r.status}`);
      const rows = (await r.json()) as MovieCardRow[] | { movies?: MovieCardRow[] };
      const list = Array.isArray(rows) ? rows : (rows.movies ?? []);
      const byId = new Map(list.map((m) => [m.id, m]));
      return suggestedIds.map((id) => byId.get(id)).filter((m): m is MovieCardRow => !!m);
    },
    enabled: signedIn && suggestedIds.length > 0,
    staleTime: 10 * 60 * 1000,
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
  // "Ways in": only the ACTIVE axis is fetched — each is a cached group index the warmer keeps hot.
  const [doorAxis, setDoorAxis] = useState<MovieDoorAxis>("genre");
  const doors = useQuery({
    queryKey: ["movies", "explore", "doors", doorAxis],
    queryFn: ({ signal }) => getJson<{ groups?: FranchiseGroupRow[] }>(
      `/API/BrowseGroups?types=${TYPES}&groupBy=${doorAxis}&groupsSkip=0&groupsTop=${doorAxis === "decade" ? 12 : 30}&perGroupTop=8&sort=imdb${doorAxis === "decade" ? "" : "&headsBy=count"}`, signal),
    enabled: deep,
    staleTime: 30 * 60 * 1000,
  });
  // Every axis already fetched this visit, so flipping back to a tab draws at once.
  const doorRows = useMemo(() => {
    const out: Partial<Record<MovieDoorAxis, FranchiseGroupRow[]>> = {};
    for (const a of MOVIE_DOOR_AXES) {
      const cached = queryClient.getQueryData<{ groups?: FranchiseGroupRow[] }>(["movies", "explore", "doors", a.key]);
      if (cached?.groups) out[a.key] = cached.groups;
    }
    return out;
    // doors.data is the trigger: a new axis landing re-reads the cache.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [doors.data, queryClient]);
  const directors = useQuery({
    queryKey: ["movies", "explore", "directors"],
    queryFn: ({ signal }) => getJson<{ groups?: FranchiseGroupRow[] }>(
      `/API/BrowseGroups?types=${TYPES}&groupBy=director&headsBy=count&groupsSkip=0&groupsTop=40&perGroupTop=14&sort=imdb`, signal),
    enabled: deep,
    staleTime: 30 * 60 * 1000,
  });
  const top = useQuery({
    queryKey: ["movies", "explore", "top"],
    queryFn: ({ signal }) => getJson<{ movies?: MovieCardRow[] }>(`/API/Browse?types=Movies&sort=imdb&page=1&pageSize=40`, signal),
    enabled: deep,
    staleTime: 30 * 60 * 1000,
  });
  const critics = useQuery({
    queryKey: ["movies", "explore", "critics"],
    queryFn: ({ signal }) => getJson<{ movies?: MovieCardRow[] }>(`/API/Browse?types=${TYPES}&sort=rt&page=1&pageSize=40`, signal),
    enabled: deep,
    staleTime: 30 * 60 * 1000,
  });
  const facets = useQuery({
    queryKey: ["movies", "explore", "facets"],
    queryFn: ({ signal }) => getJson<{ tags?: Record<string, { value: string; count: number }[]> }>(`/API/BrowseFacets?types=${TYPES}`, signal),
    enabled: deep,
    staleTime: 60 * 60 * 1000,
  });
  const decadeKey = pickColumnDecade(seed || 1);
  const decade = useQuery({
    queryKey: ["movies", "explore", "decade", decadeKey],
    queryFn: ({ signal }) => getJson<{ movies?: MovieCardRow[] }>(
      `/API/Browse?types=${TYPES}&sort=imdb&yearMin=${decadeKey}&yearMax=${Number(decadeKey) + 9}&page=1&pageSize=6`, signal),
    enabled: deep,
    staleTime: 30 * 60 * 1000,
  });
  const moodKey = pickColumnMood(facets.data?.tags?.mood, seed || 1);
  const mood = useQuery({
    queryKey: ["movies", "explore", "mood", moodKey],
    queryFn: ({ signal }) => getJson<{ movies?: MovieCardRow[] }>(
      `/API/Browse?types=${TYPES}&sort=imdb&tag=${encodeURIComponent(`mood:${moodKey}`)}&page=1&pageSize=6`, signal),
    enabled: deep && !!moodKey,
    staleTime: 30 * 60 * 1000,
  });
  // The marquee's detail (plot, runtime, director, genres) for the ONE pick that is up.
  const [heroPick, setHeroPick] = useState<{ kind: string; id: number } | null>(null);
  const heroInfo = useQuery({
    queryKey: ["movies", "explore", "hero", heroPick?.kind, heroPick?.id],
    queryFn: ({ signal }) => getJson<{ data?: TitleDetail }>(
      heroPick!.kind === "series" ? `/API/GetSeries?id=${heroPick!.id}` : `/API/GetMovie?id=${heroPick!.id}`, signal),
    enabled: !!heroPick && heroPick.kind !== "misc",
    staleTime: 60 * 60 * 1000,
  });
  const onHeroActive = useCallback((item: CardItem) => {
    setHeroPick((cur) => (cur && cur.id === item.id && cur.kind === item.kind ? cur : { kind: item.kind, id: item.id }));
  }, []);
  const heroDetail = useCallback((item: CardItem): HeroDetail | null => {
    const d = queryClient.getQueryData<{ data?: TitleDetail }>(["movies", "explore", "hero", item.kind, item.id])?.data;
    return d ? titleHeroDetail(item, d) : null;
    // heroInfo.data is the trigger: the detail landing re-renders the marquee with it.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [heroInfo.data, queryClient]);

  // The homepage rail's lineup, reused verbatim (localStorage-seeded, user-independent). No poll:
  // Explore is a landing, not the guide.
  const { lineup } = useChannelLineup({ poll: false, enabled: streaming }) as { lineup: LineupChannel[] | null };

  const data = useMemo(() => composeMoviesExplore({
    random: random.data?.movies,
    recent: recent.data?.movies,
    continueWatching: continueWatching.data?.items,
    recommendations: recommendations.data?.items,
    suggested: suggested.data,
    franchiseGroups: franchises.data?.groups,
    franchiseRun: franchiseRun.data,
    lineup: streaming ? lineup : null,
    doors: doorRows,
    doorAxis,
    directors: directors.data?.groups,
    top: top.data?.movies,
    critics: critics.data?.movies,
    decade: decade.data?.movies?.length ? { decade: decadeKey, rows: decade.data.movies } : null,
    mood: moodKey && mood.data?.movies?.length ? { mood: moodKey, rows: mood.data.movies } : null,
    seed: seed || undefined,
  }), [random.data, recent.data, continueWatching.data, recommendations.data, suggested.data, franchises.data, franchiseRun.data, lineup, streaming, seed,
    doorRows, doorAxis, directors.data, top.data, critics.data, decade.data, decadeKey, mood.data, moodKey]);

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

  const onAxis = useCallback((_rail: string, axis: string) => setDoorAxis(axis as MovieDoorAxis), []);
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
        heroEyebrow="Tonight's feature"
        heroDetail={heroDetail}
        onHeroActive={onHeroActive}
        onAxis={onAxis}
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
