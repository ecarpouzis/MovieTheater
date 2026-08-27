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
  /** With `render: "pill"`: a fixed SCALE that must read as one line — the five MPA stops (Eric,
   *  canvas 2026-08-27: "MPA = five stops on one line"). Tighter, no wrap, the row shared evenly. */
  stops?: boolean;
  /** "groups": only meaningful on grouped views (hidden when the view is flat + items). */
  appliesTo?: "all" | "groups";
  /** false → counts are shown but the facet cannot filter (no include/exclude controls). */
  filterable?: boolean;
  /** false → include only; the rail offers no "−" (the section's API cannot exclude on this facet). */
  excludable?: boolean;
  /** false → the rows show no count (a facet whose values are SCOPES the counts cannot describe — the Music shelves). */
  showCounts?: boolean;
  /** false → exclude only; the rail offers no "+" (the Arcade's deselect-a-region model: the API can only HIDE regions). */
  includable?: boolean;
  /** true → in the spec for the URL codec, the search and the chips, but NOT drawn as a rail section — another
   *  surface owns it (the Arcade's console carousel IS the System facet). */
  hidden?: boolean;
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

/**
 * A range over a numeric attribute with a FIXED scale (the Boardgames age slider: 3 · 4 · 5 · 6 · 7 ·
 * 8 · 10 · 12 · 14 · 16 · 18+): two thumbs walk the stops, a thumb parked at either end is an open
 * side, and BOTH thumbs filter (the lower one hides what sits below it — 12+ hides the kid games).
 * URL form: `<token>=min-max` (`a=12-`, `a=-8`, `t=30-60`). The rail draws it under the facet named
 * by `after` (or after every facet when unset); the stops are the whole option list, so no counts
 * are loaded for it.
 */
export interface RangeFacetDef {
  key: string;
  /** URL param (`a` in `a=12-18`) — must not collide with the codec's own or the catalog's params. */
  token: string;
  label: string;
  /** For the chip ("age 12+"). */
  one: string;
  /** Ascending. */
  stops: number[];
  /** The top stop reads as "and up" ("18+"): a max AT the top stop is an open top. */
  openTop?: boolean;
  /** How one value reads ("2.5", "60 min"); default `String(v)`. */
  format?: (v: number) => string;
  /** The facet key this section follows in the rail; unset = after every facet. */
  after?: string;
  defaultOpen?: boolean;
}

export interface FacetRange {
  min: number | null;
  max: number | null;
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
   *  sliders). `decadePills: false` draws the two-thumb range with read-outs only — no decade row.
   *  `label` names the section (the canvas calls it "Years"), and `after` places it under a named
   *  facet the way `RangeFacetDef.after` does — the approved order is Type · Genre · MPA · YEARS ·
   *  Franchise · People · Mood, and a year range pinned to the bottom of the rail is not that. */
  years?: { decadesKey: string; decadePills?: boolean; label?: string; after?: string };
  rating?: { presets: { value: number; label: string }[] };
  /** The fixed-scale ranges (age, minutes, weight) — see `RangeFacetDef`. */
  ranges?: RangeFacetDef[];
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
  /** By `RangeFacetDef.key`; absent = unset. A side that is null is open. */
  ranges: Record<string, FacetRange>;
  flags: Record<string, boolean>;
}

export const EMPTY_FACET_STATE: FacetState = Object.freeze({
  q: "",
  include: {},
  exclude: {},
  yearMin: null,
  yearMax: null,
  ratingMin: 0,
  ranges: {},
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
  for (const r of spec.ranges ?? []) if (isRangeSet(state.ranges?.[r.key])) n += 1;
  for (const flag of spec.flags ?? []) if (state.flags[flag.key]) n += 1;
  return n;
}

export function isRangeSet(range: FacetRange | undefined): boolean {
  return !!range && (range.min != null || range.max != null);
}

/** "12+", "≤8", "8–12" — how a set range reads on a chip and in the rail's read-out. */
export function rangeLabel(def: RangeFacetDef, range: FacetRange | undefined): string {
  const f = def.format ?? ((v: number) => String(v));
  const min = range?.min ?? null;
  const max = range?.max ?? null;
  if (min != null && max != null) return min === max ? f(min) : `${f(min)}–${f(max)}`;
  if (min != null) return `${f(min)}+`;
  if (max != null) return `≤${f(max)}`;
  return "any";
}

export function isEmptyFacetState(state: FacetState, spec: FacetSpec): boolean {
  return activeFacetCount(state, spec) === 0;
}
