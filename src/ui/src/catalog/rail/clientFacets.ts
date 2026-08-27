/**
 * Facets over a list the browser already holds (Boardgames' cached OData rows, Music's shelf,
 * Arcade's lobby) — the client twin of the server's `BrowseFilter`: the same `FacetState` the URL
 * codec reads, applied in memory, and the option counts the rail lists computed from the same rows.
 *
 * Semantics match the Movies server: within one facet the included values are ALL required by
 * default (Crime AND Drama; a game that plays 2 AND 4) — a facet may opt into ANY (`anyOf`) —
 * excluded values are NOT-ed, facets AND together; `q` is a case-insensitive substring over the
 * section's text; the year range brackets the section's year; a fixed-scale range brackets the
 * item's number (or overlaps its span — a 30–60 min game meets a 45–90 range); an item WITHOUT the
 * value is out once its range is set (the year rule); flags are the section's own tests.
 */
import type { FacetOptionRow, FacetState, FacetValue } from "./facetSpec";
import { facetEquals } from "./facetSpec";

export type FacetExtractor<T> = (item: T) => FacetValue | FacetValue[] | null | undefined;
/** A range's value: one number, or a [lo, hi] span the range must overlap. */
export type RangeExtractor<T> = (item: T) => number | [number, number] | null | undefined;

export interface ClientFacetOptions<T> {
  /** The text `q` searches. */
  text?: (item: T) => string | null | undefined;
  /** The year the range brackets (null = no year → excluded once a range is set). */
  year?: (item: T) => number | null | undefined;
  /** A personal flag's test (`my=`), by flag key. */
  flags?: Record<string, (item: T) => boolean>;
  /** The number (or span) each `spec.ranges` entry brackets, by range key. */
  ranges?: Record<string, RangeExtractor<T>>;
  /** Facet keys where ANY included value matches (default: ALL must). */
  anyOf?: readonly string[];
  /** A label for a value the rail lists (default: the value itself). */
  labelOf?: Record<string, (value: FacetValue) => string>;
}

function valuesOf<T>(extract: FacetExtractor<T> | undefined, item: T): FacetValue[] {
  if (!extract) return [];
  const v = extract(item);
  if (v == null) return [];
  return Array.isArray(v) ? v.filter((x) => x != null && x !== "") : v === "" ? [] : [v];
}

/** True when `item` satisfies every part of `state`. */
export function matchesFacetState<T>(item: T, state: FacetState, extractors: Record<string, FacetExtractor<T>>, opts: ClientFacetOptions<T> = {}): boolean {
  const q = state.q.trim().toLowerCase();
  if (q) {
    const text = (opts.text?.(item) ?? "").toLowerCase();
    if (!text.includes(q)) return false;
  }
  if (state.yearMin != null || state.yearMax != null) {
    const y = opts.year?.(item) ?? null;
    if (y == null) return false;
    if (state.yearMin != null && y < state.yearMin) return false;
    if (state.yearMax != null && y > state.yearMax) return false;
  }
  for (const [key, range] of Object.entries(state.ranges ?? {})) {
    if (!range || (range.min == null && range.max == null)) continue;
    const v = opts.ranges?.[key]?.(item) ?? null;
    if (v == null) return false;
    const [lo, hi] = Array.isArray(v) ? v : [v, v];
    if (range.min != null && hi < range.min) return false;
    if (range.max != null && lo > range.max) return false;
  }
  for (const [key, wanted] of Object.entries(state.include)) {
    if (!wanted?.length) continue;
    const have = valuesOf(extractors[key], item);
    const any = opts.anyOf?.includes(key) ?? false;
    const hit = (w: FacetValue) => have.some((h) => facetEquals(h, w));
    if (any ? !wanted.some(hit) : !wanted.every(hit)) return false;
  }
  for (const [key, banned] of Object.entries(state.exclude)) {
    if (!banned?.length) continue;
    const have = valuesOf(extractors[key], item);
    if (banned.some((b) => have.some((h) => facetEquals(h, b)))) return false;
  }
  for (const [flag, on] of Object.entries(state.flags)) {
    if (!on) continue;
    const test = opts.flags?.[flag];
    if (test && !test(item)) return false;
  }
  return true;
}

export function applyFacetState<T>(items: readonly T[], state: FacetState, extractors: Record<string, FacetExtractor<T>>, opts: ClientFacetOptions<T> = {}): T[] {
  return items.filter((item) => matchesFacetState(item, state, extractors, opts));
}

/**
 * The option rows per facet key over `items` — counts describe the scope handed in (the Long Box
 * rule: the caller passes the rows the rail can reach, not the current selection), most-common
 * first, ties alphabetical. A `decades` key can be derived by passing an extractor for it.
 */
export function countClientFacets<T>(items: readonly T[], extractors: Record<string, FacetExtractor<T>>, opts: Pick<ClientFacetOptions<T>, "labelOf"> = {}): Record<string, FacetOptionRow[]> {
  const out: Record<string, FacetOptionRow[]> = {};
  for (const [key, extract] of Object.entries(extractors)) {
    const counts = new Map<string, { value: FacetValue; count: number }>();
    for (const item of items) {
      for (const v of valuesOf(extract, item)) {
        const k = typeof v === "number" ? `n:${v}` : `s:${String(v).toLowerCase()}`;
        const row = counts.get(k);
        if (row) row.count += 1;
        else counts.set(k, { value: v, count: 1 });
      }
    }
    const label = opts.labelOf?.[key] ?? ((v: FacetValue) => String(v));
    out[key] = [...counts.values()]
      .map((r) => ({ value: r.value, label: label(r.value), count: r.count }))
      .sort((a, b) => b.count - a.count || a.label.localeCompare(b.label, undefined, { numeric: true, sensitivity: "base" }));
  }
  return out;
}

/** "1995" → 1990 — the decade an item's year falls in, for a `decades` extractor. */
export function decadeOf(year: number | null | undefined): number | null {
  return year != null && Number.isFinite(year) && year > 1000 ? Math.floor(year / 10) * 10 : null;
}
