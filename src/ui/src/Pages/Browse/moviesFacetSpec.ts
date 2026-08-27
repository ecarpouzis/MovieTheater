/**
 * The Movies/TV browse's facets (R9 S2) — the rail's order is Eric's: Type · Genre · MPA rating ·
 * Years · Franchise · People · the AI tag axes (mood, subgenre, era, theme, setting) · the viewer's
 * own lists. The state lives in the URL (`q/f/x/y/my` — `facetUrl.ts`); this file also owns the two
 * translations the section needs:
 *
 *   moviesFilterParams(state)  → the `BrowseFilterQuery` vocabulary `/API/Browse*` reads
 *   legacyToFacetSearch(search) → the pre-S2 `?mode=&value=&types=` links, rewritten once on entry
 *
 * and the landing seed: a clean `/` gets the persisted Type scope (`f=type:Movies` by default) once
 * per tab session, so clearing the chip later can mean "all types" (the Novels pattern).
 */
import type { FacetOptionRow, FacetSpec, FacetState, FacetValue } from "../../catalog/rail/facetSpec";
import { parseFacetState, writeFacetState } from "../../catalog/rail/facetUrl";

export const MOVIE_TYPES = ["Movies", "Series", "Short", "Misc"] as const;
export type MovieType = (typeof MOVIE_TYPES)[number];

/** The five MPA stops (SearchTools' stops, now facet values): NC-17 covers X server-side; no NR. */
export const MPA_STOPS: FacetOptionRow[] = [
  { value: "1", label: "G", count: 0 },
  { value: "2", label: "PG", count: 0 },
  { value: "3", label: "PG-13", count: 0 },
  { value: "4", label: "R", count: 0 },
  { value: "5", label: "NC-17", count: 0 },
];

/** The AI insight tag categories the rail offers, in rail order (`TagCategory` tokens the API takes). */
export const TAG_FACETS = [
  { key: "mood", label: "Mood", one: "Mood" },
  { key: "subgenre", label: "Subgenre", one: "Subgenre" },
  { key: "era", label: "Era", one: "Era" },
  { key: "theme", label: "Theme", one: "Theme" },
  { key: "setting", label: "Setting", one: "Setting" },
] as const;
const TAG_KEYS = new Set<string>(TAG_FACETS.map((t) => t.key));

/** The viewer's lists: `my=seen,want,rated` — ANDed server-side; the sider's index rows write the same param. */
export const MY_FLAGS = [
  { key: "seen", token: "seen", label: "Seen", title: "Only what you have marked seen" },
  { key: "want", token: "want", label: "Want to watch", title: "Only your queue" },
  { key: "rated", token: "rated", label: "Rated", title: "Only what you have rated" },
] as const;

/** "post-apocalypse" → "Post apocalypse" (the server's Humanize, for a chip whose option list is not loaded). */
export function humanizeTag(value: FacetValue): string {
  const s = String(value).replace(/[-_]+/g, " ").trim();
  return s ? s[0].toUpperCase() + s.slice(1) : s;
}

const mpaLabel = (value: FacetValue) => MPA_STOPS.find((s) => s.value === String(value))?.label ?? String(value);

/** A type value as the API spells it ("movies" → "Movies"); null for anything unknown. */
export function normalizeType(raw: FacetValue): MovieType | null {
  const s = String(raw).trim().toLowerCase();
  return MOVIE_TYPES.find((t) => t.toLowerCase() === s) ?? null;
}

interface FacetCountsDto {
  types: { value: string; label: string; count: number }[];
  genres: { value: string; label: string; count: number }[];
  franchises: { value: string; label: string; count: number }[];
  mpa: { value: string; label: string; count: number }[];
  decades: { value: string; label: string; count: number }[];
  tags: Record<string, { value: string; label: string; count: number }[]>;
  total: number;
}

async function getJson<T>(url: string, signal?: AbortSignal): Promise<T> {
  const r = await fetch(url, { signal });
  if (!r.ok) throw new Error(`${url} → ${r.status}`);
  return (await r.json()) as T;
}

/**
 * The spec for one viewer over one Type scope. `identity` carries the viewer facts the server keys
 * its counts on (user + age gate); `types` is the scope the counts describe (the Long Box rule) —
 * the loaded lists re-fetch when the scope changes and ride the server's cache otherwise.
 */
export function moviesFacetSpec(identity: string, types: readonly string[] = []): FacetSpec {
  const scope = types.map(normalizeType).filter((t): t is MovieType => t != null);
  return {
    identity: `movies:${identity}:${scope.join(",")}`,
    noun: "titles",
    text: true,
    years: { decadesKey: "decades", decadePills: false },
    flags: MY_FLAGS.map((f) => ({ ...f })),
    facets: [
      { key: "type", token: "type", label: "Type", one: "Type", valueType: "string", defaultOpen: true, excludable: false },
      { key: "genre", token: "genre", label: "Genre", one: "Genre", valueType: "string", defaultOpen: true },
      { key: "mpa", token: "mpa", label: "MPA rating", one: "Rated", valueType: "string", render: "pill", defaultOpen: true, excludable: false, labelOf: mpaLabel },
      { key: "franchise", token: "franchise", label: "Franchise", one: "Franchise", valueType: "string", labelOf: humanizeTag },
      { key: "person", token: "person", label: "People", one: "Person", valueType: "string", dynamic: true },
      ...TAG_FACETS.map((t) => ({ key: t.key, token: t.key, label: t.label, one: t.one, valueType: "string" as const, labelOf: humanizeTag })),
    ],
    async loadFacets(signal) {
      const p = new URLSearchParams();
      if (scope.length) p.set("types", scope.join(","));
      const f = await getJson<FacetCountsDto>(`/API/BrowseFacets?${p.toString()}`, signal);
      const rows = (list: { value: string; label: string; count: number }[] | undefined): FacetOptionRow[] =>
        (list ?? []).map((r) => ({ value: r.value, label: r.label, count: r.count }));
      const out: Record<string, FacetOptionRow[]> = {
        type: rows(f.types),
        genre: rows(f.genres),
        mpa: rows(f.mpa),
        franchise: rows(f.franchises),
        decades: rows(f.decades),
      };
      for (const t of TAG_FACETS) out[t.key] = rows(f.tags?.[t.key]);
      return out;
    },
    async loadOptions(key, q, skip, top, signal) {
      if (key !== "person") return { items: [], total: 0 };
      if (skip > 0) return { items: [], total: 0 }; // the typeahead is one page of the most-credited
      const r = await getJson<{ items: { value: string; label: string; count: number }[]; total: number }>(
        `/API/BrowsePeople?q=${encodeURIComponent(q)}&top=${top}`, signal);
      return { items: r.items.map((o) => ({ value: o.value, label: o.label, count: o.count })), total: r.total };
    },
  };
}

/** A parse-only spec (no viewer facts): the URL codec needs the tokens, not the loaders. */
export const MOVIES_PARSE_SPEC: FacetSpec = moviesFacetSpec("parse");

/** The Type scope named in the URL (`f=type:…`), API-spelled; empty = every type. */
export function typesFromSearch(search: string): MovieType[] {
  const out: MovieType[] = [];
  for (const entry of new URLSearchParams(search).getAll("f")) {
    if (!entry.startsWith("type:")) continue;
    const t = normalizeType(entry.slice(5));
    if (t && !out.includes(t)) out.push(t);
  }
  return out;
}

/** The scope's types as the API csv (`types=`), from the state. */
export function typesOf(state: FacetState): MovieType[] {
  const out: MovieType[] = [];
  for (const v of state.include.type ?? []) {
    const t = normalizeType(v);
    if (t && !out.includes(t)) out.push(t);
  }
  return out;
}

/**
 * The facet state in `/API/Browse*`'s own vocabulary — everything but the Type scope (`types=`) and
 * the sort, which the search hook adds. Repeatable keys append; excludes go to their `ex*` twins.
 */
export function moviesFilterParams(state: FacetState): URLSearchParams {
  const p = new URLSearchParams();
  const put = (key: string, values: FacetValue[] | undefined) => { for (const v of values ?? []) p.append(key, String(v)); };
  if (state.q.trim()) p.set("q", state.q.trim());
  put("genre", state.include.genre); put("exGenre", state.exclude.genre);
  put("franchise", state.include.franchise); put("exFranchise", state.exclude.franchise);
  put("person", state.include.person); put("exPerson", state.exclude.person);
  for (const key of TAG_KEYS) {
    for (const v of state.include[key] ?? []) p.append("tag", `${key}:${v}`);
    for (const v of state.exclude[key] ?? []) p.append("exTag", `${key}:${v}`);
  }
  const mpa = (state.include.mpa ?? []).map(String).filter((v) => /^[1-6]$/.test(v));
  if (mpa.length) p.set("mpa", mpa.join(","));
  if (state.yearMin != null) p.set("yearMin", String(state.yearMin));
  if (state.yearMax != null) p.set("yearMax", String(state.yearMax));
  const my = MY_FLAGS.filter((f) => state.flags[f.key]).map((f) => f.token);
  if (my.length) p.set("my", my.join(","));
  return p;
}

/** True when nothing narrows the browse beyond the Type scope — "the landing" (the Now-on-TV rail, the back-nav snapshot). */
export function isPlainMoviesSearch(state: FacetState): boolean {
  if (state.q.trim() || state.yearMin != null || state.yearMax != null || state.ratingMin > 0) return false;
  if (Object.values(state.flags).some(Boolean)) return false;
  for (const [key, list] of Object.entries(state.include)) if (key !== "type" && list.length) return false;
  for (const list of Object.values(state.exclude)) if (list.length) return false;
  return true;
}

/** The lists the state asks for (`my=`), in flag order. */
export function myListsOf(state: FacetState): string[] {
  return MY_FLAGS.filter((f) => state.flags[f.key]).map((f) => f.key);
}

const LEGACY_KEYS = ["mode", "value", "types"] as const;
const LEGACY_MODE_FACET: Record<string, string> = { actor: "person", genre: "genre", franchise: "franchise" };

/**
 * A pre-S2 browse link (`?mode=genre&value=Crime&types=Movies,Series`, the rail's old vocabulary)
 * as the facet search it means; null when the URL carries none of the legacy params. Every other
 * param (sort, the catalog's view params, an open `?title=`) rides through untouched. A letter mode
 * becomes the alphabetical sort (the strip jumps there); a rating mode becomes the MPA stop.
 */
export function legacyToFacetSearch(search: string): string | null {
  const p = new URLSearchParams(search);
  if (!LEGACY_KEYS.some((k) => p.has(k))) return null;
  const mode = p.get("mode") ?? "";
  const value = (p.get("value") ?? "").trim();
  const types = p.get("types");
  for (const k of LEGACY_KEYS) p.delete(k);
  const state = parseFacetState(`?${p.toString()}`, MOVIES_PARSE_SPEC);
  const next: FacetState = { ...state, include: { ...state.include }, exclude: { ...state.exclude }, flags: { ...state.flags } };
  if (types != null) {
    const list = types.split(",").map(normalizeType).filter((t): t is MovieType => t != null);
    if (list.length) next.include.type = list;
    else delete next.include.type;
  }
  if (mode === "title" && value) next.q = value;
  else if (LEGACY_MODE_FACET[mode] && value) next.include[LEGACY_MODE_FACET[mode]] = value.split(",").map((s) => s.trim()).filter(Boolean);
  else if (mode === "rating" && value) next.include.mpa = [value.split(",")[0].trim()].filter((v) => /^[1-6]$/.test(v)).map((v) => (v === "6" ? "5" : v));
  else if (mode === "letter") p.set("sort", "alpha");
  else if (mode === "seen" || mode === "want") next.flags[mode] = true;
  writeFacetState(p, next, MOVIES_PARSE_SPEC);
  const s = p.toString();
  return s ? `?${s}` : "";
}

export const MOVIES_SEEDED_KEY = "movies.seeded.v1";
const FACET_PARAMS = ["q", "f", "x", "y", "r", "my"];

/**
 * The search string a clean `/` should land on: the persisted Type scope as `f=type:` chips, once
 * per tab session, only when the URL carries no filter of its own. Null = leave the URL alone. A
 * cleared chip later in the session means "all types" and is not re-seeded.
 */
export function seededMoviesSearch(search: string, persistedTypes: readonly string[], storage: Pick<Storage, "getItem" | "setItem"> | null): string | null {
  const p = new URLSearchParams(search);
  if (FACET_PARAMS.some((k) => p.has(k))) return null;
  try {
    if (storage?.getItem(MOVIES_SEEDED_KEY)) return null;
    storage?.setItem(MOVIES_SEEDED_KEY, "1");
  } catch {
    /* private mode: seed this once anyway */
  }
  const list = persistedTypes.map(normalizeType).filter((t): t is MovieType => t != null);
  if (!list.length) return null;
  const state = parseFacetState(search, MOVIES_PARSE_SPEC);
  writeFacetState(p, { ...state, include: { ...state.include, type: list } }, MOVIES_PARSE_SPEC);
  const s = p.toString();
  return s ? `?${s}` : "";
}

export function markMoviesSeeded(storage: Pick<Storage, "setItem"> | null): void {
  try { storage?.setItem(MOVIES_SEEDED_KEY, "1"); } catch { /* ignore */ }
}

export function sessionStorageOrNull(): Storage | null {
  try {
    return typeof window !== "undefined" ? window.sessionStorage : null;
  } catch {
    return null;
  }
}
