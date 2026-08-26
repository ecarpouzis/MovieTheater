/**
 * The facet state as a hook over the URL: read with the codec, written with `history.push` so Back
 * walks every filter change (the site's law — the standalone kept this in memory). The catalog's own
 * `view/group/items/sort` ride along untouched; an open modal closes, because a filter change is a
 * new browse, not a change to the thing being looked at.
 *
 * The transitions are pure functions (`facetTransitions`) so the toggle semantics are testable on
 * their own: "+" on an included value removes it, "+" on an excluded value MOVES it (one value is in
 * one list at most), "−" is the mirror.
 */
import { useCallback, useMemo } from "react";
import { useHistory, useLocation } from "react-router-dom";
import type { FacetSpec, FacetState, FacetValue } from "./facetSpec";
import { activeFacetCount, facetEquals } from "./facetSpec";
import { parseFacetState, writeFacetState } from "./facetUrl";

export type FacetMode = "inc" | "exc";
export type FacetPatch = (draft: FacetState) => void;

export interface FacetActions {
  setText(q: string): void;
  /** Include a value (a no-op when it already is; an excluded value moves over). */
  add(key: string, value: FacetValue): void;
  /** Drop a value from both lists. */
  remove(key: string, value: FacetValue): void;
  /** The rail's "+"/"−": toggle the value in that list, moving it out of the other. */
  setMode(key: string, value: FacetValue, mode: FacetMode): void;
  setYears(min: number | null, max: number | null): void;
  setRating(min: number): void;
  setFlag(key: string, on: boolean): void;
  clearAll(): void;
  /** Several changes in ONE push (a group header scopes AND regroups); `params` sets other query params (`null` deletes). */
  apply(patch: FacetPatch, params?: Record<string, string | null>): void;
  /** A saved search: replace the whole query string with the one saved. */
  replaceSearch(search: string): void;
}

export const DEFAULT_ENTITY_PARAMS: readonly string[] = ["item", "series"];

export function cloneFacetState(state: FacetState): FacetState {
  return { ...state, include: { ...state.include }, exclude: { ...state.exclude }, flags: { ...state.flags } };
}

const without = (list: FacetValue[] | undefined, value: FacetValue) => (list ?? []).filter((v) => !facetEquals(v, value));
const has = (list: FacetValue[] | undefined, value: FacetValue) => (list ?? []).some((v) => facetEquals(v, value));

export const facetTransitions = {
  add(state: FacetState, key: string, value: FacetValue): FacetState {
    const d = cloneFacetState(state);
    d.exclude[key] = without(d.exclude[key], value);
    if (!has(d.include[key], value)) d.include[key] = [...(d.include[key] ?? []), value];
    return d;
  },
  remove(state: FacetState, key: string, value: FacetValue): FacetState {
    const d = cloneFacetState(state);
    d.include[key] = without(d.include[key], value);
    d.exclude[key] = without(d.exclude[key], value);
    return d;
  },
  setMode(state: FacetState, key: string, value: FacetValue, mode: FacetMode): FacetState {
    const d = cloneFacetState(state);
    const wasIncluded = has(d.include[key], value);
    const wasExcluded = has(d.exclude[key], value);
    d.include[key] = without(d.include[key], value);
    d.exclude[key] = without(d.exclude[key], value);
    if (mode === "inc" && !wasIncluded) d.include[key] = [...d.include[key], value];
    if (mode === "exc" && !wasExcluded) d.exclude[key] = [...d.exclude[key], value];
    return d;
  },
  clearAll(state: FacetState): FacetState {
    return { ...state, q: "", include: {}, exclude: {}, yearMin: null, yearMax: null, ratingMin: 0, flags: {} };
  },
};

export interface UseFacetStateResult {
  state: FacetState;
  actions: FacetActions;
  activeCount: number;
  search: string;
}

export default function useFacetState(spec: FacetSpec, opts: { entityParams?: readonly string[] } = {}): UseFacetStateResult {
  const history = useHistory();
  const location = useLocation();
  const entityParams = opts.entityParams ?? DEFAULT_ENTITY_PARAMS;
  const state = useMemo(() => parseFacetState(location.search, spec), [location.search, spec]);

  const commit = useCallback((next: FacetState, params?: Record<string, string | null>) => {
    const p = new URLSearchParams(location.search);
    writeFacetState(p, next, spec);
    for (const k of entityParams) p.delete(k);
    if (params) {
      for (const [k, v] of Object.entries(params)) {
        if (v == null) p.delete(k);
        else p.set(k, v);
      }
    }
    const search = p.toString();
    history.push({ pathname: location.pathname, search: search ? `?${search}` : "", state: location.state });
  }, [history, location.pathname, location.search, location.state, spec, entityParams]);

  const actions = useMemo<FacetActions>(() => ({
    setText: (q) => commit({ ...cloneFacetState(state), q }),
    add: (key, value) => commit(facetTransitions.add(state, key, value)),
    remove: (key, value) => commit(facetTransitions.remove(state, key, value)),
    setMode: (key, value, mode) => commit(facetTransitions.setMode(state, key, value, mode)),
    setYears: (min, max) => commit({ ...cloneFacetState(state), yearMin: min, yearMax: max }),
    setRating: (min) => commit({ ...cloneFacetState(state), ratingMin: Math.max(0, Math.min(100, Math.floor(min))) }),
    setFlag: (key, on) => {
      const d = cloneFacetState(state);
      if (on) d.flags[key] = true;
      else delete d.flags[key];
      commit(d);
    },
    clearAll: () => commit(facetTransitions.clearAll(state)),
    apply: (patch, params) => {
      const d = cloneFacetState(state);
      patch(d);
      commit(d, params);
    },
    replaceSearch: (search) => {
      const s = search.trim();
      history.push({ pathname: location.pathname, search: s && !s.startsWith("?") ? `?${s}` : s, state: location.state });
    },
  }), [state, commit, history, location.pathname, location.state]);

  return { state, actions, activeCount: activeFacetCount(state, spec), search: location.search };
}
