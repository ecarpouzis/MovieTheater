/**
 * The Boardgames catalog as ONE shared resource (R9 S2c): the whole `/odata/Boardgames` list and
 * the `/API/Boardgames/Facets` rows, read by the page AND the sider rail (two trees) through React
 * Query — one fetch, one in-memory copy, `setQueryData` for the modal's edits. Keeps the section's
 * render-from-cache-then-refresh contract: the last payload seeds the query from localStorage (the
 * same `boardgames_v1` / `boardgames_facets_v1` keys `useCachedResource` wrote), a fresh fetch
 * replaces it in the background and rewrites the cache, and a failed refresh over a warm cache keeps
 * the stale copy. `useCachedResource` itself is per-mount, which is why this exists.
 */
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { useCallback, useMemo } from "react";
import type { BoardgameFacets, BoardgameRow } from "../../catalog/sources/boardgamesSource";
import { facetsMap } from "../../catalog/sources/boardgamesSource";
import { readStored, writeStored } from "../../utils/storage";

export const BOARDGAMES_CACHE_KEY = "boardgames_v1";
export const BOARDGAMES_FACETS_CACHE_KEY = "boardgames_facets_v1";

export const boardgamesCatalogKey = ["boardgames", "catalog"] as const;
export const boardgamesFacetsKey = ["boardgames", "facets"] as const;

const CATALOG_URL = "/odata/Boardgames?$select=id,bggThingId,name,yearPublished,minPlayers,maxPlayers,playingTime,minPlayTime,maxPlayTime,minAge,averageRating,averageWeight,description,rulesPdfUrlsJson,rulesPdfCandidateUrlsJson,howToPlayVideoUrlsJson,thingType,baseGameId&$expand=imageDetails&$orderby=name";

/** The page's game row — the OData row normalized (either key casing) with the JSON columns parsed. */
export interface BoardgameGame extends BoardgameRow {
  bggThingId?: number | null;
  rulesPdfUrls: { url: string; name: string | null }[];
  rulesPdfCandidateUrls: string[];
  howToPlayVideoUrlsJson: string | null;
  howToPlayVideoUrls: string[];
  imageUrl: string | null;
}

// eslint-disable-next-line @typescript-eslint/no-explicit-any
type Raw = Record<string, any>;

function parseJsonArray(json: unknown): unknown[] | null {
  if (!json || typeof json !== "string") return null;
  try { const v = JSON.parse(json); return Array.isArray(v) ? v : null; } catch { return null; }
}

function parsePdfEntries(json: unknown): { url: string; name: string | null }[] | null {
  const arr = parseJsonArray(json);
  if (!arr) return null;
  return (arr as (Raw | string)[]).map((e) => (typeof e === "string" ? { url: e, name: null } : { url: e.Url ?? e.url ?? "", name: e.Name ?? e.name ?? null }));
}

export function normalizeGame(game: Raw): BoardgameGame {
  const details = game.imageDetails ?? game.ImageDetails ?? null;
  return {
    id: game.id ?? game.Id,
    bggThingId: game.bggThingId ?? game.BggThingId,
    name: game.name ?? game.Name,
    yearPublished: game.yearPublished ?? game.YearPublished,
    minPlayers: game.minPlayers ?? game.MinPlayers,
    maxPlayers: game.maxPlayers ?? game.MaxPlayers,
    playingTime: game.playingTime ?? game.PlayingTime,
    minPlayTime: game.minPlayTime ?? game.MinPlayTime,
    maxPlayTime: game.maxPlayTime ?? game.MaxPlayTime,
    minAge: game.minAge ?? game.MinAge,
    averageRating: game.averageRating ?? game.AverageRating,
    averageWeight: game.averageWeight ?? game.AverageWeight,
    description: game.description ?? game.Description,
    rulesPdfUrls: parsePdfEntries(game.rulesPdfUrlsJson ?? game.RulesPdfUrlsJson) ?? game.rulesPdfUrls ?? game.RulesPdfUrls ?? [],
    rulesPdfCandidateUrls: (parseJsonArray(game.rulesPdfCandidateUrlsJson ?? game.RulesPdfCandidateUrlsJson) as string[] | null) ?? game.rulesPdfCandidateUrls ?? game.RulesPdfCandidateUrls ?? [],
    howToPlayVideoUrlsJson: game.howToPlayVideoUrlsJson ?? game.HowToPlayVideoUrlsJson ?? null,
    howToPlayVideoUrls: ((parseJsonArray(game.howToPlayVideoUrlsJson ?? game.HowToPlayVideoUrlsJson) ?? game.howToPlayVideoUrls ?? game.HowToPlayVideoUrls ?? []) as (string | Raw)[])
      .map((e) => (typeof e === "string" ? e : e.Url ?? e.url ?? "")).filter(Boolean),
    imageUrl: details?.imageUrl ?? details?.ImageUrl ?? null,
    imageVersion: details?.imageVersion ?? details?.ImageVersion ?? null,
    thingType: game.thingType ?? game.ThingType ?? null,
    baseGameId: game.baseGameId ?? game.BaseGameId ?? null,
  };
}

export function extractGames(payload: unknown): BoardgameGame[] {
  const rawGames: Raw[] = Array.isArray(payload) ? payload : Array.isArray((payload as Raw)?.value) ? (payload as Raw).value : [];
  return rawGames.map(normalizeGame).filter((g) => Number.isInteger(g.id) && g.id > 0);
}

function readCached<T>(key: string): T | undefined {
  const raw = readStored(key);
  if (raw == null) return undefined;
  try { return JSON.parse(raw) as T; } catch { return undefined; }
}

function writeCached(key: string, value: unknown): void {
  try { writeStored(key, JSON.stringify(value)); } catch { /* payload too big — render-only */ }
}

async function fetchCatalog(signal?: AbortSignal): Promise<BoardgameGame[]> {
  const r = await fetch(CATALOG_URL, { signal });
  if (!r.ok) throw new Error(`boardgames → ${r.status}`);
  const games = extractGames(await r.json());
  writeCached(BOARDGAMES_CACHE_KEY, games);
  return games;
}

async function fetchFacets(signal?: AbortSignal): Promise<BoardgameFacets[]> {
  const r = await fetch("/API/Boardgames/Facets", { signal });
  if (!r.ok) throw new Error(`boardgame facets → ${r.status}`);
  const d = (await r.json()) as { items?: BoardgameFacets[] };
  const items = Array.isArray(d?.items) ? d.items : [];
  writeCached(BOARDGAMES_FACETS_CACHE_KEY, items);
  return items;
}

export interface BoardgamesCatalog {
  games: BoardgameGame[];
  /** Expansions by their base game's id. */
  expansionMap: Record<number, BoardgameGame[]>;
  facetsById: Map<number, BoardgameFacets>;
  /** Cold cache with the fetch in flight. */
  loading: boolean;
  /** The fetch failed AND there is nothing cached to show. */
  error: boolean;
  refresh: () => void;
  /** Patch the in-memory list after an edit (the cache is rewritten by the next fetch). */
  setGames: (update: (prev: BoardgameGame[]) => BoardgameGame[]) => void;
  /** Changes whenever the rows do — the facet spec's identity rides it. */
  version: string;
}

const EMPTY_GAMES: BoardgameGame[] = [];
const EMPTY_FACETS: BoardgameFacets[] = [];

export default function useBoardgamesCatalog(): BoardgamesCatalog {
  const client = useQueryClient();
  // `initialDataUpdatedAt: 0` makes the cached seed stale at once, so the mount refetches in the
  // background while the seed renders — the stale-while-revalidate the section always had.
  const catalog = useQuery<BoardgameGame[]>({
    queryKey: boardgamesCatalogKey,
    queryFn: ({ signal }) => fetchCatalog(signal),
    initialData: () => readCached<BoardgameGame[]>(BOARDGAMES_CACHE_KEY),
    initialDataUpdatedAt: 0,
    staleTime: 5 * 60 * 1000,
  });
  const facets = useQuery<BoardgameFacets[]>({
    queryKey: boardgamesFacetsKey,
    queryFn: ({ signal }) => fetchFacets(signal),
    initialData: () => readCached<BoardgameFacets[]>(BOARDGAMES_FACETS_CACHE_KEY),
    initialDataUpdatedAt: 0,
    staleTime: 5 * 60 * 1000,
  });

  const games = catalog.data ?? EMPTY_GAMES;
  const facetRows = facets.data ?? EMPTY_FACETS;
  const expansionMap = useMemo(() => {
    const map: Record<number, BoardgameGame[]> = {};
    for (const g of games) {
      if (g.baseGameId != null) (map[g.baseGameId] ??= []).push(g);
    }
    return map;
  }, [games]);
  const facetsById = useMemo(() => facetsMap(facetRows), [facetRows]);

  const setGames = useCallback((update: (prev: BoardgameGame[]) => BoardgameGame[]) => {
    client.setQueryData<BoardgameGame[]>(boardgamesCatalogKey, (prev) => update(prev ?? []));
  }, [client]);
  const refresh = useCallback(() => {
    void client.refetchQueries({ queryKey: boardgamesCatalogKey });
    void client.refetchQueries({ queryKey: boardgamesFacetsKey });
  }, [client]);

  return {
    games,
    expansionMap,
    facetsById,
    loading: catalog.data == null && catalog.isPending,
    error: catalog.data == null && catalog.isError,
    refresh,
    setGames,
    version: `${catalog.dataUpdatedAt}:${games.length}:${facets.dataUpdatedAt}:${facetRows.length}`,
  };
}
