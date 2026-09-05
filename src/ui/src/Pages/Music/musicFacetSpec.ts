/**
 * The Music browse's facets (R9 S2c) — the same `q/f/x/y` URL contract as Movies and Boardgames,
 * applied in memory over the SHELF the browser holds. Rail order: Shelf · Artist · Genre · Tag · Year · Rating.
 *
 * The Shelf facet (`f=kind:comedy` / `f=kind:audiobook`; nothing = the music library) is a SCOPE,
 * not a filter: the page fetches the named shelf (music-plan.md §2.6 — the whole point of a shelf is
 * that its rows never entered the browse catalog, so the excluded material is never one stale
 * filter away from the grid), which is why its pills carry no counts and why `kindOf` collapses the
 * state to one shelf. Everything else (artist, tag, the year range, `q`) filters the fetched rows.
 *
 * This file also owns the section's translations: `legacyToMusicSearch` (the pre-S2c `?kind=`,
 * `?tab=` / `?view=artists|albums` links, rewritten once on entry) and the album → artist fold the
 * "one per artist" grid needs (`applyMusicFacets`).
 */
import { applyFacetState, countClientFacets, decadeOf, type ClientFacetOptions, type FacetExtractor } from "../../catalog/rail/clientFacets";
import type { FacetOptionRow, FacetSpec, FacetState } from "../../catalog/rail/facetSpec";
import { parseFacetState, writeFacetState } from "../../catalog/rail/facetUrl";
import type { MusicAlbumRow, MusicArtistRow } from "../../catalog/sources/musicSource";
import { readCatalogDefaults } from "../../catalog/state/useCatalogView";

/**
 * The shelves (MusicArtist.Kind). No kind is the music library — the untagged rows — and the two
 * named shelves are where the spoken-word material lives instead of in the middle of it. One table
 * so the rail, the headings and the spec can't disagree about what a shelf is called.
 */
export const MUSIC_KINDS = [
  { key: "", label: "Music", noun: { artists: "Artists", albums: "Albums" } },
  { key: "comedy", label: "Comedy", noun: { artists: "Comedians", albums: "Comedy albums" } },
  { key: "audiobook", label: "Audiobooks", noun: { artists: "Authors", albums: "Audiobooks" } },
] as const;
export type MusicKind = (typeof MUSIC_KINDS)[number]["key"];

/** The two opt-in shelves as the Shelf facet's pills (the library is "no pill"). */
export const SHELF_OPTIONS: FacetOptionRow[] = MUSIC_KINDS.filter((k) => k.key).map((k) => ({ value: k.key, label: k.label, count: 0 }));

export const normalizeKind = (raw: string | null | undefined): MusicKind => (MUSIC_KINDS.some((k) => k.key && k.key === raw) ? (raw as MusicKind) : "");

/** The shelf a state names — the LAST kind added wins (the pills are single-select; `legacyToMusicSearch` collapses the URL). */
export function kindOf(state: FacetState): MusicKind {
  const list = (state.include.kind ?? []).map((v) => normalizeKind(String(v))).filter(Boolean);
  return list.length ? list[list.length - 1] : "";
}

export function shelfOf(kind: MusicKind) {
  return MUSIC_KINDS.find((k) => k.key === kind) ?? MUSIC_KINDS[0];
}

/** "groups" (one per artist — the landing) or "items" (every album): the catalog's Items mode from the URL or the remembered default. */
export function musicItemsMode(search: string): "groups" | "items" {
  const p = new URLSearchParams(search);
  const items = p.get("items") ?? readCatalogDefaults("music").items ?? "groups";
  return items === "items" ? "items" : "groups";
}

const ALBUM_EXTRACTORS: Record<string, FacetExtractor<MusicAlbumRow>> = {
  artist: (a) => a.artistId ?? null,
  tag: (a) => a.tag ?? null,
  // R9 S10. An album is legitimately several genres at once, so this returns the whole list and the
  // matcher's default (ALL included values must be present) reads as an AND — picking Jazz and Funk
  // narrows to the records that are both, which is what a two-pill selection should mean.
  genre: (a) => a.genres ?? null,
  decades: (a) => decadeOf(a.year),
};

function albumOptions(kind: MusicKind): ClientFacetOptions<MusicAlbumRow> {
  return {
    text: (a) => `${a.title ?? ""} ${a.artistName ?? ""} ${(a.genres ?? []).join(" ")}`,
    year: (a) => a.year ?? null,
    // The rating FLOOR reads the same blended number the Top-rated order does (the server computes
    // it once, so the sort and the floor cannot disagree). An album with no score is below every
    // floor — the year range's rule.
    rating: (a) => (typeof a.rating === "number" ? a.rating : null),
    labelOf: { decades: (v) => `${v}s` },
  };
}

/** The shelf is the scope every fetched row belongs to: a `kind:` include must never drop a row of its own shelf (the matcher excludes on an unknown key). */
const withKind = (kind: MusicKind): Record<string, FacetExtractor<MusicAlbumRow>> => ({ ...ALBUM_EXTRACTORS, kind: () => (kind || "music") });

/** True when something beyond the shelf narrows the albums (the artist grid folds to matching artists then). */
export function narrowsAlbums(state: FacetState): boolean {
  if (state.q.trim() || state.yearMin != null || state.yearMax != null || state.ratingMin > 0) return true;
  for (const [key, list] of Object.entries(state.include)) if (key !== "kind" && list.length) return true;
  for (const list of Object.values(state.exclude)) if (list.length) return true;
  return false;
}

export interface MusicResults {
  albums: MusicAlbumRow[];
  artists: MusicArtistRow[];
}

/**
 * The albums a state keeps, and the artists the "one per artist" grid shows: every artist of the
 * shelf when nothing narrows the albums; otherwise those with a kept album, plus a name match on `q`
 * and an artist named by the Artist facet (a loose-tracks-only artist has no album to be kept by).
 */
export function applyMusicFacets(albums: readonly MusicAlbumRow[], artists: readonly MusicArtistRow[], state: FacetState): MusicResults {
  const kind = kindOf(state);
  // The shelf pill is a scope; the kind include must not be judged against the rows (see withKind).
  const kept = applyFacetState(albums, state, withKind(kind), albumOptions(kind));
  if (!narrowsAlbums(state)) return { albums: kept, artists: [...artists] };
  const q = state.q.trim().toLowerCase();
  const wantedArtists = new Set((state.include.artist ?? []).map(Number));
  const withAlbum = new Set(kept.map((a) => a.artistId).filter((id): id is number => id != null));
  const onlyQ = !!q && !Object.entries(state.include).some(([k, l]) => k !== "kind" && l.length) && !Object.values(state.exclude).some((l) => l.length) && state.yearMin == null && state.yearMax == null && state.ratingMin === 0;
  return {
    albums: kept,
    artists: artists.filter((ar) => withAlbum.has(ar.id) || wantedArtists.has(ar.id) || (onlyQ && (ar.name ?? "").toLowerCase().includes(q))),
  };
}

/** The option rows the rail lists over the shelf: artists by album count (their label is the name), tags, decades. */
export function countMusicFacets(albums: readonly MusicAlbumRow[], artists: readonly MusicArtistRow[]): Record<string, FacetOptionRow[]> {
  const counts = countClientFacets(albums, ALBUM_EXTRACTORS, { labelOf: { decades: (v) => `${v}s` } });
  const nameById = new Map(artists.map((a) => [a.id, a.name ?? `#${a.id}`]));
  counts.artist = (counts.artist ?? []).map((r) => ({ ...r, label: nameById.get(Number(r.value)) ?? r.label }));
  // Artists with no album (loose tracks only) still belong in the list, at 0.
  const listed = new Set(counts.artist.map((r) => Number(r.value)));
  for (const a of artists) if (!listed.has(a.id)) counts.artist.push({ value: a.id, label: a.name ?? `#${a.id}`, count: 0 });
  counts.artist.sort((x, y) => y.count - x.count || x.label.localeCompare(y.label, undefined, { sensitivity: "base" }));
  counts.decades = [...(counts.decades ?? [])].sort((a, b) => Number(a.value) - Number(b.value));
  counts.kind = SHELF_OPTIONS;
  return counts;
}

/**
 * The spec over one shelf's rows: `identity` carries the shelf, the rows' version and the Items mode
 * (the count's noun follows it — artists on the "one per artist" grid, albums on "every album").
 */
export function musicFacetSpec(identity: string, albums: readonly MusicAlbumRow[], artists: readonly MusicArtistRow[], noun: string): FacetSpec {
  return {
    identity: `music:${identity}`,
    noun,
    text: true,
    years: { decadesKey: "decades", decadePills: false },
    facets: [
      { key: "kind", token: "kind", label: "Shelf", one: "Shelf", valueType: "string", render: "pill", excludable: false, showCounts: false, labelOf: (v) => shelfOf(normalizeKind(String(v))).label },
      { key: "artist", token: "artist", label: "Artist", one: "Artist", valueType: "number" },
      // R9 S10. `dynamic` because music genre tags are an open set of thousands with no authority
      // behind them — the rail searches and pages the long tail instead of drawing it all. Counts
      // come from the shelf the browser already holds, so the option list costs no request.
      { key: "genre", token: "genre", label: "Genre", one: "Genre", valueType: "string", dynamic: true },
      { key: "tag", token: "tag", label: "Tag", one: "Tag", valueType: "string" },
    ],
    // The floor the URL already carried (`r=`, the codec's own param) — now that there is a score
    // for it to read. The stops are the movie section's, so one habit works everywhere.
    rating: { presets: [{ value: 60, label: "60+" }, { value: 70, label: "70+" }, { value: 80, label: "80+" }, { value: 90, label: "90+" }] },
    async loadFacets() {
      return countMusicFacets(albums, artists);
    },
    /**
     * The dynamic facets' long tail, searched and paged — over the SHELF the browser already holds,
     * so it is a slice of an array rather than a request. (The contract is async because a
     * server-backed section's is; answering from memory is the cheap case, not a different shape.)
     */
    async loadOptions(key: string, q: string, skip: number, top: number) {
      const all = countMusicFacets(albums, artists)[key] ?? [];
      const term = q.trim().toLowerCase();
      const hits = term ? all.filter((r) => r.label.toLowerCase().includes(term)) : all;
      return { items: hits.slice(skip, skip + top), total: hits.length };
    },
  };
}

/** A parse-only spec (no rows): the URL codec needs the tokens, not the counts. */
export const MUSIC_PARSE_SPEC: FacetSpec = musicFacetSpec("parse", [], [], "albums");

/** The shelf the URL names (`f=kind:…`) — read before the rows exist, since it decides what to fetch. */
export function kindFromSearch(search: string): MusicKind {
  // A not-yet-rewritten legacy `?kind=` names the shelf too — so the first render fetches the right one.
  const legacy = normalizeKind(new URLSearchParams(search).get("kind"));
  return legacy || kindOf(parseFacetState(search, MUSIC_PARSE_SPEC));
}

/**
 * A pre-S2c link as the search it means: `?kind=comedy` (the old shelf picker) → `f=kind:comedy`;
 * `?tab=artists|albums` / the older `?view=artists|albums` → the catalog's `?items=`; several
 * `kind:` includes collapse to the last. Null when the URL is already in its final form.
 */
export function legacyToMusicSearch(search: string): string | null {
  const p = new URLSearchParams(search);
  const legacyKind = p.get("kind");
  const legacyTab = p.get("tab") ?? (["artists", "albums"].includes(p.get("view") ?? "") ? p.get("view") : null);
  const state = parseFacetState(search, MUSIC_PARSE_SPEC);
  const kinds = state.include.kind ?? [];
  if (legacyKind == null && legacyTab == null && kinds.length <= 1) return null;
  p.delete("kind");
  p.delete("tab");
  if (["artists", "albums"].includes(p.get("view") ?? "")) p.delete("view");
  if (legacyTab) p.set("items", legacyTab === "albums" ? "items" : "groups");
  const kind = legacyKind != null ? normalizeKind(legacyKind) : kindOf(state);
  const next: FacetState = { ...state, include: { ...state.include } };
  if (kind) next.include.kind = [kind];
  else delete next.include.kind;
  writeFacetState(p, next, MUSIC_PARSE_SPEC);
  const s = p.toString();
  return s ? `?${s}` : "";
}
