/**
 * Music → `CatalogSource`, one per tab. The page holds the shelf's whole catalog client-side (artists
 * + albums, cached per shelf) and filters it by `?q=`; this adapter maps either list onto cards and
 * rides the in-memory client source. Albums group by artist / decade / year / kind / quality tag
 * (R9 S8), and the Directory walks artists → albums (the existing `?artist=` drill opens an artist's
 * own page); artists group by the decade they became active.
 *
 * The catalog owns the sort here (the page has no sort control of its own); since R9 S3 the page's
 * grid IS the package's Grid, so there is nothing left to keep in step by hand.
 */
import { MovieAPI } from "../../MovieAPI";
import { hueSvg } from "../cards/CardImage";
import type { CardGroup, CardItem, CatalogSource, ListColumn } from "../types";
import { cardKey } from "../types";
import { ALL_VIEWS as ALL_MUSIC_VIEWS, createClientSource, type ClientGrouper, type ClientSort, type GroupKey } from "./clientSource";
import { hueOf } from "./hue";

/** One row of `/API/Music/Albums` `items`. */
export interface MusicAlbumRow {
  id: number;
  title: string;
  year?: number | null;
  tag?: string | null;
  artistId?: number | null;
  artistName: string;
  artistSortName?: string | null;
  artistKind?: string | null;
  hasArt?: boolean;
  dominantColor?: string | null;
  /** Genres, strongest first, MERGED across sources by the server (R9 S10) — the file's own tags
   *  first, then MusicBrainz/Last.fm. Absent on a shelf fetched before that leg ran. */
  genres?: string[];
  /** 0–100 external audience signal (Last.fm listeners) — how KNOWN the record is, not how good. */
  popularity?: number | null;
  /** The one blended 0–100 the "Top rated" order and the rail's rating floor read. Null = nothing
   *  known about this record, and the sort files those last rather than inventing a middle. */
  rating?: number | null;
  /** The viewer's own score, or null when they have not rated it (0 is a real score). */
  myRating?: number | null;
  ratingAvg?: number | null;
  ratingCount?: number;
}

/** One row of `/API/Music/Artists`. */
export interface MusicArtistRow {
  id: number;
  name: string;
  sortName?: string | null;
  yearRange?: string | null;
  albumCount?: number;
  trackCount?: number;
  artAlbumId?: number | null;
  hasArt?: boolean;
  dominantColor?: string | null;
  /** The artist's top three, rolled up from their albums (R9 S10). */
  genres?: string[];
  /** The best blended score among the artist's albums — an artist has no score of their own, so the
   *  Top-rated order over the "one per artist" grid means "who has the best-regarded record here". */
  topRating?: number | null;
}

export const MUSIC_KIND_LABELS: Record<string, string> = { "": "Music", comedy: "Comedy", audiobook: "Audiobooks" };

/** The Grid's base tile size before the cover-size tweak (the grids' old `minmax(150px, 1fr)`). */
export const MUSIC_GRID_CELL = 150;
/** The section's own Grid wraps — the same class names its tiles have always been laid out by. */
export const MUSIC_ALBUM_GRID_CLASS = "music-album-grid";
export const MUSIC_ARTIST_GRID_CLASS = "music-artist-grid";

/** Hue (0–359) of a `#rrggbb` colour, or null when it is not one. */
export function hexToHue(hex: string | null | undefined): number | null {
  const m = /^#?([0-9a-f]{6})$/i.exec((hex ?? "").trim());
  if (!m) return null;
  const n = parseInt(m[1], 16);
  const r = ((n >> 16) & 255) / 255;
  const g = ((n >> 8) & 255) / 255;
  const b = (n & 255) / 255;
  const max = Math.max(r, g, b);
  const min = Math.min(r, g, b);
  const d = max - min;
  if (d === 0) return 0;
  let h = max === r ? ((g - b) / d) % 6 : max === g ? (b - r) / d + 2 : (r - g) / d + 4;
  h = Math.round(h * 60);
  return h < 0 ? h + 360 : h;
}

function art(albumId: number | null | undefined, hasArt: boolean | undefined, hue: number): { imageUrl: string; imageThumbUrl?: string } {
  if (albumId && hasArt) return { imageUrl: MovieAPI.getMusicAlbumArt(albumId, true), imageThumbUrl: MovieAPI.getMusicAlbumArtThumb(albumId, true) };
  // No art on the mount: the tinted tile straight away, never a 404 retried three times.
  return { imageUrl: hueSvg(hue, 100, 100) };
}

export function toAlbumCard(a: MusicAlbumRow): CardItem {
  const id = Number(a.id);
  const title = a.title ?? `#${id}`;
  const hue = hexToHue(a.dominantColor) ?? hueOf(title);
  return {
    kind: "album",
    id,
    key: cardKey("album", id),
    title,
    subtitle: a.artistName,
    label: a.year != null ? String(a.year) : undefined,
    year: a.year ?? undefined,
    aspect: 1,
    ...art(id, a.hasArt, hue),
    hue,
    sortKey: `${a.artistSortName ?? a.artistName ?? ""} ${title}`,
    badges: a.tag ? [{ label: a.tag, tone: "system" as const }] : undefined,
    // The package's own 0–100 slot (the Newspaper picks its lead on it) — the SAME blended number
    // the Top-rated order and the rail's floor use, so nothing on the page can disagree about what
    // an album is worth.
    rating: a.rating ?? undefined,
    raw: a,
  };
}

export function toArtistCard(a: MusicArtistRow): CardItem {
  const id = Number(a.id);
  const title = a.name ?? `#${id}`;
  const hue = hexToHue(a.dominantColor) ?? hueOf(title);
  const albums = a.albumCount ?? 0;
  return {
    kind: "artist",
    id,
    key: cardKey("artist", id),
    title,
    subtitle: a.yearRange ?? undefined,
    label: `${albums} album${albums === 1 ? "" : "s"}`,
    aspect: 1,
    ...art(a.artAlbumId, a.hasArt, hue),
    hue,
    sortKey: a.sortName ?? title,
    count: albums,
    raw: a,
  };
}

const albumOf = (i: CardItem) => (i.raw ?? {}) as MusicAlbumRow;
const artistOf = (i: CardItem) => (i.raw ?? {}) as MusicArtistRow;
const collator = new Intl.Collator(undefined, { sensitivity: "base", numeric: true });

export const ALBUM_SORTS: ClientSort[] = [
  { value: "artist", label: "By artist", alpha: true, letterKey: (i) => albumOf(i).artistSortName ?? albumOf(i).artistName ?? "" },
  { value: "title", label: "A–Z title", alpha: true, compare: (a, b) => collator.compare(a.title, b.title), letterKey: (i) => i.title },
  { value: "newest", label: "Newest", compare: (a, b) => (b.year ?? 0) - (a.year ?? 0) || collator.compare(a.title, b.title) },
  { value: "oldest", label: "Oldest", compare: (a, b) => (a.year ?? 9999) - (b.year ?? 9999) || collator.compare(a.title, b.title) },
  // R9 S10. The blend (site ratings shrunk toward the popularity signal, computed server-side) —
  // NOT the raw average, or one enthusiastic 100 would top the shelf over a record five people
  // agreed was excellent. An album nothing is known about has no opinion attached and files LAST,
  // which is why the fallback is -1 rather than 0: a genuine 0 is a real score and outranks silence.
  { value: "rated", label: "Top rated", compare: (a, b) => (ratingOf(b) ?? -1) - (ratingOf(a) ?? -1) || collator.compare(a.title, b.title) },
];

/** The blended 0–100 an album card carries, or null when nothing is known about the record. */
const ratingOf = (i: CardItem): number | null => {
  const r = albumOf(i).rating;
  return typeof r === "number" ? r : null;
};

/** The first year of an artist's "1971 – 1985" range, for the newest/oldest orders. */
const artistYear = (i: CardItem): number | null => {
  const m = /(\d{4})/.exec(artistOf(i).yearRange ?? "");
  return m ? Number(m[1]) : null;
};

/**
 * The SAME sort keys as the albums (one section, one Sort pill — R9 S1b), read against the artist
 * rows the "one per artist" grid shows: the two alphabetical orders file by the artist's sort name,
 * newest/oldest by the first year the artist was active.
 */
export const ARTIST_SORTS: ClientSort[] = [
  { value: "artist", label: "By artist", alpha: true },
  { value: "title", label: "A–Z title", alpha: true },
  { value: "newest", label: "Newest", compare: (a, b) => (artistYear(b) ?? 0) - (artistYear(a) ?? 0) || collator.compare(a.title, b.title) },
  { value: "oldest", label: "Oldest", compare: (a, b) => (artistYear(a) ?? 9999) - (artistYear(b) ?? 9999) || collator.compare(a.title, b.title) },
  // The Sort pill is ONE control for the section (R9 S1b), so every album order has an artist
  // reading. An artist has no blended score of their own — the honest reading is "who has the
  // best-regarded record on the shelf", which is what an artist grid ordered by rating means.
  { value: "rated", label: "Top rated", compare: (a, b) => (artistOf(b).topRating ?? -1) - (artistOf(a).topRating ?? -1) || collator.compare(a.title, b.title) },
];

function decadeOf(year: number | null | undefined): GroupKey | null {
  if (!year) return null;
  const d = Math.floor(year / 10) * 10;
  return { key: String(d), label: `${d}s` };
}

/**
 * The Music axis set (R9 S8). `letter` is GONE — the A–Z strip is the letter axis, and a shelf per
 * letter drew the same index twice. `kind` stays: it is three values, and it is the one axis that
 * says which SHELF a row came off.
 *
 * `tag` is the album's quality/curation mark. On disk it is bracketed (`… [FLAC]`, `… [V0]`) and
 * `MusicNaming.ParseAlbumFolder` strips the brackets on ingest, so the VALUE here is `FLAC` / `V0`
 * (a folder with two brackets becomes the one comma-joined value `"FLAC, EP"`, which is exactly what
 * the rail's Tag facet matches on, so a shelf and its facet describe the same set). The brackets are
 * only wildcards in a T-SQL `LIKE` or a PowerShell path — irrelevant to this string comparison.
 */
export const ALBUM_GROUPERS: ClientGrouper[] = [
  { value: "artist", label: "Artist", keysOf: (i) => ({ key: String(albumOf(i).artistId ?? albumOf(i).artistName), label: albumOf(i).artistName }) },
  { value: "decade", label: "Decade", order: "keyDesc", alpha: false, keysOf: (i) => decadeOf(i.year) },
  { value: "year", label: "Year", order: "keyDesc", alpha: false, keysOf: (i) => (i.year ? { key: String(i.year), label: String(i.year) } : null) },
  // The library's own rows carry no kind (null = music) — they still need a key, or the grouping drops them.
  { value: "kind", label: "Kind", keysOf: (i) => { const k = albumOf(i).artistKind ?? ""; return { key: k || "music", label: MUSIC_KIND_LABELS[k] ?? k }; } },
  { value: "tag", label: "Quality tag", order: "count", alpha: false, keysOf: (i) => { const t = (albumOf(i).tag ?? "").trim(); return t ? { key: t, label: t } : null; } },
  // R9 S10. An album is legitimately several genres at once, so this axis puts a record on EVERY
  // shelf its genres name — the one axis here where the shelves overlap on purpose, which is why it
  // returns an array. Ordered by size (`count`): the long tail of one-album genres belongs at the
  // bottom, not spread alphabetically through the middle. An album with no genre is dropped rather
  // than pooled under "Unknown" — a shelf of things we know nothing about is not a shelf.
  { value: "genre", label: "Genre", order: "count", alpha: false, keysOf: (i) => (albumOf(i).genres ?? []).map((g) => ({ key: g, label: g })) },
];

const firstYearOf = (range: string | null | undefined) => {
  const m = /(\d{4})/.exec(range ?? "");
  return m ? Number(m[1]) : null;
};

export const ARTIST_GROUPERS: ClientGrouper[] = [
  { value: "decade", label: "Active since", order: "keyDesc", alpha: false, keysOf: (i) => decadeOf(firstYearOf(artistOf(i).yearRange)) },
  { value: "genre", label: "Genre", order: "count", alpha: false, keysOf: (i) => (artistOf(i).genres ?? []).map((g) => ({ key: g, label: g })) },
];

export const ALBUM_LIST_COLUMNS: ListColumn[] = [
  { key: "title", label: "Album", width: "2fr", value: (i) => i.title },
  { key: "artist", label: "Artist", width: "1.4fr", value: (i) => albumOf(i).artistName },
  { key: "genre", label: "Genre", width: "1fr", value: (i) => (albumOf(i).genres ?? []).slice(0, 2).join(", ") },
  { key: "year", label: "Year", width: "64px", mono: true, value: (i) => i.year },
  { key: "rating", label: "Rated", width: "64px", mono: true, align: "right", value: (i) => (ratingOf(i) == null ? null : Math.round(ratingOf(i)!)) },
  { key: "tag", label: "Tag", width: "90px", value: (i) => albumOf(i).tag },
];

export const ARTIST_LIST_COLUMNS: ListColumn[] = [
  { key: "name", label: "Artist", width: "2fr", value: (i) => i.title },
  { key: "genre", label: "Genre", width: "1.2fr", value: (i) => (artistOf(i).genres ?? []).slice(0, 2).join(", ") },
  { key: "years", label: "Active", width: "110px", mono: true, value: (i) => artistOf(i).yearRange },
  { key: "albums", label: "Albums", width: "70px", mono: true, align: "right", value: (i) => artistOf(i).albumCount },
  { key: "tracks", label: "Tracks", width: "70px", mono: true, align: "right", value: (i) => artistOf(i).trackCount },
];

export interface MusicSourceOptions {
  /** The albums as the page shows them (the shelf, filtered by the search). */
  albums: MusicAlbumRow[];
  /**
   * The artists the "one per artist" grid shows — NOT derived from the albums: an artist with only
   * loose tracks has no album to be represented by, and the Artist facet / a name match on `q` keep
   * an artist whose albums were filtered out (`applyMusicFacets`). Group representatives would lose
   * all three, so the flat stream pages THIS list in the "one per artist" mode instead.
   */
  artists?: MusicArtistRow[];
  /**
   * True when the flat views are showing one card per ARTIST (`?items=groups` on Grid/Wall/List).
   * The source then pages the artist rows directly; the grouped views never see it (they band
   * albums by artist, which is a different question).
   */
  artistItems?: boolean;
  /** Names what makes the list a DIFFERENT list (shelf + search). */
  listKey: string;
  /** The section's own Grid card (R9 S3) — module-level, supplied by the page. */
  renderCard?: CatalogSource["renderCard"];
  onOpenAlbum: (id: number) => void;
  onOpenArtist: (id: number) => void;
  /**
   * Scope in place: a group header that has a matching FACET adds it and regroups a level. Artist
   * headers do not come here — they open the artist's own drill (`?artist=`), which is the section's
   * Directory second level and older than the rail.
   */
  onScope?: (patch: { facet?: { key: string; value: string }; years?: [number, number]; group?: string }) => void;
}

/** Which facet a group header becomes; `decade`/`year` are the year range and are handled on their own. */
const GROUP_FACET: Record<string, string> = { kind: "kind", tag: "tag", genre: "genre" };

/**
 * ONE Music catalog — the albums (R9 S1b; Eric: artists and albums are not two sections). "By
 * artist" is a group axis (the Extended / Shelves / Newspaper views band by artist) and "One per
 * artist" is the Items mode (the page's own Grid renders that as the artist grid); a fresh visitor
 * lands on one-per-artist, as the old Artists tab did. The Directory walks artists → albums.
 */
export function createMusicSource(o: MusicSourceOptions): CatalogSource {
  const open = (item: CardItem) => (item.kind === "artist" ? o.onOpenArtist(item.id) : o.onOpenAlbum(item.id));
  if (o.artistItems) {
    // One card per artist. The offer is IDENTICAL to the album source's (same views, same sorts,
    // same Items modes) so the host resolves the URL to the same state either way — only `items`
    // and the flat pages differ. No `fetchGroupBand`, so the flat stream pages these rows rather
    // than collapsing album groups to representatives.
    const artistSource = createClientSource({
      queryKey: `music:${o.listKey}:artists`,
      title: "Music",
      itemNoun: "artist",
      groupNoun: "artists",
      itemsLabels: { items: "Every album", groups: "One per artist" },
      items: (o.artists ?? []).map(toArtistCard),
      sorts: ARTIST_SORTS,
      listColumns: ARTIST_LIST_COLUMNS,
      defaultAspect: 1,
      renderCard: o.renderCard,
      gridClass: MUSIC_ARTIST_GRID_CLASS,
      gridCell: MUSIC_GRID_CELL,
      onOpen: open,
    });
    return { ...artistSource, supports: ALL_MUSIC_VIEWS, groups: ALBUM_GROUPERS.map(({ value, label }) => ({ value, label })), itemsModes: ["items", "groups"], defaultItems: "groups", defaultGroup: "artist" };
  }
  return createClientSource({
    queryKey: `music:${o.listKey}`,
    title: "Music",
    itemNoun: "album",
    groupNoun: "artists",
    itemsLabels: { items: "Every album", groups: "One per artist" },
    items: o.albums.map(toAlbumCard),
    groups: ALBUM_GROUPERS,
    sorts: ALBUM_SORTS,
    defaultGroup: "artist",
    defaultItems: "groups",
    directoryGroup: "artist",
    listColumns: ALBUM_LIST_COLUMNS,
    defaultAspect: 1,
    renderCard: o.renderCard,
    gridClass: MUSIC_ALBUM_GRID_CLASS,
    gridCell: MUSIC_GRID_CELL,
    onOpen: open,
    onOpenGroup: (group: CardGroup, groupBy: string) => {
      if (groupBy === "artist") {
        const id = Number(group.key);
        if (Number.isInteger(id) && id > 0) o.onOpenArtist(id);
        return;
      }
      if (!o.onScope) return;
      if (groupBy === "decade" || groupBy === "year") {
        const y = Number(group.key);
        if (!Number.isFinite(y)) return;
        // A decade shelf is ten years; a year shelf is one.
        return o.onScope({ years: groupBy === "decade" ? [y, y + 9] : [y, y], group: "artist" });
      }
      // The Kind header names a SHELF, so scoping to it re-fetches that shelf (the S2c rule); the
      // library's own rows are the shelf-less scope, which is "no kind pill" rather than `kind:music`.
      const facet = GROUP_FACET[groupBy];
      if (!facet) return;
      if (groupBy === "kind" && group.key === "music") return o.onScope({ group: "artist" });
      o.onScope({ facet: { key: facet, value: group.key }, group: "artist" });
    },
  });
}
