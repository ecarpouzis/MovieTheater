/**
 * The facet rail's contract — what a section declares so the generic rail, smart search, chips and
 * saved searches can drive it. A `FacetSpec` is data plus two loaders; the STATE is a plain object the
 * URL codec (`facetUrl.ts`) reads and writes. Nothing here renders.
 */

export type FacetValue = string | number;

/** One option of a facet as the rail lists it: the value the filter takes, a label, a count. */
export interface FacetOptionRow {
  value: FacetValue;
  label: string;
  count: number;
  hue?: number;
  imageUrl?: string | null;
}

export interface FacetDef {
  /** State key and the section's own name for it ("series"). */
  key: string;
  /** URL + smart-search prefix ("series" in `f=series:12`). */
  token: string;
  label: string;
  /** Singular, for a chip ("Series", "Author"). */
  one: string;
  valueType: "string" | "number";
  /** Long tail served by `loadOptions` (searched, paged) instead of the up-front facet list. */
  dynamic?: boolean;
  defaultOpen?: boolean;
  /** How the rail draws the options: checks, publisher swatches, collection tiles, or one row of
   *  toggle pills for a short fixed scale (the five MPA stops — a click includes, a second click clears). */
  render?: "check" | "swatch" | "tile" | "pill";
  /** "groups": only meaningful on grouped views (hidden when the view is flat + items). */
  appliesTo?: "all" | "groups";
  /** false → counts are shown but the facet cannot filter (no include/exclude controls). */
  filterable?: boolean;
  /** false → include only; the rail offers no "−" (the section's API cannot exclude on this facet). */
  excludable?: boolean;
  /** How a raw value reads on a chip when the loaded option list does not carry it (a composite "category:value"). */
  labelOf?: (value: FacetValue) => string;
}

export interface FacetFlagDef {
  key: string;
  token: string;
  label: string;
  title?: string;
  appliesTo?: "all" | "groups";
}

export interface FacetSpec {
  /** Memo key for the loaded options (per section + user facts). */
  identity: string;
  /** What a result is called in the count ("comics", "titles"); defaults to "results". */
  noun?: string;
  facets: FacetDef[];
  /** Offer free-text search (`q`). */
  text?: boolean;
  /** Offer the year range; the decades list comes from `loadFacets()[decadesKey]` (it sizes the
   *  sliders). `decadePills: false` draws the two-thumb range with read-outs only — no decade row. */
  years?: { decadesKey: string; decadePills?: boolean };
  rating?: { presets: { value: number; label: string }[] };
  flags?: FacetFlagDef[];
  loadFacets(signal?: AbortSignal): Promise<Record<string, FacetOptionRow[]>>;
  loadOptions?(key: string, q: string, skip: number, top: number, signal?: AbortSignal): Promise<{ items: FacetOptionRow[]; total: number }>;
}

export interface FacetState {
  q: string;
  include: Record<string, FacetValue[]>;
  exclude: Record<string, FacetValue[]>;
  yearMin: number | null;
  yearMax: number | null;
  /** 0 = no floor. */
  ratingMin: number;
  flags: Record<string, boolean>;
}

export const EMPTY_FACET_STATE: FacetState = Object.freeze({
  q: "",
  include: {},
  exclude: {},
  yearMin: null,
  yearMax: null,
  ratingMin: 0,
  flags: {},
}) as FacetState;

/** Case-insensitive for strings (the standalone's `eqFilter`), exact for numbers. */
export function facetEquals(a: FacetValue, b: FacetValue): boolean {
  if (typeof a === "number" || typeof b === "number") return String(a) === String(b);
  return a.localeCompare(b, undefined, { sensitivity: "accent" }) === 0;
}

export function hasFacetValue(list: FacetValue[] | undefined, value: FacetValue): boolean {
  return !!list && list.some((v) => facetEquals(v, value));
}

/** How many filters are active — the rail badge and the "Clear all" affordance. */
export function activeFacetCount(state: FacetState, spec: FacetSpec): number {
  let n = 0;
  for (const f of spec.facets) n += (state.include[f.key]?.length ?? 0) + (state.exclude[f.key]?.length ?? 0);
  if (state.q.trim()) n += 1;
  if (state.yearMin != null || state.yearMax != null) n += 1;
  if (state.ratingMin > 0) n += 1;
  for (const flag of spec.flags ?? []) if (state.flags[flag.key]) n += 1;
  return n;
}

export function isEmptyFacetState(state: FacetState, spec: FacetSpec): boolean {
  return activeFacetCount(state, spec) === 0;
}
