/**
 * One shelf of the Music catalog as ONE shared resource (R9 S2c): the shelf's albums + artists,
 * read by the page AND the sider rail (two trees) through React Query — one fetch, one in-memory
 * copy. Keeps the section's stale-while-revalidate contract (the boardgames pattern): the last
 * payload seeds the query from localStorage (the same `music.catalog.v1:<shelf>` key
 * `useCachedResource` wrote), a fresh fetch replaces it in the background and rewrites the cache,
 * and a failed refresh over a warm cache keeps the stale copy. Fetched PER SHELF on purpose
 * (music-plan.md §2.6): the excluded material never enters the browse catalog.
 */
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { useCallback, useMemo } from "react";
import { useLocation } from "react-router-dom";
import type { FacetState } from "../../catalog/rail/facetSpec";
import type { MusicAlbumRow, MusicArtistRow } from "../../catalog/sources/musicSource";
import { MovieAPI } from "../../MovieAPI";
import { readStored, writeStored } from "../../utils/storage";
import { applyMusicFacets, kindFromSearch, musicFacetSpec, musicItemsMode, shelfOf, type MusicKind, type MusicResults } from "./musicFacetSpec";

export const MUSIC_ENTITY_PARAMS = ["album"] as const;

export interface MusicShelfData {
  albums: MusicAlbumRow[];
  artists: MusicArtistRow[];
}

export const musicShelfKey = (kind: MusicKind) => ["music", "shelf", kind || "music"] as const;
const cacheKeyFor = (kind: MusicKind) => `music.catalog.v1:${kind || "music"}`;

function readCached(kind: MusicKind): MusicShelfData | undefined {
  const raw = readStored(cacheKeyFor(kind));
  if (raw == null) return undefined;
  try {
    const v = JSON.parse(raw) as Partial<MusicShelfData> | null;
    return v && Array.isArray(v.albums) && Array.isArray(v.artists) ? { albums: v.albums, artists: v.artists } : undefined;
  } catch { return undefined; }
}

async function fetchShelf(kind: MusicKind): Promise<MusicShelfData> {
  const [albumsRes, artistsRes] = await Promise.all([MovieAPI.getMusicAlbums(kind), MovieAPI.getMusicArtists(kind)]);
  if (!albumsRes.ok || !artistsRes.ok) throw new Error(`music shelf → ${albumsRes.status}/${artistsRes.status}`);
  const [albumData, artistData] = await Promise.all([albumsRes.json(), artistsRes.json()]);
  const data: MusicShelfData = { albums: albumData?.items ?? [], artists: artistData ?? [] };
  try { writeStored(cacheKeyFor(kind), JSON.stringify(data)); } catch { /* payload too big — render-only */ }
  return data;
}

export interface MusicShelf extends MusicShelfData {
  kind: MusicKind;
  /** Cold cache with the fetch in flight. */
  loading: boolean;
  /** The fetch failed AND there is nothing cached to show. */
  error: boolean;
  refresh: () => void;
  /** Changes whenever the rows do — the facet spec's identity rides it. */
  version: string;
}

const EMPTY_ALBUMS: MusicAlbumRow[] = [];
const EMPTY_ARTISTS: MusicArtistRow[] = [];

export function useMusicShelf(kind: MusicKind, enabled = true): MusicShelf {
  const client = useQueryClient();
  const shelf = useQuery<MusicShelfData>({
    queryKey: musicShelfKey(kind),
    queryFn: () => fetchShelf(kind),
    initialData: () => readCached(kind),
    initialDataUpdatedAt: 0,
    staleTime: 5 * 60 * 1000,
    enabled,
  });
  const refresh = useCallback(() => { void client.refetchQueries({ queryKey: musicShelfKey(kind) }); }, [client, kind]);
  return {
    kind,
    albums: shelf.data?.albums ?? EMPTY_ALBUMS,
    artists: shelf.data?.artists ?? EMPTY_ARTISTS,
    loading: enabled && shelf.data == null && shelf.isPending,
    error: shelf.data == null && shelf.isError,
    refresh,
    version: `${shelf.dataUpdatedAt}:${shelf.data?.albums.length ?? 0}:${shelf.data?.artists.length ?? 0}`,
  };
}

export interface MusicBrowse extends MusicShelf {
  spec: ReturnType<typeof musicFacetSpec>;
  /** "groups" = one per artist (the artist grid), "items" = every album. */
  itemsMode: "groups" | "items";
}

/**
 * What the Music rail surfaces share: the shelf the URL names, its rows, and the spec over them
 * (the count's noun follows the Items mode — "artists" on the one-per-artist grid).
 */
export default function useMusicBrowse(viewer: { hasPassword?: boolean | null } | null | undefined): MusicBrowse {
  const location = useLocation();
  const kind = kindFromSearch(location.search);
  const itemsMode = musicItemsMode(location.search);
  const shelf = useMusicShelf(kind, !!viewer?.hasPassword);
  const noun = itemsMode === "groups" ? shelfOf(kind).noun.artists.toLowerCase() : shelfOf(kind).noun.albums.toLowerCase();
  const spec = useMemo(() => musicFacetSpec(`${kind || "music"}:${shelf.version}:${itemsMode}`, shelf.albums, shelf.artists, noun), [kind, shelf.version, shelf.albums, shelf.artists, itemsMode, noun]);
  return { ...shelf, spec, itemsMode };
}

/** The albums a state keeps and the artists the one-per-artist grid shows — the page's lists, the rail's count. */
export function useMusicResults(shelf: Pick<MusicShelfData, "albums" | "artists">, state: FacetState): MusicResults {
  return useMemo(() => applyMusicFacets(shelf.albums, shelf.artists, state), [shelf.albums, shelf.artists, state]);
}
