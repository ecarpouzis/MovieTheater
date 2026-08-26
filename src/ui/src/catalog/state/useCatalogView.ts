import { useCallback, useMemo } from "react";
import { useHistory, useLocation } from "react-router-dom";
import { readStored, writeStored } from "../../utils/storage";
import type { CatalogSource, ItemsMode, ViewMode } from "../types";

/**
 * The switcher's state — view / group / items / sort — lives in the URL (`?view=&group=&items=&sort=`)
 * so a filtered Wall is linkable and Back walks through it, the way every detail modal on the site
 * already does. The standalone site kept all of this in localStorage and nothing in the URL; the
 * per-section DEFAULT is what survives here (`catalog.view.v1:<section>`), applied whenever the URL
 * says nothing.
 *
 * Every value is validated against what the source offers and what the package has implemented:
 * a stale `?view=shelf` on a section without shelves falls back to the section's default instead
 * of rendering nothing.
 */
export interface CatalogViewState {
  view: ViewMode;
  group: string;
  items: ItemsMode;
  sort: string;
}

export const CATALOG_PARAM_KEYS = ["view", "group", "items", "sort"] as const;

export const NO_GROUP = "none";

export function storageKeyFor(section: string): string {
  return `catalog.view.v1:${section}`;
}

function readStoredDefaults(section: string): Partial<CatalogViewState> {
  const raw = readStored(storageKeyFor(section), null) as string | null;
  if (!raw) return {};
  try {
    const parsed = JSON.parse(raw) as Partial<CatalogViewState>;
    return parsed && typeof parsed === "object" ? parsed : {};
  } catch {
    return {};
  }
}

/** Pure: what the state IS for a URL + stored defaults + a source. Exported for the tests. */
export function resolveViewState(
  search: string,
  stored: Partial<CatalogViewState>,
  source: CatalogSource,
  available: readonly ViewMode[],
): CatalogViewState {
  const params = new URLSearchParams(search);
  const views = source.supports.filter((v) => available.includes(v));
  const fallbackView = views.includes(source.defaultView ?? "grid") ? (source.defaultView ?? "grid") : (views[0] ?? "grid");
  const urlView = params.get("view") as ViewMode | null;
  const view = urlView && views.includes(urlView) ? urlView
    : stored.view && views.includes(stored.view) ? stored.view
    : fallbackView;

  const groupValues = source.groups.map((g) => g.value);
  const fallbackGroup = source.defaultGroup && groupValues.includes(source.defaultGroup) ? source.defaultGroup : NO_GROUP;
  const pickGroup = (g: string | null | undefined) => (g && (g === NO_GROUP || groupValues.includes(g)) ? g : null);
  const group = pickGroup(params.get("group")) ?? pickGroup(stored.group) ?? fallbackGroup;

  const itemsModes = source.itemsModes ?? ["items"];
  const pickItems = (i: string | null | undefined): ItemsMode | null =>
    i === "items" || i === "groups" ? (itemsModes.includes(i) ? i : null) : null;
  const items = pickItems(params.get("items")) ?? pickItems(stored.items) ?? "items";

  const sortValues = source.sorts.map((s) => s.value);
  const fallbackSort = source.defaultSort && sortValues.includes(source.defaultSort) ? source.defaultSort : (sortValues[0] ?? "");
  const pickSort = (s: string | null | undefined) => (s && sortValues.includes(s) ? s : null);
  const sort = pickSort(params.get("sort")) ?? pickSort(stored.sort) ?? fallbackSort;

  return { view, group, items, sort };
}

export default function useCatalogView(section: string, source: CatalogSource, available: readonly ViewMode[]) {
  const history = useHistory();
  const location = useLocation();
  const search = location.search;

  const state = useMemo(
    () => resolveViewState(search, readStoredDefaults(section), source, available),
    // The source's OFFER (supports/groups/sorts) is what matters, not its identity; its queryKey
    // changes on every filter edit and must not re-resolve the view.
    // eslint-disable-next-line react-hooks/exhaustive-deps
    [search, section, source.supports, source.groups, source.sorts, source.itemsModes, source.defaultView, source.defaultGroup, source.defaultSort, available],
  );

  /** Apply a change: the URL carries it (push — Back undoes it), the section remembers it as its default. */
  const set = useCallback((patch: Partial<CatalogViewState>) => {
    const next = { ...state, ...patch };
    const params = new URLSearchParams(location.search);
    for (const key of CATALOG_PARAM_KEYS) params.set(key, String(next[key]));
    writeStored(storageKeyFor(section), JSON.stringify(next));
    history.push({ pathname: location.pathname, search: `?${params.toString()}`, state: location.state });
  }, [state, section, history, location.pathname, location.search, location.state]);

  const setView = useCallback((view: ViewMode) => set({ view }), [set]);
  const setGroup = useCallback((group: string) => set({ group }), [set]);
  const setItems = useCallback((items: ItemsMode) => set({ items }), [set]);
  const setSort = useCallback((sort: string) => set({ sort }), [set]);

  return { state, set, setView, setGroup, setItems, setSort };
}
