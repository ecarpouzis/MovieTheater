/**
 * The Music Explore composition (R9 S7) — assembled in the browser out of the shelf the section
 * ALREADY holds (`useMusicShelf`'s React-Query copy of `/API/Music/Albums` + `/API/Music/Artists`)
 * plus one small read the playlists page already makes. Nothing new is asked of the API.
 *
 * | Rail | Where it comes from |
 * |---|---|
 * | spotlight + `random` | the cached shelf, deterministically shuffled by the URL's seed |
 * | `just-added` | the cached shelf, by descending id |
 * | `favourites` | `/API/Music/Playlist/Mine` — the Favorites list's album ids, resolved in the shelf |
 * | `artists` | the cached shelf's artists — GROUP cards, routed by `f=artist:<id>` |
 * | `best` | the cached shelf, by the blended 0–100 the browse's Top-rated order uses (R9 S10) |
 * | `most-played` | the cached shelf, by library-wide play count (R9 closing pass) |
 * | `recently-played` | the cached shelf, by when anyone here last put the record on |
 *
 * One honesty note, stated because it is a judgement call: **Music has no "added" stamp.**
 * `MusicAlbum` carries `Year` and nothing else about when it landed, so "Just added" orders by
 * descending id — the identity column IS the ingest order — and the rail is labelled for what that
 * actually means rather than claiming a date the data does not have.
 */
import { exploreRail, exploreResponse } from "../../catalog/explore/composeExplore";
import { facetHref } from "../../catalog/rail/facetUrl";
import type { CardItem, ExploreResponse } from "../../catalog/types";
import { toAlbumCard, toArtistCard, type MusicAlbumRow, type MusicArtistRow } from "../../catalog/sources/musicSource";

export interface MusicPlaylistRow {
  id: number;
  name: string;
  count?: number;
  isFavorites?: boolean;
  albumIds?: number[];
}

export interface MusicExploreInput {
  albums?: MusicAlbumRow[];
  artists?: MusicArtistRow[];
  playlists?: MusicPlaylistRow[];
  seed?: number;
}

export const MUSIC_SPOTLIGHT_SIZE = 5;
const RANDOM_TAKE = 30;
const ADDED_TAKE = 24;
const BEST_TAKE = 18;
const ARTISTS_TAKE = 18;
const FAVOURITES_TAKE = 12;
const PLAYED_TAKE = 18;
const RECENT_TAKE = 12;

export const MUSIC_MORE = {
  artists: "/music?items=groups",
  favourites: "/music/playlists",
  random: "/music",
  // The rail's "more" IS the browse under the same order, so the rail is a window onto a real view
  // rather than a hand-picked list that ends where it ends.
  best: "/music?items=items&sort=rated",
  played: "/music?items=items&sort=played",
};

/** `/music?f=artist:412` — the Music rail's artist facet is numeric, so the id rides straight in. */
export function musicArtistHref(artistId: number | string): string {
  return facetHref("/music", [["artist", artistId]]);
}

/**
 * A deterministic shuffle: the same seed always produces the same order, so Back walks the rolls
 * and a re-render does not reshuffle the page under the reader. (`Math.random` here would reorder
 * the hero on every keystroke elsewhere in the app.)
 */
export function seededPick<T>(rows: readonly T[], seed: number, take: number): T[] {
  const n = rows.length;
  if (n === 0) return [];
  const list = rows.slice();
  let s = (seed || 1) >>> 0;
  for (let i = n - 1; i > 0; i -= 1) {
    s = (s * 1664525 + 1013904223) >>> 0;
    const j = s % (i + 1);
    [list[i], list[j]] = [list[j], list[i]];
  }
  return list.slice(0, take);
}

/** The Favorites playlist's albums, in the order they were hearted, resolved against the shelf. */
export function favouriteAlbums(albums: readonly MusicAlbumRow[], playlists: readonly MusicPlaylistRow[] | undefined): MusicAlbumRow[] {
  const favs = (playlists ?? []).find((p) => p.isFavorites);
  if (!favs?.albumIds?.length) return [];
  const byId = new Map(albums.map((a) => [Number(a.id), a]));
  const out: MusicAlbumRow[] = [];
  for (const id of favs.albumIds) {
    const hit = byId.get(Number(id));
    if (hit) out.push(hit);
    if (out.length >= FAVOURITES_TAKE) break;
  }
  return out;
}

/**
 * The best-regarded records on the shelf (R9 S10): the SAME blended 0–100 the browse's "Top rated"
 * order uses, computed once on the server so this rail and that view cannot disagree.
 *
 * Albums with no score at all are DROPPED rather than sorted last — a rail titled "Best on the
 * shelf" whose tail is a run of records nobody has an opinion about is a lie about its own contents,
 * and an empty rail is dropped by `exploreRail` anyway, which is the honest empty state before the
 * enrich pass has run.
 */
export function bestAlbums(albums: readonly MusicAlbumRow[], take = BEST_TAKE): MusicAlbumRow[] {
  return albums
    .filter((a) => typeof a.rating === "number")
    .slice()
    .sort((a, b) => (b.rating ?? 0) - (a.rating ?? 0) || (b.ratingCount ?? 0) - (a.ratingCount ?? 0))
    .slice(0, take);
}

/**
 * The records this house actually plays (R9 closing pass) — library-wide counts, summed across every
 * listener, the same number the browse's "Most played" order reads.
 *
 * Albums nobody has played are DROPPED, not sorted last: before anyone has listened with the beacon
 * live, EVERY album has zero plays, and a rail titled "Most played" full of records nobody has ever
 * put on would be a lie about its own contents. Dropping them makes the rail empty instead, and
 * `exploreRail` drops an empty rail — so this rail simply does not appear until there is something
 * true to say. That is the honest empty state, and it fills in on its own.
 */
export function mostPlayedAlbums(albums: readonly MusicAlbumRow[], take = PLAYED_TAKE): MusicAlbumRow[] {
  return albums
    .filter((a) => (a.playCount ?? 0) > 0)
    .slice()
    .sort((a, b) => (b.playCount ?? 0) - (a.playCount ?? 0) || Number(a.id) - Number(b.id))
    .slice(0, take);
}

/**
 * What went on most recently — free from the same rows, because the play table keeps a last-played
 * stamp beside the count. Same empty-state rule: never played = not in the rail.
 */
export function recentlyPlayedAlbums(albums: readonly MusicAlbumRow[], take = RECENT_TAKE): MusicAlbumRow[] {
  const at = (a: MusicAlbumRow) => (a.lastPlayedUtc ? Date.parse(a.lastPlayedUtc) : NaN);
  return albums
    .filter((a) => Number.isFinite(at(a)))
    .slice()
    .sort((a, b) => at(b) - at(a) || Number(b.id) - Number(a.id))
    .slice(0, take);
}

/** Artists with the most on the shelf first — the ones a rail of eighteen should actually contain. */
export function topArtists(artists: readonly MusicArtistRow[], take = ARTISTS_TAKE): MusicArtistRow[] {
  return artists.slice()
    .sort((a, b) => (b.albumCount ?? 0) - (a.albumCount ?? 0) || (b.trackCount ?? 0) - (a.trackCount ?? 0))
    .slice(0, take);
}

/** An artist card IS a group card here — `groupKey` is the id the `f=artist:` facet takes. */
function toArtistGroupCard(a: MusicArtistRow): CardItem {
  const card = toArtistCard(a);
  return { ...card, groupKey: String(a.id) };
}

export function composeMusicExplore(input: MusicExploreInput): ExploreResponse {
  const albums = input.albums ?? [];
  const shuffled = seededPick(albums, input.seed ?? 1, RANDOM_TAKE);
  const spotlight = shuffled.slice(0, MUSIC_SPOTLIGHT_SIZE).map(toAlbumCard);
  // Descending id = the order music-ingest wrote them; see the note at the top of the file.
  const added = albums.slice().sort((a, b) => Number(b.id) - Number(a.id)).slice(0, ADDED_TAKE);

  return exploreResponse(spotlight, [
    exploreRail("favourites", "Your favourites", "strip", favouriteAlbums(albums, input.playlists).map(toAlbumCard), MUSIC_MORE.favourites),
    exploreRail("just-added", "Latest on the shelf", "wall", added.map(toAlbumCard)),
    exploreRail("best", "Best on the shelf", "strip", bestAlbums(albums).map(toAlbumCard), MUSIC_MORE.best),
    exploreRail("recently-played", "Recently played", "strip", recentlyPlayedAlbums(albums).map(toAlbumCard)),
    exploreRail("most-played", "Most played", "strip", mostPlayedAlbums(albums).map(toAlbumCard), MUSIC_MORE.played),
    exploreRail("artists", "Artists to sit with", "strip", topArtists(input.artists ?? []).map(toArtistGroupCard), MUSIC_MORE.artists),
    exploreRail("random", "Reach for something", "grid", shuffled.slice(MUSIC_SPOTLIGHT_SIZE).map(toAlbumCard), MUSIC_MORE.random),
  ], input.seed);
}

/** Rails whose point is that they are CURRENT — shuffling them would be a lie. */
export const MUSIC_UNSEEDED_RAILS: ReadonlySet<string> = new Set(["favourites", "just-added", "artists", "best", "most-played", "recently-played"]);
