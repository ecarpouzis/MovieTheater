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
 * | `genres` | the shelf rows' merged `genres` — GROUP cards, routed by `f=genre:<name>` |
 * | `popular` | the cached shelf, by `popularity` — how widely heard (Last.fm listeners) |
 * | `best` | the cached shelf, by `rating` — a verdict; EMPTY until a rating source answers |
 * | `most-played` | the cached shelf, by library-wide play count (R9 closing pass) |
 * | `recently-played` | the cached shelf, by when anyone here last put the record on |
 * | `ways-in` | the cached shelf cut by genre / decade / artist — DOORS, each fanned with its best-regarded covers |
 * | `popular` | now the RANKED ten (how widely heard) |
 * | `artist` | one artist with four or more records, chosen by the seed — a FOCUS plate over their albums |
 * | `quick` | three columns: best regarded, most played here, deep cuts (well regarded, little heard) |
 *
 * One honesty note, stated because it is a judgement call: **Music has no "added" stamp.**
 * `MusicAlbum` carries `Year` and nothing else about when it landed, so "Just added" orders by
 * descending id — the identity column IS the ingest order — and the rail is labelled for what that
 * actually means rather than claiming a date the data does not have.
 */
import { exploreColumn, exploreColumns, exploreDoors, exploreFocus, exploreRail, exploreResponse, distinctCovers, seededItem, seededShuffle } from "../../catalog/explore/composeExplore";
import type { HeroDetail } from "../../catalog/explore/HeroSpotlight";
import { facetHref } from "../../catalog/rail/facetUrl";
import type { CardItem, ExploreDoor, ExploreResponse } from "../../catalog/types";
import { toAlbumCard, type MusicAlbumRow, type MusicArtistRow } from "../../catalog/sources/musicSource";

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
  /** Which "Ways in" tab is showing (the doors are computed from the shelf, so every tab is free). */
  doorAxis?: string;
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
const GENRES_TAKE = 18;
// A genre one record claims is a typo or a micro-tag, not a way into the collection.
const GENRE_MIN = 4;

export const MUSIC_MORE = {
  artists: "/music?items=groups",
  favourites: "/music/playlists",
  random: "/music",
  // The rail's "more" IS the browse under the same order, so the rail is a window onto a real view
  // rather than a hand-picked list that ends where it ends.
  popular: "/music?items=items&sort=popular",
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
 * The best-REGARDED records on the shelf: a verdict, from `rating`, the same number the browse's
 * "Top rated" order uses, computed once on the server so this rail and that view cannot disagree.
 *
 * Albums with no score at all are DROPPED rather than sorted last — a rail titled "Best on the
 * shelf" whose tail is a run of records nobody has an opinion about is a lie about its own contents,
 * and an empty rail is dropped by `exploreRail` anyway, which is the honest empty state before a
 * rating source has answered.
 */
export function bestAlbums(albums: readonly MusicAlbumRow[], take = BEST_TAKE): MusicAlbumRow[] {
  return albums
    .filter((a) => typeof a.rating === "number")
    .slice()
    .sort((a, b) => (b.rating ?? 0) - (a.rating ?? 0) || (b.ratingCount ?? 0) - (a.ratingCount ?? 0))
    .slice(0, take);
}

/**
 * The most widely HEARD records on the shelf — a different question, and kept a different rail
 * (2026-08-31).
 *
 * These two were one rail called "Best on the shelf" reading a server-side blend of the house's own
 * ratings with the popularity signal. With no house ratings — and, per Eric, no realistic prospect
 * of enough listeners with overlapping taste to ever produce them — that blend WAS the popularity
 * number, so the rail was ranking records by fame under a name that promised quality. Popularity is
 * worth a rail; it just has to be its own, wearing its own name.
 */
export function popularAlbums(albums: readonly MusicAlbumRow[], take = BEST_TAKE): MusicAlbumRow[] {
  return albums
    .filter((a) => typeof a.popularity === "number")
    .slice()
    .sort((a, b) => (b.popularity ?? 0) - (a.popularity ?? 0) || Number(a.id) - Number(b.id))
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

export interface GenreShelf {
  /** The spelling to show — the one the library uses most, not whichever source answered first. */
  name: string;
  count: number;
  face?: MusicAlbumRow;
}

/**
 * The genres worth a way in, with a record to wear as the card's face (R9 S10 follow-up).
 *
 * The shelf rows already carry `genres` merged across all three sources, so this asks the API for
 * nothing new — it is the same "one fetch, everything the browse can ask about" rule the rest of
 * this file runs on.
 *
 * Two judgement calls, both about not lying:
 *  * **Folded on case AND on the hyphen.** The file tags say "indie rock", Last.fm says "Indie
 *    Rock", and two cards for one genre would split the shelf in half. The hyphen matters just as
 *    much: measured on this library, "Hip-Hop" (185 albums) and "Hip Hop" (168) were two separate
 *    cards for one genre, and so were Post-Rock and Post Rock. The label shown is the spelling the
 *    library uses MOST, so the pill reads the way the collection does rather than the way whichever
 *    source happened to answer first. `&` is deliberately left alone — folding it would merge
 *    "R&B" into anything else beginning with an R.
 *  * **A floor of `GENRE_MIN` albums.** A genre one record claims is a typo or a micro-tag, not a
 *    way into the collection, and a rail of them is a worse answer than a shorter rail.
 */
const genreKey = (name: string) => name.toLowerCase().replace(/[-_]+/g, " ").replace(/\s+/g, " ").trim();
export function genreShelves(albums: readonly MusicAlbumRow[], take = GENRES_TAKE): GenreShelf[] {
  const byKey = new Map<string, { count: number; spellings: Map<string, number>; face?: MusicAlbumRow }>();
  for (const album of albums) {
    for (const raw of album.genres ?? []) {
      const name = (raw ?? "").trim();
      if (!name) continue;
      const key = genreKey(name);
      const hit = byKey.get(key) ?? { count: 0, spellings: new Map<string, number>(), face: undefined };
      hit.count += 1;
      hit.spellings.set(name, (hit.spellings.get(name) ?? 0) + 1);
      // The best-regarded record carries the genre, and only one WITH art can — a face-less card
      // falls back to a placeholder and says nothing about what the genre sounds like.
      const better = (album.rating ?? -1) > (hit.face?.rating ?? -1);
      if (album.hasArt && (!hit.face || better)) hit.face = album;
      byKey.set(key, hit);
    }
  }
  return [...byKey.values()]
    .filter((v) => v.count >= GENRE_MIN)
    .map((v) => ({
      name: [...v.spellings.entries()].sort((a, b) => b[1] - a[1] || a[0].localeCompare(b[0]))[0][0],
      count: v.count,
      face: v.face,
    }))
    .sort((a, b) => b.count - a.count || a.name.localeCompare(b.name))
    .slice(0, take);
}

/** `/music?f=genre:Post-Rock` — the genre facet takes the name, and URLSearchParams encodes it. */
export function musicGenreHref(genre: string): string {
  return facetHref("/music", [["genre", genre]]);
}

/** Artists with the most on the shelf first — the ones a rail of eighteen should actually contain. */
export function topArtists(artists: readonly MusicArtistRow[], take = ARTISTS_TAKE): MusicArtistRow[] {
  return artists.slice()
    .sort((a, b) => (b.albumCount ?? 0) - (a.albumCount ?? 0) || (b.trackCount ?? 0) - (a.trackCount ?? 0))
    .slice(0, take);
}

// ── Doors, focus, columns ──────────────────────────────────────────────────────────────────────

export const MUSIC_DOOR_AXES = [
  { key: "genre", label: "Genre" },
  { key: "decade", label: "Decade" },
  { key: "artist", label: "Artist" },
] as const;
const DOORS_TAKE = 18;
/** An artist worth a focus plate has at least this many records here. */
export const FOCUS_ARTIST_MIN = 4;

/** The best-regarded records WITH art first — a door's fan should show what the shelf sounds like. */
function faceOrder(a: MusicAlbumRow, b: MusicAlbumRow): number {
  return Number(!!b.hasArt) - Number(!!a.hasArt) || (b.rating ?? -1) - (a.rating ?? -1) || (b.popularity ?? -1) - (a.popularity ?? -1);
}

function coversOf(rows: readonly MusicAlbumRow[]): ExploreDoor["covers"] {
  return rows.slice().sort(faceOrder).slice(0, 12).map(toAlbumCard).map((c) => ({ src: c.imageThumbUrl ?? c.imageUrl, hue: c.hue }));
}

/** Doors with their covers made distinct across the axis (see `distinctCovers`). */
function spreadCovers(doors: ExploreDoor[]): ExploreDoor[] {
  const covers = distinctCovers(doors, (d) => d.covers);
  return doors.map((d, i) => ({ ...d, covers: covers[i] }));
}

/** The shelf cut along one axis, as doors. Genres fold their spellings the way `genreShelves` does. */
export function musicDoors(axis: string, albums: readonly MusicAlbumRow[], artists: readonly MusicArtistRow[]): ExploreDoor[] {
  return spreadCovers(rawMusicDoors(axis, albums, artists));
}

function rawMusicDoors(axis: string, albums: readonly MusicAlbumRow[], artists: readonly MusicArtistRow[]): ExploreDoor[] {
  if (axis === "artist") {
    const byArtist = new Map<number, MusicAlbumRow[]>();
    for (const a of albums) if (a.artistId != null) (byArtist.get(a.artistId) ?? byArtist.set(a.artistId, []).get(a.artistId)!).push(a);
    return topArtists(artists, DOORS_TAKE).filter((a) => (a.albumCount ?? 0) > 1).map((a) => ({
      key: String(a.id), label: a.name, count: a.albumCount, href: musicArtistHref(a.id), covers: coversOf(byArtist.get(a.id) ?? []),
    }));
  }
  if (axis === "decade") {
    const byDecade = new Map<number, MusicAlbumRow[]>();
    for (const a of albums) if (a.year && a.year > 1900) {
      const d = Math.floor(a.year / 10) * 10;
      (byDecade.get(d) ?? byDecade.set(d, []).get(d)!).push(a);
    }
    return [...byDecade.entries()].filter(([, rows]) => rows.length >= GENRE_MIN).sort((x, y) => x[0] - y[0])
      .map(([d, rows]) => ({ key: String(d), label: `${d}s`, count: rows.length, href: `/music?y=${d}-${d + 9}`, covers: coversOf(rows) }));
  }
  const shelves = genreShelves(albums, DOORS_TAKE);
  const members = new Map<string, MusicAlbumRow[]>();
  for (const a of albums) for (const g of a.genres ?? []) {
    const k = genreKey((g ?? "").trim());
    if (k) (members.get(k) ?? members.set(k, []).get(k)!).push(a);
  }
  return shelves.map((g) => ({ key: g.name, label: g.name, count: g.count, href: musicGenreHref(g.name), covers: coversOf(members.get(genreKey(g.name)) ?? []) }));
}

/** The artist the focus plate looks at: one with a real run of records here, chosen by the seed. */
export function pickFocusArtist(artists: readonly MusicArtistRow[], seed: number): MusicArtistRow | null {
  const list = artists.filter((a) => (a.albumCount ?? 0) >= FOCUS_ARTIST_MIN).sort((a, b) => a.id - b.id);
  return seededItem(list, seed, 5) ?? null;
}

/**
 * Deep cuts: records rated well that few people have heard — the column that most rewards a browse.
 * Of the albums rated 70 or better, the least-heard third (relative, because what counts as "obscure"
 * depends on the shelf: a fixed popularity cut-off left this one empty).
 */
export function deepCuts(albums: readonly MusicAlbumRow[]): MusicAlbumRow[] {
  const rated = albums.filter((a) => (a.rating ?? 0) >= 70 && a.popularity != null)
    .sort((a, b) => (a.popularity ?? 0) - (b.popularity ?? 0) || a.id - b.id);
  return rated.slice(0, Math.max(6, Math.ceil(rated.length / 3)));
}

/** An album's own row as the marquee's lines: artist, year, genres (no blurb exists for a record). */
export function albumHeroDetail(item: CardItem): HeroDetail | null {
  const a = (item.raw ?? null) as MusicAlbumRow | null;
  if (!a || typeof a !== "object") return null;
  const meta = [a.year ? String(a.year) : null, a.tag || null, a.playCount ? `${a.playCount} plays here` : null].filter((x): x is string => !!x);
  return { meta, tags: (a.genres ?? []).slice(0, 4), subtitle: a.artistName };
}

export function composeMusicExplore(input: MusicExploreInput): ExploreResponse {
  const albums = input.albums ?? [];
  const artists = input.artists ?? [];
  const seed = input.seed ?? 1;
  // The marquee wants records that LOOK like something: art first, then the seed's shuffle.
  const shuffled = seededPick(albums, seed, RANDOM_TAKE);
  const withArt = shuffled.filter((a) => a.hasArt);
  const spotlight = (withArt.length >= MUSIC_SPOTLIGHT_SIZE ? withArt : shuffled).slice(0, MUSIC_SPOTLIGHT_SIZE);
  const spotIds = new Set(spotlight.map((a) => a.id));
  // Descending id = the order music-ingest wrote them; see the note at the top of the file.
  const added = albums.slice().sort((a, b) => Number(b.id) - Number(a.id)).slice(0, ADDED_TAKE);
  const focus = pickFocusArtist(artists, seed);
  const focusAlbums = focus ? albums.filter((a) => a.artistId === focus.id).sort((a, b) => (a.year ?? 9999) - (b.year ?? 9999)) : [];
  const axis = input.doorAxis ?? "genre";
  const doorAxes = MUSIC_DOOR_AXES.map((a) => ({ key: a.key, label: a.label, doors: musicDoors(a.key, albums, artists) }));

  return exploreResponse(spotlight.map(toAlbumCard), [
    exploreRail("recently-played", "Recently played", "strip", recentlyPlayedAlbums(albums).map(toAlbumCard)),
    exploreRail("favourites", "Your favourites", "strip", favouriteAlbums(albums, input.playlists).map(toAlbumCard), MUSIC_MORE.favourites),
    albums.length ? exploreDoors("ways-in", "Ways in", doorAxes, axis) : null,
    exploreRail("just-added", "Latest on the shelf", "wall", added.map(toAlbumCard)),
    exploreRail("popular", "The ten most widely heard", "ranked", popularAlbums(albums, 10).map(toAlbumCard), MUSIC_MORE.popular),
    focus && focusAlbums.length
      ? exploreFocus("artist", "A closer look", { name: focus.name, count: focus.albumCount, blurb: focus.yearRange ? `Records from ${focus.yearRange}, oldest first.` : "Their records, oldest first.", href: musicArtistHref(focus.id) }, focusAlbums.map(toAlbumCard))
      : null,
    exploreColumns("quick", "Quick picks", [
      exploreColumn("best", "Best regarded", seededShuffle(bestAlbums(albums, 30), seed + 3).slice(0, 6).map(toAlbumCard), MUSIC_MORE.best),
      exploreColumn("played", "Most played here", mostPlayedAlbums(albums, 6).map(toAlbumCard), MUSIC_MORE.played),
      exploreColumn("deep", "Deep cuts", seededShuffle(deepCuts(albums), seed + 7).slice(0, 6).map(toAlbumCard)),
    ]),
    exploreRail("random", "Reach for something", "grid", shuffled.filter((a) => !spotIds.has(a.id)).map(toAlbumCard), MUSIC_MORE.random),
  ], input.seed);
}

/** Rails whose point is that they are CURRENT (or fixed) — shuffling them would be a lie. */
export const MUSIC_UNSEEDED_RAILS: ReadonlySet<string> = new Set(["favourites", "just-added", "popular", "recently-played", "ways-in"]);
