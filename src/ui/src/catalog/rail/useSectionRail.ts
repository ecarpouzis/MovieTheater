/**
 * Everything a section's rail surfaces share, in one hook (R9 S2 normalization). Every section ran
 * the same six lines in two trees — the sider rail and the page — before this: parse the URL into a
 * `FacetState`, load the spec's option lists, read whether the current view groups, and open the
 * section's saved-search store with a "save the current search" closure over it.
 *
 * The two trees still read the URL rather than each other (nothing crosses the sider/page boundary
 * through props); this only stops each section from re-deriving the same four things by hand.
 * `SectionSiderRail` draws the sider from it, `useSectionRailSurfaces` draws the page's pill / chips
 * / bar search / phone sheet, and the section is left with only the parts that are actually its own:
 * its spec and its result count.
 */
import { useCallback } from "react";
import { useLocation } from "react-router-dom";
import { isGroupedBrowse } from "../state/useCatalogView";
import type { FacetSpec, FacetState } from "./facetSpec";
import { savableSearch, useSavedSearches, type SavedSearch } from "./savedSearches";
import useFacetOptions, { type FacetLists } from "./useFacetOptions";
import useFacetState, { type FacetActions } from "./useFacetState";

export interface SectionRailState {
  /** The section key — the saved searches' store and the catalog's view state both key on it. */
  section: string;
  spec: FacetSpec;
  state: FacetState;
  actions: FacetActions;
  activeCount: number;
  /** Whether the view on screen groups (the groups-only facets and flags show only then). */
  grouped: boolean;
  facets: { data?: FacetLists; isLoading: boolean };
  saved: { list: SavedSearch[]; remove: (id: string) => void };
  /** Save the whole current query string (minus the entity params) under a name. */
  saveCurrent: (name: string) => void;
}

export interface SectionRailOptions {
  /** The section's modal/entity params — dropped from a filter push and from a saved search. */
  entityParams?: readonly string[];
  /** Load the option lists (false while the section can't use them — Books' Directory, a gated Music). */
  facetsEnabled?: boolean;
  /** Override the grouped reading (Novels has no grouped views at all). */
  grouped?: boolean;
}

export default function useSectionRail(section: string, spec: FacetSpec, opts: SectionRailOptions = {}): SectionRailState {
  const location = useLocation();
  const { state, actions, activeCount } = useFacetState(spec, { entityParams: opts.entityParams });
  const facets = useFacetOptions(spec, opts.facetsEnabled ?? true);
  const saved = useSavedSearches(section);
  const savedSave = saved.save;
  const saveCurrent = useCallback(
    (name: string) => savedSave(name, savableSearch(location.search, opts.entityParams)),
    [savedSave, location.search, opts.entityParams],
  );
  return {
    section,
    spec,
    state,
    actions,
    activeCount,
    grouped: opts.grouped ?? isGroupedBrowse(location.search, section),
    facets,
    saved,
    saveCurrent,
  };
}
