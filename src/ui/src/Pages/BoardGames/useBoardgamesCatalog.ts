/**
 * The Boardgames catalog as ONE shared resource (R9 S2c): the whole `/odata/Boardgames` list and
 * the `/API/Boardgames/Facets` rows, read by the page AND the sider rail (two trees) through
 * `useSharedCachedResource` — one fetch, one in-memory copy, one `setData` for the modal's edits.
 * That hook is `useCachedResource`'s contract for two trees, so the section keeps its
 * render-from-cache-then-refresh behaviour on the same `boardgames_v1` / `boardgames_facets_v1`
 * localStorage keys it always used.
 */
import { useCallback, useMemo } from "react";
import type { BoardgameFacets, BoardgameRow } from "../../catalog/sources/boardgamesSource";
import { facetsMap } from "../../catalog/sources/boardgamesSource";
import useSharedCachedResource from "../../hooks/useSharedCachedResource";

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

/** A cached seed is only usable when it is still a list (an older shape reads as a cold cache). */
const asArray = <T,>(raw: unknown): T[] | undefined => (Array.isArray(raw) ? (raw as T[]) : undefined);

async function fetchCatalog(signal?: AbortSignal): Promise<BoardgameGame[]> {
  const r = await fetch(CATALOG_URL, { signal });
  if (!r.ok) throw new Error(`boardgames → ${r.status}`);
  return extractGames(await r.json());
}

async function fetchFacets(signal?: AbortSignal): Promise<BoardgameFacets[]> {
  const r = await fetch("/API/Boardgames/Facets", { signal });
  if (!r.ok) throw new Error(`boardgame facets → ${r.status}`);
  const d = (await r.json()) as { items?: BoardgameFacets[] };
  return Array.isArray(d?.items) ? d.items : [];
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
  const catalog = useSharedCachedResource<BoardgameGame[]>({
    queryKey: boardgamesCatalogKey,
    storageKey: BOARDGAMES_CACHE_KEY,
    fetcher: fetchCatalog,
    parse: asArray,
  });
  const facets = useSharedCachedResource<BoardgameFacets[]>({
    queryKey: boardgamesFacetsKey,
    storageKey: BOARDGAMES_FACETS_CACHE_KEY,
    fetcher: fetchFacets,
    parse: asArray,
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

  const catalogSetData = catalog.setData;
  const setGames = useCallback((update: (prev: BoardgameGame[]) => BoardgameGame[]) => {
    catalogSetData((prev) => update(prev ?? []));
  }, [catalogSetData]);
  const catalogRefresh = catalog.refresh;
  const facetsRefresh = facets.refresh;
  const refresh = useCallback(() => { catalogRefresh(); facetsRefresh(); }, [catalogRefresh, facetsRefresh]);

  return {
    games,
    expansionMap,
    facetsById,
    loading: catalog.loading,
    error: catalog.error,
    refresh,
    setGames,
    version: `${catalog.version}:${games.length}:${facets.version}:${facetRows.length}`,
  };
}
