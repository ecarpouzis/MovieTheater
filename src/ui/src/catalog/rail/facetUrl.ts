/**
 * The facet state's URL form — the site's law is that browse state lives in the URL (a filtered wall
 * is linkable; Back walks it), where the standalone kept it in memory.
 *
 *   q=hellboy                 free text
 *   f=series:4123&f=tag:Noir  include (repeatable, `token:value`)
 *   x=tag:Manga               exclude (same form)
 *   y=1980-1989 | y=1990- | y=-1965   year range, open ends allowed
 *   r=80                      rating floor (0–100)
 *   my=read,want              flags, by token
 *   a=12-18 | a=12- | a=-8    a fixed-scale range (`FacetSpec.ranges`), under its own token
 *   dir=8817                  a section-specific extra the codec preserves but does not interpret
 *
 * Unknown tokens are dropped on read; values are whatever `URLSearchParams` gives back (already
 * decoded). The catalog's own `view/group/items/sort` and the modals' params are never touched.
 */
import type { FacetDef, FacetRange, FacetSpec, FacetState, FacetValue } from "./facetSpec";
import { EMPTY_FACET_STATE } from "./facetSpec";

export const FACET_PARAM_KEYS = ["q", "f", "x", "y", "r", "my", "dir"] as const;

function defByToken(spec: FacetSpec): Map<string, FacetDef> {
  return new Map(spec.facets.map((f) => [f.token, f]));
}

function parseValue(def: FacetDef, raw: string): FacetValue | null {
  if (def.valueType === "number") {
    const n = Number(raw);
    return Number.isFinite(n) ? n : null;
  }
  return raw.length > 0 ? raw : null;
}

function splitToken(entry: string): [string, string] | null {
  const i = entry.indexOf(":");
  if (i <= 0) return null;
  return [entry.slice(0, i), entry.slice(i + 1)];
}

export function parseFacetState(search: string, spec: FacetSpec): FacetState {
  const params = new URLSearchParams(search);
  const byToken = defByToken(spec);
  const include: Record<string, FacetValue[]> = {};
  const exclude: Record<string, FacetValue[]> = {};

  const read = (param: "f" | "x", into: Record<string, FacetValue[]>) => {
    for (const entry of params.getAll(param)) {
      const parts = splitToken(entry);
      if (!parts) continue;
      const def = byToken.get(parts[0]);
      if (!def) continue;
      const value = parseValue(def, parts[1]);
      if (value == null) continue;
      (into[def.key] ??= []).push(value);
    }
  };
  read("f", include);
  read("x", exclude);

  let yearMin: number | null = null;
  let yearMax: number | null = null;
  const y = params.get("y");
  if (y) {
    const m = /^(\d{1,4})?-(\d{1,4})?$/.exec(y.trim());
    if (m) {
      yearMin = m[1] ? Number(m[1]) : null;
      yearMax = m[2] ? Number(m[2]) : null;
    }
  }

  const r = Number(params.get("r") ?? "0");
  const ratingMin = Number.isFinite(r) && r > 0 ? Math.min(100, Math.floor(r)) : 0;

  const ranges: Record<string, FacetRange> = {};
  for (const def of spec.ranges ?? []) {
    const parsed = parseRange(params.get(def.token));
    if (parsed) ranges[def.key] = parsed;
  }

  const flags: Record<string, boolean> = {};
  const flagByToken = new Map((spec.flags ?? []).map((f) => [f.token, f]));
  for (const token of (params.get("my") ?? "").split(",").map((s) => s.trim()).filter(Boolean)) {
    const def = flagByToken.get(token);
    if (def) flags[def.key] = true;
  }

  return { q: (params.get("q") ?? "").trim(), include, exclude, yearMin, yearMax, ratingMin, ranges, flags };
}

/** `12-18` / `12-` / `-8` → a range; null for anything else (and for a fully open `-`). */
export function parseRange(raw: string | null): FacetRange | null {
  if (!raw) return null;
  const m = /^(\d+(?:\.\d+)?)?-(\d+(?:\.\d+)?)?$/.exec(raw.trim());
  if (!m) return null;
  const min = m[1] != null ? Number(m[1]) : null;
  const max = m[2] != null ? Number(m[2]) : null;
  if (min == null && max == null) return null;
  return { min, max };
}

export function formatRange(range: FacetRange): string {
  return `${range.min ?? ""}-${range.max ?? ""}`;
}

/** Rewrite the facet params in place, leaving every other param alone. */
export function writeFacetState(params: URLSearchParams, state: FacetState, spec: FacetSpec): void {
  for (const key of ["q", "f", "x", "y", "r", "my"]) params.delete(key);
  for (const def of spec.ranges ?? []) params.delete(def.token);
  if (state.q.trim()) params.set("q", state.q.trim());
  for (const def of spec.facets) {
    for (const v of state.include[def.key] ?? []) params.append("f", `${def.token}:${v}`);
  }
  for (const def of spec.facets) {
    for (const v of state.exclude[def.key] ?? []) params.append("x", `${def.token}:${v}`);
  }
  if (state.yearMin != null || state.yearMax != null) params.set("y", `${state.yearMin ?? ""}-${state.yearMax ?? ""}`);
  if (state.ratingMin > 0) params.set("r", String(state.ratingMin));
  for (const def of spec.ranges ?? []) {
    const range = state.ranges?.[def.key];
    if (range && (range.min != null || range.max != null)) params.set(def.token, formatRange(range));
  }
  const flags = (spec.flags ?? []).filter((f) => state.flags[f.key]).map((f) => f.token);
  if (flags.length) params.set("my", flags.join(","));
}

/** A canonical, order-independent key for the state — the source's `queryKey` and the cache signature. */
export function facetStateKey(state: FacetState): string {
  const canon = (rec: Record<string, FacetValue[]>) =>
    Object.keys(rec).sort().filter((k) => rec[k]?.length).map((k) => `${k}=${[...rec[k]].map(String).sort().join("|")}`).join(";");
  const flags = Object.keys(state.flags).filter((k) => state.flags[k]).sort().join(",");
  const ranges = Object.keys(state.ranges ?? {}).sort()
    .filter((k) => state.ranges[k] && (state.ranges[k].min != null || state.ranges[k].max != null))
    .map((k) => `${k}=${formatRange(state.ranges[k])}`).join(";");
  return `q=${state.q.trim().toLowerCase()};i=${canon(state.include)};x=${canon(state.exclude)};y=${state.yearMin ?? ""}-${state.yearMax ?? ""};r=${state.ratingMin};rg=${ranges};my=${flags}`;
}

/** True when `search` carries none of the facet params (a landing check) — the spec's range tokens included when given. */
export function hasNoFacetParams(search: string, spec?: Pick<FacetSpec, "ranges">): boolean {
  const params = new URLSearchParams(search);
  return FACET_PARAM_KEYS.every((k) => !params.has(k)) && (spec?.ranges ?? []).every((r) => !params.has(r.token));
}

export function emptyFacetState(): FacetState {
  return { ...EMPTY_FACET_STATE, include: {}, exclude: {}, ranges: {}, flags: {} };
}

/**
 * A browse URL carrying nothing but facets — the rail URL contract written from scratch (R9 S7).
 * Explore's group cards and "More →" links land on `<pathname>?f=token:value…`, which is exactly the
 * state the section's rail would have produced by hand, so the chip is present the moment the page
 * opens. `extra` carries the non-facet params a link may also want (`view`, `group`, `sort`).
 */
export function facetHref(
  pathname: string,
  facets: readonly (readonly [string, string | number])[],
  extra: Readonly<Record<string, string | number | undefined>> = {},
): string {
  const params = new URLSearchParams();
  for (const [token, value] of facets) {
    const v = String(value ?? "").trim();
    if (token && v) params.append("f", `${token}:${v}`);
  }
  for (const [k, v] of Object.entries(extra)) if (v != null && String(v).length > 0) params.set(k, String(v));
  const q = params.toString();
  return q ? `${pathname}?${q}` : pathname;
}
