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
import { useQuery } from "@tanstack/react-query";
import { useCallback, useMemo } from "react";
import { useHistory, useLocation } from "react-router-dom";
import ExploreTab from "../../catalog/explore/ExploreTab";
import { FACET_GROUP_KINDS } from "../../catalog/explore/mapExplore";
import { useExploreDepth } from "../../catalog/explore/useNearViewport";
import type { CardGroup, CardItem } from "../../catalog/types";
import type { ArcadeGameRow } from "../../catalog/sources/arcadeSource";
import { MovieAPI } from "../../MovieAPI";
import {
  ARCADE_UNSEEDED_RAILS,
  arcadeSystemHref,
  composeArcadeExplore,
  pickSpinSystem,
  type LiveRoomRow,
  type RecentlyPlayedRow,
  type SystemFacetRow,
  type TrophyGameRow,
} from "./arcadeExplore";
import "./ArcadePage.css";

const RAIL_SUBTITLES: Record<string, string> = {
  recent: "Your own save activity — pick up where you stopped",
  live: "Rooms open right now; a card drops you straight in",
  trophies: "The games you last unlocked something in",
  systems: "Every console on the shelf",
  top: "The best-rated games in the catalog",
  spin: "One console, chosen by the roll — shuffle for another",
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

  const data = useMemo(() => composeArcadeExplore({
    recent: recent.data,
    rooms: rooms.data,
    trophies: trophies.data?.games,
    systems: filters.data?.systems,
    top: top.data?.games,
    spin: spinSystem && spin.data?.games?.length ? { system: spinSystem, games: spin.data.games } : null,
    seed,
  }), [recent.data, rooms.data, trophies.data, filters.data, top.data, spin.data, spinSystem, seed]);

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
        heroEyebrow="On the shelf"
        emptyMessage="The arcade has nothing ingested yet."
      />
    </div>
  );
}
