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
 *   dir=8817                  a section-specific extra the codec preserves but does not interpret
 *
 * Unknown tokens are dropped on read; values are whatever `URLSearchParams` gives back (already
 * decoded). The catalog's own `view/group/items/sort` and the modals' params are never touched.
 */
import type { FacetDef, FacetSpec, FacetState, FacetValue } from "./facetSpec";
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

  const flags: Record<string, boolean> = {};
  const flagByToken = new Map((spec.flags ?? []).map((f) => [f.token, f]));
  for (const token of (params.get("my") ?? "").split(",").map((s) => s.trim()).filter(Boolean)) {
    const def = flagByToken.get(token);
    if (def) flags[def.key] = true;
  }

  return { q: (params.get("q") ?? "").trim(), include, exclude, yearMin, yearMax, ratingMin, flags };
}

/** Rewrite the facet params in place, leaving every other param alone. */
export function writeFacetState(params: URLSearchParams, state: FacetState, spec: FacetSpec): void {
  for (const key of ["q", "f", "x", "y", "r", "my"]) params.delete(key);
  if (state.q.trim()) params.set("q", state.q.trim());
  for (const def of spec.facets) {
    for (const v of state.include[def.key] ?? []) params.append("f", `${def.token}:${v}`);
  }
  for (const def of spec.facets) {
    for (const v of state.exclude[def.key] ?? []) params.append("x", `${def.token}:${v}`);
  }
  if (state.yearMin != null || state.yearMax != null) params.set("y", `${state.yearMin ?? ""}-${state.yearMax ?? ""}`);
  if (state.ratingMin > 0) params.set("r", String(state.ratingMin));
  const flags = (spec.flags ?? []).filter((f) => state.flags[f.key]).map((f) => f.token);
  if (flags.length) params.set("my", flags.join(","));
}

/** A canonical, order-independent key for the state — the source's `queryKey` and the cache signature. */
export function facetStateKey(state: FacetState): string {
  const canon = (rec: Record<string, FacetValue[]>) =>
    Object.keys(rec).sort().filter((k) => rec[k]?.length).map((k) => `${k}=${[...rec[k]].map(String).sort().join("|")}`).join(";");
  const flags = Object.keys(state.flags).filter((k) => state.flags[k]).sort().join(",");
  return `q=${state.q.trim().toLowerCase()};i=${canon(state.include)};x=${canon(state.exclude)};y=${state.yearMin ?? ""}-${state.yearMax ?? ""};r=${state.ratingMin};my=${flags}`;
}

/** True when `search` carries none of the facet params (a landing check). */
export function hasNoFacetParams(search: string): boolean {
  const params = new URLSearchParams(search);
  return FACET_PARAM_KEYS.every((k) => !params.has(k));
}

export function emptyFacetState(): FacetState {
  return { ...EMPTY_FACET_STATE, include: {}, exclude: {}, flags: {} };
}
