/**
 * The Arcade lobby's facets (R9 S2c): the rail's `q/f/x` URL contract over the lobby's SERVER-side
 * filter (`/API/Arcade/Games` is paged over ~13k cards — nothing is filtered in the browser). The
 * spec's option lists come from `/API/Arcade/Filters` (faceted against the current scope, shared with
 * the console carousel through `useArcadeFilters`).
 *
 *   f=system:snes (repeatable)   → system=snes,genesis   the carousel IS this facet (Eric, canvas
 *                                                        2026-08-27): the rail draws no System section
 *                                                        (`hidden`), the SmartSearch still takes `system:`
 *   x=region:Japan (repeatable)  → hideRegions=Japan     the deselect model: the API can only HIDE a
 *                                                        region, so the facet is exclude-only
 *   f=players:2                  → maxPlayers=2          one value (2+ · 3+ · 4+ · 5)
 *   f=genre:RPG                  → genre=RPG             one value (the API takes one)
 *   f=variant:modded             → variant=modded        one value (release · modded · romhacks)
 *   f=ra:achievements            → ra=achievements       one value
 *   q=mario                      → search=mario
 *
 * `legacyToArcadeSearch` rewrites the pre-S2c `?system=&hideRegions=&players=&variant=&genre=&ra=`
 * links once on entry (and collapses a single-valued facet to its last value); `arcadeFilterParams`
 * is the state in the API's vocabulary — the page's pump, the letters strip, the facets request and
 * the catalog source all read it.
 */
import type { FacetOptionRow, FacetSpec, FacetState, FacetValue } from "../../catalog/rail/facetSpec";
import { parseFacetState, writeFacetState } from "../../catalog/rail/facetUrl";
import type { ArcadeFilters } from "../../catalog/sources/arcadeSource";
import { systemLabel } from "./arcadeSystems";

export const ARCADE_ENTITY_PARAMS = ["game"] as const;

/** The `/API/Arcade/Filters` response, the fields the spec reads. */
export interface ArcadeFacetsDto {
  total?: number;
  systems?: { value: string; count: number }[];
  regions?: { value: string; count: number }[];
  genres?: { value: string; count: number }[];
  variants?: { value: string; count: number }[];
  ra?: { achievements?: number; highScores?: number; speedruns?: number };
}

export const PLAYER_OPTIONS: FacetOptionRow[] = [
  { value: 2, label: "2+", count: 0 },
  { value: 3, label: "3+", count: 0 },
  { value: 4, label: "4+", count: 0 },
  { value: 5, label: "5", count: 0 },
];
export const VARIANT_OPTIONS: FacetOptionRow[] = [
  { value: "release", label: "Official releases", count: 0 },
  { value: "modded", label: "Mods & hacks", count: 0 },
  { value: "romhacks", label: "Our romhacks", count: 0 },
];
const RA_VALUES = [
  { value: "achievements", label: "🏆 Achievements", pick: (r: NonNullable<ArcadeFacetsDto["ra"]>) => r.achievements },
  { value: "highscores", label: "🥇 High scores", pick: (r: NonNullable<ArcadeFacetsDto["ra"]>) => r.highScores },
  { value: "speedruns", label: "⏱️ Speedruns", pick: (r: NonNullable<ArcadeFacetsDto["ra"]>) => r.speedruns },
] as const;

/** The facets whose API param takes ONE value: the last include wins (the pills read as single-select). */
export const SINGLE_VALUED = ["players", "genre", "variant", "ra"] as const;

const labelOfTable = (rows: FacetOptionRow[]) => (v: FacetValue) => rows.find((r) => String(r.value) === String(v))?.label ?? String(v);
const playersLabel = labelOfTable(PLAYER_OPTIONS);
const variantLabel = labelOfTable(VARIANT_OPTIONS);
const raLabel = (v: FacetValue) => RA_VALUES.find((r) => r.value === String(v))?.label ?? String(v);

// The canvas order (2026-08-27): Genre · Players · Region · Mods & hacks · RetroAchievements. The
// System facet stays hidden — the console carousel above the grid IS it.
export const ARCADE_FACET_DEFS: FacetSpec["facets"] = [
  { key: "system", token: "system", label: "System", one: "System", valueType: "string", hidden: true, excludable: false, labelOf: (v) => systemLabel(String(v)) },
  { key: "genre", token: "genre", label: "Genre", one: "Genre", valueType: "string", defaultOpen: true, excludable: false },
  { key: "players", token: "players", label: "Players", one: "Players", valueType: "number", render: "pill", defaultOpen: true, excludable: false, showCounts: false, labelOf: playersLabel },
  { key: "region", token: "region", label: "Region", one: "Region", valueType: "string", includable: false, defaultOpen: true },
  { key: "variant", token: "variant", label: "Mods & hacks", one: "Variant", valueType: "string", render: "pill", excludable: false, showCounts: false, labelOf: variantLabel },
  { key: "ra", token: "ra", label: "RetroAchievements", one: "RA", valueType: "string", render: "pill", excludable: false, labelOf: raLabel },
];

/** The option rows from one facets response (systems by name; regions/genres by count; RA from its counters). */
export function arcadeFacetRows(facets: ArcadeFacetsDto | null | undefined): Record<string, FacetOptionRow[]> {
  const counted = (list: { value: string; count: number }[] | undefined, label?: (v: string) => string): FacetOptionRow[] =>
    (list ?? []).map((r) => ({ value: r.value, label: label ? label(r.value) : r.value, count: r.count }));
  const ra = facets?.ra;
  return {
    system: counted(facets?.systems, (v) => systemLabel(v)),
    region: counted(facets?.regions),
    genre: counted(facets?.genres),
    players: PLAYER_OPTIONS,
    variant: VARIANT_OPTIONS,
    ra: RA_VALUES.map((r) => ({ value: r.value, label: r.label, count: ra ? r.pick(ra) ?? 0 : 0 })),
  };
}

/** The spec over one facets response; `identity` is the facet scope's key (the counts describe the scope). */
export function arcadeFacetSpec(identity: string, facets: ArcadeFacetsDto | null | undefined): FacetSpec {
  return {
    identity: `arcade:${identity}`,
    noun: "games",
    text: true,
    facets: ARCADE_FACET_DEFS,
    async loadFacets() {
      return arcadeFacetRows(facets);
    },
  };
}

/** A parse-only spec: the URL codec needs the tokens, not the counts. */
export const ARCADE_PARSE_SPEC: FacetSpec = arcadeFacetSpec("parse", null);

const lastOf = (list: FacetValue[] | undefined): string => (list?.length ? String(list[list.length - 1]) : "");

/** The state in the API's vocabulary (`ArcadeFilters`) — `sort` is the server's ("" = A–Z), added by the caller. */
export function arcadeFilterParams(state: FacetState, sort = ""): ArcadeFilters {
  return {
    system: (state.include.system ?? []).map((v) => String(v).toLowerCase()).filter(Boolean).join(","),
    hideRegions: (state.exclude.region ?? []).map(String).filter(Boolean).join(","),
    maxPlayers: lastOf(state.include.players),
    variant: lastOf(state.include.variant),
    genre: lastOf(state.include.genre),
    sort,
    search: state.q.trim(),
    ra: lastOf(state.include.ra),
  };
}

/** True when anything narrows the lobby (the empty state reads "no match" only then). */
export function arcadeNarrows(filters: ArcadeFilters): boolean {
  return !!(filters.system || filters.hideRegions || filters.maxPlayers || (filters.variant && filters.variant !== "all") || filters.genre || filters.search || filters.ra);
}

const LEGACY_KEYS = ["system", "hideRegions", "players", "variant", "genre", "ra"] as const;

/**
 * A pre-S2c lobby link as the facet search it means; null when the URL is already in its final form.
 * `?variant=all` is the old default written out loud — it drops. A single-valued facet carrying
 * several values (two `f=genre:` pills) collapses to its last, since the API takes one.
 */
export function legacyToArcadeSearch(search: string): string | null {
  const p = new URLSearchParams(search);
  const state = parseFacetState(search, ARCADE_PARSE_SPEC);
  const hasLegacy = LEGACY_KEYS.some((k) => p.has(k));
  const overfull = SINGLE_VALUED.some((k) => (state.include[k]?.length ?? 0) > 1);
  if (!hasLegacy && !overfull) return null;
  const next: FacetState = { ...state, include: { ...state.include }, exclude: { ...state.exclude } };
  const csv = (v: string | null) => (v ?? "").split(",").map((s) => s.trim()).filter(Boolean);
  if (p.has("system")) {
    const systems = csv(p.get("system")).map((s) => s.toLowerCase());
    if (systems.length) next.include.system = [...new Set([...(next.include.system ?? []).map(String), ...systems])];
  }
  if (p.has("hideRegions")) {
    const regions = csv(p.get("hideRegions"));
    if (regions.length) next.exclude.region = [...new Set([...(next.exclude.region ?? []).map(String), ...regions])];
  }
  const players = Number(p.get("players"));
  if (Number.isInteger(players) && players > 0) next.include.players = [players];
  const variant = (p.get("variant") ?? "").trim();
  if (variant && variant !== "all") next.include.variant = [variant];
  const genre = (p.get("genre") ?? "").trim();
  if (genre) next.include.genre = [genre];
  const ra = (p.get("ra") ?? "").trim();
  if (ra) next.include.ra = [ra];
  for (const k of SINGLE_VALUED) {
    const list = next.include[k];
    if (list && list.length > 1) next.include[k] = [list[list.length - 1]];
    if (list && list.length === 0) delete next.include[k];
  }
  for (const k of LEGACY_KEYS) p.delete(k);
  writeFacetState(p, next, ARCADE_PARSE_SPEC);
  const s = p.toString();
  return s ? `?${s}` : "";
}
