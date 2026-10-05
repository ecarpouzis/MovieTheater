/**
 * `/arcade/explore` — the Arcade section's landing (R9 S7), and the new home of the lobby's
 * "Recently played" strip (a deferral from S2c/S3: the lobby keeps the console carousel, the live
 * rooms banner and the grid; the personal shelves come here).
 *
 * Every rail is an endpoint the arcade already served. The two cheap, current ones (recents, rooms)
 * plus the facet list load on landing; the trophy room and the seeded "spin the shelf" wait for
 * `useExploreDepth`, so an Explore that is opened and left alone makes four small calls.
 *
 * A game card opens the lobby's own URL-driven modal (`/arcade?game=<versionId>` — the modal is
 * where the version, the cheats, the renderer and Start live, and it must stay the one place they
 * live). A LIVE ROOM card joins the room instead. A console card lands on `/arcade?f=system:<value>`,
 * which is the console carousel's own facet.
 */
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { useCallback, useMemo, useState } from "react";
import { useHistory, useLocation } from "react-router-dom";
import ExploreTab from "../../catalog/explore/ExploreTab";
import { FACET_GROUP_KINDS } from "../../catalog/explore/mapExplore";
import { useExploreDepth } from "../../catalog/explore/useNearViewport";
import type { CardGroup, CardItem } from "../../catalog/types";
import type { ArcadeGameRow } from "../../catalog/sources/arcadeSource";
import { MovieAPI } from "../../MovieAPI";
import {
  ARCADE_DOOR_AXES,
  ARCADE_UNSEEDED_RAILS,
  POCKET_SYSTEMS,
  arcadeHeroDetail,
  arcadeSystemHref,
  composeArcadeExplore,
  pickSpinSystem,
  type ArcadeDoorAxis,
  type ArcadeGroupRow,
  type LiveRoomRow,
  type RecentlyPlayedRow,
  type SystemFacetRow,
  type TrophyGameRow,
} from "./arcadeExplore";
import "./ArcadePage.css";

const RAIL_SUBTITLES: Record<string, string> = {
  recent: "Your own saves, newest first",
  live: "Rooms open right now. A card drops you straight in",
  "ways-in": "Start from a console, a genre or how many are playing",
  trophies: "The games you last unlocked something in",
  top: "By rating, across every console",
  quick: "Three short lists, rolled fresh with every shuffle",
};

export function readSeed(search: string): number {
  const raw = new URLSearchParams(search).get("seed");
  if (raw && /^[0-9]{1,9}$/.test(raw)) {
    const n = Number(raw);
    if (Number.isSafeInteger(n) && n > 0) return n;
  }
  return 1;
}

async function json<T>(res: Response, fallback: T): Promise<T> {
  if (!res.ok) throw new Error(String(res.status));
  return (await res.json()) as T ?? fallback;
}

export default function ArcadeExplorePage({ userData }: { userData?: unknown }) {
  const history = useHistory();
  const queryClient = useQueryClient();
  const location = useLocation();
  const seed = readSeed(location.search);
  const deep = useExploreDepth();

  const recent = useQuery({
    queryKey: ["arcade", "explore", "recent"],
    queryFn: () => MovieAPI.getArcadeRecentlyPlayed(12) as Promise<RecentlyPlayedRow[]>,
    staleTime: 60 * 1000,
  });
  const rooms = useQuery({
    queryKey: ["arcade", "explore", "rooms"],
    queryFn: async () => json<LiveRoomRow[]>(await MovieAPI.getArcadeRooms(), []),
    staleTime: 15 * 1000,
  });
  const filters = useQuery({
    queryKey: ["arcade", "explore", "filters"],
    queryFn: async () => json<{ systems?: SystemFacetRow[] }>(await MovieAPI.getArcadeFilters({}), {}),
    staleTime: 30 * 60 * 1000,
  });
  const top = useQuery({
    queryKey: ["arcade", "explore", "top"],
    queryFn: async ({ signal }) => json<{ games?: ArcadeGameRow[] }>(await MovieAPI.getArcadeGames({ sort: "rating", page: 1, pageSize: 40 }, signal), {}),
    staleTime: 30 * 60 * 1000,
  });
  const trophies = useQuery({
    queryKey: ["arcade", "explore", "trophies"],
    queryFn: () => MovieAPI.getMyArcadeTrophies() as Promise<{ games?: TrophyGameRow[] }>,
    enabled: deep,
    staleTime: 5 * 60 * 1000,
  });

  const spinSystem = pickSpinSystem(filters.data?.systems, seed);
  const spin = useQuery({
    queryKey: ["arcade", "explore", "spin", spinSystem],
    queryFn: async ({ signal }) => json<{ games?: ArcadeGameRow[] }>(await MovieAPI.getArcadeGames({ system: spinSystem!, sort: "rating", page: 1, pageSize: 24 }, signal), {}),
    enabled: deep && !!spinSystem,
    staleTime: 30 * 60 * 1000,
  });

  // "Ways in": only the ACTIVE axis is fetched (each a cached group index on the server).
  const [doorAxis, setDoorAxis] = useState<ArcadeDoorAxis>("system");
  const doors = useQuery({
    queryKey: ["arcade", "explore", "doors", doorAxis],
    queryFn: async ({ signal }) => {
      const r = await fetch(`/API/Arcade/GameGroups?groupBy=${doorAxis}&groupsSkip=0&groupsTop=40&perGroupTop=8&sort=rating`, { signal });
      return json<{ groups?: ArcadeGroupRow[] }>(r, {});
    },
    enabled: deep,
    staleTime: 30 * 60 * 1000,
  });
  const doorRows = useMemo(() => {
    const out: Partial<Record<ArcadeDoorAxis, ArcadeGroupRow[]>> = {};
    for (const a of ARCADE_DOOR_AXES) {
      const cached = queryClient.getQueryData<{ groups?: ArcadeGroupRow[] }>(["arcade", "explore", "doors", a.key]);
      if (cached?.groups) out[a.key] = cached.groups;
    }
    return out;
    // doors.data is the trigger: a new axis landing re-reads the cache.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [doors.data, queryClient]);
  const onAxis = useCallback((_rail: string, axis: string) => setDoorAxis(axis as ArcadeDoorAxis), []);

  // The three quick-pick columns: a page of the best-rated in each slice; the seed picks six.
  const column = (key: string, params: Record<string, string | number>) => ({
    queryKey: ["arcade", "explore", "column", key],
    queryFn: async ({ signal }: { signal: AbortSignal }) => json<{ games?: ArcadeGameRow[] }>(await MovieAPI.getArcadeGames({ sort: "rating", page: 1, pageSize: 30, ...params }, signal), {}),
    enabled: deep,
    staleTime: 30 * 60 * 1000,
  });
  const coop = useQuery(column("coop", { maxPlayers: 2 }));
  const pocket = useQuery(column("pocket", { system: POCKET_SYSTEMS }));
  const coinop = useQuery(column("coinop", { system: "arcade" }));

  const data = useMemo(() => composeArcadeExplore({
    recent: recent.data,
    rooms: rooms.data,
    trophies: trophies.data?.games,
    systems: filters.data?.systems,
    top: top.data?.games,
    spin: spinSystem && spin.data?.games?.length ? { system: spinSystem, games: spin.data.games } : null,
    doors: doorRows,
    doorAxis,
    coop: coop.data?.games,
    pocket: pocket.data?.games,
    coinop: coinop.data?.games,
    seed,
  }), [recent.data, rooms.data, trophies.data, filters.data, top.data, spin.data, spinSystem, seed, doorRows, doorAxis, coop.data, pocket.data, coinop.data]);

  const onSeed = useCallback((next: number) => {
    const p = new URLSearchParams(location.search);
    p.set("seed", String(next));
    history.push({ pathname: location.pathname, search: `?${p.toString()}` });
  }, [history, location.pathname, location.search]);

  const onOpen = useCallback((item: CardItem) => {
    const room = (item.raw ?? {}) as { roomCode?: string };
    if (room.roomCode) { history.push(`/arcade/room/${room.roomCode}`); return; }
    history.push(`/arcade?game=${item.id}`);
  }, [history]);

  const onOpenGroup = useCallback((group: CardGroup, groupBy: string) => {
    if (groupBy !== "system") return;
    history.push(arcadeSystemHref(group.key));
  }, [history]);

  const ready = !top.isPending || !!top.data;
  return (
    <div className="arcade-page arcade-explore" data-user={userData ? "in" : "out"}>
      <ExploreTab
        data={ready ? data : null}
        loading={top.isFetching}
        error={top.error && !top.data ? top.error : undefined}
        onSeed={onSeed}
        onOpen={onOpen}
        onOpenGroup={onOpenGroup}
        groupKinds={FACET_GROUP_KINDS}
        moreHref={(href) => href || null}
        unseededRails={ARCADE_UNSEEDED_RAILS}
        railSubtitle={(rail) => RAIL_SUBTITLES[rail.key]}
        heroEyebrow="Insert coin"
        heroDetail={arcadeHeroDetail}
        onAxis={onAxis}
        emptyMessage="The arcade has nothing ingested yet."
      />
    </div>
  );
}
